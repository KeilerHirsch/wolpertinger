using System.Runtime.CompilerServices;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Telemetry;

public sealed record JournalSourceRecord(
    byte[] Payload,
    RawEvidenceSourceLocator Locator);

public sealed class JournalTailer
{
    private const int MaximumReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly string _journalDirectory;
    private string? _currentPath;
    private ulong _nextOffset;
    private bool _initialized;

    public JournalTailer(string journalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        _journalDirectory = Path.GetFullPath(journalDirectory);
    }

    public async IAsyncEnumerable<JournalSourceRecord> ReadAvailableAsync(
        IReadOnlyList<RecoveredRawEvidence> recovered,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovered);

        if (!_initialized)
        {
            if (!Initialize(recovered))
                yield break;
        }

        while (_currentPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_currentPath))
            {
                throw new EvidenceCorruptionException(
                    $"Journal source disappeared: {Path.GetFileName(_currentPath)}.");
            }

            var sourceId = JournalSourceId.FromFileName(_currentPath);
            var baseOffset = _nextOffset;
            var data = await ReadTailBytesAsync(
                _currentPath,
                baseOffset,
                cancellationToken).ConfigureAwait(false);

            var position = 0;
            while (position < data.Length)
            {
                var newline = Array.IndexOf(data, (byte)'\n', position);
                if (newline < 0)
                {
                    _nextOffset = checked(baseOffset + (ulong)position);
                    yield break;
                }

                var sourceLength = checked(newline - position + 1);
                var payloadLength = sourceLength - 1;
                if (payloadLength > 0 && data[position + payloadLength - 1] == (byte)'\r')
                    payloadLength--;

                var payload = data.AsSpan(position, payloadLength).ToArray();
                var absoluteOffset = checked(baseOffset + (ulong)position);
                var locator = new RawEvidenceSourceLocator(
                    sourceId,
                    absoluteOffset,
                    checked((uint)sourceLength));
                position = newline + 1;
                _nextOffset = checked(baseOffset + (ulong)position);
                yield return new JournalSourceRecord(payload, locator);
            }

            var later = EnumerateJournalFiles()
                .FirstOrDefault(path => CompareJournalNames(path, _currentPath) > 0);
            if (later is null)
                yield break;

            _currentPath = later;
            _nextOffset = 0;
        }
    }

    private static async Task<byte[]> ReadTailBytesAsync(
        string path,
        ulong offset,
        CancellationToken cancellationToken)
    {
        IOException? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (offset > (ulong)stream.Length)
                {
                    throw new EvidenceCorruptionException(
                        $"Journal source is shorter than recovered offset: {Path.GetFileName(path)}.");
                }

                stream.Position = checked((long)offset);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                if (attempt < MaximumReadAttempts)
                    await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Journal source could not be read after {MaximumReadAttempts} attempts: {Path.GetFileName(path)}.",
            lastFailure);
    }

    private bool Initialize(IReadOnlyList<RecoveredRawEvidence> recovered)
    {
        var files = EnumerateJournalFiles();
        var lastJournal = recovered
            .Where(record => record.SourceKind == RawEvidenceSourceKind.LocalJournal)
            .OrderBy(record => record.Reference.RawOrdinal)
            .LastOrDefault();

        if (lastJournal is not null)
        {
            if (lastJournal.SourceLocator is not { } locator)
            {
                throw new EvidenceCorruptionException(
                    "Recovered LocalJournal evidence has no source locator.");
            }

            var match = files.SingleOrDefault(
                path => JournalSourceId.FromFileName(path) == locator.SourceId);
            if (match is null)
            {
                throw new EvidenceCorruptionException(
                    "Recovered journal source id does not match any current journal file.");
            }

            _currentPath = match;
            _nextOffset = checked(locator.SourceOffset + locator.SourceLength);
            _initialized = true;
            return true;
        }

        if (files.Count == 0)
            return false;

        _currentPath = files[^1];
        _nextOffset = 0;
        _initialized = true;
        return true;
    }

    private List<string> EnumerateJournalFiles()
    {
        if (!Directory.Exists(_journalDirectory))
            return [];

        var files = Directory.GetFiles(_journalDirectory, "Journal.*.log").ToList();
        files.Sort(CompareJournalNames);
        return files;
    }

    private static int CompareJournalNames(string left, string right)
        => StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right));
}
