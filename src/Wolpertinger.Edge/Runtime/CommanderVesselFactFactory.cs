using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Edge.Kernel;

namespace Wolpertinger.Edge.Runtime;

internal static class CommanderVesselFactFactory
{
    internal static CommanderVesselFact Create(
        ObservationEnvelope observation,
        KernelApplyResult result)
    {
        var fact = result.CommanderVesselFact
            ?? throw new InvalidDataException("Applied Commander/Vessel observation did not return a fact.");
        if (fact.Cursor != observation.Cursor || result.Cursor != observation.Cursor)
            throw new InvalidDataException("Kernel Commander/Vessel fact cursor does not match observation cursor.");

        return new CommanderVesselFact(
            observation.Cursor,
            observation.Profile,
            result.StateDigest,
            fact.CommanderName,
            fact.CommanderAlive,
            fact.CommanderDocked,
            fact.CommanderOnFoot,
            fact.VesselName,
            fact.ShipAlive,
            fact.Provenance,
            fact.Freshness,
            observation.ObservedUnixMs);
    }
}
