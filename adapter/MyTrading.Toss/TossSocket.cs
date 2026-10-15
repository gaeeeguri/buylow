/*
 * stockapp: the Toss WebSocket (stockapp spec §2.1, §7.4, §11.1), connection #2 of the account's two.
 *
 * Subscriptions are declarative: one JSON array naming every topic, sent after connecting; a changed
 * symbol list reconnects with the new declaration. Topics: `trade:kr` / `trade:us` with symbol codes
 * (frames {"type":"message","topic":"trade:kr:005930","data":{price,timestamp,...}}) and
 * `personal:order` with the account seq. Personal order payloads are still unobserved (stockapp spec
 * §13.3), so an order frame is only a wake-up: the adapter re-reads that order (or every tracked
 * order) over REST, which is the source of truth either way. "PING" is sent every 30 s.
 * Reconnects back off 1, 2, 4, 8, 16, 30 s.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace MyTrading.Toss
{
    public enum FrameKind { Other, Pong, Ack, Order, Trade, Error }

    public class Frame
    {
        public FrameKind Kind;
        public string Market;    // KR | US for trades
        public string Symbol;
        public decimal Price;
        public string OrderId;   // order frames, when the payload names one
        public string Error;
    }

    public class TossSocket
    {
        private static readonly int[] Backoff = { 1, 2, 4, 8, 16, 30 };
        private readonly TossSettings _settings;
        private readonly Func<string> _token;
        private readonly Action _invalidateToken;
        private readonly Func<long> _accountSeq;
        private readonly Action<Frame> _onFrame;
        private readonly object _lock = new object();
        private List<string> _kr = new List<string>();
        private List<string> _us = new List<string>();
        private ClientWebSocket _current;
        private CancellationTokenSource _cts;
        private Thread _thread;

        public bool Connected { get; private set; }
        public DateTime? DownSince { get; private set; } = DateTime.UtcNow;
        public int AuthFailures { get; private set; }

        public TossSocket(TossSettings settings, Func<string> token, Action invalidateToken, Func<long> accountSeq,
                          Action<Frame> onFrame)
        {
            _settings = settings;
            _token = token;
            _invalidateToken = invalidateToken;
            _accountSeq = accountSeq;
            _onFrame = onFrame;
        }

        public static string Declaration(long accountSeq, IEnumerable<string> kr, IEnumerable<string> us)
        {
            var list = new JArray { new JObject { ["id"] = "stockapp-lean" },
                                    new JObject { ["type"] = "personal:order", ["codes"] = new JArray(accountSeq.ToString(CultureInfo.InvariantCulture)) } };
            var krCodes = kr.ToArray();
            var usCodes = us.ToArray();
            if (krCodes.Length > 0) list.Add(new JObject { ["type"] = "trade:kr", ["codes"] = new JArray(krCodes) });
            if (usCodes.Length > 0) list.Add(new JObject { ["type"] = "trade:us", ["codes"] = new JArray(usCodes) });
            return list.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static Frame Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Frame();
            if (text.Trim().Equals("pong", StringComparison.OrdinalIgnoreCase)) return new Frame { Kind = FrameKind.Pong };
            JObject msg;
            try { msg = JObject.Parse(text); } catch { return new Frame(); }
            var type = msg.Value<string>("type");
            if (type == "pong") return new Frame { Kind = FrameKind.Pong };
            if (type == "subscriptions") return new Frame { Kind = FrameKind.Ack };
            if (type == "error")
                return new Frame { Kind = FrameKind.Error, Error = (msg["error"] as JObject)?.Value<string>("code") ?? msg["error"]?.ToString() };
            if (type != "message") return new Frame();
            var topic = (msg.Value<string>("topic") ?? "").Split(':');
            var data = msg["data"] as JObject;
            if (topic.Length >= 2 && topic[0] == "personal" && topic[1] == "order")
                return new Frame { Kind = FrameKind.Order, OrderId = data?.Value<string>("orderId") };
            if (topic.Length >= 3 && topic[0] == "trade" && data != null &&
                decimal.TryParse(data["price"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                return new Frame { Kind = FrameKind.Trade, Market = topic[1].ToUpperInvariant(), Symbol = topic[2], Price = price };
            return new Frame();
        }

        public void SetSymbols(IEnumerable<string> kr, IEnumerable<string> us)
        {
            lock (_lock)
            {
                var k = kr.Distinct().OrderBy(x => x).ToList();
                var u = us.Distinct().OrderBy(x => x).ToList();
                if (k.SequenceEqual(_kr) && u.SequenceEqual(_us)) return;
                _kr = k;
                _us = u;
                try { _current?.Abort(); } catch { }  // reconnect with the new declaration
            }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _thread = new Thread(Run) { IsBackground = true, Name = "toss-ws" };
            _thread.Start();
        }

        public void Stop() { try { _cts?.Cancel(); } catch { } }

        private void Run()
        {
            var token = _cts.Token;
            var failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    RunOnce(token);
                    failures = 0;
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    Log.Trace($"TossSocket: {e.GetType().Name}: {e.Message}");
                }
                Connected = false;
                DownSince ??= DateTime.UtcNow;
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Backoff[Math.Min(failures++, Backoff.Length - 1)]));
            }
        }

        private void RunOnce(CancellationToken stop)
        {
            using var ws = new ClientWebSocket();
            if (!string.IsNullOrEmpty(_settings.Proxy) && _settings.Proxy != "direct")
                ws.Options.Proxy = new WebProxy(_settings.Proxy);
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token());
            ws.Options.CollectHttpResponseDetails = true;
            try
            {
                ws.ConnectAsync(new Uri(_settings.WsUrl), stop).GetAwaiter().GetResult();
            }
            catch (WebSocketException)
            {
                if (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
                {
                    AuthFailures++;
                    _invalidateToken();
                }
                throw;
            }
            AuthFailures = 0;
            string declaration;
            lock (_lock)
            {
                declaration = Declaration(_accountSeq(), _kr, _us);
                _current = ws;
            }
            var sendLock = new object();
            lock (sendLock) Send(ws, declaration, stop);
            Connected = true;
            DownSince = null;
            _onFrame(new Frame { Kind = FrameKind.Order });  // (re)connected: re-read every tracked order
            // Cancelling a pending ReceiveAsync aborts a ClientWebSocket, so pings come from a timer and a
            // changed symbol list aborts the socket on purpose (SetSymbols).
            using var pinger = new Timer(_ =>
            {
                try { lock (sendLock) Send(ws, "PING", stop); } catch { /* the receive loop sees the failure */ }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
                    _onFrame(Parse(text.ToString()));
                }
            }
            finally
            {
                lock (_lock) _current = null;
            }
        }

        private static void Send(ClientWebSocket ws, string text, CancellationToken stop) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, stop)
              .GetAwaiter().GetResult();
    }
}
