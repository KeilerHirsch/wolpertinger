using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.Transport;

public interface IPresentationSnapshotClient
{
    IAsyncEnumerable<PresentationSnapshot> ReadSnapshotsAsync(
        CancellationToken cancellationToken = default);
}
