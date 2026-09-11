using System.Security.Cryptography;
using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Evidence;

public sealed class EncryptedSegmentedEvidenceLog : IAsyncDisposable
{
    public const long DefaultSegmentSizeBytes = 16L * 1024 * 1024;

    private readonly string _directoryPath;
    private readonly EvidenceKeyMaterial _key;
    private readonly long _segmentSizeBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream _stream;
    private uint _segmentNumber;
    private ulong _nextRawOrdinal;
    private byte[] _previousDigest;
    private bool _disposed;

    private EncryptedSegmentedEvidenceLog(
        string directoryPath,
        EvidenceKeyMaterial key,
        long segmentSizeBytes,
        FileStream stream,
        uint segmentNumber,
        ulong nextRawOrdinal,
        byte[] previousDigest)
    {
        _directoryPath = directoryPath;
        _key = key;
        _segmentSizeBytes = segmentSizeBytes;
        _stream = stream;
        _segmentNumber = segmentNumber;
        _nextRawOrdinal = nextRawOrdinal;
        _previousDigest = previousDigest;
    }

    public static async Task<EncryptedSegmentedEvidenceLog> OpenAsync(
        string directoryPath,
        EvidenceKeyStore keyStore,
        long segmentSizeBytes = DefaultSegmentSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(keyStore);
        if (segmentSizeBytes <= EvidenceRecordFormatV2.FrameOverhead)
            throw new ArgumentOutOfRangeException(nameof(segmentSizeBytes));

        Directory.CreateDirectory(directoryPath);
        var state = await EvidenceLogRecovery.RecoverStateAsync(
            directoryPath, keyStore, cancellationToken).ConfigureAwait(false);
        var key = await keyStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);

        var segmentNumber = state.ActiveSegmentNumber;
        if (state.HasSegments && GetSegmentVersion(directoryPath, segmentNumber) != EvidenceRecordFormatV2.FormatVersion)
            segmentNumber = checked(segmentNumber + 1);
        var path = EvidenceRecordFormat.SegmentPath(directoryPath, segmentNumber);
        var stream = OpenSegment(path);
        stream.Position = stream.Length;

        return new EncryptedSegmentedEvidenceLog(
            directoryPath,
            key,
            segmentSizeBytes,
            stream,
            segmentNumber,
            state.NextRawOrdinal,
            state.PreviousDigest);
    }

    public async Task<RawEvidenceReceipt> AppendAsync(
        RawEvidenceInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Payload.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(input), "Raw evidence payload is empty.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var nonce = RandomNumberGenerator.GetBytes(EvidenceRecordFormatV2.NonceSize);
            var frame = EvidenceRecordFormatV2.Encrypt(
                input, _nextRawOrdinal, _previousDigest, _key, nonce);
            if (_stream.Length > 0 && _stream.Length + frame.Length > _segmentSizeBytes)
            {
                await RotateAsync().ConfigureAwait(false);
                nonce = RandomNumberGenerator.GetBytes(EvidenceRecordFormatV2.NonceSize);
                frame = EvidenceRecordFormatV2.Encrypt(
                    input, _nextRawOrdinal, _previousDigest, _key, nonce);
            }

            var offset = _stream.Position;
            try
            {
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                _stream.Flush(flushToDisk: true);
            }
            catch
            {
                TryRollback(offset);
                throw;
            }

            var digest = frame.AsSpan(frame.Length - EvidenceRecordFormatV2.DigestSize,
                EvidenceRecordFormatV2.DigestSize).ToArray();
            var reference = new EvidenceReference(
                _nextRawOrdinal, _segmentNumber, offset, frame.Length);
            _nextRawOrdinal++;
            _previousDigest = digest;
            return new RawEvidenceReceipt(
                reference,
                input.SourceKind,
                FixedBytes32.FromBytes(digest), input.ObservedUtc,
                DateTimeOffset.UtcNow,
                IsDurable: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RotateAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _segmentNumber = checked(_segmentNumber + 1);
        _stream = OpenSegment(EvidenceRecordFormat.SegmentPath(_directoryPath, _segmentNumber));
    }

    private void TryRollback(long offset)
    {
        try
        {
            _stream.SetLength(offset);
            _stream.Position = offset;
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Startup recovery validates/truncates only an incomplete final tail.
        }
    }
    private static ushort GetSegmentVersion(string directoryPath, uint segmentNumber)
    {
        var path = EvidenceRecordFormat.SegmentPath(directoryPath, segmentNumber);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return EvidenceRecordFormatV2.FormatVersion;
        Span<byte> prefix = stackalloc byte[6];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Read(prefix) != prefix.Length || !prefix[..4].SequenceEqual("WLEV"u8))
            throw new EvidenceCorruptionException("Evidence segment header is unreadable.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(prefix[4..6]);
    }

    private static FileStream OpenSegment(string path)
        => new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
