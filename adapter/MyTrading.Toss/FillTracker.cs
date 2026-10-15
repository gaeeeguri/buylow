/*
 * stockapp: turns Toss's cumulative order state into LEAN fill events (stockapp spec §7.4).
 *
 * Toss reports cumulative filled quantity, average price and fees. WebSocket order events and REST
 * polls may repeat or arrive out of order, so each order's last seen cumulative quantity is kept and
 * only an increase becomes a fill: the increment is booked at the incremental average price
 * (filled × avg − previous filled × previous avg) / increment. A terminal status is reported once.
 * Every event carries the cumulative Toss state as JSON in its message ("toss:{...}"), which is
 * what stockapp's ledger books; LEAN's own portfolio only sees the increments.
 */

using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace MyTrading.Toss
{
    public enum TrackedStatus { None, Accepted, Partial, Filled, Canceled, Rejected }

    public class FillStep
    {
        public decimal FillQty;        // the increment (positive), 0 if none
        public decimal FillPrice;      // price of the increment
        public TrackedStatus Status;   // LEAN status to report; None: nothing to report
        public string Message;         // "toss:{...}" with Toss's cumulative state
        public bool Terminal;
    }

    public class FillTracker
    {
        private class State { public decimal Filled; public decimal Avg; public bool Terminal; }
        private readonly ConcurrentDictionary<string, State> _orders = new ConcurrentDictionary<string, State>();

        public void Track(string orderId, decimal filled = 0m, decimal avg = 0m) =>
            _orders.TryAdd(orderId, new State { Filled = filled, Avg = avg });

        public bool IsTracked(string orderId) => _orders.ContainsKey(orderId);
        public string[] Tracked() { var keys = new string[_orders.Count]; _orders.Keys.CopyTo(keys, 0); return keys; }
        public void Forget(string orderId) => _orders.TryRemove(orderId, out _);

        public static string Describe(TossOrderStatus st) => "toss:" + new JObject
        {
            ["status"] = st.Status,
            ["orderId"] = st.OrderId,
            ["filled"] = st.FilledQty.ToString(CultureInfo.InvariantCulture),
            ["avg"] = st.AvgPrice.ToString(CultureInfo.InvariantCulture),
            ["fees"] = (st.Commission + st.Tax).ToString(CultureInfo.InvariantCulture),
        }.ToString(Newtonsoft.Json.Formatting.None);

        /// <summary>What to report for one observation of an order; null if nothing changed.</summary>
        public FillStep Observe(TossOrderStatus st)
        {
            if (st == null || !st.Found || !_orders.TryGetValue(st.OrderId, out var s) || s.Terminal) return null;
            var status = (st.Status ?? "").ToUpperInvariant();
            var step = new FillStep { Message = Describe(st) };
            if (st.FilledQty > s.Filled)
            {
                step.FillQty = st.FilledQty - s.Filled;
                step.FillPrice = (st.FilledQty * st.AvgPrice - s.Filled * s.Avg) / step.FillQty;
                step.Status = status == "FILLED" ? TrackedStatus.Filled : TrackedStatus.Partial;
                s.Filled = st.FilledQty;
                s.Avg = st.AvgPrice;
            }
            if (st.IsTerminal())
            {
                s.Terminal = true;
                step.Terminal = true;
                if (status == "FILLED") step.Status = TrackedStatus.Filled;
                else if (status == "REJECTED") step.Status = TrackedStatus.Rejected;
                else if (step.FillQty == 0) step.Status = TrackedStatus.Canceled;  // CANCELED / EXPIRED / REPLACED
                // A last fill and a cancel in one observation: report the fill now, the cancel next time.
                else s.Terminal = false;
                if (s.Terminal) _orders.TryRemove(st.OrderId, out _);
            }
            return step.Status == TrackedStatus.None ? null : step;
        }
    }
}
