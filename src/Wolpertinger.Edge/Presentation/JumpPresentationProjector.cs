using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Presentation;

public static class JumpPresentationProjector
{
    public static PresentationSnapshot Project(
        JumpFact fact,
        ContextDecision decision,
        ulong revision)
        => Project(fact, decision, PresentationSnapshot.Empty, revision);

    public static PresentationSnapshot Project(
        JumpFact fact,
        ContextDecision decision,
        PresentationSnapshot current,
        ulong revision)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            ProtocolVersion = PresentationProtocol.Version,
            Revision = revision,
            Jump = ProjectJump(fact, decision),
        };
    }
    private static JumpPresentation ProjectJump(JumpFact fact, ContextDecision decision)
        => new(
            new PresentationCursor(fact.Cursor.EvidenceSequence, fact.Cursor.MessageOrdinal),
            new PresentationProfile(fact.Profile.Fid, Map(fact.Profile.Realm), fact.Profile.SaveEpoch),
            new PresentationEvidenceReference(
                fact.EvidenceReference.RawOrdinal,
                fact.EvidenceReference.SegmentNumber,
                fact.EvidenceReference.ByteOffset,
                fact.EvidenceReference.FrameLength),
            fact.EvidenceDigest.Hex,
            fact.StateDigest.Hex,
            fact.SystemAddress,
            fact.StarSystem,
            fact.Position.X.ToString(),
            fact.Position.Y.ToString(),
            fact.Position.Z.ToString(),
            fact.JumpDistance.ToString(),
            fact.FuelUsed.ToString(),
            fact.FuelLevel.ToString(),
            Map(fact.LocationProvenance),
            Map(fact.LocationFreshness),
            Map(fact.FuelProvenance),
            Map(fact.FuelFreshness),
            decision.ReasonCode);

    private static PresentationRealm Map(GalaxyRealm value) => value switch
    {
        GalaxyRealm.Unknown => PresentationRealm.Unknown,
        GalaxyRealm.Live => PresentationRealm.Live,
        GalaxyRealm.Legacy => PresentationRealm.Legacy,
        GalaxyRealm.BetaOrPts => PresentationRealm.BetaOrPts,
        _ => throw new InvalidDataException($"Undefined galaxy realm: {value}."),
    };
    private static PresentationProvenance Map(SourceProvenance value) => value switch
    {
        SourceProvenance.Unknown => PresentationProvenance.Unknown,
        SourceProvenance.LocalJournal => PresentationProvenance.LocalJournal,
        SourceProvenance.LocalStatus => PresentationProvenance.LocalStatus,
        SourceProvenance.FrontierApi => PresentationProvenance.FrontierApi,
        SourceProvenance.Community => PresentationProvenance.Community,
        SourceProvenance.UserEntered => PresentationProvenance.UserEntered,
        _ => throw new InvalidDataException($"Undefined source provenance: {value}."),
    };

    private static PresentationFreshness Map(FreshnessState value) => value switch
    {
        FreshnessState.Unknown => PresentationFreshness.Unknown,
        FreshnessState.Current => PresentationFreshness.Current,
        FreshnessState.Stale => PresentationFreshness.Stale,
        FreshnessState.Conflicting => PresentationFreshness.Conflicting,
        _ => throw new InvalidDataException($"Undefined freshness state: {value}."),
    };
}
