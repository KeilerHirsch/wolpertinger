using System.Runtime.CompilerServices;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Telemetry;

public sealed record JournalSourceRecord(
    byte[] Payload,
    RawEvidenceSourceLocator Locator);

public sealed class JournalTailer
{
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
            {
                yield break;
            }
        }

        while (_currentPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_currentPath))
            {
                throw new EvidenceCorruptionException(
                    $"Journal source disappeared: {Path.GetFileName(_currentPath)}.");
            }

            var data = await File.ReadAllBytesAsync(_currentPath, cancellationToken).ConfigureAwait(false);
            if (_nextOffset > (ulong)data.LongLength)
            {
                throw new EvidenceCorruptionException(
                    $"Journal source is shorter than recovered offset: {Path.GetFileName(_currentPath)}.");
            }

            var sourceId = JournalSourceId.FromFileName(_currentPath);
            var position = checked((int)_nextOffset);
            while (position < data.Length)
            {
                var newline = Array.IndexOf(data, (byte)'\n', position);
                if (newline < 0)
                {
                    _nextOffset = checked((ulong)position);
                    yield break;
                }

                var sourceLength = checked(newline - position + 1);
                var payloadLength = sourceLength - 1;
                if (payloadLength > 0 && data[position + payloadLength - 1] == (byte)'\r')
                {
                    payloadLength--;
                }

                var payload = data.AsSpan(position, payloadLength).ToArray();
                var locator = new RawEvidenceSourceLocator(
                    sourceId,
                    checked((ulong)position),
                    checked((uint)sourceLength));
                position = newline + 1;
                _nextOffset = checked((ulong)position);
                yield return new JournalSourceRecord(payload, locator);
            }

            var later = EnumerateJournalFiles()
                .FirstOrDefault(path => CompareJournalNames(path, _currentPath) > 0);
            if (later is null)
            {
                yield break;
            }

            _currentPath = later;
            _nextOffset = 0;
        }
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
        {
            return false;
        }

        _currentPath = files[^1];
        _nextOffset = 0;
        _initialized = true;
        return true;
    }

    private List<string> EnumerateJournalFiles()
    {
        if (!Directory.Exists(_journalDirectory))
        {
            return [];
        }

        var files = Directory.GetFiles(_journalDirectory, "Journal.*.log").ToList();
        files.Sort(CompareJournalNames);
        return files;
    }

    private static int CompareJournalNames(string left, string right)
        => StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right));
}
