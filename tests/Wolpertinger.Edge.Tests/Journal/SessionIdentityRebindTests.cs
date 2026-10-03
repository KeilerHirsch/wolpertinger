using System.Text.Json;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Journal;

namespace Wolpertinger.Edge.Tests.Journal;

public sealed class SessionIdentityRebindTests
{
    [Fact]
    public void NewJournalSessionAdvancesSaveEpoch()
    {
        var tracker = new SessionIdentityTracker();

        using (var header = JsonDocument.Parse(
            """{"event":"Fileheader","gameversion":"4.2.2.0","build":"r1"}"""))
            _ = tracker.Observe(Receipt(0, "01"), header.RootElement);
        using (var commander = JsonDocument.Parse("""{"event":"Commander","FID":"F100"}"""))
        {
            var first = tracker.Observe(Receipt(1, "02"), commander.RootElement);
            Assert.Equal(0UL, first.Profile!.SaveEpoch);
        }

        using (var header = JsonDocument.Parse(
            """{"event":"Fileheader","gameversion":"4.2.2.0","build":"r2"}"""))
            _ = tracker.Observe(Receipt(2, "03"), header.RootElement);
        using (var commander = JsonDocument.Parse("""{"event":"Commander","FID":"F100"}"""))
        {
            var second = tracker.Observe(Receipt(3, "04"), commander.RootElement);
            Assert.Equal(1UL, second.Profile!.SaveEpoch);
            Assert.NotNull(second.Draft);
            Assert.Equal(1UL, second.Draft!.Profile.SaveEpoch);
        }
    }

    [Fact]
    public void RestoreSeedsNextSaveEpoch()
    {
        var tracker = new SessionIdentityTracker();
        tracker.Restore(new SessionBinding(
            FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"),
            new ProfileKey("F100", GalaxyRealm.Live, 7)));

        using (var header = JsonDocument.Parse(
            """{"event":"Fileheader","gameversion":"4.2.2.0","build":"r8"}"""))
            _ = tracker.Observe(Receipt(8, "08"), header.RootElement);
        using var commander = JsonDocument.Parse("""{"event":"Commander","FID":"F100"}""");

        var rebound = tracker.Observe(Receipt(9, "09"), commander.RootElement);

        Assert.Equal(8UL, rebound.Profile!.SaveEpoch);
    }

    private static RawEvidenceReceipt Receipt(ulong ordinal, string pair)
        => new(
            new EvidenceReference(ordinal, 0, checked((long)ordinal * 100), 100),
            RawEvidenceSourceKind.LocalJournal,
            FixedBytes32.FromHex(string.Concat(Enumerable.Repeat(pair, 32))),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + (long)ordinal),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500 + (long)ordinal),
            IsDurable: true);
}
