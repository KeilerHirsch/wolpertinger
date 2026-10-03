using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Presentation.State;
using Wolpertinger.Presentation.ViewModels;

namespace Wolpertinger.Presentation.Tests.ViewModels;

public sealed class SmartHubViewModelTests
{
    [Fact]
    public void CurrentLocalSystemWinsWhileCommanderVesselStillSurfaces()
    {
        var snapshot = PresentationSnapshot.Empty with
        {
            Revision = 4,
            Context = PresentationGameContext.Flight,
            Intent = new PresentationIntent(
                PresentationComposition.Flight,
                false,
                "ContextFlight",
                PresentationSelectionMode.Auto),
            Jump = Jump("Local System", PresentationProvenance.LocalJournal),
            CommanderVessel = Commander("Remote System"),
            RuntimeHealth = new RuntimeHealthPresentation(
                ProductRuntimeHealth.Ready,
                "Ready"),
            FrontierAccount = new FrontierAccountPresentation(
                FrontierAccountState.Connected,
                PresentationFreshness.Current,
                1234,
                "ProfileCurrent"),
        };

        var model = SmartHubViewModel.Create(new PresentationStoreState(
            PresentationConnectionState.Live,
            snapshot));

        Assert.Equal("CMDR TEST", model.CommanderName);
        Assert.Equal("KRAIT (Krait Mk II)", model.ShipText);
        Assert.Equal("Local System", model.SystemText);
        Assert.Equal("LocalJournal", model.SystemSourceText);
        Assert.Equal("Flight", model.CompositionText);
    }

    [Fact]
    public void CurrentFrontierSystemFillsMissingLocalSystem()
    {
        var snapshot = PresentationSnapshot.Empty with
        {
            Revision = 2,
            CommanderVessel = Commander("CAPI System"),
        };

        var model = SmartHubViewModel.Create(new PresentationStoreState(
            PresentationConnectionState.Live,
            snapshot));

        Assert.Equal("CAPI System", model.SystemText);
        Assert.Equal("FrontierApi", model.SystemSourceText);
        Assert.Equal("Current", model.SystemFreshnessText);
    }

    private static CommanderVesselPresentation Commander(string system)
        => new(
            new PresentationCursor(2, 0),
            new PresentationProfile("FTEST", PresentationRealm.Live, 1),
            new string('A', 64),
            "CMDR TEST",
            "Krait Mk II",
            "KRAIT",
            system,
            "Jameson Memorial",
            PresentationTriState.False,
            PresentationTriState.True,
            PresentationProvenance.FrontierApi,
            PresentationFreshness.Current,
            1234);

    private static JumpPresentation Jump(
        string system,
        PresentationProvenance provenance)
        => new(
            new PresentationCursor(1, 0),
            new PresentationProfile("FTEST", PresentationRealm.Live, 1),
            new PresentationEvidenceReference(1, 0, 0, 1),
            new string('B', 64),
            new string('C', 64),
            1,
            system,
            "1",
            "2",
            "3",
            "12.5",
            "1.2",
            "18.8",
            provenance,
            PresentationFreshness.Current,
            provenance,
            PresentationFreshness.Current,
            "TrustedJump");
}
