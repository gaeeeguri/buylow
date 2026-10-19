/*
 * stockapp: a KIS order's id as stockapp's ledger keeps it, "{ORD_DT}:{branch}:{ODNO}". KIS numbers orders
 * per trading day, and a cancel names the branch that forwarded the order (KRX_FWDG_ORD_ORGNO).
 *
 * order-cash and inquire-daily-ccld need not pad the numbers alike (ODNO "117057" vs odno "0000117057"), so
 * every id is canonical: the branch zero-padded to 5 digits and ODNO to 10, whatever padding KIS sent. The
 * same rule is stockapp's `order_id` (stockapp/kis/account.py), so ids from both endpoints are byte-identical.
 */

using System;

namespace MyTrading.Kis
{
    public static class KisOrderId
    {
        public const int BranchDigits = 5, OdnoDigits = 10;

        /// <summary>All digits: leading zeros dropped, then padded to width. Anything else: trimmed, unchanged.</summary>
        public static string Pad(string value, int width)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return v;
            foreach (var c in v) if (c < '0' || c > '9') return v;
            return v.TrimStart('0').PadLeft(width, '0');
        }

        public static string Format(string day, string branch, string odno) =>
            $"{day}:{Pad(branch, BranchDigits)}:{Pad(odno, OdnoDigits)}";

        public static bool TryParse(string id, out string day, out string branch, out string odno)
        {
            day = branch = odno = null;
            var parts = (id ?? "").Split(':');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
            (day, branch, odno) = (parts[0], parts[1], parts[2]);
            return true;
        }

        /// <summary>The canonical form of an id; an unparsable one is returned as is.</summary>
        public static string Canonical(string id) =>
            TryParse(id, out var day, out var branch, out var odno) ? Format(day, branch, odno) : id;

        public static bool Same(string a, string b) => a != null && b != null && string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
    }
}
