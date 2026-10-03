using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.Tests.Contracts;

public sealed class PresentationV2Tests
{
    [Fact]
    public void ProtocolAndEnumsAreFrozenForV2()
    {
        Assert.Equal(2, PresentationProtocol.Version);
        Assert.Equal("wolpertinger.presentation.v2", PresentationProtocol.DefaultPipeName);
        Assert.Equal(6, (byte)PresentationProvenance.Sample);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6 },
            Enum.GetValues<PresentationProvenance>().Select(value => (byte)value));
        Assert.Equal(11, (byte)PresentationGameContext.Unknown);
        Assert.Equal(8, (byte)PresentationComposition.Unknown);
        Assert.Equal(1, (byte)PresentationSelectionMode.Manual);
    }

    [Fact]
    public void EmptySnapshotHasHonestRequiredV2State()
    {
        var snapshot = PresentationSnapshot.Empty;
        Assert.Equal(0UL, snapshot.Revision);
        Assert.Equal(PresentationGameContext.InactiveOrNoGame, snapshot.Context);
        Assert.Equal(PresentationComposition.Quiet, snapshot.Intent.Composition);
        Assert.Equal(PresentationSelectionMode.Auto, snapshot.Intent.SelectionMode);
        Assert.Null(snapshot.Jump);
        Assert.Null(snapshot.CommanderVessel);
        Assert.Equal(ProductRuntimeHealth.Starting, snapshot.RuntimeHealth.Health);
        Assert.Equal(FrontierAccountState.Disconnected, snapshot.FrontierAccount.State);
        Assert.Equal(PresentationFreshness.Unknown, snapshot.FrontierAccount.Freshness);
        PresentationSnapshotValidator.Validate(snapshot);
    }

    [Fact]
    public void ProtocolV1FailsClosedUnderV2Validator()
    {
        Assert.Throws<InvalidDataException>(() =>
            PresentationSnapshotValidator.Validate(PresentationSnapshot.Empty with { ProtocolVersion = 1 }));
    }

    [Theory]
    [InlineData(PresentationSelectionMode.Manual, "ContextFlight")]
    [InlineData(PresentationSelectionMode.Auto, "ManualPin")]
    public void SelectionModeAndReasonMustAgree(
        PresentationSelectionMode mode,
        string reason)
    {
        var snapshot = PresentationSnapshot.Empty with
        {
            Intent = new PresentationIntent(PresentationComposition.Flight, false, reason, mode),
        };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot));
    }

    [Fact]
    public void UndefinedRequiredEnumFailsClosed()
    {
        var snapshot = PresentationSnapshot.Empty with { Context = (PresentationGameContext)255 };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot));
    }

    [Fact]
    public void CommanderVesselRequiresValidDigestAndMetadata()
    {
        var snapshot = PresentationSnapshot.Empty with
        {
            Revision = 1,
            CommanderVessel = new CommanderVesselPresentation(
                new PresentationCursor(1, 0),
                new PresentationProfile("F123", PresentationRealm.Live, 0),
                "bad",
                "CMDR Test",
                "Sidewinder",
                null,
                "Sol",
                null,
                PresentationTriState.False,
                PresentationTriState.True,
                PresentationProvenance.LocalJournal,
                PresentationFreshness.Current,
                1),
        };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot));

        snapshot = snapshot with
        {
            CommanderVessel = snapshot.CommanderVessel! with
            {
                StateDigestHex = new string('a', 64),
                Provenance = (PresentationProvenance)255,
            },
        };
        Assert.Throws<InvalidDataException>(() => PresentationSnapshotValidator.Validate(snapshot));
    }
}
