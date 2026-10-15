/*
 * stockapp's patches to the Toss adapter, part 2 (stockapp spec §7.2, §7.4): WebSocket frames and the
 * brokerage's ledger-backed reporting, refusal, acceptance and uncertain-send paths. The adapter's
 * sockets and threads are exercised end to end by stockapp's integration tests against its fake
 * Toss server (tests/integration/test_live_e2e.py).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using MyTrading.Toss;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using Xunit;

namespace MyTrading.Toss.Tests
{
    public class SocketFrameTests
    {
        [Fact]
        public void Declaration_and_frames()
        {
            Assert.Equal("[{\"id\":\"stockapp-lean\"},{\"type\":\"personal:order\",\"codes\":[\"7\"]},{\"type\":\"trade:kr\",\"codes\":[\"005930\"]}]",
                TossSocket.Declaration(7, new[] { "005930" }, new string[0]));
            var trade = TossSocket.Parse("{\"type\":\"message\",\"topic\":\"trade:us:AAPL\",\"data\":{\"price\":\"194.31\",\"timestamp\":1}}");
            Assert.Equal((FrameKind.Trade, "US", "AAPL", 194.31m), (trade.Kind, trade.Market, trade.Symbol, trade.Price));
            var order = TossSocket.Parse("{\"type\":\"message\",\"topic\":\"personal:order:7\",\"data\":{\"orderId\":\"O1\"}}");
            Assert.Equal((FrameKind.Order, "O1"), (order.Kind, order.OrderId));
            Assert.Equal(FrameKind.Order, TossSocket.Parse("{\"type\":\"message\",\"topic\":\"personal:order:7\",\"data\":{}}").Kind);
            Assert.Equal(FrameKind.Pong, TossSocket.Parse("PONG").Kind);
            Assert.Equal(FrameKind.Other, TossSocket.Parse("not json").Kind);
        }
    }

    public class NoAggregator : IDataAggregator
    {
        public IEnumerator<BaseData> Add(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler) => null;
        public bool Remove(SubscriptionDataConfig dataConfig) => true;
        public void Update(BaseData input) { }
        public void Initialize(DataAggregatorInitializeParameters parameters) { }
        public void Dispose() { }
    }

    public class BrokerageTests
    {
        private const string View = "{\"cash\":{\"KRW\":1000000,\"USD\":500},\"positions\":[" +
            "{\"symbol\":\"005930\",\"market\":\"KR\",\"qty\":3,\"avg_cost\":70000},{\"symbol\":\"AAPL\",\"market\":\"US\",\"qty\":2,\"avg_cost\":190}]," +
            "\"open_orders\":[{\"client_order_id\":\"000007-0000ya-001-01\",\"order_id\":\"O9\",\"symbol\":\"005930\",\"market\":\"KR\"," +
            "\"side\":\"BUY\",\"qty\":5,\"filled_qty\":1,\"avg_fill_price\":70000,\"limit_price\":72100}]}";

        private static (TossBrokerage b, SequenceHandler h, List<OrderEvent> events) Make(bool trading)
        {
            var (rest, h, tokenPath) = Fixture.Rest();
            var viewPath = Path.Combine(Path.GetDirectoryName(tokenPath), "ledger-view.json");
            File.WriteAllText(viewPath, View);
            var s = Fixture.Settings(trading);
            s.LedgerView = viewPath;
            var b = new TossBrokerage(null, s, rest, new NoAggregator());
            var events = new List<OrderEvent>();
            b.OrdersStatusChanged += (_, list) => events.AddRange(list);
            return (b, h, events);
        }

        [Fact]
        public void Reports_the_ledger_not_the_account()
        {
            var (b, _, _) = Make(true);
            Assert.Equal(new[] { ("KRW", 1000000m), ("USD", 500m) }, b.GetCashBalance().Select(c => (c.Currency, c.Amount)).OrderBy(x => x.Item1));
            var holdings = b.GetAccountHoldings();
            Assert.Equal(new[] { "005930:krx:3", "AAPL:usa:2" },
                holdings.Select(x => $"{x.Symbol.Value}:{x.Symbol.ID.Market}:{x.Quantity}"));
            var open = Assert.Single(b.GetOpenOrders());
            Assert.Equal(("000007-0000ya-001-01", "O9", 5m), (open.Tag, open.BrokerId.Single(), open.Quantity));
            Assert.False(b.ShouldPerformCashSync(DateTime.UtcNow));
        }

        [Fact]
        public void Alert_only_refuses_before_any_request()
        {
            var (b, h, events) = Make(false);
            var order = new LimitOrder(new TossSymbolMapper().LeanSymbol("005930", "KR"), 2, 72100m, DateTime.UtcNow, "000007-0000ya-001-01");
            Assert.False(b.PlaceOrder(order));
            Assert.Equal(OrderStatus.Invalid, Assert.Single(events).Status);
            Assert.StartsWith("guardrail: trading is disabled", events[0].Message);
            Assert.DoesNotContain(h.Seen, s => s.key.StartsWith("POST"));
        }

        [Fact]
        public void An_accepted_order_is_tracked_and_a_rejected_one_is_invalid()
        {
            var (b, h, events) = Make(true);
            h.On("GET /api/v1/price-limits", SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"lowerLimitPrice\":\"49000\",\"upperLimitPrice\":\"91000\"}}"));
            h.On("POST /api/v1/orders",
                SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"orderId\":\"O1\"}}"),
                SequenceHandler.Json(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"stock-restricted\",\"message\":\"no\"}}"));
            var sym = new TossSymbolMapper().LeanSymbol("005930", "KR");
            var ok = new LimitOrder(sym, 2, 72100m, DateTime.UtcNow, "000007-0000ya-001-01");
            Assert.True(b.PlaceOrder(ok));
            Assert.Equal("O1", ok.BrokerId.Single());
            Assert.Equal(OrderStatus.Submitted, events[0].Status);
            var bad = new LimitOrder(sym, 2, 72100m, DateTime.UtcNow, "000007-0000ya-002-01");
            Assert.False(b.PlaceOrder(bad));
            Assert.Equal(OrderStatus.Invalid, events[1].Status);
            Assert.Contains("stock-restricted", events[1].Message);
        }

        [Fact]
        public void An_uncertain_send_raises_no_event_and_returns_true()
        {
            var (b, h, events) = Make(true);
            h.On("GET /api/v1/price-limits", SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"lowerLimitPrice\":\"49000\",\"upperLimitPrice\":\"91000\"}}"));
            h.On("POST /api/v1/orders", () => null);
            var order = new LimitOrder(new TossSymbolMapper().LeanSymbol("005930", "KR"), 2, 72100m, DateTime.UtcNow, "000007-0000ya-001-01");
            Assert.True(b.PlaceOrder(order));
            Assert.Empty(events);
            Assert.Empty(order.BrokerId);
        }

        [Fact]
        public void A_rejection_after_an_uncertain_send_stays_uncertain_until_matching_decides()
        {
            // Toss may have created the order on the first POST; a 4xx on the resend doesn't prove it didn't
            // (stockapp spec §7.4: same-id behaviour for accepted orders is unverified, field matching decides).
            var (b, h, events) = Make(true);
            var messages = new List<BrokerageMessageEvent>();
            b.Message += (_, m) => messages.Add(m);
            h.On("GET /api/v1/price-limits", SequenceHandler.Json(HttpStatusCode.OK, "{\"result\":{\"lowerLimitPrice\":\"49000\",\"upperLimitPrice\":\"91000\"}}"));
            h.On("POST /api/v1/orders", () => null,
                SequenceHandler.Json(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"duplicate-order\",\"message\":\"exists\"}}"));
            var order = new LimitOrder(new TossSymbolMapper().LeanSymbol("005930", "KR"), 2, 72100m, DateTime.UtcNow, "000007-0000ya-001-01");
            Assert.True(b.PlaceOrder(order));
            b.ResendUncertain(DateTime.UtcNow.AddSeconds(10));
            Assert.Equal(2, h.Seen.Count(s => s.key.StartsWith("POST")));
            Assert.Empty(events);                                   // not reported Invalid
            b.ResendUncertain(DateTime.UtcNow.AddMinutes(9));
            Assert.Equal("toss-unknown", Assert.Single(messages).Code);
            Assert.Equal("000007-0000ya-001-01", messages[0].Message);
        }
    }
}
