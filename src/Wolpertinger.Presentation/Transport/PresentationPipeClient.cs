using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.Transport;

public sealed class PresentationPipeClient(string pipeName = PresentationProtocol.DefaultPipeName) : IPresentationSnapshotClient
{
    public async IAsyncEnumerable<PresentationSnapshot> ReadSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct);
        while (true)
        {
            PresentationSnapshot snapshot;
            try { snapshot = await PresentationFrameCodec.ReadAsync(pipe, ct); }
            catch (EndOfStreamException) { yield break; }
            yield return snapshot;
        }
    }
}
