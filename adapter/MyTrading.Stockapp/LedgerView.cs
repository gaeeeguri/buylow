/*
 * stockapp: what the adapter reports to LEAN at startup (stockapp spec §7.2): the strategy's ledger
 * positions, ledger cash and its live orders, never the raw account. stockapp's backend writes the
 * view (stockapp.ledger.store.Ledger.view) before it starts LEAN.
 * {"cash": {"KRW": .., "USD": ..},
 *  "positions": [{"symbol","market":"KR"|"US","qty","avg_cost"}],
 *  "open_orders": [{"client_order_id","order_id","symbol","market","side","qty","filled_qty","avg_fill_price",
 *                   "limit_price"}]}
 */

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MyTrading.Stockapp
{
    public class LedgerOrder
    {
        public string ClientOrderId;
        public string OrderId;
        public string Symbol;
        public string Market;   // KR | US
        public bool Buy;
        public decimal Qty;
        public decimal FilledQty;
        public decimal AvgFillPrice;
        public decimal LimitPrice;
    }

    public class LedgerView
    {
        public Dictionary<string, decimal> Cash = new Dictionary<string, decimal>();
        public List<(string symbol, string market, decimal qty, decimal avgCost)> Positions = new List<(string, string, decimal, decimal)>();
        public List<LedgerOrder> OpenOrders = new List<LedgerOrder>();

        public static LedgerView Parse(string json)
        {
            var doc = JObject.Parse(json);
            var view = new LedgerView();
            foreach (var p in (doc["cash"] as JObject) ?? new JObject())
                view.Cash[p.Key] = p.Value.Value<decimal>();
            foreach (var p in (doc["positions"] as JArray) ?? new JArray())
                if (p.Value<decimal>("qty") != 0)
                    view.Positions.Add((p.Value<string>("symbol"), p.Value<string>("market"), p.Value<decimal>("qty"),
                                        p.Value<decimal>("avg_cost")));
            foreach (var o in (doc["open_orders"] as JArray) ?? new JArray())
                view.OpenOrders.Add(new LedgerOrder
                {
                    ClientOrderId = o.Value<string>("client_order_id"),
                    OrderId = o.Value<string>("order_id"),
                    Symbol = o.Value<string>("symbol"),
                    Market = o.Value<string>("market"),
                    Buy = o.Value<string>("side") == "BUY",
                    Qty = o.Value<decimal>("qty"),
                    FilledQty = o.Value<decimal>("filled_qty"),
                    AvgFillPrice = o.Value<decimal?>("avg_fill_price") ?? 0m,
                    LimitPrice = o.Value<decimal>("limit_price"),
                });
            return view;
        }

        public static LedgerView Load(string path) => Parse(File.ReadAllText(path));
    }
}
