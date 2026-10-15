/*
 * stockapp live settings for the Toss adapter (stockapp spec §3.2, §7.2, §8.4, §10).
 *
 * stockapp runs this adapter with a token file instead of a client secret: its backend is the
 * only token issuer, and LEAN reads the current access token from a private 0600 file. Holdings,
 * cash and open orders come from the strategy ledger (a JSON view the backend writes before
 * launch), never from the raw account. Without `toss-token-file` the adapter keeps buylow's
 * client-credentials behaviour.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MyTrading.Toss
{
    public class TossSettings
    {
        public string BaseUrl = TossConstants.RestUrl;
        public string WsUrl = TossConstants.WsUrl;
        public string Proxy = "";            // "socks5://127.0.0.1:1080", or ""/"direct" for none
        public string TokenFile = "";        // stockapp: the backend's token file
        public string LedgerView = "";       // stockapp: ledger positions, cash and open orders (JSON)
        public bool TradingEnabled = false;  // false: PlaceOrder refuses everything (alert-only)
        public decimal MaxOrderKrw = 0m;     // 0: off (buylow); stockapp always sets both
        public decimal MaxOrderUsd = 0m;
        public decimal UsdKrw = 0m;          // rate LEAN's cash book uses to value USD (not a trading input)
        public TimeSpan UnknownAfter = TimeSpan.FromMinutes(8);  // stop resending the same clientOrderId
        public TimeSpan FeedDownAfter = TimeSpan.FromMinutes(3);

        public bool Stockapp => !string.IsNullOrEmpty(TokenFile);

        public static TossSettings FromBrokerageData(IReadOnlyDictionary<string, string> data)
        {
            string Read(string k, string dflt = "") => data.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : dflt;
            decimal Dec(string k) => decimal.TryParse(Read(k, "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
            var s = new TossSettings
            {
                BaseUrl = Read("toss-base-url", TossConstants.RestUrl).TrimEnd('/'),
                WsUrl = Read("toss-ws-url", TossConstants.WsUrl),
                Proxy = Read("toss-proxy"),
                TokenFile = Read("toss-token-file"),
                LedgerView = Read("toss-ledger-view"),
                TradingEnabled = Read("toss-trading-enabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                MaxOrderKrw = Dec("toss-max-order-krw"),
                MaxOrderUsd = Dec("toss-max-order-usd"),
                UsdKrw = Dec("toss-usdkrw"),
            };
            if (s.MaxOrderKrw == 0m) s.MaxOrderKrw = Dec("toss-max-order-amount");  // buylow's key
            return s;
        }
    }
}
