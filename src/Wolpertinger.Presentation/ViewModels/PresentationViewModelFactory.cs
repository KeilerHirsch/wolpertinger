using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Presentation.State;

namespace Wolpertinger.Presentation.ViewModels;

public static class PresentationViewModelFactory
{
    public static JumpViewModel CreateJump(PresentationStoreState state, DensityPreset density)
    {
        var (snapshot, jump) = RequireJump(state);
        var isLive = state.Connection == PresentationConnectionState.Live;

        return new JumpViewModel(
            snapshot.ProtocolVersion,
            snapshot.Revision,
            jump.Cursor,
            jump.Profile.Fid,
            MapRealm(jump.Profile.Realm),
            jump.Profile.SaveEpoch,
            jump.SystemAddress,
            jump.StarSystem,
            jump.PositionX,
            jump.PositionY,
            jump.PositionZ,
            AppendUnit(jump.JumpDistance, "ly"),
            AppendUnit(jump.FuelUsed, "t"),
            AppendUnit(jump.FuelLevel, "t"),
            MapProvenance(jump.LocationProvenance),
            MapFreshness(jump.LocationFreshness),
            MapProvenance(jump.FuelProvenance),
            MapFreshness(jump.FuelFreshness),
            jump.EvidenceReference,
            jump.EvidenceDigestHex,
            jump.StateDigestHex,
            jump.ReasonCode,
            state.Connection,
            isLive,
            MapAvailability(state.Connection),
            density);
    }

    public static DiagnosticsViewModel CreateDiagnostics(PresentationStoreState state)
    {
        var (snapshot, jump) = RequireJump(state);

        return new DiagnosticsViewModel(
            snapshot.ProtocolVersion,
            snapshot.Revision,
            jump.Cursor,
            jump.Profile.Fid,
            MapRealm(jump.Profile.Realm),
            jump.Profile.SaveEpoch,
            jump.EvidenceReference,
            jump.EvidenceDigestHex,
            jump.StateDigestHex,
            jump.SystemAddress,
            jump.StarSystem,
            jump.PositionX,
            jump.PositionY,
            jump.PositionZ,
            jump.JumpDistance,
            jump.FuelUsed,
            jump.FuelLevel,
            MapProvenance(jump.LocationProvenance),
            MapFreshness(jump.LocationFreshness),
            MapProvenance(jump.FuelProvenance),
            MapFreshness(jump.FuelFreshness),
            jump.ReasonCode,
            state.Connection);
    }

    private static (PresentationSnapshot Snapshot, JumpPresentation Jump) RequireJump(PresentationStoreState state)
    {
        var snapshot = state.Snapshot ??
            throw new InvalidOperationException("No validated presentation snapshot is available.");
        var jump = snapshot.Jump ??
            throw new InvalidOperationException("The current presentation snapshot contains no jump state.");
        return (snapshot, jump);
    }

    private static string AppendUnit(string value, string unit) => $"{value} {unit}";

    private static string MapAvailability(PresentationConnectionState state) => state switch
    {
        PresentationConnectionState.Connecting => "Connecting",
        PresentationConnectionState.Live => "Live",
        PresentationConnectionState.Disconnected => "Disconnected — last known data",
        PresentationConnectionState.Incompatible => "Incompatible — last known data",
        _ => throw new InvalidDataException("Undefined presentation connection state.")
    };

    private static string MapFreshness(PresentationFreshness freshness) => freshness switch
    {
        PresentationFreshness.Current => "Current",
        PresentationFreshness.Stale => "Stale",
        PresentationFreshness.Unknown => "Unknown",
        PresentationFreshness.Conflicting => "Conflicting",
        _ => throw new InvalidDataException("Undefined presentation freshness state.")
    };

    private static string MapProvenance(PresentationProvenance provenance) => provenance switch
    {
        PresentationProvenance.Unknown => "Unknown",
        PresentationProvenance.LocalJournal => "LocalJournal",
        PresentationProvenance.LocalStatus => "LocalStatus",
        PresentationProvenance.FrontierApi => "FrontierApi",
        PresentationProvenance.Community => "Community",
        PresentationProvenance.UserEntered => "UserEntered",
        PresentationProvenance.Sample => "Sample",
        _ => throw new InvalidDataException("Undefined presentation provenance.")
    };

    private static string MapRealm(PresentationRealm realm) => realm switch
    {
        PresentationRealm.Unknown => "Unknown",
        PresentationRealm.Live => "Live",
        PresentationRealm.Legacy => "Legacy",
        PresentationRealm.BetaOrPts => "BetaOrPts",
        _ => throw new InvalidDataException("Undefined presentation realm.")
    };
}
