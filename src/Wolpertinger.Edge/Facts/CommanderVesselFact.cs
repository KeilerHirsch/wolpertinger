using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Facts;

public sealed record CommanderVesselFact(
    ObservationCursor Cursor,
    ProfileKey Profile,
    FixedBytes32 StateDigest,
    CommanderName CommanderName,
    bool CommanderAlive,
    bool CommanderDocked,
    bool CommanderOnFoot,
    VesselName VesselName,
    bool ShipAlive,
    SourceProvenance Provenance,
    FreshnessState Freshness,
    long ObservedUnixMs);
