/*
 * stockapp: the shared core (MyTrading.Stockapp) as a second adapter will use it: broker-neutral
 * snapshots, and guardrail messages that name the adapter's own config key and limits source.
 */

using MyTrading.Stockapp;
using Xunit;

namespace MyTrading.Toss.Tests
{
    public class SharedCoreTests
    {
        private static OrderSnapshot Snap(string state, decimal filled, decimal avg) =>
            new OrderSnapshot { Found = true, OrderId = "D:B:1", State = state, FilledQty = filled, AvgPrice = avg, Message = "kis:{}" };

        [Fact]
        public void Cumulative_snapshots_become_increments_and_one_terminal_event()
        {
            var f = new CumulativeFills();
            f.Track("D:B:1");
            var a = f.Observe(Snap("PARTIAL", 4, 100m));
            Assert.Equal((4m, 100m, TrackedStatus.Partial, "kis:{}"), (a.FillQty, a.FillPrice, a.Status, a.Message));
            Assert.Null(f.Observe(Snap("PARTIAL", 4, 100m)));                 // a repeat is harmless
            var b = f.Observe(Snap("FILLED", 10, 101.2m));
            Assert.Equal((6m, 102m, TrackedStatus.Filled), (b.FillQty, b.FillPrice, b.Status));
            Assert.False(f.IsTracked("D:B:1"));
            Assert.Null(f.Observe(Snap("FILLED", 10, 101.2m)));
        }

        [Fact]
        public void Expired_without_fills_is_a_cancel()
        {
            var f = new CumulativeFills();
            f.Track("D:B:1");
            Assert.Equal(TrackedStatus.Canceled, f.Observe(Snap("EXPIRED", 0, 0)).Status);
        }

        [Fact]
        public void Guardrail_messages_name_the_adapters_key_and_source()
        {
            const string tag = "000007-0000ya-001-01";
            Assert.Equal("no max order amount is set for KR (kis-max-order-krw)",
                Guardrails.Check(true, 0m, "kis-max-order-krw", tag, "KR", 1, 70000m, (49000m, 91000m), "KIS inquire-price"));
            Assert.Equal("today's price limits are unknown (KIS inquire-price gave none)",
                Guardrails.Check(true, 1_000_000m, "kis-max-order-krw", tag, "KR", 1, 70000m, null, "KIS inquire-price"));
            Assert.Null(Guardrails.Check(true, 1_000_000m, "kis-max-order-krw", tag, "KR", 1, 70000m, (49000m, 91000m), "KIS inquire-price"));
            Assert.Equal("trading is disabled for this run (alert-only)",
                Guardrails.Check(false, 1_000_000m, "k", tag, "KR", 1, 70000m, (49000m, 91000m), "s"));
        }
    }
}
