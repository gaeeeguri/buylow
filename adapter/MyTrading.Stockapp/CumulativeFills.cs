/*
 * stockapp: a broker's cumulative order state as LEAN fill events (stockapp spec §7.4).
 *
 * Brokers report cumulative filled quantity and average price. WebSocket wake-ups and REST polls may repeat
 * or arrive out of order, so each order's last seen cumulative quantity is kept and only an increase becomes
 * a fill: the increment is booked at the incremental average price
 * (filled × avg − previous filled × previous avg) / increment. A terminal state is reported once. Each step
 * carries the snapshot's Message ("toss:{...}" / "kis:{...}"), the cumulative state stockapp's ledger books;
 * LEAN's own portfolio only sees the increments.
 */

using System.Collections.Concurrent;

namespace MyTrading.Stockapp
{
    public enum TrackedStatus { None, Accepted, Partial, Filled, Canceled, Rejected }

    public class FillStep
    {
        public decimal FillQty;        // the increment (positive), 0 if none
        public decimal FillPrice;      // price of the increment
        public TrackedStatus Status;   // LEAN status to report; None: nothing to report
        public string Message;         // the broker's cumulative state, for the ledger
        public bool Terminal;
    }

    /// <summary>One observation of an order, broker-neutral.</summary>
    public class OrderSnapshot
    {
        public bool Found;
        public string OrderId;
        public string State;           // ACCEPTED | PARTIAL | FILLED | CANCELED | REJECTED | EXPIRED
        public decimal FilledQty;
        public decimal AvgPrice;
        public string Message;

        public bool Terminal => State == "FILLED" || State == "CANCELED" || State == "REJECTED" || State == "EXPIRED";
    }

    public class CumulativeFills
    {
        private class Entry { public decimal Filled; public decimal Avg; public bool Terminal; }
        private readonly ConcurrentDictionary<string, Entry> _orders = new ConcurrentDictionary<string, Entry>();

        public void Track(string orderId, decimal filled = 0m, decimal avg = 0m) =>
            _orders.TryAdd(orderId, new Entry { Filled = filled, Avg = avg });

        public bool IsTracked(string orderId) => _orders.ContainsKey(orderId);
        public string[] Tracked() { var keys = new string[_orders.Count]; _orders.Keys.CopyTo(keys, 0); return keys; }
        public void Forget(string orderId) => _orders.TryRemove(orderId, out _);

        /// <summary>What to report for one observation of an order; null if nothing changed.</summary>
        public FillStep Observe(OrderSnapshot st)
        {
            if (st == null || !st.Found || st.OrderId == null || !_orders.TryGetValue(st.OrderId, out var s) || s.Terminal)
                return null;
            var step = new FillStep { Message = st.Message };
            if (st.FilledQty > s.Filled)
            {
                step.FillQty = st.FilledQty - s.Filled;
                step.FillPrice = (st.FilledQty * st.AvgPrice - s.Filled * s.Avg) / step.FillQty;
                step.Status = st.State == "FILLED" ? TrackedStatus.Filled : TrackedStatus.Partial;
                s.Filled = st.FilledQty;
                s.Avg = st.AvgPrice;
            }
            if (st.Terminal)
            {
                s.Terminal = true;
                step.Terminal = true;
                if (st.State == "FILLED") step.Status = TrackedStatus.Filled;
                else if (st.State == "REJECTED") step.Status = TrackedStatus.Rejected;
                else if (step.FillQty == 0) step.Status = TrackedStatus.Canceled;  // CANCELED / EXPIRED
                // A last fill and a cancel in one observation: report the fill now, the cancel next time.
                else s.Terminal = false;
                if (s.Terminal) _orders.TryRemove(st.OrderId, out _);
            }
            return step.Status == TrackedStatus.None ? null : step;
        }
    }
}
