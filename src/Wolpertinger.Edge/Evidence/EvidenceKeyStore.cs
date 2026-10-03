using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wolpertinger.Edge.Evidence;

public sealed class EvidenceKeyStore
{
    private const ushort FormatVersion = 1;
    private const int HeaderSize = 16;
    private static ReadOnlySpan<byte> Magic => "WLEK"u8;
    private readonly string _path;
    private readonly IEvidenceKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EvidenceKeyStore(string path, IEvidenceKeyProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public async Task<EvidenceKeyMaterial> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
                return await ReadStoredAsync(null, cancellationToken).ConfigureAwait(false);

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var material = new EvidenceKeyMaterial(CreateKeyId(), RandomNumberGenerator.GetBytes(32));
            var protectedBytes = Protect(material.KeyBytes);
            var frame = BuildFrame(material.KeyId, protectedBytes);
            var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await WriteAtomicallyAsync(temp, frame, cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temp, _path, overwrite: false);
                    return material;
                }
                catch (IOException) when (File.Exists(_path))
                {
                    File.Delete(temp);
                    return await ReadStoredAsync(null, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally { _gate.Release(); }
    }
    public async Task<EvidenceKeyMaterial> LoadAsync(
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        if (keyId == 0) throw new EvidenceCorruptionException("Evidence key id 0 is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadStoredAsync(keyId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<EvidenceKeyMaterial> ReadStoredAsync(
        uint? expectedKeyId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            throw new EvidenceCorruptionException("Evidence key file is missing.");

        byte[] frame;
        try { frame = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new EvidenceCorruptionException($"Evidence key file cannot be read: {ex.Message}"); }

        if (frame.Length < HeaderSize || !frame.AsSpan(0, 4).SequenceEqual(Magic))
            throw new EvidenceCorruptionException("Evidence key file header is invalid.");
        if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2)) != FormatVersion
            || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2)) != 0)
            throw new EvidenceCorruptionException("Evidence key file version/reserved bytes are invalid.");
        var keyId = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4));
        var protectedLength = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12, 4));
        if (keyId == 0 || protectedLength == 0 || protectedLength > int.MaxValue
            || HeaderSize + (long)protectedLength != frame.Length)
            throw new EvidenceCorruptionException("Evidence key file length/id is invalid.");
        if (expectedKeyId is uint expected && keyId != expected)
            throw new EvidenceCorruptionException($"Evidence key id {expected} is unavailable.");

        byte[] raw;
        try { raw = _protector.Unprotect(frame.AsSpan(HeaderSize, (int)protectedLength)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new EvidenceCorruptionException($"Evidence key cannot be unprotected: {ex.Message}"); }
        if (raw.Length != 32)
            throw new EvidenceCorruptionException("Evidence key material has invalid length.");
        return new EvidenceKeyMaterial(keyId, raw);
    }

    private byte[] Protect(ReadOnlySpan<byte> raw)
    {
        var protectedBytes = _protector.Protect(raw);
        if (protectedBytes.Length == 0)
            throw new InvalidOperationException("Evidence key protector returned no data.");
        return protectedBytes;
    }
    private static byte[] BuildFrame(uint keyId, byte[] protectedBytes)
    {
        var frame = new byte[HeaderSize + protectedBytes.Length];
        Magic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), FormatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), keyId);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), (uint)protectedBytes.Length);
        protectedBytes.CopyTo(frame, HeaderSize);
        return frame;
    }

    private static uint CreateKeyId()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint keyId;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            keyId = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        } while (keyId == 0);
        return keyId;
    }

    private static async Task WriteAtomicallyAsync(
        string tempPath,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
