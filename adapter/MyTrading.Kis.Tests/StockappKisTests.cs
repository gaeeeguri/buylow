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

        [Fact]
        public void A_trade_frame_is_a_tick()
        {
            var fields = Enumerable.Repeat("0", 13).ToArray();
            fields[0] = "005930"; fields[1] = "091500"; fields[2] = "71500";
            var t = KisSocket.Parse("0|H0STCNT0|001|" + string.Join("^", fields), null, null);
            Assert.Equal((KisFrameKind.Trade, "005930", 71500m), (t.Kind, t.Symbol, t.Price));
        }
    }
}
