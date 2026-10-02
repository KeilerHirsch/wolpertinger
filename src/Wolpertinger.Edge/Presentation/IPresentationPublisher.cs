using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Presentation;

public interface IPresentationPublisher
{
    PresentationSnapshot Current { get; }
    ValueTask PublishJumpAsync(JumpFact fact, ContextDecision decision, CancellationToken cancellationToken = default);
    ValueTask PublishCommanderVesselAsync(CommanderVesselFact fact, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PresentationSnapshot> ReadUpdatesAsync(CancellationToken cancellationToken = default);
}
