/*
 * stockapp live settings for the KIS adapter (stockapp KIS spec §5.2). The adapter runs only under stockapp:
 * the backend issues the access token (token file) and the WebSocket approval key, and writes the app key and
 * secret, which KIS wants on every REST call, to a 0600 secrets file next to the token. Holdings, cash and open
 * orders come from the strategy ledger's view, never from the raw account.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MyTrading.Kis
{
    public class KisSettings
    {
        public string Env = "demo";              // real | demo
        public string BaseUrl;                   // null: the env's KIS host
        public string WsUrl;
        public string Proxy = "";                // "socks5://127.0.0.1:1080", or ""/"direct"
        public string TokenFile = "";
        public string SecretsFile = "";
        public string LedgerView = "";
        public string AccountNo = "";            // 12345678-01
        public string HtsId = "";                // the fill-notice tr_key
        public bool TradingEnabled = false;      // false: PlaceOrder refuses everything (alert-only)
        public decimal MaxOrderKrw = 0m;         // 0 refuses every order (fail closed)
        public decimal UsdKrw = 0m;              // quoted for USDKRW once a minute (the algorithm subscribes it)
        public TimeSpan FeedDownAfter = TimeSpan.FromMinutes(3);

        public bool Demo => KisConstants.IsDemo(Env);
        public string Cano => (AccountNo ?? "").Split('-')[0];
        public string Product => (AccountNo ?? "").Contains("-") ? AccountNo.Split('-')[1] : "01";
        public string RestUrl => string.IsNullOrEmpty(BaseUrl) ? KisConstants.RestUrl(Env) : BaseUrl.TrimEnd('/');
        public string SocketUrl => string.IsNullOrEmpty(WsUrl) ? KisConstants.WsUrl(Env) + "/tryitout" : WsUrl;

        public static KisSettings FromBrokerageData(IReadOnlyDictionary<string, string> data)
        {
            string Read(string k, string dflt = "") => data.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : dflt;
            decimal Dec(string k) => decimal.TryParse(Read(k, "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
            return new KisSettings
            {
                Env = Read("kis-env", "demo"),
                BaseUrl = Read("kis-base-url", null),
                WsUrl = Read("kis-ws-url", null),
                Proxy = Read("kis-proxy"),
                TokenFile = Read("kis-token-file"),
                SecretsFile = Read("kis-secrets-file"),
                LedgerView = Read("kis-ledger-view"),
                AccountNo = Read("kis-account-no"),
                HtsId = Read("kis-hts-id"),
                TradingEnabled = Read("kis-trading-enabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                MaxOrderKrw = Dec("kis-max-order-krw"),
                UsdKrw = Dec("kis-usdkrw"),
            };
        }
    }
}
