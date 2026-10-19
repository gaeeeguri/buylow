/*
 * stockapp: Toss's cumulative order state as LEAN fill events (stockapp spec §7.4). The logic is the shared
 * CumulativeFills (MyTrading.Stockapp); this maps a TossOrderStatus onto it and keeps the "toss:{...}"
 * message stockapp's ledger books.
 */

using System.Globalization;
using MyTrading.Stockapp;
using Newtonsoft.Json.Linq;

namespace MyTrading.Toss
{
    public class FillTracker
    {
        private readonly CumulativeFills _fills = new CumulativeFills();

        public void Track(string orderId, decimal filled = 0m, decimal avg = 0m) => _fills.Track(orderId, filled, avg);
        public bool IsTracked(string orderId) => _fills.IsTracked(orderId);
        public string[] Tracked() => _fills.Tracked();
        public void Forget(string orderId) => _fills.Forget(orderId);

        public static string Describe(TossOrderStatus st) => "toss:" + new JObject
        {
            ["status"] = st.Status,
            ["orderId"] = st.OrderId,
            ["filled"] = st.FilledQty.ToString(CultureInfo.InvariantCulture),
            ["avg"] = st.AvgPrice.ToString(CultureInfo.InvariantCulture),
            ["fees"] = (st.Commission + st.Tax).ToString(CultureInfo.InvariantCulture),
        }.ToString(Newtonsoft.Json.Formatting.None);

        public static OrderSnapshot Snapshot(TossOrderStatus st) => new OrderSnapshot
        {
            Found = st.Found, OrderId = st.OrderId, State = StateOf(st), FilledQty = st.FilledQty,
            AvgPrice = st.AvgPrice, Message = Describe(st),
        };

        private static string StateOf(TossOrderStatus st)
        {
            var status = (st.Status ?? "").ToUpperInvariant();
            if (status == "FILLED") return "FILLED";
            if (status == "REJECTED") return "REJECTED";
            if (st.IsTerminal()) return "CANCELED";  // CANCELED / CANCELLED / EXPIRED / REPLACED: no more fills
            return st.FilledQty > 0 ? "PARTIAL" : "ACCEPTED";
        }

        /// <summary>What to report for one observation of an order; null if nothing changed.</summary>
        public FillStep Observe(TossOrderStatus st) => st == null ? null : _fills.Observe(Snapshot(st));
    }
}
