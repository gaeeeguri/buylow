/*
 * stockapp: KIS REST for the adapter (stockapp KIS spec §5.3–§5.5): the token from the backend's file, the keys
 * from its secrets file, calls spaced by the account's limit (20/s real, 2/s demo).
 *
 * An order is sent once. Only gateway refusals, which KIS answers before processing anything, are retried:
 * EGW00201 (the per-second limit; up to 3 times after a second) and EGW00121/EGW00123 (a stale token; once,
 * after rereading the token file). A completed answer with rt_cd != "0" is a definite rejection. Anything else
 * (a transport error, a timeout, another non-200, an unreadable body) is uncertain: KIS has no idempotency key,
 * so a resend could place a second real order, and stockapp's backend resolves the order by field matching.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MyTrading.Stockapp;
using Newtonsoft.Json.Linq;

namespace MyTrading.Kis
{
    public enum SendKind { Accepted, Rejected, Uncertain }

    public class SendResult
    {
        public SendKind Kind;
        public string OrderId;
        public string Code;
        public string Message;
    }

    public class KisRestException : StockappException
    {
        public string Code;
        public KisRestException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>One inquire-daily-ccld row. Revise/cancel requests are rows of their own (orgn_odno set).</summary>
    public class KisOrderRow
    {
        public string Day, Branch, Odno, Symbol;
        public bool Buy, Revision;
        public string CnclYn = "";
        public decimal Qty, Price, Filled, Avg, Remaining, Cancelled, Rejected;

        public string OrderId => KisOrderId.Format(Day, Branch, Odno);

        private static decimal D(JObject r, string k) =>
            decimal.TryParse(r.Value<string>(k), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

        public static KisOrderRow Parse(JObject r) => new KisOrderRow
        {
            Day = r.Value<string>("ord_dt") ?? "", Branch = r.Value<string>("ord_gno_brno") ?? "",
            Odno = r.Value<string>("odno") ?? "", Symbol = r.Value<string>("pdno") ?? "",
            Buy = r.Value<string>("sll_buy_dvsn_cd") == "02",
            Revision = (r.Value<string>("orgn_odno") ?? "").Trim('0', ' ').Length > 0,
            Qty = D(r, "ord_qty"), Price = D(r, "ord_unpr"), Filled = D(r, "tot_ccld_qty"), Avg = D(r, "avg_prvs"),
            CnclYn = r.Value<string>("cncl_yn") ?? "",
            Remaining = D(r, "rmn_qty"), Cancelled = D(r, "cncl_cfrm_qty"), Rejected = D(r, "rjct_qty"),
        };

        /// <summary>The same mapping as stockapp's `kis_state` (stockapp/kis/account.py).</summary>
        public string State(string today)
        {
            if (Qty > 0 && Filled >= Qty) return "FILLED";
            if (Remaining == 0 && (Cancelled > 0 || CnclYn == "Y")) return "CANCELED";
            if (Remaining == 0 && Rejected > 0) return "REJECTED";
            if (string.CompareOrdinal(Day, today) < 0) return "EXPIRED";  // a KRX day order ends at the close
            return Filled > 0 ? "PARTIAL" : "ACCEPTED";
        }

        public OrderSnapshot Snapshot(string today)
        {
            var state = State(today);
            return new OrderSnapshot { Found = true, OrderId = OrderId, State = state, FilledQty = Filled, AvgPrice = Avg,
                                       Message = Describe(state, OrderId, Filled, Avg) };
        }

        /// <summary>"kis:{...}": what stockapp's ledger books (stockapp/live/session.py event_update).</summary>
        public static string Describe(string state, string orderId, decimal filled, decimal avg, string code = null, string message = null) =>
            "kis:" + new JObject
            {
                ["status"] = state, ["orderId"] = orderId,
                ["filled"] = filled.ToString(CultureInfo.InvariantCulture), ["avg"] = avg.ToString(CultureInfo.InvariantCulture),
                ["code"] = code, ["message"] = message,
            }.ToString(Newtonsoft.Json.Formatting.None);
    }

    public class KisRest
    {
        public const string RateLimit = "EGW00201";
        private const int MaxPages = 20;
        private readonly KisSettings _s;
        private readonly TokenFileProvider _tokens;
        private readonly KisSecrets _secrets;
        private readonly HttpClient _http;
        private readonly Action<TimeSpan> _sleep;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new object();
        private DateTime _last = DateTime.MinValue;

        public int AuthFailures { get; private set; }

        public KisRest(KisSettings settings, TokenFileProvider tokens, KisSecrets secrets, HttpClient http,
                       Action<TimeSpan> sleep = null, Func<DateTime> utcNow = null)
        {
            _s = settings;
            _tokens = tokens;
            _secrets = secrets;
            _http = http;
            _sleep = sleep ?? (t => System.Threading.Thread.Sleep(t));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static HttpClient CreateHttp(string proxy, TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler();
            if (!string.IsNullOrEmpty(proxy) && proxy != "direct") { handler.Proxy = new WebProxy(proxy); handler.UseProxy = true; }
            else handler.UseProxy = false;
            return new HttpClient(handler) { Timeout = timeout };
        }

        public static string Tr(string real, bool demo) => demo && "TJC".IndexOf(real[0]) >= 0 ? "V" + real.Substring(1) : real;

        public string Today() => _utcNow().AddHours(9).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        public static string OrderBody(KisSettings s, string pdno, decimal qty, decimal limit) => new JObject
        {
            ["CANO"] = s.Cano,
            ["ACNT_PRDT_CD"] = s.Product,
            ["PDNO"] = pdno,
            ["ORD_DVSN"] = KisConstants.OrdDvsnLimit,
            ["ORD_QTY"] = decimal.Truncate(qty).ToString(CultureInfo.InvariantCulture),
            ["ORD_UNPR"] = decimal.Round(limit, 0).ToString("0", CultureInfo.InvariantCulture),
            ["EXCG_ID_DVSN_CD"] = KisConstants.ExchangeKrx,
        }.ToString(Newtonsoft.Json.Formatting.None);

        private void Throttle()
        {
            var interval = TimeSpan.FromMilliseconds(_s.Demo ? 550 : 60);
            lock (_gate)
            {
                var since = _utcNow() - _last;
                var wait = interval - since;
                if (wait > TimeSpan.Zero && wait <= interval) _sleep(wait);  // a clock jump can never stall it
                _last = _utcNow();
            }
        }

        private (int status, JObject body, string trCont) Call(HttpMethod method, string path, string tr, string query,
                                                               string json, string trCont = "")
        {
            Throttle();
            var req = new HttpRequestMessage(method, _s.RestUrl + path + (string.IsNullOrEmpty(query) ? "" : "?" + query));
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + _tokens.Token());
            req.Headers.TryAddWithoutValidation("appkey", _secrets.AppKey);
            req.Headers.TryAddWithoutValidation("appsecret", _secrets.AppSecret);
            req.Headers.TryAddWithoutValidation("tr_id", tr);
            req.Headers.TryAddWithoutValidation("custtype", "P");
            req.Headers.TryAddWithoutValidation("tr_cont", trCont);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = _http.SendAsync(req).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JObject body = null;
            try { body = string.IsNullOrEmpty(text) ? null : JObject.Parse(text); } catch { }
            var cont = resp.Headers.TryGetValues("tr_cont", out var v) ? v.FirstOrDefault() ?? "" : "";
            return ((int)resp.StatusCode, body, cont);
        }

        private static string Query(IDictionary<string, string> q) =>
            string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));

        private JObject Get(string path, string tr, IDictionary<string, string> q, string trCont, out string more)
        {
            var rateRetries = 0;
            var reauthed = false;
            while (true)
            {
                var (status, body, cont) = Call(HttpMethod.Get, path, tr, Query(q), null, trCont);
                var code = body?.Value<string>("msg_cd") ?? "";
                if (code == RateLimit && rateRetries < 3) { rateRetries++; _sleep(TimeSpan.FromSeconds(1)); continue; }
                if (KisConstants.AuthCodes.Contains(code))
                {
                    AuthFailures++;
                    if (!reauthed) { reauthed = true; _tokens.Invalidate(); continue; }
                }
                if (status != 200 || body == null || body.Value<string>("rt_cd") != "0")
                    throw new KisRestException(code, $"HTTP {status} {code}: {body?.Value<string>("msg1")}");
                AuthFailures = 0;
                more = cont;
                return body;
            }
        }

        private Dictionary<string, string> Account() => new Dictionary<string, string> { ["CANO"] = _s.Cano, ["ACNT_PRDT_CD"] = _s.Product };

        public SendResult SendOrder(bool buy, string pdno, decimal qty, decimal limit)
        {
            var body = OrderBody(_s, pdno, qty, limit);
            var tr = Tr(buy ? KisConstants.TrOrderBuyReal : KisConstants.TrOrderSellReal, _s.Demo);
            var rateRetries = 0;
            var reauthed = false;
            while (true)
            {
                int status;
                JObject data;
                try { (status, data, _) = Call(HttpMethod.Post, KisConstants.PathOrderCash, tr, null, body); }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                {
                    return new SendResult { Kind = SendKind.Uncertain, Code = "transport", Message = e.GetType().Name };
                }
                catch (Exception e)
                {
                    return new SendResult { Kind = SendKind.Uncertain, Code = "error", Message = e.GetType().Name };
                }
                try
                {
                var code = data?.Value<string>("msg_cd") ?? "";
                if (code == RateLimit && rateRetries < 3) { rateRetries++; _sleep(TimeSpan.FromSeconds(1)); continue; }
                if (KisConstants.AuthCodes.Contains(code) && !reauthed)
                {
                    reauthed = true;
                    AuthFailures++;
                    _tokens.Invalidate();
                    continue;
                }
                if (status == 200 && data != null && data.Value<string>("rt_cd") == "0")
                {
                    var output = data["output"] as JObject;
                    var odno = output?.Value<string>("ODNO");
                    var branch = output?.Value<string>("KRX_FWDG_ORD_ORGNO");
                    AuthFailures = 0;
                    if (!string.IsNullOrEmpty(odno) && !string.IsNullOrEmpty(branch))
                        return new SendResult { Kind = SendKind.Accepted, OrderId = KisOrderId.Format(Today(), branch, odno) };
                    return new SendResult { Kind = SendKind.Uncertain, Code = "no-order-number", Message = "KIS accepted without ODNO" };
                }
                if (status == 200 && data?["rt_cd"] != null)
                    return new SendResult { Kind = SendKind.Rejected, Code = code, Message = data.Value<string>("msg1") };
                return new SendResult { Kind = SendKind.Uncertain, Code = "http-" + status, Message = code };
                }
                catch (Exception e)
                {
                    return new SendResult { Kind = SendKind.Uncertain, Code = "error", Message = e.GetType().Name };
                }
            }
        }

        private JObject Quote(string pdno) =>
            (Get(KisConstants.PathInquirePrice, KisConstants.TrInquirePrice,
                 new Dictionary<string, string> { ["FID_COND_MRKT_DIV_CODE"] = "J", ["FID_INPUT_ISCD"] = pdno }, "", out _)
             ["output"] as JObject) ?? new JObject();

        private static decimal Dec(JObject o, string k) =>
            decimal.TryParse(o.Value<string>(k), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

        public (decimal lower, decimal upper)? PriceLimits(string pdno)
        {
            var o = Quote(pdno);
            var (lower, upper) = (Dec(o, "stck_llam"), Dec(o, "stck_mxpr"));
            return lower > 0 && upper > 0 ? (lower, upper) : ((decimal, decimal)?)null;
        }

        public decimal LastPrice(string pdno) => Dec(Quote(pdno), "stck_prpr");

        public List<KisOrderRow> DailyOrders(string from, string to)
        {
            var q = Account();
            foreach (var kv in new Dictionary<string, string>
            {
                ["INQR_STRT_DT"] = from, ["INQR_END_DT"] = to, ["SLL_BUY_DVSN_CD"] = "00", ["PDNO"] = "", ["CCLD_DVSN"] = "00",
                ["INQR_DVSN"] = "00", ["INQR_DVSN_1"] = "", ["INQR_DVSN_3"] = "00", ["EXCG_ID_DVSN_CD"] = KisConstants.ExchangeAll,
                ["ORD_GNO_BRNO"] = "", ["ODNO"] = "", ["CTX_AREA_FK100"] = "", ["CTX_AREA_NK100"] = "",
            }) q[kv.Key] = kv.Value;
            var rows = new List<KisOrderRow>();
            var cont = "";
            for (var page = 0; page < MaxPages; page++)
            {
                var data = Get(KisConstants.PathDailyCcld, Tr(KisConstants.TrDailyCcldReal, _s.Demo), q, cont, out var more);
                foreach (var r in (data["output1"] as JArray) ?? new JArray()) rows.Add(KisOrderRow.Parse((JObject)r));
                if (more != "M" && more != "F") return rows;
                q["CTX_AREA_FK100"] = data.Value<string>("ctx_area_fk100") ?? "";
                q["CTX_AREA_NK100"] = data.Value<string>("ctx_area_nk100") ?? "";
                cont = "N";
            }
            throw new KisRestException("too-many-pages", $"inquire-daily-ccld returned more than {MaxPages} pages");
        }

        /// <summary>Cancels all that is left of the order. True if KIS accepted the cancel request; the order's
        /// terminal state still comes from the daily orders.</summary>
        public bool Cancel(string orderId)
        {
            if (!KisOrderId.TryParse(orderId, out _, out var branch, out var odno)) return false;
            var body = new JObject
            {
                ["CANO"] = _s.Cano, ["ACNT_PRDT_CD"] = _s.Product, ["KRX_FWDG_ORD_ORGNO"] = branch, ["ORGN_ODNO"] = odno,
                ["ORD_DVSN"] = KisConstants.OrdDvsnLimit, ["RVSE_CNCL_DVSN_CD"] = KisConstants.CancelCode, ["ORD_QTY"] = "0",
                ["ORD_UNPR"] = "0", ["QTY_ALL_ORD_YN"] = "Y", ["EXCG_ID_DVSN_CD"] = KisConstants.ExchangeKrx,
            }.ToString(Newtonsoft.Json.Formatting.None);
            try
            {
                var (status, data, _) = Call(HttpMethod.Post, KisConstants.PathOrderRvseCncl,
                                             Tr(KisConstants.TrReviseCancelReal, _s.Demo), null, body);
                return status == 200 && data?.Value<string>("rt_cd") == "0";
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
            {
                return false;
            }
        }
    }
}
