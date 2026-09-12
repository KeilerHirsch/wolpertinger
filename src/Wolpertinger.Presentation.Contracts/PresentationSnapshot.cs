namespace Wolpertinger.Presentation.Contracts;

public enum PresentationRealm : byte { Unknown = 0, Live = 1, Legacy = 2, BetaOrPts = 3 }
public enum PresentationProvenance : byte
{
    Unknown = 0,
    LocalJournal = 1,
    LocalStatus = 2,
    FrontierApi = 3,
    Community = 4,
    UserEntered = 5,
    Sample = 6,
}
public enum PresentationFreshness : byte { Unknown = 0, Current = 1, Stale = 2, Conflicting = 3 }

public readonly record struct PresentationCursor(ulong EvidenceSequence, uint MessageOrdinal);
public sealed record PresentationProfile(string Fid, PresentationRealm Realm, ulong SaveEpoch);
public readonly record struct PresentationEvidenceReference(
    ulong RawOrdinal,
    uint SegmentNumber,
    long ByteOffset,
    int FrameLength);

public sealed record JumpPresentation(
    PresentationCursor Cursor,
    PresentationProfile Profile,
    PresentationEvidenceReference EvidenceReference,
    string EvidenceDigestHex,
    string StateDigestHex,
    ulong SystemAddress,
    string StarSystem,
    string PositionX,
    string PositionY,
    string PositionZ,
    string JumpDistance,
    string FuelUsed,
    string FuelLevel,
    PresentationProvenance LocationProvenance,
    PresentationFreshness LocationFreshness,
    PresentationProvenance FuelProvenance,
    PresentationFreshness FuelFreshness,
    string ReasonCode);

public sealed record CommanderVesselPresentation(
    PresentationCursor Cursor,
    PresentationProfile Profile,
    string StateDigestHex,
    string CommanderName,
    string ShipModel,
    string? ShipName,
    string? SystemName,
    string? StationName,
    PresentationTriState Docked,
    PresentationTriState Alive,
    PresentationProvenance Provenance,
    PresentationFreshness Freshness,
    long ObservedUnixMs);

public sealed record PresentationSnapshot(
    int ProtocolVersion,
    ulong Revision,
    PresentationGameContext Context,
    PresentationIntent Intent,
    JumpPresentation? Jump,
    CommanderVesselPresentation? CommanderVessel,
    RuntimeHealthPresentation RuntimeHealth,
    FrontierAccountPresentation FrontierAccount)
{
    public static PresentationSnapshot Empty { get; } = new(
        PresentationProtocol.Version,
        0,
        PresentationGameContext.InactiveOrNoGame,
        new PresentationIntent(
            PresentationComposition.Quiet,
            false,
            "Startup",
            PresentationSelectionMode.Auto),
        null,
        null,
        new RuntimeHealthPresentation(ProductRuntimeHealth.Starting, "Startup"),
        new FrontierAccountPresentation(
            FrontierAccountState.Disconnected,
            PresentationFreshness.Unknown,
            null,
            "NotConnected"));
}
