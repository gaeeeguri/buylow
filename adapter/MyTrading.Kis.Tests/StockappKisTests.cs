/*
 * stockapp: the KIS adapter in stockapp mode (stockapp KIS spec §5): order bodies, send classification
 * (never a second send), daily-order paging and states, and the shared fill logic over KIS rows.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MyTrading.Kis;
using MyTrading.Stockapp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MyTrading.Kis.Tests
{
    public class KisStub : HttpMessageHandler
    {
        // Each route serves its responses in order; the last one repeats. A null response throws (a dropped connection).
        public readonly Dictionary<string, List<Func<HttpResponseMessage>>> Routes = new Dictionary<string, List<Func<HttpResponseMessage>>>();
        public readonly List<(string key, string tr, string auth, string trCont, string body, string query)> Seen =
            new List<(string, string, string, string, string, string)>();

        public void On(string key, params Func<HttpResponseMessage>[] responses) => Routes[key] = responses.ToList();

        public static Func<HttpResponseMessage> Json(HttpStatusCode code, string json, string trCont = null) => () =>
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent(json) };
            if (trCont != null) r.Headers.Add("tr_cont", trCont);
            return r;
        };

        private static string H(HttpRequestMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.First() : null;

        protected override HttpResponseMessage Send(HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            var key = request.Method.Method + " " + request.RequestUri.AbsolutePath;
            Seen.Add((key, H(request, "tr_id"), H(request, "authorization"), H(request, "tr_cont"),
                      request.Content?.ReadAsStringAsync().Result, request.RequestUri.Query));
            if (!Routes.TryGetValue(key, out var list)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            var next = list[0];
            if (list.Count > 1) list.RemoveAt(0);
            return next() ?? throw new HttpRequestException("connection dropped");
        }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(Send(r, ct));
    }

    public static class KisFixture
    {
        public const string Ccld = "/uapi/domestic-stock/v1/trading/inquire-daily-ccld";
        public const string Cash = "/uapi/domestic-stock/v1/trading/order-cash";
        public const string Price = "/uapi/domestic-stock/v1/quotations/inquire-price";
        public static readonly DateTime Now = new DateTime(2026, 10, 15, 0, 15, 0, DateTimeKind.Utc);  // 09:15 KST

        public static string Ok(string output) => "{\"rt_cd\":\"0\",\"msg_cd\":\"APBK0013\",\"msg1\":\"ok\",\"output\":" + output + "}";
        public static string Refuse(string code) => "{\"rt_cd\":\"1\",\"msg_cd\":\"" + code + "\",\"msg1\":\"no\"}";
        public const string Accepted = "{\"KRX_FWDG_ORD_ORGNO\":\"91252\",\"ODNO\":\"0000117057\",\"ORD_TMD\":\"091512\"}";

        public static string Row(string odno, string filled, string avg, string rmn, string day = "20261015",
                                 string orgn = "", string cancelled = "0", string cnclYn = "N") =>
            "{\"ord_dt\":\"" + day + "\",\"ord_gno_brno\":\"91252\",\"odno\":\"" + odno + "\",\"orgn_odno\":\"" + orgn +
            "\",\"sll_buy_dvsn_cd\":\"02\",\"pdno\":\"005930\",\"ord_qty\":\"10\",\"ord_unpr\":\"70000\",\"ord_tmd\":\"091512\"," +
            "\"tot_ccld_qty\":\"" + filled + "\",\"avg_prvs\":\"" + avg + "\",\"rmn_qty\":\"" + rmn + "\",\"cncl_cfrm_qty\":\"" +
            cancelled + "\",\"rjct_qty\":\"0\",\"cncl_yn\":\"" + cnclYn + "\"}";

        public static string Rows(params string[] rows) =>
            "{\"rt_cd\":\"0\",\"msg_cd\":\"0\",\"msg1\":\"ok\",\"output1\":[" + string.Join(",", rows) +
            "],\"ctx_area_fk100\":\"F1\",\"ctx_area_nk100\":\"N1\"}";

        public static (string dir, string tokenPath) Files()
        {
            var dir = Directory.CreateTempSubdirectory("kis-test-").FullName;
            var tokenPath = Path.Combine(dir, "token.json");
            File.WriteAllText(tokenPath, "{\"access_token\":\"TOK1\",\"expires_at\":0}");
            File.WriteAllText(Path.Combine(dir, "kis-secrets.json"), "{\"app_key\":\"PSkey\",\"app_secret\":\"S3\",\"approval_key\":\"A1\"}");
            return (dir, tokenPath);
        }

        public static KisSettings Settings(string dir, bool trading = true) => new KisSettings
        {
            Env = "demo", BaseUrl = "http://fake", WsUrl = "ws://fake/tryitout", TokenFile = Path.Combine(dir, "token.json"),
            SecretsFile = Path.Combine(dir, "kis-secrets.json"), LedgerView = Path.Combine(dir, "ledger-view.json"),
            AccountNo = "12345678-01", HtsId = "myhts", TradingEnabled = trading, MaxOrderKrw = 1_000_000m,
        };

        public static (KisRest rest, KisStub h, string dir, List<TimeSpan> sleeps) Rest(bool trading = true, Func<DateTime> now = null)
        {
            var (dir, tokenPath) = Files();
            var h = new KisStub();
            var sleeps = new List<TimeSpan>();
            var s = Settings(dir, trading);
            var rest = new KisRest(s, new TokenFileProvider(tokenPath), new KisSecrets(s.SecretsFile), new HttpClient(h),
                                   sleeps.Add, now ?? (() => Now));
            return (rest, h, dir, sleeps);
        }
    }

    public class KisRestTests
    {
        [Fact]
        public void Order_body_is_a_krx_limit_with_string_values()
        {
            var (_, _, dir, _) = KisFixture.Rest();
            var body = JObject.Parse(KisRest.OrderBody(KisFixture.Settings(dir), "005930", 2, 72100m));
            Assert.Equal(new Dictionary<string, string> {
                ["CANO"] = "12345678", ["ACNT_PRDT_CD"] = "01", ["PDNO"] = "005930", ["ORD_DVSN"] = "00",
                ["ORD_QTY"] = "2", ["ORD_UNPR"] = "72100", ["EXCG_ID_DVSN_CD"] = "KRX" },
                body.Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.String ? p.Value.Value<string>() : "NOT A STRING"));
            Assert.Equal("VTTC0012U", KisRest.Tr("TTTC0012U", true));
            Assert.Equal("TTTC0011U", KisRest.Tr("TTTC0011U", false));
        }

        [Fact]
        public void An_accepted_send_carries_the_day_scoped_order_id_and_the_keys()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, KisFixture.Ok(KisFixture.Accepted)));
            var r = rest.SendOrder(true, "005930", 2, 72100m);
            Assert.Equal((SendKind.Accepted, "20261015:91252:0000117057"), (r.Kind, r.OrderId));
            var seen = Assert.Single(h.Seen);
            Assert.Equal(("VTTC0012U", "Bearer TOK1"), (seen.tr, seen.auth));
        }

        [Fact]
        public void A_refusal_in_a_completed_answer_is_definite()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, KisFixture.Refuse("APBK0952")));
            var r = rest.SendOrder(true, "005930", 2, 72100m);
            Assert.Equal((SendKind.Rejected, "APBK0952"), (r.Kind, r.Code));
        }

        [Fact]
        public void Gateway_refusals_are_retried_because_nothing_was_processed()
        {
            var (rest, h, dir, sleeps) = KisFixture.Rest();
            h.On("POST " + KisFixture.Cash,
                KisStub.Json(HttpStatusCode.InternalServerError, KisFixture.Refuse("EGW00201")),
                KisStub.Json(HttpStatusCode.InternalServerError, KisFixture.Refuse("EGW00123")),
                KisStub.Json(HttpStatusCode.OK, KisFixture.Ok(KisFixture.Accepted)));
            File.WriteAllText(Path.Combine(dir, "token.json"), "{\"access_token\":\"TOK2\",\"expires_at\":0}");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "token.json"), DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(SendKind.Accepted, rest.SendOrder(true, "005930", 2, 72100m).Kind);
            Assert.Equal(3, h.Seen.Count);
            Assert.Contains(TimeSpan.FromSeconds(1), sleeps);
            Assert.Equal("Bearer TOK2", h.Seen[2].auth);
        }

        [Theory]
        [InlineData(502)]
        [InlineData(500)]
        [InlineData(0)]  // 0: the connection drops
        public void Anything_else_is_uncertain_and_sent_once(int status)
        {
            var (rest, h, _, _) = KisFixture.Rest();
            if (status == 0) h.On("POST " + KisFixture.Cash, () => null);
            else h.On("POST " + KisFixture.Cash, KisStub.Json((HttpStatusCode)status, "{\"error\":\"gateway\"}"));
            Assert.Equal(SendKind.Uncertain, rest.SendOrder(true, "005930", 2, 72100m).Kind);
            Assert.Single(h.Seen);
        }

        [Fact]
        public void An_odd_body_shape_is_uncertain_and_sent_once()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, "{\"rt_cd\":\"0\",\"output\":\"not-an-object\"}"));
            Assert.Equal(SendKind.Uncertain, rest.SendOrder(true, "005930", 2, 72100m).Kind);
            Assert.Single(h.Seen);
        }

        [Fact]
        public void Daily_orders_follow_tr_cont_and_parse_rows()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("GET " + KisFixture.Ccld,
                KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117057", "4", "70000", "6")), "M"),
                KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117058", "0", "0", "0", orgn: "0000117057")), "D"));
            var rows = rest.DailyOrders("20261015", "20261015");
            Assert.Equal(new[] { "20261015:91252:0000117057", "20261015:91252:0000117058" }, rows.Select(r => r.OrderId));
            Assert.Equal(("PARTIAL", false), (rows[0].State("20261015"), rows[0].Revision));
            Assert.True(rows[1].Revision);
            Assert.Equal(("", "N"), (h.Seen[0].trCont, h.Seen[1].trCont));
            Assert.Contains("CTX_AREA_NK100=N1", h.Seen[1].query);
            Assert.Contains("EXCG_ID_DVSN_CD=ALL", h.Seen[0].query);
            Assert.Equal("VTTC0081R", h.Seen[0].tr);
        }

        [Theory]
        [InlineData("10", "70000", "0", "0", "20261015", "FILLED")]
        [InlineData("4", "70000", "0", "6", "20261015", "CANCELED")]
        [InlineData("0", "0", "0", "0", "20261015", "CANCELED", "Y")]
        [InlineData("0", "0", "10", "0", "20261014", "EXPIRED")]
        [InlineData("0", "0", "10", "0", "20261015", "ACCEPTED")]
        public void Row_states(string filled, string avg, string rmn, string cancelled, string day, string state, string cnclYn = "N")
        {
            var row = KisOrderRow.Parse(JObject.Parse(KisFixture.Row("1", filled, avg, rmn, day, cancelled: cancelled, cnclYn: cnclYn)));
            Assert.Equal(state, row.State("20261015"));
        }

        [Fact]
        public void Order_ids_round_trip()
        {
            Assert.True(KisOrderId.TryParse(KisOrderId.Format("20261015", "91252", "0000117057"), out var d, out var b, out var o));
            Assert.Equal(("20261015", "91252", "0000117057"), (d, b, o));
            Assert.False(KisOrderId.TryParse("0000117057", out _, out _, out _));
        }

        [Theory]
        [InlineData("91252", "117057")]
        [InlineData("91252", "0000117057")]
        [InlineData("0091252", "117057")]
        [InlineData("00091252", "00000000117057")]
        public void Order_ids_are_canonical_whatever_padding_KIS_answers_with(string branch, string odno)
        {
            Assert.Equal("20261015:91252:0000117057", KisOrderId.Format("20261015", branch, odno));
            Assert.Equal("20261015:91252:0000117057", KisOrderId.Canonical("20261015:" + branch + ":" + odno));
            Assert.True(KisOrderId.Same("20261015:" + branch + ":" + odno, "20261015:91252:0000117057"));
            Assert.False(KisOrderId.Same("20261014:" + branch + ":" + odno, "20261015:91252:0000117057"));
        }

        [Fact]
        public void An_order_cash_id_and_its_ccld_row_are_byte_identical()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK,
                KisFixture.Ok("{\"KRX_FWDG_ORD_ORGNO\":\"91252\",\"ODNO\":\"117057\",\"ORD_TMD\":\"091512\"}")));
            var sent = rest.SendOrder(true, "005930", 2, 72100m);
            var row = KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "0", "0", "10")));
            var padded = JObject.Parse(KisFixture.Row("0000117057", "0", "0", "10"));
            padded["ord_gno_brno"] = "0091252";
            Assert.Equal((SendKind.Accepted, "20261015:91252:0000117057"), (sent.Kind, sent.OrderId));
            Assert.Equal(sent.OrderId, row.OrderId);
            Assert.Equal(sent.OrderId, KisOrderRow.Parse(padded).OrderId);
        }

        [Fact]
        public void A_cancel_confirmed_only_on_the_revision_row_cancels_the_original()
        {
            var rows = new List<KisOrderRow>
            {
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "0", "0", "10", cancelled: "0"))),
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117058", "0", "0", "0", orgn: "0000117057", cancelled: "10", cnclYn: "Y"))),
            };
            var folded = KisOrderRow.Fold(rows);
            Assert.Equal(("CANCELED", 0m), (folded[0].State("20261015"), folded[0].Remaining));
            // a partly filled original: the cancel takes the rest; remaining never goes below zero
            var partial = KisOrderRow.Fold(new List<KisOrderRow>
            {
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "4", "70000", "6"))),
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117058", "0", "0", "0", orgn: "117057", cancelled: "10"))),
            });
            Assert.Equal(("CANCELED", 0m, 4m), (partial[0].State("20261015"), partial[0].Remaining, partial[0].Filled));
            // the shape seen so far (confirmation on the original, echoed on the revision) is still CANCELED
            var both = KisOrderRow.Fold(new List<KisOrderRow>
            {
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "0", "0", "0", cancelled: "10"))),
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117058", "0", "0", "0", orgn: "0000117057", cancelled: "10", cnclYn: "Y"))),
            });
            Assert.Equal(("CANCELED", 0m), (both[0].State("20261015"), both[0].Remaining));
            // a revision of another day's order is not folded into today's
            var otherDay = KisOrderRow.Fold(new List<KisOrderRow>
            {
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "0", "0", "10"))),
                KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117058", "0", "0", "0", day: "20261014", orgn: "0000117057", cancelled: "10"))),
            });
            Assert.Equal("ACCEPTED", otherDay[0].State("20261015"));
        }

        [Fact]
        public void Daily_orders_fold_a_cancel_confirmed_on_the_revision_row()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("GET " + KisFixture.Ccld, KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(
                KisFixture.Row("0000117057", "0", "0", "10"),
                KisFixture.Row("0000117058", "0", "0", "0", orgn: "0000117057", cancelled: "10", cnclYn: "Y"))));
            var rows = rest.DailyOrders("20261015", "20261015");
            Assert.Equal("CANCELED", rows.Single(r => !r.Revision).State("20261015"));
        }

        [Fact]
        public void Kis_rows_feed_the_shared_fill_logic()
        {
            var fills = new CumulativeFills();
            fills.Track("20261015:91252:0000117057");
            var a = fills.Observe(KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "4", "70000", "6"))).Snapshot("20261015"));
            Assert.Equal((4m, 70000m, TrackedStatus.Partial), (a.FillQty, a.FillPrice, a.Status));
            Assert.StartsWith("kis:", a.Message);
            Assert.Equal("PARTIAL", JObject.Parse(a.Message.Substring(4)).Value<string>("status"));
            var b = fills.Observe(KisOrderRow.Parse(JObject.Parse(KisFixture.Row("0000117057", "10", "70060", "0"))).Snapshot("20261015"));
            Assert.Equal((6m, 70100m, TrackedStatus.Filled), (b.FillQty, b.FillPrice, b.Status));
        }

        [Fact]
        public void Price_limits_come_from_inquire_price()
        {
            var (rest, h, _, _) = KisFixture.Rest();
            h.On("GET " + KisFixture.Price, KisStub.Json(HttpStatusCode.OK,
                KisFixture.Ok("{\"stck_prpr\":\"70000\",\"stck_mxpr\":\"91000\",\"stck_llam\":\"49000\"}")));
            Assert.Equal((49000m, 91000m), rest.PriceLimits("005930"));
            Assert.Equal(70000m, rest.LastPrice("005930"));
        }
    }

    public class KisSocketTests
    {
        private static string Encrypt(string plain, string key, string iv)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            using var enc = aes.CreateEncryptor();
            var bytes = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(enc.TransformFinalBlock(bytes, 0, bytes.Length));
        }

        [Fact]
        public void Subscribe_frame()
        {
            Assert.Equal("{\"header\":{\"approval_key\":\"A1\",\"custtype\":\"P\",\"tr_type\":\"1\",\"content-type\":\"utf-8\"}," +
                         "\"body\":{\"input\":{\"tr_id\":\"H0STCNI9\",\"tr_key\":\"myhts\"}}}",
                KisSocket.Subscribe("A1", "H0STCNI9", "myhts", true));
        }

        [Fact]
        public void Control_frames()
        {
            Assert.Equal(KisFrameKind.Ping, KisSocket.Parse("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261015091500\"}}", null, null).Kind);
            var ack = KisSocket.Parse("{\"header\":{\"tr_id\":\"H0STCNI9\"},\"body\":{\"rt_cd\":\"0\",\"msg1\":\"SUBSCRIBE SUCCESS\"," +
                                      "\"output\":{\"iv\":\"0123456789abcdef\",\"key\":\"0123456789abcdef0123456789abcdef\"}}}", null, null);
            Assert.Equal((KisFrameKind.Ack, true, "0123456789abcdef0123456789abcdef", "0123456789abcdef"), (ack.Kind, ack.AckOk, ack.Key, ack.Iv));
            var refused = KisSocket.Parse("{\"header\":{\"tr_id\":\"H0STCNI9\"},\"body\":{\"rt_cd\":\"1\",\"msg1\":\"invalid approval : NOT FOUND\"}}", null, null);
            Assert.Equal((KisFrameKind.Ack, false), (refused.Kind, refused.AckOk));
            Assert.Equal(KisFrameKind.Other, KisSocket.Parse("garbage", null, null).Kind);
        }

        [Fact]
        public void An_encrypted_fill_notice_names_its_order_and_wakes_without_a_key()
        {
            const string key = "0123456789abcdef0123456789abcdef", iv = "0123456789abcdef";
            var fields = Enumerable.Repeat("", 14).ToArray();
            fields[2] = "0000117057"; fields[4] = "02"; fields[8] = "005930"; fields[9] = "4"; fields[10] = "70000"; fields[13] = "2";
            var frame = "1|H0STCNI9|001|" + Encrypt(string.Join("^", fields), key, iv);
            var f = KisSocket.Parse(frame, key, iv);
            Assert.Equal((KisFrameKind.Fill, "0000117057", "005930"), (f.Kind, f.OrderNo, f.Symbol));
            var blind = KisSocket.Parse(frame, null, null);
            Assert.Equal((KisFrameKind.Fill, (string)null), (blind.Kind, blind.OrderNo));  // still a wake-up
        }

        [Theory]
        [InlineData("{\"header\":null}")]
        [InlineData("{\"header\":5}")]
        [InlineData("{\"header\":{\"tr_id\":{}}}")]
        [InlineData("{\"header\":{\"tr_id\":\"H0STCNI9\"},\"body\":[]}")]
        [InlineData("[1,2]")]
        [InlineData("{\"header\":{\"tr_id\":\"H0STCNI9\"},\"body\":{\"output\":5,\"rt_cd\":{}}}")]
        public void Odd_json_never_throws(string text)
        {
            var f = KisSocket.Parse(text, null, null);
            Assert.True(f.Kind == KisFrameKind.Other || f.Kind == KisFrameKind.Ack);
        }

        [Fact]
        public void A_trade_frame_is_a_tick()
        {
            var fields = Enumerable.Repeat("0", 13).ToArray();
            fields[0] = "005930"; fields[1] = "091500"; fields[2] = "71500";
            var t = KisSocket.Parse("0|H0STCNT0|001|" + string.Join("^", fields), null, null);
            Assert.Equal((KisFrameKind.Trade, "005930", 71500m), (t.Kind, t.Symbol, t.Price));
        }
    }

    public class NoAggregator : QuantConnect.Data.IDataAggregator
    {
        public IEnumerator<QuantConnect.Data.BaseData> Add(QuantConnect.Data.SubscriptionDataConfig c, EventHandler h) => null;
        public bool Remove(QuantConnect.Data.SubscriptionDataConfig c) => true;
        public void Update(QuantConnect.Data.BaseData input) { }
        public void Initialize(QuantConnect.Data.DataAggregatorInitializeParameters p) { }
        public void Dispose() { }
    }

    public class KisBrokerageTests
    {
        private const string View = "{\"cash\":{\"KRW\":1000000,\"USD\":0},\"positions\":[" +
            "{\"symbol\":\"005930\",\"market\":\"KR\",\"qty\":3,\"avg_cost\":70000}]," +
            "\"open_orders\":[{\"client_order_id\":\"000007-0000ya-001-01\",\"order_id\":\"20261015:91252:0000117050\"," +
            "\"symbol\":\"005930\",\"market\":\"KR\",\"side\":\"BUY\",\"qty\":5,\"filled_qty\":1,\"avg_fill_price\":70000,\"limit_price\":72100}]}";
        private const string Tag = "000007-0000ya-002-01";
        private static readonly QuantConnect.Symbol Samsung = new KisSymbolMapper().GetLeanSymbol("005930", QuantConnect.SecurityType.Equity, "krx");

        private static (KisBrokerage b, KisStub h, List<QuantConnect.Orders.OrderEvent> events,
                        List<QuantConnect.Brokerages.BrokerageMessageEvent> messages, Dictionary<string, List<QuantConnect.Orders.Order>> byId)
            Make(bool trading = true, Func<DateTime> now = null, KisSocket socket = null, string view = null)
        {
            var (rest, h, dir, _) = KisFixture.Rest(trading, now);
            File.WriteAllText(Path.Combine(dir, "ledger-view.json"), view ?? View);
            var byId = new Dictionary<string, List<QuantConnect.Orders.Order>>();
            var b = new KisBrokerage(null, KisFixture.Settings(dir, trading), rest, new NoAggregator(),
                                     id => byId.TryGetValue(id, out var l) ? l : new List<QuantConnect.Orders.Order>(), now ?? (() => KisFixture.Now), socket);
            var events = new List<QuantConnect.Orders.OrderEvent>();
            var messages = new List<QuantConnect.Brokerages.BrokerageMessageEvent>();
            b.OrdersStatusChanged += (_, list) => events.AddRange(list);
            b.Message += (_, m) => messages.Add(m);
            return (b, h, events, messages, byId);
        }

        private static void Quote(KisStub h) => h.On("GET " + KisFixture.Price, KisStub.Json(HttpStatusCode.OK,
            KisFixture.Ok("{\"stck_prpr\":\"70000\",\"stck_mxpr\":\"91000\",\"stck_llam\":\"49000\"}")));

        [Fact]
        public void Reports_the_ledger_not_the_account()
        {
            var (b, _, _, _, _) = Make();
            Assert.Equal(1000000m, b.GetCashBalance().Single(c => c.Currency == "KRW").Amount);
            Assert.Equal("005930:krx:3", b.GetAccountHoldings().Select(x => $"{x.Symbol.Value}:{x.Symbol.ID.Market}:{x.Quantity}").Single());
            var open = Assert.Single(b.GetOpenOrders());
            Assert.Equal(("000007-0000ya-001-01", "20261015:91252:0000117050"), (open.Tag, open.BrokerId.Single()));
            Assert.False(b.ShouldPerformCashSync(DateTime.UtcNow));
        }

        [Fact]
        public void Refuses_non_limit_and_non_krx_before_any_request()
        {
            var (b, h, events, _, _) = Make();
            Assert.False(b.PlaceOrder(new QuantConnect.Orders.MarketOrder(Samsung, 2, DateTime.UtcNow, Tag)));
            var aapl = new QuantConnect.Symbol(QuantConnect.SecurityIdentifier.GenerateEquity("AAPL", QuantConnect.Market.USA, mapSymbol: false), "AAPL");
            Assert.False(b.PlaceOrder(new QuantConnect.Orders.LimitOrder(aapl, 1, 190m, DateTime.UtcNow, Tag)));
            Assert.Equal(new[] { "guardrail: only limit orders are sent", "guardrail: KIS trades KR (krx) equities only" },
                         events.Select(e => e.Message));
            Assert.All(events, e => Assert.Equal(QuantConnect.Orders.OrderStatus.Invalid, e.Status));
            Assert.Empty(h.Seen);
        }

        [Theory]
        [InlineData(2, 72100.5)]
        [InlineData(2.5, 72100)]
        public void A_fractional_price_or_quantity_is_refused_before_any_request(double qty, double limit)
        {
            var (b, h, events, _, _) = Make();
            Quote(h);
            Assert.False(b.PlaceOrder(new QuantConnect.Orders.LimitOrder(Samsung, (decimal)qty, (decimal)limit, DateTime.UtcNow, Tag)));
            var e = Assert.Single(events);
            Assert.Equal((QuantConnect.Orders.OrderStatus.Invalid, "guardrail: price and quantity must be whole"), (e.Status, e.Message));
            Assert.Empty(h.Seen);
        }

        [Fact]
        public void A_fill_on_an_id_the_ledger_kept_unpadded_is_still_matched()
        {
            // an open order the ledger kept as "…:117050" is the ccld row "0000117050"
            var (b, h, events, _, byId) = Make(view: View.Replace("0000117050", "117050"));
            var open = Assert.Single(b.GetOpenOrders());
            Assert.Equal("20261015:91252:117050", open.BrokerId.Single());
            byId["20261015:91252:117050"] = new List<QuantConnect.Orders.Order> { open };
            h.On("GET " + KisFixture.Ccld, KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117050", "4", "70000", "6"))));
            b.Poll(KisFixture.Now.AddSeconds(31));
            var fill = Assert.Single(events);
            Assert.Equal((QuantConnect.Orders.OrderStatus.PartiallyFilled, 3m), (fill.Status, fill.FillQuantity));
        }

        [Fact]
        public void Alert_only_refuses_before_any_request()
        {
            var (b, h, events, _, _) = Make(trading: false);
            Assert.False(b.PlaceOrder(new QuantConnect.Orders.LimitOrder(Samsung, 2, 72100m, DateTime.UtcNow, Tag)));
            Assert.StartsWith("guardrail: trading is disabled", Assert.Single(events).Message);
            Assert.Empty(h.Seen);
        }

        [Fact]
        public void An_accepted_order_is_tracked_and_a_rejected_one_is_invalid()
        {
            var (b, h, events, _, _) = Make();
            Quote(h);
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, KisFixture.Ok(KisFixture.Accepted)),
                 KisStub.Json(HttpStatusCode.OK, KisFixture.Refuse("APBK0952")));
            var ok = new QuantConnect.Orders.LimitOrder(Samsung, 2, 72100m, DateTime.UtcNow, Tag);
            Assert.True(b.PlaceOrder(ok));
            Assert.Equal("20261015:91252:0000117057", ok.BrokerId.Single());
            Assert.Equal(QuantConnect.Orders.OrderStatus.Submitted, events[0].Status);
            Assert.Equal("ACCEPTED", JObject.Parse(events[0].Message.Substring(4)).Value<string>("status"));
            var bad = new QuantConnect.Orders.LimitOrder(Samsung, 2, 72100m, DateTime.UtcNow, "000007-0000ya-003-01");
            Assert.False(b.PlaceOrder(bad));
            Assert.Equal(QuantConnect.Orders.OrderStatus.Invalid, events[1].Status);
            Assert.Contains("APBK0952", events[1].Message);
        }

        [Fact]
        public void An_uncertain_send_is_never_sent_again()
        {
            var (b, h, events, messages, _) = Make();
            Quote(h);
            h.On("POST " + KisFixture.Cash, () => null);  // the connection drops after the request left
            h.On("GET " + KisFixture.Ccld, KisStub.Json(HttpStatusCode.OK, KisFixture.Rows()));
            var order = new QuantConnect.Orders.LimitOrder(Samsung, 2, 72100m, DateTime.UtcNow, Tag);
            Assert.True(b.PlaceOrder(order));
            Assert.Empty(order.BrokerId);
            Assert.Empty(events);
            Assert.Equal(("kis-unknown", Tag), (messages.Single().Code, messages.Single().Message));
            b.Poll(KisFixture.Now.AddMinutes(10));
            b.Poll(KisFixture.Now.AddMinutes(20));
            Assert.Equal(1, h.Seen.Count(s => s.key == "POST " + KisFixture.Cash));
        }

        [Fact]
        public void Fills_come_from_the_daily_orders()
        {
            var (b, h, events, _, byId) = Make();
            Quote(h);
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, KisFixture.Ok(KisFixture.Accepted)));
            h.On("GET " + KisFixture.Ccld,
                KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117057", "4", "70000", "6"))),
                KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117057", "10", "70060", "0"),
                                                                KisFixture.Row("0000117099", "0", "0", "0", orgn: "0000117057"))));
            var order = new QuantConnect.Orders.LimitOrder(Samsung, 10, 72100m, DateTime.UtcNow, Tag);
            byId["20261015:91252:0000117057"] = new List<QuantConnect.Orders.Order> { order };
            b.PlaceOrder(order);
            b.Poll(KisFixture.Now.AddSeconds(31));
            b.Poll(KisFixture.Now.AddSeconds(62));
            var fills = events.Skip(1).ToList();
            Assert.Equal(new[] { (QuantConnect.Orders.OrderStatus.PartiallyFilled, 4m, 70000m), (QuantConnect.Orders.OrderStatus.Filled, 6m, 70100m) },
                         fills.Select(e => (e.Status, e.FillQuantity, e.FillPrice)));
            Assert.All(fills, e => Assert.StartsWith("kis:", e.Message));
        }

        [Fact]
        public void A_fill_seen_before_LEAN_knows_the_order_is_reported_later()
        {
            var (b, h, events, _, byId) = Make();
            Quote(h);
            h.On("POST " + KisFixture.Cash, KisStub.Json(HttpStatusCode.OK, KisFixture.Ok(KisFixture.Accepted)));
            h.On("GET " + KisFixture.Ccld, KisStub.Json(HttpStatusCode.OK, KisFixture.Rows(KisFixture.Row("0000117057", "4", "70000", "6"))));
            var order = new QuantConnect.Orders.LimitOrder(Samsung, 10, 72100m, DateTime.UtcNow, Tag);
            b.PlaceOrder(order);
            b.Poll(KisFixture.Now.AddSeconds(31));  // LEAN has no order for the id yet: the increment must not be consumed
            Assert.Single(events);
            byId["20261015:91252:0000117057"] = new List<QuantConnect.Orders.Order> { order };
            b.Poll(KisFixture.Now.AddSeconds(62));
            var fill = Assert.Single(events.Skip(1));
            Assert.Equal((QuantConnect.Orders.OrderStatus.PartiallyFilled, 4m, 70000m), (fill.Status, fill.FillQuantity, fill.FillPrice));
        }

        [Fact]
        public void Feed_down_after_three_minutes_without_socket_or_rest()
        {
            var (_, _, dir, _) = KisFixture.Rest();
            var socket = new KisSocket(KisFixture.Settings(dir), () => "A1", () => { }, _ => { });  // never started: down
            var (b, h, _, messages, _) = Make(now: () => DateTime.UtcNow, socket: socket);
            h.On("GET " + KisFixture.Ccld, KisStub.Json(HttpStatusCode.InternalServerError, "{}"));
            b.Poll(DateTime.UtcNow.AddMinutes(4));
            Assert.Contains(messages, m => m.Code == "kis-feed-down");
        }
    }
}
