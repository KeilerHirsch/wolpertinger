using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.ViewModels;

public sealed record PresentationDisplayField(
    string? Value,
    PresentationProvenance Provenance,
    PresentationFreshness Freshness)
{
    public bool Available => !string.IsNullOrWhiteSpace(Value);
}

public static class PresentationFieldSelector
{
    public static PresentationDisplayField SelectSystem(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var jump = snapshot.Jump;
        if (jump is not null
            && jump.LocationFreshness == PresentationFreshness.Current
            && jump.LocationProvenance is
                PresentationProvenance.LocalJournal or
                PresentationProvenance.LocalStatus or
                PresentationProvenance.Sample
            && !string.IsNullOrWhiteSpace(jump.StarSystem))
        {
            return new PresentationDisplayField(
                jump.StarSystem,
                jump.LocationProvenance,
                jump.LocationFreshness);
        }

        var commander = snapshot.CommanderVessel;
        if (commander is not null
            && commander.Provenance == PresentationProvenance.FrontierApi
            && commander.Freshness == PresentationFreshness.Current
            && !string.IsNullOrWhiteSpace(commander.SystemName))
        {
            return new PresentationDisplayField(
                commander.SystemName,
                commander.Provenance,
                commander.Freshness);
        }

        return new PresentationDisplayField(
            null,
            PresentationProvenance.Unknown,
            PresentationFreshness.Unknown);
    }
}
