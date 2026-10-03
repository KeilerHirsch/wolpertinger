using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Presentation.State;

namespace Wolpertinger.Presentation.ViewModels;

public sealed record SmartHubViewModel(
    string ConnectionText,
    string ContextText,
    string CompositionText,
    string SelectionModeText,
    string RuntimeText,
    string FrontierAccountText,
    string CommanderName,
    string ShipText,
    string SystemText,
    string SystemSourceText,
    string SystemFreshnessText,
    string JumpDistanceText,
    string FuelLevelText,
    string ReasonText)
{
    public static SmartHubViewModel Create(PresentationStoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = state.Snapshot;
        if (snapshot is null)
        {
            return new SmartHubViewModel(
                state.Connection.ToString(),
                "Unavailable",
                "Quiet",
                "Auto",
                "Starting",
                "Disconnected",
                "—",
                "—",
                "—",
                "Unknown",
                "Unknown",
                "—",
                "—",
                "No validated snapshot");
        }

        var commander = snapshot.CommanderVessel;
        var jump = snapshot.Jump;
        var system = PresentationFieldSelector.SelectSystem(snapshot);
        var ship = commander is null
            ? "—"
            : string.IsNullOrWhiteSpace(commander.ShipName)
                ? commander.ShipModel
                : $"{commander.ShipName} ({commander.ShipModel})";

        return new SmartHubViewModel(
            state.Connection.ToString(),
            snapshot.Context.ToString(),
            snapshot.Intent.Composition.ToString(),
            snapshot.Intent.SelectionMode.ToString(),
            $"{snapshot.RuntimeHealth.Health}: {snapshot.RuntimeHealth.ReasonCode}",
            $"{snapshot.FrontierAccount.State}: {snapshot.FrontierAccount.ReasonCode}",
            commander?.CommanderName ?? "—",
            ship,
            system.Value ?? "—",
            system.Provenance.ToString(),
            system.Freshness.ToString(),
            jump?.JumpDistance ?? "—",
            jump?.FuelLevel ?? "—",
            snapshot.Intent.ReasonCode);
    }
}
