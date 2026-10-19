/*
 * TossBrokerage — LEAN live brokerage + data queue for Toss Securities, patched for stockapp
 * (stockapp spec §3.2, §7, §8.4, §10, §11.1).
 *
 * - Orders: only LIMIT orders whose tag is a stockapp client order id (persisted in stockapp's
 *   ledger before LEAN sees the order). KR → OPG LIMIT, US → DAY LIMIT. Stateless guardrails run
 *   first (OrderPolicy). A send whose outcome is unknown (transport error, timeout, 5xx,
 *   request-in-progress, or any answer to a resend of such a send: Toss may already hold the order) is
 *   resent with the same clientOrderId every 5 s, inside Toss's 10-minute idempotency window,
 *   until Toss answers or 8 minutes have passed; then the adapter reports `toss-unknown` and
 *   never sends it again (stockapp resolves it by field matching and pauses the strategy).
 * - Fills: WebSocket `personal:order` frames wake the adapter, which re-reads orders over REST
 *   (FillTracker turns cumulative state into fill increments). Tracked orders are also re-read
 *   every 30 s, and every 3 s while the socket is down.
 * - Holdings, cash and open orders reported to LEAN are the strategy ledger's (LedgerView); LEAN's
 *   periodic cash sync is off, since LEAN's own fills keep its book in step with the ledger.
 * - Market data: WebSocket trade ticks for subscribed KR/US symbols, REST /prices every 15 s while
 *   the socket is down, and a USD/KRW quote once a minute so LEAN can value USD (stockapp plan fact 3).
 * - Problems LEAN can't express as order events go to the algorithm as brokerage messages:
 *   `toss-unknown` (message: the client order id), `toss-auth`, `toss-feed-down`.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MyTrading.Stockapp;
using Newtonsoft.Json.Linq;
using NodaTime;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace MyTrading.Toss
{
    public class TossBrokerage : Brokerage, IDataQueueHandler
    {
        static TossBrokerage()
        {
            try { Market.Add(TossConstants.KrxMarket, TossConstants.KrxMarketId); }
            catch { /* already registered */ }
        }

        private class Pending { public Order Order; public string Body; public DateTime First; public DateTime Next; public bool Resend; }

        private readonly IAlgorithm _algorithm;
        private readonly TossSettings _settings;
        private readonly TossRestClient _rest;
        private readonly TokenFileProvider _tokens;
        private readonly TossSymbolMapper _mapper = new TossSymbolMapper();
        private readonly IDataAggregator _aggregator;
        private readonly FillTracker _tracker = new FillTracker();
        private readonly ConcurrentDictionary<int, Pending> _uncertain = new ConcurrentDictionary<int, Pending>();
        private readonly HashSet<Symbol> _subscribed = new HashSet<Symbol>();
        private readonly object _subLock = new object();
        private TossSocket _socket;
        private CancellationTokenSource _cts;
        private Thread _poller;
        private bool _connected;
        private DateTime _lastOrderPoll = DateTime.MinValue, _lastPricePoll = DateTime.MinValue, _lastFx = DateTime.MinValue;
        private DateTime _restOkAt = DateTime.UtcNow;
        private bool _feedDownReported, _authReported;

        public override bool IsConnected => _connected;

        public TossBrokerage(IAlgorithm algorithm, TossSettings settings, TossRestClient rest = null,
                             IDataAggregator aggregator = null)
            : base("TossBrokerage")
        {
            _algorithm = algorithm;
            _settings = settings;
            AccountBaseCurrency = TossConstants.KrwCurrency;
            if (rest != null) _rest = rest;
            else
            {
                _tokens = new TokenFileProvider(settings.TokenFile);
                _rest = new TossRestClient(_tokens, settings.BaseUrl, TossRestClient.CreateHttp(settings.Proxy, TimeSpan.FromSeconds(15)));
            }
            _aggregator = aggregator ?? Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"));
        }

        // ── connection ───────────────────────────────────────────────────
        public override void Connect()
        {
            if (_connected) return;
            _rest.AccountSeq();  // fails fast if the token or the tunnel is bad
            _cts = new CancellationTokenSource();
            _socket = new TossSocket(_settings, () => _rest.AccessToken(), () => _tokens?.Invalidate(), _rest.AccountSeq, OnFrame);
            UpdateSocket();  // LEAN may have subscribed before connecting
            _socket.Start();
            _poller = new Thread(PollLoop) { IsBackground = true, Name = "toss-poller" };
            _poller.Start();
            _connected = true;
            Log.Trace("TossBrokerage.Connect(): connected (stockapp mode, trading " +
                      (_settings.TradingEnabled ? "enabled" : "disabled") + ")");
        }

        public override void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            _socket?.Stop();
            _connected = false;
        }

        public override bool ShouldPerformCashSync(DateTime currentTimeUtc) => false;

        // ── orders ───────────────────────────────────────────────────────
        private static string MarketOf(Symbol symbol) => symbol.ID.Market == TossConstants.KrxMarket ? "KR" : "US";

        private void Report(Order order, OrderStatus status, string message, decimal fillQty = 0m, decimal fillPrice = 0m)
        {
            var ccy = MarketOf(order.Symbol) == "KR" ? TossConstants.KrwCurrency : TossConstants.UsdCurrency;
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message)
            {
                Status = status,
                FillQuantity = order.Direction == OrderDirection.Buy ? fillQty : -fillQty,
                FillPrice = fillPrice,
                FillPriceCurrency = ccy,
            });
        }

        private static string Toss(string status, string orderId = null, string code = null, string message = null) =>
            "toss:" + new JObject { ["status"] = status, ["orderId"] = orderId, ["code"] = code, ["message"] = message }
                .ToString(Newtonsoft.Json.Formatting.None);

        public override bool PlaceOrder(Order order)
        {
            var market = MarketOf(order.Symbol);
            var symbol = _mapper.GetBrokerageSymbol(order.Symbol);
            var limit = order is LimitOrder lo ? lo.LimitPrice : 0m;
            string refusal = order.Type != OrderType.Limit ? "only limit orders are sent" : null;
            (decimal, decimal)? limits = null;
            if (refusal == null && market == "KR" && _settings.TradingEnabled)
            {
                try { limits = _rest.GetPriceLimits(symbol); }
                catch (Exception e) { refusal = "couldn't read today's price limits: " + e.GetType().Name; }
            }
            refusal ??= OrderPolicy.Check(_settings, order.Tag, market, order.AbsoluteQuantity, limit, limits);
            if (refusal != null)
            {
                Report(order, OrderStatus.Invalid, "guardrail: " + refusal);
                return false;
            }
            var body = OrderPolicy.Body(order.Tag, symbol, market, order.Direction == OrderDirection.Buy,
                                        order.AbsoluteQuantity, limit);
            return Send(new Pending { Order = order, Body = body, First = DateTime.UtcNow });
        }

        private bool Send(Pending p)
        {
            var outcome = _rest.SendOrderOnce(p.Body);
            switch (outcome.Kind)
            {
                case SendKind.Accepted:
                    _uncertain.TryRemove(p.Order.Id, out _);
                    p.Order.BrokerId.Add(outcome.OrderId);
                    _tracker.Track(outcome.OrderId);
                    Report(p.Order, OrderStatus.Submitted, Toss("PENDING", outcome.OrderId));
                    return true;
                // A rejection is definite only for a first send. After an uncertain send, Toss may already
                // hold the order, and a 4xx on the resend doesn't prove otherwise (stockapp spec §7.4: same-id
                // behaviour for accepted orders is unverified), so it stays uncertain and field matching decides.
                case SendKind.Rejected when !p.Resend:
                    _uncertain.TryRemove(p.Order.Id, out _);
                    Report(p.Order, OrderStatus.Invalid, Toss("REJECTED", null, outcome.Code, outcome.Message));
                    return false;
                default:
                    p.Resend = true;
                    p.Next = DateTime.UtcNow.AddSeconds(Math.Max(5, outcome.RetryAfterSeconds));
                    _uncertain[p.Order.Id] = p;
                    Log.Trace($"TossBrokerage: send of {p.Order.Tag} uncertain ({outcome.Code}); resending the same id");
                    return true;
            }
        }

        public override bool UpdateOrder(Order order) => false;  // stockapp v1 never modifies

        public override bool CancelOrder(Order order)
        {
            var orderId = order.BrokerId.FirstOrDefault();
            if (string.IsNullOrEmpty(orderId)) return false;
            var res = _rest.CancelOrder(orderId);
            Refresh(orderId);  // the terminal state comes from Toss, never from the cancel answer
            return res.Ok || (res.Code ?? "").StartsWith("already-", StringComparison.Ordinal);
        }

        // ── ledger-backed account (stockapp spec §7.2) ────────────────────
        private LedgerView View() => LedgerView.Load(_settings.LedgerView);

        public override List<CashAmount> GetCashBalance() =>
            View().Cash.Select(c => new CashAmount(c.Value, c.Key)).ToList();

        public override List<Holding> GetAccountHoldings() =>
            View().Positions.Select(p => new Holding
            {
                Symbol = _mapper.LeanSymbol(p.symbol, p.market),
                Quantity = p.qty,
                AveragePrice = p.avgCost,
                MarketPrice = p.avgCost,
                CurrencySymbol = p.market == "KR" ? "₩" : "$",
            }).ToList();

        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            foreach (var o in View().OpenOrders)
            {
                var order = new LimitOrder(_mapper.LeanSymbol(o.Symbol, o.Market), o.Buy ? o.Qty : -o.Qty, o.LimitPrice,
                                           DateTime.UtcNow, o.ClientOrderId);
                order.BrokerId.Add(o.OrderId);
                _tracker.Track(o.OrderId, o.FilledQty, o.AvgFillPrice);
                orders.Add(order);
            }
            return orders;
        }

        // ── fills, prices, retries ───────────────────────────────────────
        private void OnFrame(Frame f)
        {
            try
            {
                if (f.Kind == FrameKind.Order)
                {
                    if (f.OrderId != null && _tracker.IsTracked(f.OrderId)) Refresh(f.OrderId);
                    else foreach (var id in _tracker.Tracked()) Refresh(id);
                }
                else if (f.Kind == FrameKind.Trade)
                {
                    Tick(_mapper.LeanSymbol(f.Symbol, f.Market), f.Price, quote: false);
                }
            }
            catch (Exception e) { Log.Trace($"TossBrokerage.OnFrame: {e.Message}"); }
        }

        private void Refresh(string orderId)
        {
            var st = _rest.GetOrder(orderId);
            if (!st.Found) return;
            _restOkAt = DateTime.UtcNow;
            var step = _tracker.Observe(st);
            if (step == null) return;
            var status = step.Status switch
            {
                TrackedStatus.Filled => OrderStatus.Filled,
                TrackedStatus.Partial => OrderStatus.PartiallyFilled,
                TrackedStatus.Rejected => OrderStatus.Invalid,
                TrackedStatus.Canceled => OrderStatus.Canceled,
                _ => OrderStatus.Submitted,
            };
            foreach (var order in _algorithm.Transactions.GetOrdersByBrokerageId(orderId) ?? new List<Order>())
                Report(order, status, step.Message, step.FillQty, step.FillPrice);
        }

        private void Tick(Symbol symbol, decimal price, bool quote)
        {
            lock (_subLock) { if (!_subscribed.Contains(symbol)) return; }
            var tz = _algorithm.Securities.ContainsKey(symbol) ? _algorithm.Securities[symbol].Exchange.TimeZone : DateTimeZone.Utc;
            var now = DateTime.UtcNow.ConvertFromUtc(tz);
            _aggregator.Update(quote ? new Tick(now, symbol, price, price) { TickType = TickType.Quote }
                                     : new Tick(now, symbol, "", "", 0m, price) { TickType = TickType.Trade });
        }

        private void PollLoop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try { Poll(); }
                catch (Exception e) { Log.Trace($"TossBrokerage poller: {e.GetType().Name}: {e.Message}"); }
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }

        private void Poll()
        {
            var now = DateTime.UtcNow;
            ResendUncertain(now);
            var socketUp = _socket?.Connected == true;
            if (now - _lastOrderPoll >= TimeSpan.FromSeconds(socketUp ? 30 : 3))
            {
                _lastOrderPoll = now;
                foreach (var id in _tracker.Tracked()) Refresh(id);
            }
            if (!socketUp && now - _lastPricePoll >= TimeSpan.FromSeconds(15)) { _lastPricePoll = now; PollPrices(); }
            if (_settings.UsdKrw > 0 && now - _lastFx >= TimeSpan.FromMinutes(1))
            {
                _lastFx = now;
                Tick(Symbol.Create("USDKRW", SecurityType.Forex, QuantConnect.Market.Oanda), _settings.UsdKrw, quote: true);
            }
            var down = _socket?.DownSince;
            if (down.HasValue && now - down.Value > _settings.FeedDownAfter && now - _restOkAt > _settings.FeedDownAfter)
            {
                if (!_feedDownReported)
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "toss-feed-down",
                        "WebSocket and REST both down for more than " + _settings.FeedDownAfter.TotalMinutes + " minutes"));
                _feedDownReported = true;
            }
            else if (!down.HasValue) _feedDownReported = false;
            if ((_socket?.AuthFailures ?? 0) >= 3 && !_authReported)
            {
                _authReported = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "toss-auth", "Toss refused the access token"));
            }
        }

        /// <summary>The poller's resend step: resends uncertain sends that are due with the same client order
        /// id, and reports `toss-unknown` for those past `UnknownAfter`, never sending them again.</summary>
        public void ResendUncertain(DateTime now)
        {
            foreach (var p in _uncertain.Values.ToArray())
            {
                if (now - p.First >= _settings.UnknownAfter)
                {
                    _uncertain.TryRemove(p.Order.Id, out _);
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "toss-unknown", p.Order.Tag));
                }
                else if (now >= p.Next) Send(p);
            }
        }

        private void PollPrices()
        {
            Symbol[] equities;
            lock (_subLock) { equities = _subscribed.Where(s => s.SecurityType == SecurityType.Equity).ToArray(); }
            for (var i = 0; i < equities.Length; i += 200)
            {
                var batch = equities.Skip(i).Take(200).ToDictionary(s => s.Value, s => s);
                foreach (var p in _rest.GetPrices(batch.Keys))
                    if (p.LastPrice > 0 && batch.TryGetValue(p.Symbol, out var sym)) Tick(sym, p.LastPrice, quote: false);
                _restOkAt = DateTime.UtcNow;
            }
        }

        // ── IDataQueueHandler ────────────────────────────────────────────
        private static bool CanSubscribe(Symbol s) =>
            s != null && !s.Value.Contains("universe", StringComparison.OrdinalIgnoreCase) &&
            ((s.SecurityType == SecurityType.Equity && (s.ID.Market == TossConstants.KrxMarket || s.ID.Market == QuantConnect.Market.USA)) ||
             (s.SecurityType == SecurityType.Forex && s.Value == "USDKRW"));

        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig config, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(config.Symbol) || config.TickType == TickType.OpenInterest) return null;
            var enumerator = _aggregator.Add(config, newDataAvailableHandler);
            lock (_subLock) { _subscribed.Add(config.Symbol); }
            UpdateSocket();
            return enumerator;
        }

        public void Unsubscribe(SubscriptionDataConfig config)
        {
            if (!CanSubscribe(config.Symbol)) return;
            lock (_subLock) { _subscribed.Remove(config.Symbol); }
            _aggregator.Remove(config);
            UpdateSocket();
        }

        private void UpdateSocket()
        {
            Symbol[] equities;
            lock (_subLock) { equities = _subscribed.Where(s => s.SecurityType == SecurityType.Equity).ToArray(); }
            _socket?.SetSymbols(equities.Where(s => MarketOf(s) == "KR").Select(s => s.Value),
                                equities.Where(s => MarketOf(s) == "US").Select(s => s.Value));
        }

        public void SetJob(LiveNodePacket job) { }

        public override void Dispose()
        {
            Disconnect();
            _aggregator?.Dispose();
        }
    }
}
