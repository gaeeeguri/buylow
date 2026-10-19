/*
 * stockapp: a KIS order's id as stockapp's ledger keeps it, "{ORD_DT}:{branch}:{ODNO}". KIS numbers orders
 * per trading day, and a cancel names the branch that forwarded the order (KRX_FWDG_ORD_ORGNO).
 */

namespace MyTrading.Kis
{
    public static class KisOrderId
    {
        public static string Format(string day, string branch, string odno) => $"{day}:{branch}:{odno}";

        public static bool TryParse(string id, out string day, out string branch, out string odno)
        {
            day = branch = odno = null;
            var parts = (id ?? "").Split(':');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
            (day, branch, odno) = (parts[0], parts[1], parts[2]);
            return true;
        }
    }
}
