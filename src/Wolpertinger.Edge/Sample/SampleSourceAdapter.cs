using System.Text;
using System.Text.Json;

namespace Wolpertinger.Edge.Sample;

public interface ISampleIngestTarget
{
    Task ProcessSampleJournalAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
    Task ProcessSampleStatusAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
    Task ProcessSampleCommanderVesselAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

public sealed class SampleSourceAdapter
{
    private readonly ISampleIngestTarget _target;

    public SampleSourceAdapter(ISampleIngestTarget target)
        => _target = target ?? throw new ArgumentNullException(nameof(target));

    public async Task RunAsync(
        string fixturePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        await foreach (var line in File.ReadLinesAsync(fixturePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("source", out var sourceElement)
                || sourceElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payload", out var payloadElement))
            {
                throw new InvalidDataException(
                    "Sample record must contain string source and payload.");
            }

            var source = sourceElement.GetString();
            var payload = Encoding.UTF8.GetBytes(payloadElement.GetRawText());
            switch (source)
            {
                case "journal":
                    await _target.ProcessSampleJournalAsync(
                        payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "status":
                    await _target.ProcessSampleStatusAsync(
                        payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "commanderVessel":
                    await _target.ProcessSampleCommanderVesselAsync(
                        payload, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported sample source: {source ?? "<null>"}.");
            }
        }
    }
}
