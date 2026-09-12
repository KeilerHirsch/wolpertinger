using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.Tests.Contracts;

public sealed class PresentationSnapshotTests
{
    [Fact]
    public void ProtocolBindingsAreFrozen()
    {
        Assert.Equal(2, PresentationProtocol.Version);
        Assert.Equal(65_536, PresentationProtocol.MaximumFrameBytes);
        Assert.Equal("wolpertinger.presentation.v2", PresentationProtocol.DefaultPipeName);
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PresentationRealm)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PresentationProvenance)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PresentationFreshness)));
        Assert.Equal(new[] { "Unknown", "Live", "Legacy", "BetaOrPts" }, Enum.GetNames<PresentationRealm>());
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, Enum.GetValues<PresentationRealm>().Select(value => (byte)value));
        Assert.Equal(new[] { "Unknown", "LocalJournal", "LocalStatus", "FrontierApi", "Community", "UserEntered", "Sample" }, Enum.GetNames<PresentationProvenance>());
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6 }, Enum.GetValues<PresentationProvenance>().Select(value => (byte)value));
        Assert.Equal(new[] { "Unknown", "Current", "Stale", "Conflicting" }, Enum.GetNames<PresentationFreshness>());
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, Enum.GetValues<PresentationFreshness>().Select(value => (byte)value));
    }

    [Fact]
    public void InitialSnapshotIsEmptyAtRevisionZero()
    {
        Assert.Equal(2, PresentationSnapshot.Empty.ProtocolVersion);
        Assert.Equal(0UL, PresentationSnapshot.Empty.Revision);
        Assert.Null(PresentationSnapshot.Empty.Jump);
        PresentationSnapshotValidator.Validate(PresentationSnapshot.Empty);
    }

    [Fact]
    public void ValidJumpSnapshotPassesValidation()
    {
        PresentationSnapshotValidator.Validate(TestSnapshots.Jump(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void UnsupportedProtocolFailsClosed(int version)
    {
        Assert.Throws<InvalidDataException>(() =>
            PresentationSnapshotValidator.Validate(PresentationSnapshot.Empty with { ProtocolVersion = version }));
    }

    [Fact]
    public void JumpAtRevisionZeroFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(TestSnapshots.Jump(0)));
    }

    [Fact]
    public void JumpWithNonHexStateDigestFailsClosed()
    {
        var snapshot = TestSnapshots.Jump(1);
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(
            snapshot with { Jump = snapshot.Jump! with { StateDigestHex = "NOPE" } }));
    }

    public static IEnumerable<object[]> InvalidDigests()
    {
        foreach (var field in new[] { "Evidence", "State" })
            foreach (var value in new string?[] { null, "", " ", new('a', 63), new('a', 65), new('g', 64), new('é', 64) })
                yield return new object[] { field, value! };
    }

    [Theory]
    [MemberData(nameof(InvalidDigests))]
    public void InvalidDigestFailsClosed(string field, string value)
    {
        var snapshot = TestSnapshots.Jump(1);
        var jump = snapshot.Jump!;
        jump = field == "Evidence" ? jump with { EvidenceDigestHex = value } : jump with { StateDigestHex = value };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot with { Jump = jump }));
    }

    public static IEnumerable<object[]> BlankFields()
    {
        foreach (var field in new[] { "Fid", "StarSystem", "ReasonCode", "PositionX", "PositionY", "PositionZ", "JumpDistance", "FuelUsed", "FuelLevel" })
            foreach (var value in new string?[] { null, "", " \t\r\n" })
                yield return new object[] { field, value! };
    }

    [Theory]
    [MemberData(nameof(BlankFields))]
    public void BlankMandatoryFieldFailsClosed(string field, string value)
    {
        var snapshot = TestSnapshots.Jump(1);
        var jump = snapshot.Jump!;
        jump = field switch
        {
            "Fid" => jump with { Profile = jump.Profile with { Fid = value } },
            "StarSystem" => jump with { StarSystem = value },
            "ReasonCode" => jump with { ReasonCode = value },
            "PositionX" => jump with { PositionX = value },
            "PositionY" => jump with { PositionY = value },
            "PositionZ" => jump with { PositionZ = value },
            "JumpDistance" => jump with { JumpDistance = value },
            "FuelUsed" => jump with { FuelUsed = value },
            "FuelLevel" => jump with { FuelLevel = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot with { Jump = jump }));
    }

    [Theory]
    [InlineData(-1L, 1)]
    [InlineData(1L, -1)]
    [InlineData(long.MinValue, int.MinValue)]
    public void NegativeEvidenceBoundsFailClosed(long offset, int length)
    {
        var snapshot = TestSnapshots.Jump(1);
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot with
        {
            Jump = snapshot.Jump! with { EvidenceReference = new(1, 0, offset, length) }
        }));
    }

    [Theory]
    [InlineData(PresentationFreshness.Unknown)]
    [InlineData(PresentationFreshness.Stale)]
    [InlineData(PresentationFreshness.Conflicting)]
    public void UncertainMetadataIsPreserved(PresentationFreshness freshness)
    {
        var snapshot = TestSnapshots.Jump(1);
        snapshot = snapshot with
        {
            Jump = snapshot.Jump! with
            {
                Profile = new("F123", PresentationRealm.Unknown, 0),
                EvidenceReference = new(0, 0, 0, 0),
                LocationProvenance = PresentationProvenance.Unknown,
                FuelProvenance = PresentationProvenance.Unknown,
                LocationFreshness = freshness,
                FuelFreshness = freshness
            }
        };
        var before = snapshot with { };
        PresentationSnapshotValidator.Validate(snapshot);
        Assert.Equal(before, snapshot);
        Assert.Equal(freshness, snapshot.Jump.FuelFreshness);
        Assert.Equal(PresentationProvenance.Unknown, snapshot.Jump.LocationProvenance);
    }

    [Fact]
    public void MissingProfileFailsClosed()
    {
        var snapshot = TestSnapshots.Jump(1);
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(
            snapshot with { Jump = snapshot.Jump! with { Profile = null! } }));
    }
}

internal static class TestSnapshots
{
    public static PresentationSnapshot Jump(ulong revision) => new(
        PresentationProtocol.Version,
        revision,
        PresentationGameContext.PostJump,
        new PresentationIntent(PresentationComposition.PostJump, true, "ContextPostJump", PresentationSelectionMode.Auto),
        new JumpPresentation(new(1, 0), new("F123", PresentationRealm.Live, 0),
            new(1, 0, 0, 128), new string('a', 64), new string('B', 64),
            42, "Sol", "0", "-1.5", "2", "3.5", "1", "12",
            PresentationProvenance.LocalJournal, PresentationFreshness.Current,
            PresentationProvenance.LocalStatus, PresentationFreshness.Current, "Accepted"),
        null,
        new RuntimeHealthPresentation(ProductRuntimeHealth.Ready, "Ready"),
        new FrontierAccountPresentation(FrontierAccountState.Disconnected, PresentationFreshness.Unknown, null, "NotConnected"));
}
