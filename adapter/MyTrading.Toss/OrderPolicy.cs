/*
 * stockapp: how a LEAN limit order becomes a Toss order, and the adapter's stateless guardrails
 * (stockapp spec §6.6, §7.4, §8.4).
 *
 * - The clientOrderId is the LEAN order's tag, which stockapp's algorithm sets to the id it
 *   persisted in the ledger before placing the order. Anything else is refused.
 * - KR orders are OPG LIMIT (opening auction, sent 08:00–08:50 KST); US orders are DAY LIMIT.
 *   KR prices are whole won; US prices have 2 decimals (4 below $1).
 * - Guardrails here need no state: trading enabled, the per-order amount, and KR prices inside
 *   Toss's daily limits. Per-cycle totals, weights and loss/error halts are checked by stockapp's
 *   algorithm from the ledger, before the order reaches LEAN.
 */

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MyTrading.Toss
{
    public static class OrderPolicy
    {
        private static readonly Regex ClientOrderId = new Regex("^[0-9a-z]{6}-[0-9a-z]{6}-[0-9a-z]{3}-[0-9a-z]{2}$");

        public static bool IsClientOrderId(string tag) => tag != null && ClientOrderId.IsMatch(tag);

        public static string FormatPrice(string market, decimal price)
        {
            if (market == "KR") return decimal.Round(price, 0).ToString("0", CultureInfo.InvariantCulture);
            return price.ToString(price >= 1m ? "0.00" : "0.0000", CultureInfo.InvariantCulture);
        }

        public static string Body(string clientOrderId, string symbol, string market, bool buy, decimal qty, decimal limit)
        {
            return new JObject
            {
                ["clientOrderId"] = clientOrderId,
                ["symbol"] = symbol,
                ["side"] = buy ? "BUY" : "SELL",
                ["orderType"] = "LIMIT",
                ["timeInForce"] = market == "KR" ? "OPG" : "DAY",
                ["quantity"] = decimal.Truncate(qty).ToString(CultureInfo.InvariantCulture),
                ["price"] = FormatPrice(market, limit),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>The first stateless guardrail the order breaks, or null.</summary>
        public static string Check(TossSettings s, string tag, string market, decimal qty, decimal limit,
                                   (decimal lower, decimal upper)? priceLimits)
        {
            if (!s.TradingEnabled) return "trading is disabled for this run (alert-only)";
            if (!IsClientOrderId(tag)) return "the order carries no stockapp client order id";
            if (qty <= 0 || limit <= 0) return "quantity and limit price must be positive";
            // Fail closed: a missing cap or unknown KR limits refuse the order rather than skip the check.
            var max = market == "KR" ? s.MaxOrderKrw : s.MaxOrderUsd;
            if (max <= 0) return $"no max order amount is set for {market} (toss-max-order-{(market == "KR" ? "krw" : "usd")})";
            var amount = qty * limit;
            if (amount > max)
                return $"max order amount: {amount.ToString(CultureInfo.InvariantCulture)} > {max.ToString(CultureInfo.InvariantCulture)}";
            if (market == "KR" && !priceLimits.HasValue) return "today's price limits are unknown (Toss /price-limits gave none)";
            if (priceLimits.HasValue && (limit < priceLimits.Value.lower || limit > priceLimits.Value.upper))
                return $"price {limit.ToString(CultureInfo.InvariantCulture)} is outside today's limits " +
                       $"{priceLimits.Value.lower.ToString(CultureInfo.InvariantCulture)}–{priceLimits.Value.upper.ToString(CultureInfo.InvariantCulture)}";
            return null;
        }
    }
}
