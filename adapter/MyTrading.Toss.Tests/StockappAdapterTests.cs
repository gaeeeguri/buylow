/*
 * stockapp's patches to the Toss adapter, part 1 (stockapp spec §7.4, §8.4, §10): order bodies and
 * stateless guardrails, send-outcome classification, the token file, price limits and fill
 * increments. No network: SequenceHandler serves canned responses.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using MyTrading.Stockapp;
using MyTrading.Toss;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using Xunit;

namespace MyTrading.Toss.Tests
{
    public class SequenceHandler : HttpMessageHandler
    {
        // Each route serves its responses in order; the last one repeats. A null response throws.
        public readonly Dictionary<string, List<Func<HttpResponseMessage>>> Routes = new Dictionary<string, List<Func<HttpResponseMessage>>>();
        public readonly List<(string key, string auth, string body)> Seen = new List<(string, string, string)>();

        public void On(string key, params Func<HttpResponseMessage>[] responses) => Routes[key] = responses.ToList();

        public static Func<HttpResponseMessage> Json(HttpStatusCode code, string json) =>
            () => new HttpResponseMessage(code) { Content = new StringContent(json) };

        protected override HttpResponseMessage Send(HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            var key = request.Method.Method + " " + request.RequestUri.AbsolutePath;
            Seen.Add((key, request.Headers.Authorization?.ToString(), request.Content?.ReadAsStringAsync().Result));
            if (!Routes.TryGetValue(key, out var list)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            var next = list.Count > 1 ? list[0] : list[0];
            if (list.Count > 1) list.RemoveAt(0);
            return next() ?? throw new HttpRequestException("connection dropped");
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(Send(r, ct));
    }

    public static class Fixture
    {
        public const string Accounts = "{\"result\":[{\"accountNo\":\"1\",\"accountSeq\":7,\"accountType\":\"BROKERAGE\"}]}";

        public static (TossRestClient rest, SequenceHandler h, string tokenPath) Rest()
        {
            var dir = Directory.CreateTempSubdirectory("toss-test-").FullName;
            var tokenPath = Path.Combine(dir, "toss-token.json");
            File.WriteAllText(tokenPath, "{\"access_token\":\"TOK1\",\"expires_at\":0}");
            var h = new SequenceHandler();
            h.On("GET /api/v1/accounts", SequenceHandler.Json(HttpStatusCode.OK, Accounts));
            return (new TossRestClient(new TokenFileProvider(tokenPath), "http://fake", new HttpClient(h)), h, tokenPath);
        }

        public static TossSettings Settings(bool trading = true) => new TossSettings
        {
            TokenFile = "unused", TradingEnabled = trading, MaxOrderKrw = 1_000_000m, MaxOrderUsd = 1_000m,
        };
    }

    public class OrderPolicyTests
    {
        [Fact]
        public void Guardrails_fail_closed_without_a_cap_or_kr_price_limits()
        {
            const string tag = "000007-0000ya-001-01";
            var noCap = Fixture.Settings();
            noCap.MaxOrderKrw = 0m;
            noCap.MaxOrderUsd = 0m;
            Assert.StartsWith("no max order amount", OrderPolicy.Check(noCap, tag, "KR", 1, 70000m, (49000m, 91000m)));
            Assert.StartsWith("no max order amount", OrderPolicy.Check(noCap, tag, "US", 1, 10m, null));
            // Toss answered without limits for a KR symbol: unknown limits refuse, they don't skip the check
            Assert.StartsWith("today's price limits are unknown", OrderPolicy.Check(Fixture.Settings(), tag, "KR", 1, 70000m, null));
            Assert.Null(OrderPolicy.Check(Fixture.Settings(), tag, "US", 1, 10m, null));  // US has no /price-limits
        }

        [Fact]
        public void Kr_orders_are_opg_with_whole_won_and_us_orders_are_day_with_cents()
        {
            var kr = JObject.Parse(OrderPolicy.Body("000007-0000ya-001-01", "005930", "KR", true, 2, 72100m));
            Assert.Equal("OPG", kr.Value<string>("timeInForce"));
            Assert.Equal("72100", kr.Value<string>("price"));
            Assert.Equal("2", kr.Value<string>("quantity"));
            Assert.Equal("000007-0000ya-001-01", kr.Value<string>("clientOrderId"));
            var us = JObject.Parse(OrderPolicy.Body("000007-0000ya-002-01", "AAPL", "US", false, 1, 194.3m));
            Assert.Equal("DAY", us.Value<string>("timeInForce"));
            Assert.Equal("194.30", us.Value<string>("price"));
            Assert.Equal("SELL", us.Value<string>("side"));
            Assert.Equal("0.5123", OrderPolicy.FormatPrice("US", 0.5123m));
        }

        [Theory]
        [InlineData(false, "000007-0000ya-001-01", "KR", 2, 72100, "trading is disabled")]
        [InlineData(true, "buylow-1", "KR", 2, 72100, "no stockapp client order id")]
        [InlineData(true, "000007-0000ya-001-01", "KR", 14, 72100, "max order amount")]
        [InlineData(true, "000007-0000ya-001-01", "US", 6, 200, "max order amount")]
        [InlineData(true, "000007-0000ya-001-01", "KR", 2, 99000, "outside today's limits")]
        [InlineData(true, "000007-0000ya-001-01", "KR", 2, 72100, null)]
        public void Stateless_guardrails(bool trading, string tag, string market, int qty, int limit, string expected)
        {
            var got = OrderPolicy.Check(Fixture.Settings(trading), tag, market, qty, limit, market == "KR" ? (49000m, 91000m) : null);
            if (expected == null) Assert.Null(got);
            else Assert.Contains(expected, got);
        }
    }

    public class SendOutcomeTests
    {
        private const string Body = "{\"clientOrderId\":\"x\"}";

        [Fact]
        public void Accepted_rejected_and_uncertain()
        {
            var (rest, h, _) = Fixture.Rest();
            h.On("POST /api/v1/orders",
                SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"orderId\":\"O1\"}}"),
                SequenceHandler.Json(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"insufficient-buying-power\",\"message\":\"no\"}}"),
                SequenceHandler.Json(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"request-in-progress\",\"data\":{\"retryAfterSeconds\":2}}}"),
                SequenceHandler.Json(HttpStatusCode.BadGateway, "oops"),
                () => null);
            var a = rest.SendOrderOnce(Body);
            Assert.Equal(SendKind.Accepted, a.Kind);
            Assert.Equal("O1", a.OrderId);
            var r = rest.SendOrderOnce(Body);
            Assert.Equal(SendKind.Rejected, r.Kind);
            Assert.Equal("insufficient-buying-power", r.Code);
            var busy = rest.SendOrderOnce(Body);
            Assert.Equal(SendKind.Uncertain, busy.Kind);
            Assert.Equal(2, busy.RetryAfterSeconds);
            Assert.Equal(SendKind.Uncertain, rest.SendOrderOnce(Body).Kind);   // 502
            Assert.Equal(SendKind.Uncertain, rest.SendOrderOnce(Body).Kind);   // transport error
            Assert.All(h.Seen.Where(s => s.key == "POST /api/v1/orders"), s => Assert.Equal(Body, s.body));
        }

        [Fact]
        public void A_429_is_retried_inside_the_call()
        {
            var (rest, h, _) = Fixture.Rest();
            h.On("POST /api/v1/orders",
                () => { var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") }; r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return r; },
                SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"orderId\":\"O2\"}}"));
            Assert.Equal("O2", rest.SendOrderOnce(Body).OrderId);
        }

        [Fact]
        public void A_401_rereads_the_token_file_once()
        {
            var (rest, h, tokenPath) = Fixture.Rest();
            h.On("GET /api/v1/prices",
                () => { File.WriteAllText(tokenPath, "{\"access_token\":\"TOK2\",\"expires_at\":0}"); return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }; },
                SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":[]}"));
            rest.GetPrices(new[] { "005930" });
            Assert.Equal(new[] { "Bearer TOK1", "Bearer TOK2" }, h.Seen.Where(s => s.key == "GET /api/v1/prices").Select(s => s.auth));
        }

        [Fact]
        public void Price_limits_for_kr_and_none_for_us()
        {
            var (rest, h, _) = Fixture.Rest();
            h.On("GET /api/v1/price-limits",
                SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"lowerLimitPrice\":\"49000\",\"upperLimitPrice\":\"91000\"}}"),
                SequenceHandler.Json(HttpStatusCode.NotFound, "{}"));
            Assert.Equal((49000m, 91000m), rest.GetPriceLimits("005930"));
            Assert.Null(rest.GetPriceLimits("AAPL"));
        }
    }

    public class FillTrackerTests
    {
        private static TossOrderStatus St(string status, decimal filled, decimal avg, decimal fee = 0m) =>
            new TossOrderStatus { Found = true, OrderId = "O1", Status = status, FilledQty = filled, AvgPrice = avg, Commission = fee };

        [Fact]
        public void Cumulative_state_becomes_increments_once()
        {
            var t = new FillTracker();
            t.Track("O1");
            Assert.Null(t.Observe(St("PENDING", 0, 0)));
            var a = t.Observe(St("PARTIAL_FILLED", 4, 100m));
            Assert.Equal((4m, 100m, TrackedStatus.Partial), (a.FillQty, a.FillPrice, a.Status));
            Assert.Null(t.Observe(St("PARTIAL_FILLED", 4, 100m)));   // duplicate
            Assert.Null(t.Observe(St("PARTIAL_FILLED", 2, 100m)));   // stale
            var b = t.Observe(St("FILLED", 10, 101.2m, 5m));
            Assert.Equal((6m, 102m, TrackedStatus.Filled), (b.FillQty, b.FillPrice, b.Status));
            Assert.Contains("\"fees\":\"5\"", b.Message);
            Assert.False(t.IsTracked("O1"));
        }

        [Fact]
        public void Partial_then_cancel_reports_the_fill_then_the_cancel()
        {
            var t = new FillTracker();
            t.Track("O1");
            var a = t.Observe(St("CANCELED", 3, 99m));
            Assert.Equal((3m, TrackedStatus.Partial), (a.FillQty, a.Status));
            var b = t.Observe(St("CANCELED", 3, 99m));
            Assert.Equal((0m, TrackedStatus.Canceled), (b.FillQty, b.Status));
            Assert.Null(t.Observe(St("CANCELED", 3, 99m)));
        }

        [Fact]
        public void A_restored_order_continues_from_the_ledger()
        {
            var t = new FillTracker();
            t.Track("O1", 4, 100m);
            var a = t.Observe(St("FILLED", 6, 100m));
            Assert.Equal((2m, 100m), (a.FillQty, a.FillPrice));
        }
    }
}
