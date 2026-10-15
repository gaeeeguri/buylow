/*
 * TossBrokerageFactory — LEAN Composer가 라이브 토스 브로커리지를 생성하는 팩토리.
 *
 * config.json의 live-mode-brokerage="TossBrokerage"일 때 BrokerageSetupHandler가 이 팩토리를 찾아
 * BrokerageData(아래 키들, LeanRunner가 live config에 주입)로 TossBrokerage를 만든다.
 *
 * BrokerageData 키(=LeanRunner build_toss_live_config가 채움):
 *   toss-client-id / toss-client-secret /
 *   toss-max-order-amount(원, 선택 한도) / toss-token-cache(토큰 디스크캐시 경로, 선택)
 * (KIS와 달리 계좌번호·HTS ID·env 키가 없다 — accountSeq 자동해석, 폴링 체결, 실전 단일.)
 *
 * stockapp keys (TossSettings): toss-token-file, toss-ledger-view, toss-base-url, toss-ws-url,
 * toss-proxy, toss-trading-enabled, toss-max-order-krw, toss-max-order-usd, toss-usdkrw. stockapp
 * never passes a client secret: the token file is required.
 */

using System.Collections.Generic;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace MyTrading.Toss
{
    public class TossBrokerageFactory : BrokerageFactory
    {
        public TossBrokerageFactory() : base(typeof(TossBrokerage)) { }

        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            { "toss-client-id", Config.Get("toss-client-id") },
            { "toss-client-secret", Config.Get("toss-client-secret") },
            { "toss-max-order-amount", Config.Get("toss-max-order-amount", "0") },
            { "toss-token-cache", Config.Get("toss-token-cache") },
            { "toss-token-file", Config.Get("toss-token-file") },
            { "toss-ledger-view", Config.Get("toss-ledger-view") },
            { "toss-base-url", Config.Get("toss-base-url") },
            { "toss-ws-url", Config.Get("toss-ws-url") },
            { "toss-proxy", Config.Get("toss-proxy") },
            { "toss-trading-enabled", Config.Get("toss-trading-enabled", "false") },
            { "toss-max-order-krw", Config.Get("toss-max-order-krw", "0") },
            { "toss-max-order-usd", Config.Get("toss-max-order-usd", "0") },
            { "toss-usdkrw", Config.Get("toss-usdkrw", "0") },
        };

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new TossBrokerageModel();

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var settings = TossSettings.FromBrokerageData(job.BrokerageData);
            if (!settings.Stockapp)
                throw new TossException("toss-token-file is required: this adapter runs under stockapp, which issues tokens");
            if (string.IsNullOrEmpty(settings.LedgerView))
                throw new TossException("toss-ledger-view is required: holdings and cash come from the strategy ledger");
            var brokerage = new TossBrokerage(algorithm, settings);
            // The same instance is LEAN's data queue handler (prices and orders share one session).
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);
            return brokerage;
        }

        public override void Dispose() { /* NOP */ }
    }
}
