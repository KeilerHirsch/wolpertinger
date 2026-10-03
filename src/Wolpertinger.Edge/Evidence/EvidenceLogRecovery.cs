using System.Buffers.Binary;
using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Evidence;

internal sealed record EvidenceRecoveryState(
    uint ActiveSegmentNumber,
    ulong NextRawOrdinal,
    byte[] PreviousDigest,
    IReadOnlyList<RecoveredRawEvidence> Records,
    bool HasSegments);

public static class EvidenceLogRecovery
{
    public static async Task<IReadOnlyList<RecoveredRawEvidence>> RecoverAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
        => (await RecoverStateCoreAsync(
            directoryPath, keyStore: null, cancellationToken).ConfigureAwait(false)).Records;

    public static async Task<IReadOnlyList<RecoveredRawEvidence>> RecoverAsync(
        string directoryPath,
        EvidenceKeyStore keyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        return (await RecoverStateCoreAsync(
            directoryPath, keyStore, cancellationToken).ConfigureAwait(false)).Records;
    }
    internal static Task<EvidenceRecoveryState> RecoverStateAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
        => RecoverStateCoreAsync(directoryPath, keyStore: null, cancellationToken);

    internal static Task<EvidenceRecoveryState> RecoverStateAsync(
        string directoryPath,
        EvidenceKeyStore keyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        return RecoverStateCoreAsync(directoryPath, keyStore, cancellationToken);
    }

    private static async Task<EvidenceRecoveryState> RecoverStateCoreAsync(
        string directoryPath,
        EvidenceKeyStore? keyStore,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        var segments = EnumerateSegments(directoryPath);
        ValidateSegmentSequence(segments);

        var records = new List<RecoveredRawEvidence>();
        var previousDigest = new byte[EvidenceRecordFormat.DigestSize];
        ulong nextRawOrdinal = 0;
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[segmentIndex];
            var isFinalSegment = segmentIndex == segments.Count - 1;
            var data = await File.ReadAllBytesAsync(segment.Path, cancellationToken).ConfigureAwait(false);
            var position = 0;

            while (position < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordStart = position;
                var remaining = data.Length - position;
                if (remaining < EvidenceRecordFormat.FixedHeaderSize)
                {
                    if (isFinalSegment)
                    {
                        TruncateTail(segment.Path, recordStart);
                        break;
                    }
                    throw Corruption(segment, recordStart, "truncated fixed header in closed segment");
                }

                if (!data.AsSpan(position, 4).SequenceEqual("WLEV"u8))
                    throw Corruption(segment, recordStart, "bad magic");
                var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 4, 2));
                try
                {
                    var parsed = version switch
                    {
                        EvidenceRecordFormat.FormatVersion => ParseV1(
                            data, position, segment, isFinalSegment,
                            nextRawOrdinal, previousDigest),
                        EvidenceRecordFormatV2.FormatVersion => await ParseV2Async(
                            data, position, segment, isFinalSegment,
                            nextRawOrdinal, previousDigest, keyStore,
                            cancellationToken).ConfigureAwait(false),
                        _ => throw Corruption(segment, recordStart,
                            $"unsupported format version {version}"),
                    };

                    records.Add(parsed.Record);
                    previousDigest = parsed.Record.EvidenceDigest.ToArray();
                    nextRawOrdinal++;
                    position += parsed.FrameLength;
                }
                catch (IncompleteTailException) when (isFinalSegment)
                {
                    TruncateTail(segment.Path, recordStart);
                    break;
                }
            }

            if (!isFinalSegment && data.Length == 0)
                throw Corruption(segment, 0, "empty closed segment");
        }
        return new EvidenceRecoveryState(
            segments.Count == 0 ? 0U : segments[^1].Number,
            nextRawOrdinal,
            previousDigest,
            records,
            HasSegments: segments.Count != 0);
    }

    private static ParsedFrame ParseV1(
        byte[] data,
        int position,
        SegmentInfo segment,
        bool isFinalSegment,
        ulong expectedRawOrdinal,
        byte[] previousDigest)
    {
        var remaining = data.Length - position;
        var header = data.AsSpan(position, EvidenceRecordFormat.FixedHeaderSize);
        var sourceValue = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6, 2));
        var rawOrdinal = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
        var observedUnixMs = BinaryPrimitives.ReadInt64BigEndian(header.Slice(16, 8));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(24, 4));

        if (!EvidenceRecordFormat.IsValidSourceKind(sourceValue))
            throw Corruption(segment, position, "unknown source kind");
        if (rawOrdinal != expectedRawOrdinal)
            throw Corruption(segment, position,
                $"raw ordinal {rawOrdinal} != expected {expectedRawOrdinal}");

        var frameLengthLong = EvidenceRecordFormat.FrameOverhead + (long)payloadLength;
        if (frameLengthLong > int.MaxValue || frameLengthLong > remaining)
        {
            if (isFinalSegment) throw new IncompleteTailException();
            throw Corruption(segment, position, "truncated record in closed segment");
        }

        var frameLength = (int)frameLengthLong;
        var payloadOffset = position + EvidenceRecordFormat.FixedHeaderSize;
        var previousOffset = payloadOffset + (int)payloadLength;
        var digestOffset = previousOffset + EvidenceRecordFormat.DigestSize;
        var payload = data.AsSpan(payloadOffset, (int)payloadLength);
        var storedPrevious = data.AsSpan(previousOffset, EvidenceRecordFormat.DigestSize);
        var storedDigest = data.AsSpan(digestOffset, EvidenceRecordFormat.DigestSize);
        var expectedDigest = EvidenceRecordFormat.ComputeDigest(previousDigest, header, payload);
        var frameEndsAtFileEnd = position + frameLength == data.Length;

        if (!storedPrevious.SequenceEqual(previousDigest)
            || !storedDigest.SequenceEqual(expectedDigest))
        {
            if (isFinalSegment && frameEndsAtFileEnd)
                throw new IncompleteTailException();
            throw Corruption(segment, position, "digest chain mismatch");
        }

        var observedUtc = ParseObservedUtc(observedUnixMs, segment, position);
        var reference = new EvidenceReference(rawOrdinal, segment.Number, position, frameLength);
        var record = new RecoveredRawEvidence(
            reference,
            (RawEvidenceSourceKind)sourceValue,
            payload.ToArray(),
            observedUtc,
            FixedBytes32.FromBytes(storedPrevious),
            FixedBytes32.FromBytes(storedDigest));
        return new ParsedFrame(record, frameLength);
    }

    private static async Task<ParsedFrame> ParseV2Async(
        byte[] data,
        int position,
        SegmentInfo segment,
        bool isFinalSegment,
        ulong expectedRawOrdinal,
        byte[] previousDigest,
        EvidenceKeyStore? keyStore,
        CancellationToken cancellationToken)
    {
        var remaining = data.Length - position;
        if (remaining < EvidenceRecordFormatV2.FixedHeaderSize)
        {
            if (isFinalSegment) throw new IncompleteTailException();
            throw Corruption(segment, position, "truncated v2 header in closed segment");
        }
        var header = EvidenceRecordFormatV2.ParseHeader(
            data.AsSpan(position, EvidenceRecordFormatV2.FixedHeaderSize));
        if (header.RawOrdinal != expectedRawOrdinal)
            throw Corruption(segment, position,
                $"raw ordinal {header.RawOrdinal} != expected {expectedRawOrdinal}");

        var frameLengthLong = EvidenceRecordFormatV2.FrameOverhead + (long)header.CiphertextLength;
        if (frameLengthLong > int.MaxValue || frameLengthLong > remaining)
        {
            if (isFinalSegment) throw new IncompleteTailException();
            throw Corruption(segment, position, "truncated v2 record in closed segment");
        }
        if (keyStore is null)
            throw Corruption(segment, position, "encrypted evidence requires a key store");

        var frameLength = (int)frameLengthLong;
        var key = await keyStore.LoadAsync(header.KeyId, cancellationToken).ConfigureAwait(false);
        DecryptedEvidenceV2 decrypted;
        try
        {
            decrypted = EvidenceRecordFormatV2.Decrypt(
                data.AsSpan(position, frameLength), previousDigest, key);
        }
        catch (EvidenceCorruptionException ex)
        {
            throw new EvidenceCorruptionException(
                $"Evidence corruption in segment {segment.Number} at offset {position}: {ex.Message}");
        }

        var observedUtc = ParseObservedUtc(header.ObservedUnixMs, segment, position);
        var reference = new EvidenceReference(
            header.RawOrdinal, segment.Number, position, frameLength);
        var record = new RecoveredRawEvidence(
            reference,
            header.SourceKind,
            decrypted.Plaintext,
            observedUtc,
            decrypted.PreviousDigest,
            decrypted.RecordDigest,
            header.SourceLocator);
        return new ParsedFrame(record, frameLength);
    }

    private static DateTimeOffset ParseObservedUtc(
        long observedUnixMs,
        SegmentInfo segment,
        long recordStart)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(observedUnixMs); }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new EvidenceCorruptionException(
                $"Invalid observed timestamp in segment {segment.Number} at offset {recordStart}: {ex.Message}");
        }
    }
    private static List<SegmentInfo> EnumerateSegments(string directoryPath)
    {
        var result = new List<SegmentInfo>();
        foreach (var path in Directory.GetFiles(directoryPath, "evidence-*.wlev"))
        {
            if (!EvidenceRecordFormat.TryParseSegmentNumber(path, out var number))
                throw new EvidenceCorruptionException($"Unrecognized evidence segment name: {path}");
            result.Add(new SegmentInfo(number, path));
        }
        result.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        return result;
    }

    private static void ValidateSegmentSequence(IReadOnlyList<SegmentInfo> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].Number != (uint)index)
                throw new EvidenceCorruptionException(
                    $"Evidence segment sequence gap: found {segments[index].Number}, expected {index}.");
        }
    }

    private static void TruncateTail(string path, long validLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(validLength);
        stream.Flush(flushToDisk: true);
    }
    private static EvidenceCorruptionException Corruption(
        SegmentInfo segment,
        long offset,
        string reason)
        => new($"Evidence corruption in segment {segment.Number} at offset {offset}: {reason}.");

    private sealed record SegmentInfo(uint Number, string Path);
    private sealed record ParsedFrame(RecoveredRawEvidence Record, int FrameLength);
    private sealed class IncompleteTailException : Exception;
}
