/*
 * stockapp: the adapters' stateless guardrails (stockapp spec §8.4). Per-cycle totals, weights and loss/error
 * halts are checked by stockapp's algorithm from the ledger before an order reaches LEAN; these need no state:
 * trading enabled, a stockapp client order id as the tag, the per-order amount, and KR prices inside today's
 * limits. They fail closed: no cap, or unknown KR limits, refuse the order rather than skip the check.
 */

using System.Globalization;
using System.Text.RegularExpressions;

namespace MyTrading.Stockapp
{
    public static class Guardrails
    {
        private static readonly Regex ClientOrderId = new Regex("^[0-9a-z]{6}-[0-9a-z]{6}-[0-9a-z]{3}-[0-9a-z]{2}$");

        public static bool IsClientOrderId(string tag) => tag != null && ClientOrderId.IsMatch(tag);

        /// <summary>The first guardrail the order breaks, or null. `capKey` and `limitsSource` name the adapter's
        /// config key and where KR limits come from, for the message.</summary>
        public static string Check(bool tradingEnabled, decimal maxOrder, string capKey, string tag, string market,
                                   decimal qty, decimal limit, (decimal lower, decimal upper)? priceLimits, string limitsSource)
        {
            if (!tradingEnabled) return "trading is disabled for this run (alert-only)";
            if (!IsClientOrderId(tag)) return "the order carries no stockapp client order id";
            if (qty <= 0 || limit <= 0) return "quantity and limit price must be positive";
            if (maxOrder <= 0) return $"no max order amount is set for {market} ({capKey})";
            var amount = qty * limit;
            if (amount > maxOrder)
                return $"max order amount: {amount.ToString(CultureInfo.InvariantCulture)} > {maxOrder.ToString(CultureInfo.InvariantCulture)}";
            if (market == "KR" && !priceLimits.HasValue) return $"today's price limits are unknown ({limitsSource} gave none)";
            if (priceLimits.HasValue && (limit < priceLimits.Value.lower || limit > priceLimits.Value.upper))
                return $"price {limit.ToString(CultureInfo.InvariantCulture)} is outside today's limits " +
                       $"{priceLimits.Value.lower.ToString(CultureInfo.InvariantCulture)}–{priceLimits.Value.upper.ToString(CultureInfo.InvariantCulture)}";
            return null;
        }
    }
}
