/*
 * KisBrokerage — LEAN live brokerage + data queue for KIS (한국투자증권), stockapp mode (stockapp KIS spec §5).
 *
 * - Orders: only LIMIT orders on krx equities whose tag is a stockapp client order id (persisted in stockapp's
 *   ledger before LEAN sees the order), after the shared stateless guardrails. Body: ORD_DVSN 00,
 *   EXCG_ID_DVSN_CD KRX (the opening auction, never SOR/NXT). Sent once: an uncertain send is reported as
 *   `kis-unknown` and never sent again, because KIS has no idempotency key and a resend could place a second
 *   real order. stockapp's backend resolves it by field matching.
 * - Fills: inquire-daily-ccld is the truth. Tracked orders are re-read every 30 s, every 5 s while the socket is
 *   down, and when a fill notice arrives. Cumulative state becomes fill increments (CumulativeFills); each event
 *   carries "kis:{...}" for the ledger.
 * - Holdings, cash and open orders come from the strategy ledger (LedgerView); LEAN's cash sync is off.
 * - Market data: H0STCNT0 ticks, inquire-price every 15 s while the socket is down, and a USDKRW quote once a
 *   minute (the algorithm subscribes it).
 * - Brokerage messages: `kis-unknown` (message: the client order id), `kis-auth`, `kis-feed-down`.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MyTrading.Stockapp;
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

namespace MyTrading.Kis
{
    public class KisBrokerage : Brokerage, IDataQueueHandler
    {
        static KisBrokerage()
        {
            try { Market.Add(KisConstants.KrxMarket, KisConstants.KrxMarketId); }
            catch { /* already registered */ }
        }

        private readonly IAlgorithm _algorithm;
        private readonly KisSettings _settings;
        private readonly KisRest _rest;
        private readonly KisSecrets _secrets;
        private readonly KisSymbolMapper _mapper = new KisSymbolMapper();
        private readonly IDataAggregator _aggregator;
        private readonly Func<string, List<Order>> _ordersByBrokerId;
        private readonly Func<DateTime> _utcNow;
        private readonly CumulativeFills _fills = new CumulativeFills();
        private readonly HashSet<Symbol> _subscribed = new HashSet<Symbol>();
        private readonly object _subLock = new object(), _refreshLock = new object();
        private KisSocket _socket;
        private CancellationTokenSource _cts;
        private Thread _poller;
        private bool _connected, _feedDownReported, _authReported;
        private DateTime _lastOrderPoll = DateTime.MinValue, _lastPricePoll = DateTime.MinValue, _lastFx = DateTime.MinValue;
        private DateTime _restOkAt;

        public override bool IsConnected => _connected;

        public KisBrokerage(IAlgorithm algorithm, KisSettings settings, KisRest rest = null, IDataAggregator aggregator = null,
                            Func<string, List<Order>> ordersByBrokerId = null, Func<DateTime> utcNow = null, KisSocket socket = null)
            : base("KisBrokerage")
        {
            _algorithm = algorithm;
            _settings = settings;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _restOkAt = _utcNow();
            AccountBaseCurrency = KisConstants.KrwCurrency;
            _secrets = new KisSecrets(settings.SecretsFile);
            _rest = rest ?? new KisRest(settings, new TokenFileProvider(settings.TokenFile), _secrets,
                                        KisRest.CreateHttp(settings.Proxy, TimeSpan.FromSeconds(15)), null, _utcNow);
            _ordersByBrokerId = ordersByBrokerId ?? (id => _algorithm.Transactions.GetOrdersByBrokerageId(id));
            _socket = socket;
            _aggregator = aggregator ?? Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"));
        }

        // ── connection ───────────────────────────────────────────────────
        public override void Connect()
        {
            if (_connected) return;
            var today = _rest.Today();
            _rest.DailyOrders(today, today);  // fails fast if the token, the secrets or the tunnel is bad
            _cts = new CancellationTokenSource();
            _socket ??= new KisSocket(_settings, () => _secrets.ApprovalKey, _secrets.Invalidate, OnFrame);
            UpdateSocket();
            _socket.Start();
            _poller = new Thread(PollLoop) { IsBackground = true, Name = "kis-poller" };
            _poller.Start();
            _connected = true;
            Log.Trace("KisBrokerage.Connect(): connected (stockapp mode, " + _settings.Env + ", trading " +
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
        private void Report(Order order, OrderStatus status, string message, decimal fillQty = 0m, decimal fillPrice = 0m)
        {
            OnOrderEvent(new OrderEvent(order, _utcNow(), OrderFee.Zero, message)
            {
                Status = status,
                FillQuantity = order.Direction == OrderDirection.Buy ? fillQty : -fillQty,
                FillPrice = fillPrice,
                FillPriceCurrency = KisConstants.KrwCurrency,
            });
        }

        public override bool PlaceOrder(Order order)
        {
            string refusal = null;
            if (order.Type != OrderType.Limit) refusal = "only limit orders are sent";
            else if (order.Symbol.SecurityType != SecurityType.Equity || order.Symbol.ID.Market != KisConstants.KrxMarket)
                refusal = "KIS trades KR (krx) equities only";
            var limit = order is LimitOrder lo ? lo.LimitPrice : 0m;
            // KIS takes whole won and whole shares; never let the body round them silently
            if (refusal == null && (limit != decimal.Truncate(limit) || order.AbsoluteQuantity != decimal.Truncate(order.AbsoluteQuantity)))
                refusal = "price and quantity must be whole";
            string pdno = null;
            (decimal, decimal)? limits = null;
            if (refusal == null)
            {
                pdno = _mapper.GetBrokerageSymbol(order.Symbol);
                if (_settings.TradingEnabled)
                {
                    try { limits = _rest.PriceLimits(pdno); }
                    catch (Exception e) { refusal = "couldn't read today's price limits: " + e.GetType().Name; }
                }
            }
            refusal ??= Guardrails.Check(_settings.TradingEnabled, _settings.MaxOrderKrw, "kis-max-order-krw", order.Tag, "KR",
                                         order.AbsoluteQuantity, limit, limits, "KIS inquire-price");
            if (refusal != null)
            {
                Report(order, OrderStatus.Invalid, "guardrail: " + refusal);
                return false;
            }
            var result = _rest.SendOrder(order.Direction == OrderDirection.Buy, pdno, order.AbsoluteQuantity, limit);
            switch (result.Kind)
            {
                case SendKind.Accepted:
                    order.BrokerId.Add(result.OrderId);
                    _fills.Track(result.OrderId);
                    Report(order, OrderStatus.Submitted, KisOrderRow.Describe("ACCEPTED", result.OrderId, 0m, 0m));
                    return true;
                case SendKind.Rejected:
                    Report(order, OrderStatus.Invalid, KisOrderRow.Describe("REJECTED", null, 0m, 0m, result.Code, result.Message));
                    return false;
                default:
                    // Never sent again (stockapp KIS spec §5.4): stockapp matches it against the daily orders.
                    Log.Trace($"KisBrokerage: send of {order.Tag} uncertain ({result.Code}); left to stockapp's field matching");
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "kis-unknown", order.Tag));
                    return true;
            }
        }

        public override bool UpdateOrder(Order order) => false;  // stockapp v1 never modifies

        public override bool CancelOrder(Order order)
        {
            var orderId = order.BrokerId.FirstOrDefault();
            if (string.IsNullOrEmpty(orderId)) return false;
            var ok = _rest.Cancel(orderId);
            try { Refresh(); }  // the terminal state comes from the daily orders, never from the cancel answer
            catch (Exception e) { Log.Trace($"KisBrokerage.CancelOrder: refresh after cancel failed: {e.GetType().Name}: {e.Message}"); }
            return ok;
        }

        // ── ledger-backed account (stockapp spec §7.2) ────────────────────
        private LedgerView View() => LedgerView.Load(_settings.LedgerView);
        private Symbol Lean(string code) => _mapper.GetLeanSymbol(code, SecurityType.Equity, KisConstants.KrxMarket);

        public override List<CashAmount> GetCashBalance() => View().Cash.Select(c => new CashAmount(c.Value, c.Key)).ToList();

        public override List<Holding> GetAccountHoldings() => View().Positions.Select(p => new Holding
        {
            Symbol = Lean(p.symbol), Quantity = p.qty, AveragePrice = p.avgCost, MarketPrice = p.avgCost, CurrencySymbol = "₩",
        }).ToList();

        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            foreach (var o in View().OpenOrders)
            {
                var order = new LimitOrder(Lean(o.Symbol), o.Buy ? o.Qty : -o.Qty, o.LimitPrice, _utcNow(), o.ClientOrderId);
                order.BrokerId.Add(o.OrderId);
                _fills.Track(o.OrderId, o.FilledQty, o.AvgFillPrice);
                orders.Add(order);
            }
            return orders;
        }

        // ── fills, prices ────────────────────────────────────────────────
        private void OnFrame(KisFrame f)
        {
            try
            {
                if (f.Kind == KisFrameKind.Fill) Refresh();
                else if (f.Kind == KisFrameKind.Trade) Tick(Lean(f.Symbol), f.Price, quote: false);
            }
            catch (Exception e) { Log.Trace($"KisBrokerage.OnFrame: {e.Message}"); }
        }

        private void Refresh()
        {
            lock (_refreshLock)
            {
                var ids = _fills.Tracked();
                if (ids.Length == 0) return;
                var today = _rest.Today();
                var first = ids.Select(id => KisOrderId.TryParse(id, out var day, out _, out _) ? day : today)
                               .OrderBy(d => d, StringComparer.Ordinal).First();
                List<KisOrderRow> rows;
                try { rows = _rest.DailyOrders(first, today); }
                catch (Exception e) { Log.Trace($"KisBrokerage.Refresh: {e.Message}"); return; }
                _restOkAt = _utcNow();
                foreach (var id in ids)
                {
                    // matched on the canonical id, so an id kept with other padding still finds its row
                    var row = rows.FirstOrDefault(r => !r.Revision && KisOrderId.Same(r.OrderId, id));
                    if (row == null) continue;
                    // Not yet known to LEAN (e.g. between GetOpenOrders and AddOpenOrder at startup): leave the
                    // increment unobserved so a later refresh reports it instead of losing it.
                    var orders = _ordersByBrokerId(id) ?? new List<Order>();
                    if (orders.Count == 0) continue;
                    var snapshot = row.Snapshot(today);
                    snapshot.OrderId = id;  // the tracker and LEAN know the order by the id they were given
                    var step = _fills.Observe(snapshot);
                    if (step == null) continue;
                    var status = step.Status switch
                    {
                        TrackedStatus.Filled => OrderStatus.Filled,
                        TrackedStatus.Partial => OrderStatus.PartiallyFilled,
                        TrackedStatus.Rejected => OrderStatus.Invalid,
                        TrackedStatus.Canceled => OrderStatus.Canceled,
                        _ => OrderStatus.Submitted,
                    };
                    foreach (var order in orders)
                        Report(order, status, step.Message, step.FillQty, step.FillPrice);
                }
            }
        }

        private void Tick(Symbol symbol, decimal price, bool quote)
        {
            lock (_subLock) { if (!_subscribed.Contains(symbol)) return; }
            var tz = _algorithm != null && _algorithm.Securities.ContainsKey(symbol) ? _algorithm.Securities[symbol].Exchange.TimeZone : DateTimeZone.Utc;
            var now = _utcNow().ConvertFromUtc(tz);
            _aggregator.Update(quote ? new Tick(now, symbol, price, price) { TickType = TickType.Quote }
                                     : new Tick(now, symbol, "", "", 0m, price) { TickType = TickType.Trade });
        }

        private void PollLoop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try { Poll(_utcNow()); }
                catch (Exception e) { Log.Trace($"KisBrokerage poller: {e.GetType().Name}: {e.Message}"); }
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }

        /// <summary>One poller step (public for tests). It never sends an order.</summary>
        public void Poll(DateTime now)
        {
            var socketUp = _socket?.Connected == true;
            if (now - _lastOrderPoll >= TimeSpan.FromSeconds(socketUp ? 30 : 5)) { _lastOrderPoll = now; Refresh(); }
            if (!socketUp && now - _lastPricePoll >= TimeSpan.FromSeconds(15)) { _lastPricePoll = now; PollPrices(); }
            if (_settings.UsdKrw > 0 && now - _lastFx >= TimeSpan.FromMinutes(1))
            {
                _lastFx = now;
                Tick(Symbol.Create("USDKRW", SecurityType.Forex, Market.Oanda), _settings.UsdKrw, quote: true);
            }
            var down = _socket?.DownSince;
            if (down.HasValue && now - down.Value > _settings.FeedDownAfter && now - _restOkAt > _settings.FeedDownAfter)
            {
                if (!_feedDownReported)
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "kis-feed-down",
                        "WebSocket and REST both down for more than " + _settings.FeedDownAfter.TotalMinutes + " minutes"));
                _feedDownReported = true;
            }
            else if (!down.HasValue) _feedDownReported = false;
            if ((_rest.AuthFailures >= 3 || (_socket?.AuthFailures ?? 0) >= 3) && !_authReported)
            {
                _authReported = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "kis-auth", "KIS refused the access token"));
            }
        }

        private void PollPrices()
        {
            Symbol[] equities;
            lock (_subLock) { equities = _subscribed.Where(s => s.SecurityType == SecurityType.Equity).ToArray(); }
            foreach (var s in equities)
            {
                try
                {
                    var p = _rest.LastPrice(_mapper.GetBrokerageSymbol(s));
                    if (p > 0) Tick(s, p, quote: false);
                    _restOkAt = _utcNow();
                }
                catch (Exception e) { Log.Trace($"KisBrokerage.PollPrices {s.Value}: {e.Message}"); }
            }
        }

        // ── IDataQueueHandler ────────────────────────────────────────────
        private static bool CanSubscribe(Symbol s) =>
            s != null && !s.Value.Contains("universe", StringComparison.OrdinalIgnoreCase) &&
            ((s.SecurityType == SecurityType.Equity && s.ID.Market == KisConstants.KrxMarket) ||
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
            string[] codes;
            lock (_subLock) { codes = _subscribed.Where(s => s.SecurityType == SecurityType.Equity).Select(s => s.Value).ToArray(); }
            _socket?.SetSymbols(codes);
        }

        public void SetJob(LiveNodePacket job) { }

        public override void Dispose()
        {
            Disconnect();
            _aggregator?.Dispose();
        }
    }
}
