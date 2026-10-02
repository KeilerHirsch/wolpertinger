using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Presentation;

internal static class CommanderVesselPresentationProjector
{
    internal static CommanderVesselPresentation Project(CommanderVesselFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new CommanderVesselPresentation(
            new PresentationCursor(fact.Cursor.EvidenceSequence, fact.Cursor.MessageOrdinal),
            new PresentationProfile(
                fact.Profile.Fid,
                fact.Profile.Realm switch
                {
                    GalaxyRealm.Unknown => PresentationRealm.Unknown,
                    GalaxyRealm.Live => PresentationRealm.Live,
                    GalaxyRealm.Legacy => PresentationRealm.Legacy,
                    GalaxyRealm.BetaOrPts => PresentationRealm.BetaOrPts,
                    _ => throw new InvalidDataException($"Undefined galaxy realm: {fact.Profile.Realm}."),
                },
                fact.Profile.SaveEpoch),
            fact.StateDigest.Hex,
            fact.CommanderName.Value,
            fact.VesselName.Value,
            ShipName: null,
            SystemName: null,
            StationName: null,
            fact.CommanderDocked ? PresentationTriState.True : PresentationTriState.False,
            fact.CommanderAlive ? PresentationTriState.True : PresentationTriState.False,
            fact.Provenance switch
            {
                SourceProvenance.FrontierApi => PresentationProvenance.FrontierApi,
                SourceProvenance.Sample => PresentationProvenance.Sample,
                _ => throw new InvalidDataException(
                    $"Unsupported Commander/Vessel provenance: {fact.Provenance}."),
            },
            fact.Freshness switch
            {
                FreshnessState.Unknown => PresentationFreshness.Unknown,
                FreshnessState.Current => PresentationFreshness.Current,
                FreshnessState.Stale => PresentationFreshness.Stale,
                FreshnessState.Conflicting => PresentationFreshness.Conflicting,
                _ => throw new InvalidDataException($"Undefined freshness state: {fact.Freshness}."),
            },
            fact.ObservedUnixMs);
    }
}
