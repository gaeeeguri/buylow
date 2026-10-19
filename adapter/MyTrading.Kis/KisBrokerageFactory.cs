/*
 * KisBrokerageFactory — LEAN Composer's factory for the KIS live brokerage, stockapp mode only
 * (stockapp KIS spec §5.2). stockapp's backend writes these keys into the live config (KisBroker.lean_brokerage).
 * It never passes the app secret in the config: it is in the 0600 secrets file the config names.
 */

using System.Collections.Generic;
using System.Text.RegularExpressions;
using MyTrading.Stockapp;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace MyTrading.Kis
{
    public class KisBrokerageFactory : BrokerageFactory
    {
        private static readonly string[] Keys =
        {
            "kis-env", "kis-token-file", "kis-secrets-file", "kis-ledger-view", "kis-account-no", "kis-hts-id",
            "kis-trading-enabled", "kis-max-order-krw", "kis-usdkrw", "kis-proxy", "kis-base-url", "kis-ws-url",
        };

        public KisBrokerageFactory() : base(typeof(KisBrokerage)) { }

        public override Dictionary<string, string> BrokerageData
        {
            get
            {
                var data = new Dictionary<string, string>();
                foreach (var k in Keys) data[k] = Config.Get(k);
                return data;
            }
        }

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new KisBrokerageModel();

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var s = KisSettings.FromBrokerageData(job.BrokerageData);
            if (string.IsNullOrEmpty(s.TokenFile) || string.IsNullOrEmpty(s.SecretsFile))
                throw new StockappException("kis-token-file and kis-secrets-file are required: this adapter runs under stockapp, which issues tokens");
            if (string.IsNullOrEmpty(s.LedgerView))
                throw new StockappException("kis-ledger-view is required: holdings and cash come from the strategy ledger");
            if (!Regex.IsMatch(s.AccountNo ?? "", "^[0-9]{8}-[0-9]{2}$") || string.IsNullOrEmpty(s.HtsId))
                throw new StockappException("kis-account-no (12345678-01) and kis-hts-id are required");
            var brokerage = new KisBrokerage(algorithm, s);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);  // prices and orders share one session
            return brokerage;
        }

        public override void Dispose() { }
    }
}
