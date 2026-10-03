using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Presentation.Preferences;
using Wolpertinger.Presentation.State;
using Wolpertinger.Presentation.ViewModels;

namespace Wolpertinger.Presentation.Tests.ViewModels;

public sealed class PresentationViewModelFactoryTests
{
    [Fact]
    public void LiveCurrentJumpProducesConciseCommanderGrammar()
    {
        var store = new PresentationStore();
        Assert.True(store.ApplySnapshot(CreateSnapshot(7)));

        var vm = PresentationViewModelFactory.CreateJump(store.State, DensityPreset.Standard);

        Assert.Equal(7UL, vm.Revision);
        Assert.Equal(new PresentationCursor(123, 2), vm.Cursor);
        Assert.Equal("W. Grantler NX-42", vm.StarSystem);
        Assert.Equal("42.125 ly", vm.JumpDistanceText);
        Assert.Equal("2.5 t", vm.FuelUsedText);
        Assert.Equal("14.75 t", vm.FuelLevelText);
        Assert.Equal("Current", vm.LocationStatusText);
        Assert.Equal("Current", vm.FuelStatusText);
        Assert.True(vm.IsLive);
        Assert.Equal("Live", vm.AvailabilityText);
        Assert.Equal(DensityPreset.Standard, vm.Density);
    }

    [Theory]
    [InlineData(PresentationFreshness.Stale, "Stale")]
    [InlineData(PresentationFreshness.Unknown, "Unknown")]
    [InlineData(PresentationFreshness.Conflicting, "Conflicting")]
    public void NonCurrentFreshnessIsExplicitText(PresentationFreshness freshness, string expected)
    {
        var store = new PresentationStore();
        Assert.True(store.ApplySnapshot(CreateSnapshot(7, freshness, freshness)));

        var vm = PresentationViewModelFactory.CreateJump(store.State, DensityPreset.Compact);

        Assert.Equal(expected, vm.LocationStatusText);
        Assert.Equal(expected, vm.FuelStatusText);
    }

    [Fact]
    public void DisconnectedStateKeepsValuesButIsNotLive()
    {
        var store = new PresentationStore();
        Assert.True(store.ApplySnapshot(CreateSnapshot(7)));
        store.MarkDisconnected();

        var vm = PresentationViewModelFactory.CreateJump(store.State, DensityPreset.Expanded);

        Assert.Equal("W. Grantler NX-42", vm.StarSystem);
        Assert.Equal("42.125 ly", vm.JumpDistanceText);
        Assert.False(vm.IsLive);
        Assert.Equal("Disconnected — last known data", vm.AvailabilityText);
    }

    [Fact]
    public void InvalidSnapshotMakesRetainedCommanderContextExplicitlyNonLive()
    {
        var store = new PresentationStore();
        var valid = CreateSnapshot(7);
        Assert.True(store.ApplySnapshot(valid));
        var invalid = CreateSnapshot(8) with { ProtocolVersion = 99 };

        Assert.Throws<InvalidDataException>(() => store.ApplySnapshot(invalid));
        var vm = PresentationViewModelFactory.CreateJump(store.State, DensityPreset.Standard);

        Assert.Equal(valid.Jump!.StarSystem, vm.StarSystem);
        Assert.False(vm.IsLive);
        Assert.Equal("Incompatible — last known data", vm.AvailabilityText);
    }

    [Fact]
    public void CanonicalNumberStringsAreOnlyGivenUnits()
    {
        var snapshot = CreateSnapshot(7);
        snapshot = snapshot with
        {
            Jump = snapshot.Jump! with
            {
                JumpDistance = "1.2300",
                FuelUsed = "0.500",
                FuelLevel = "09.750"
            }
        };
        var store = new PresentationStore();
        Assert.True(store.ApplySnapshot(snapshot));

        var vm = PresentationViewModelFactory.CreateJump(store.State, DensityPreset.Standard);

        Assert.Equal("1.2300 ly", vm.JumpDistanceText);
        Assert.Equal("0.500 t", vm.FuelUsedText);
        Assert.Equal("09.750 t", vm.FuelLevelText);
    }

    [Fact]
    public void DiagnosticsProjectsTheSameSnapshotWithoutASecondSource()
    {
        var store = new PresentationStore();
        var snapshot = CreateSnapshot(7, PresentationFreshness.Stale, PresentationFreshness.Conflicting);
        Assert.True(store.ApplySnapshot(snapshot));

        var vm = PresentationViewModelFactory.CreateDiagnostics(store.State);

        Assert.Equal(snapshot.ProtocolVersion, vm.ProtocolVersion);
        Assert.Equal(snapshot.Revision, vm.Revision);
        Assert.Equal(snapshot.Jump!.Cursor, vm.Cursor);
        Assert.Equal(snapshot.Jump.StarSystem, vm.StarSystem);
        Assert.Equal(snapshot.Jump.SystemAddress, vm.SystemAddress);
        Assert.Equal(snapshot.Jump.EvidenceReference, vm.EvidenceReference);
        Assert.Equal(snapshot.Jump.EvidenceDigestHex, vm.EvidenceDigestHex);
        Assert.Equal(snapshot.Jump.StateDigestHex, vm.StateDigestHex);
        Assert.Equal("LocalJournal", vm.LocationProvenanceText);
        Assert.Equal("Stale", vm.LocationFreshnessText);
        Assert.Equal("LocalStatus", vm.FuelProvenanceText);
        Assert.Equal("Conflicting", vm.FuelFreshnessText);
        Assert.Equal("F123456", vm.Fid);
        Assert.Equal("Live", vm.RealmText);
        Assert.Equal(7UL, vm.SaveEpoch);
        Assert.Equal(PresentationConnectionState.Live, vm.Connection);
    }

    [Fact]
    public void SampleProvenanceRemainsExplicitInDiagnostics()
    {
        var snapshot = CreateSnapshot(8);
        snapshot = snapshot with
        {
            Jump = snapshot.Jump! with
            {
                LocationProvenance = PresentationProvenance.Sample,
                FuelProvenance = PresentationProvenance.Sample,
            },
        };
        var store = new PresentationStore();
        Assert.True(store.ApplySnapshot(snapshot));

        var vm = PresentationViewModelFactory.CreateDiagnostics(store.State);

        Assert.Equal("Sample", vm.LocationProvenanceText);
        Assert.Equal("Sample", vm.FuelProvenanceText);
    }

    [Fact]
    public void PreferenceDefaultsAreBoundedAndExplicit()
    {
        Assert.Equal(new PresentationPreferences(true, false, DockPreset.Right, DensityPreset.Standard, null), PresentationPreferences.Default);
    }

    private static PresentationSnapshot CreateSnapshot(
        ulong revision,
        PresentationFreshness locationFreshness = PresentationFreshness.Current,
        PresentationFreshness fuelFreshness = PresentationFreshness.Current)
    {
        return new PresentationSnapshot(
            PresentationProtocol.Version,
            revision,
            PresentationGameContext.PostJump,
            new PresentationIntent(PresentationComposition.PostJump, true, "ContextPostJump", PresentationSelectionMode.Auto),
            new JumpPresentation(
                new PresentationCursor(123, 2),
                new PresentationProfile("F123456", PresentationRealm.Live, 7),
                new PresentationEvidenceReference(44, 3, 512, 192),
                new string('a', 64),
                new string('B', 64),
                424242,
                "W. Grantler NX-42",
                "1.25",
                "-2.5",
                "3.75",
                "42.125",
                "2.5",
                "14.75",
                PresentationProvenance.LocalJournal,
                locationFreshness,
                PresentationProvenance.LocalStatus,
                fuelFreshness,
                "JumpCompleted"),
            null,
            new RuntimeHealthPresentation(ProductRuntimeHealth.Ready, "Ready"),
            new FrontierAccountPresentation(FrontierAccountState.Disconnected, PresentationFreshness.Unknown, null, "NotConnected"));
    }
}
