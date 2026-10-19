/*
 * stockapp: the KIS WebSocket (stockapp KIS spec §5.5). It subscribes the account's fill notice (H0STCNI0 real /
 * H0STCNI9 demo, keyed by the HTS ID) and a trade feed (H0STCNT0) per universe symbol, within KIS's 40
 * subscriptions per session. A fill notice is only a wake-up: the adapter re-reads its orders over REST,
 * which is the truth. Notices are AES-256-CBC with the key and iv from the subscribe answer; one that can't be
 * decrypted still wakes the adapter. PINGPONG frames are echoed. ClientWebSocket goes through the SOCKS5 tunnel
 * like REST. KIS's socket is plain ws://: the approval key and HTS ID cross the network in clear between the
 * vpn host and KIS (a property of KIS's service, recorded in the spec). Reconnects back off 1, 2, 4, 8, 16, 30 s;
 * a changed symbol list reconnects with the new subscriptions.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Globalization;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace MyTrading.Kis
{
    /// <summary>One fill notice (H0STCNI0/9), decrypted.</summary>
    public class KisFill
    {
        public string OrderNo;     // ODER_NO
        public string Ticker;      // STCK_SHRN_ISCD
        public bool Buy;           // SELN_BYOV_CLS: 02 buy / 01 sell
        public int FillQty;        // CNTG_QTY
        public decimal FillPrice;  // CNTG_UNPR
        public string FillTime;    // STCK_CNTG_HOUR
        public bool IsFilled;      // CNTG_YN == "2"
    }

    public enum KisFrameKind { Other, Ping, Ack, Fill, Trade }

    public class KisFrame
    {
        public KisFrameKind Kind;
        public string TrId;
        public string Symbol;
        public decimal Price;
        public string OrderNo;
        public string Key;
        public string Iv;
        public bool AckOk = true;
        public string AckMessage;
    }

    public class KisSocket
    {
        public const int MaxSubscriptions = 40;
        private static readonly int[] Backoff = { 1, 2, 4, 8, 16, 30 };
        private readonly KisSettings _settings;
        private readonly Func<string> _approval;
        private readonly Action _invalidate;
        private readonly Action<KisFrame> _onFrame;
        private readonly object _lock = new object();
        private List<string> _kr = new List<string>();
        private ClientWebSocket _current;
        private CancellationTokenSource _cts;
        private Thread _thread;
        private string _key, _iv;

        public bool Connected { get; private set; }
        public DateTime? DownSince { get; private set; } = DateTime.UtcNow;
        public int AuthFailures { get; private set; }

        public KisSocket(KisSettings settings, Func<string> approvalKey, Action invalidateApproval, Action<KisFrame> onFrame)
        {
            _settings = settings;
            _approval = approvalKey;
            _invalidate = invalidateApproval;
            _onFrame = onFrame;
        }

        public static string Subscribe(string approvalKey, string trId, string trKey, bool on) => new JObject
        {
            ["header"] = new JObject { ["approval_key"] = approvalKey, ["custtype"] = "P", ["tr_type"] = on ? "1" : "2", ["content-type"] = "utf-8" },
            ["body"] = new JObject { ["input"] = new JObject { ["tr_id"] = trId, ["tr_key"] = trKey } },
        }.ToString(Newtonsoft.Json.Formatting.None);

        public static string Decrypt(string cipherBase64, string key, string iv)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            using var dec = aes.CreateDecryptor();
            var cipher = Convert.FromBase64String(cipherBase64);
            return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
        }

        /// <summary>H0STCNT0 body: (ticker, price, volume, hhmmss). 0 MKSC_SHRN_ISCD, 1 STCK_CNTG_HOUR, 2 STCK_PRPR, 12 CNTG_VOL.</summary>
        public static Tuple<string, decimal, decimal, string> ParseTradeBody(string body)
        {
            var f = (body ?? "").Split('^');
            if (f.Length < 13) return null;
            if (!decimal.TryParse(f[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) return null;
            decimal.TryParse(f[12], NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);
            return Tuple.Create(f[0], price, vol, f[1]);
        }

        /// <summary>H0STCNI0/9 decrypted body. 2 ODER_NO, 4 SELN_BYOV_CLS, 8 STCK_SHRN_ISCD, 9 CNTG_QTY, 10 CNTG_UNPR,
        /// 11 STCK_CNTG_HOUR, 13 CNTG_YN.</summary>
        public static KisFill ParseFillBody(string body)
        {
            var f = (body ?? "").Split('^');
            if (f.Length < 14) return null;
            decimal.TryParse(f[10], NumberStyles.Any, CultureInfo.InvariantCulture, out var fillPrice);
            int.TryParse(f[9], out var fillQty);
            return new KisFill { OrderNo = f[2], Buy = f[4] == "02", Ticker = f[8], FillQty = fillQty, FillPrice = fillPrice,
                                 FillTime = f[11], IsFilled = f[13] == "2" };
        }

        public static KisFrame Parse(string text, string key, string iv)
        {
            if (string.IsNullOrEmpty(text)) return new KisFrame();
            if (text[0] == '{')
            {
                JObject o;
                try { o = JObject.Parse(text); } catch { return new KisFrame(); }
                var tr = o["header"]?.Value<string>("tr_id");
                if (tr == "PINGPONG") return new KisFrame { Kind = KisFrameKind.Ping, TrId = tr };
                var body = o["body"] as JObject;
                var output = body?["output"] as JObject;
                return new KisFrame
                {
                    Kind = KisFrameKind.Ack, TrId = tr, Key = output?.Value<string>("key"), Iv = output?.Value<string>("iv"),
                    AckOk = body?.Value<string>("rt_cd") != "1", AckMessage = body?.Value<string>("msg1"),
                };
            }
            var parts = text.Split(new[] { '|' }, 4);
            if (parts.Length < 4) return new KisFrame();
            var trId = parts[1];
            if (trId == KisConstants.WsTrPrice)
            {
                var t = ParseTradeBody(parts[3]);
                return t == null ? new KisFrame() : new KisFrame { Kind = KisFrameKind.Trade, TrId = trId, Symbol = t.Item1, Price = t.Item2 };
            }
            if (trId == KisConstants.WsTrFillReal || trId == KisConstants.WsTrFillDemo)
            {
                var plain = parts[3];
                if (parts[0] == "1")
                {
                    if (key == null || iv == null) return new KisFrame { Kind = KisFrameKind.Fill, TrId = trId };
                    try { plain = Decrypt(parts[3], key, iv); }
                    catch (Exception) { return new KisFrame { Kind = KisFrameKind.Fill, TrId = trId }; }
                }
                var fill = ParseFillBody(plain);
                return new KisFrame { Kind = KisFrameKind.Fill, TrId = trId, OrderNo = fill?.OrderNo, Symbol = fill?.Ticker };
            }
            return new KisFrame();
        }

        public void SetSymbols(IEnumerable<string> kr)
        {
            lock (_lock)
            {
                var k = kr.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (k.SequenceEqual(_kr)) return;
                _kr = k;
                try { _current?.Abort(); } catch { }  // reconnect with the new subscriptions
            }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _thread = new Thread(Run) { IsBackground = true, Name = "kis-ws" };
            _thread.Start();
        }

        public void Stop() { try { _cts?.Cancel(); } catch { } }

        private void Run()
        {
            var token = _cts.Token;
            var failures = 0;
            while (!token.IsCancellationRequested)
            {
                try { RunOnce(token); failures = 0; }
                catch (Exception e) when (!token.IsCancellationRequested) { Log.Trace($"KisSocket: {e.GetType().Name}: {e.Message}"); }
                Connected = false;
                DownSince ??= DateTime.UtcNow;
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Backoff[Math.Min(failures++, Backoff.Length - 1)]));
            }
        }

        private static void Send(ClientWebSocket ws, string text, CancellationToken stop) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, stop).GetAwaiter().GetResult();

        private void RunOnce(CancellationToken stop)
        {
            using var ws = new ClientWebSocket();
            if (!string.IsNullOrEmpty(_settings.Proxy) && _settings.Proxy != "direct")
                ws.Options.Proxy = new WebProxy(_settings.Proxy);
            ws.ConnectAsync(new Uri(_settings.SocketUrl), stop).GetAwaiter().GetResult();
            var approval = _approval();
            List<string> symbols;
            lock (_lock)
            {
                _current = ws;
                symbols = _kr.Take(MaxSubscriptions - 1).ToList();  // one subscription is the fill notice
                if (_kr.Count > symbols.Count)
                    Log.Error($"KisSocket: {_kr.Count} symbols, KIS allows {MaxSubscriptions - 1} trade feeds; the rest are polled");
            }
            var sendLock = new object();
            lock (sendLock)
            {
                Send(ws, Subscribe(approval, _settings.Demo ? KisConstants.WsTrFillDemo : KisConstants.WsTrFillReal, _settings.HtsId, true), stop);
                foreach (var s in symbols) Send(ws, Subscribe(approval, KisConstants.WsTrPrice, s, true), stop);
            }
            Connected = true;
            DownSince = null;
            _onFrame(new KisFrame { Kind = KisFrameKind.Fill });  // (re)connected: re-read every tracked order
            var buffer = new byte[64 * 1024];
            try
            {
                while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var text = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), stop).GetAwaiter().GetResult();
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    } while (!r.EndOfMessage);
                    var raw = text.ToString();
                    var f = Parse(raw, _key, _iv);
                    if (f.Kind == KisFrameKind.Ping) { lock (sendLock) Send(ws, raw, stop); continue; }
                    if (f.Kind == KisFrameKind.Ack)
                    {
                        if (f.Key != null && f.Iv != null) { _key = f.Key; _iv = f.Iv; }
                        if (!f.AckOk && (f.AckMessage ?? "").IndexOf("approval", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            AuthFailures++;
                            _invalidate();
                            return;  // reconnect with the backend's current approval key
                        }
                        if (f.AckOk) AuthFailures = 0;
                        continue;
                    }
                    _onFrame(f);
                }
            }
            finally
            {
                lock (_lock) _current = null;
            }
        }
    }
}
