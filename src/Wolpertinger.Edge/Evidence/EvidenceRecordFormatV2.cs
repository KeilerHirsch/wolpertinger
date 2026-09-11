using System.Buffers.Binary;
using System.Security.Cryptography;
using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Evidence;

internal sealed record EvidenceRecordHeaderV2(
    RawEvidenceSourceKind SourceKind,
    ulong RawOrdinal,
    long ObservedUnixMs,
    uint KeyId,
    uint CiphertextLength,
    RawEvidenceSourceLocator? SourceLocator);

internal sealed record DecryptedEvidenceV2(
    EvidenceRecordHeaderV2 Header,
    byte[] Plaintext,
    FixedBytes32 PreviousDigest,
    FixedBytes32 RecordDigest);

internal static class EvidenceRecordFormatV2
{
    public const ushort FormatVersion = 2;
    public const int FixedHeaderSize = 64;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int DigestSize = 32;
    public const int FrameOverhead = FixedHeaderSize + NonceSize + TagSize + (2 * DigestSize);
    private const ushort LocatorPresentFlag = 1;
    private static ReadOnlySpan<byte> Magic => "WLEV"u8;

    public static byte[] BuildHeader(RawEvidenceInput input, ulong rawOrdinal, uint keyId)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsValidSourceKind((ushort)input.SourceKind))
            throw new ArgumentOutOfRangeException(nameof(input), "Unknown raw evidence source kind.");
        if (input.Payload.IsEmpty || input.Payload.Length > int.MaxValue - FrameOverhead)
            throw new ArgumentOutOfRangeException(nameof(input), "Raw evidence payload length is invalid.");
        if (keyId == 0) throw new ArgumentOutOfRangeException(nameof(keyId));
        if (input.SourceLocator is { SourceLength: 0 })
            throw new ArgumentOutOfRangeException(nameof(input), "Source locator length must be nonzero.");

        var header = new byte[FixedHeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), FormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)input.SourceKind);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), rawOrdinal);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16, 8), input.ObservedUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24, 2), input.SourceLocator is null ? (ushort)0 : LocatorPresentFlag);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(26, 4), keyId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(30, 4), checked((uint)input.Payload.Length));
        if (input.SourceLocator is { } locator)
        {
            locator.SourceId.ToArray().CopyTo(header, 34);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(50, 8), locator.SourceOffset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(58, 4), locator.SourceLength);
        }

        return header;
    }

    public static EvidenceRecordHeaderV2 ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != FixedHeaderSize)
            throw new EvidenceCorruptionException("Evidence v2 header has invalid length.");
        if (!header[..4].SequenceEqual(Magic))
            throw new EvidenceCorruptionException("Evidence v2 header magic is invalid.");
        if (BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2)) != FormatVersion)
            throw new EvidenceCorruptionException("Evidence v2 header version is invalid.");

        var sourceValue = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6, 2));
        if (!IsValidSourceKind(sourceValue))
            throw new EvidenceCorruptionException("Evidence v2 source kind is invalid.");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(24, 2));
        if ((flags & ~LocatorPresentFlag) != 0)
            throw new EvidenceCorruptionException("Evidence v2 header flags are unsupported.");
        var keyId = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(26, 4));
        var ciphertextLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(30, 4));
        if (keyId == 0 || ciphertextLength == 0)
            throw new EvidenceCorruptionException("Evidence v2 key id or ciphertext length is invalid.");
        if (header[62] != 0 || header[63] != 0)
            throw new EvidenceCorruptionException("Evidence v2 reserved bytes are nonzero.");

        RawEvidenceSourceLocator? locator = null;
        if ((flags & LocatorPresentFlag) != 0)
        {
            var sourceLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(58, 4));
            if (sourceLength == 0)
                throw new EvidenceCorruptionException("Evidence v2 source locator length is invalid.");
            locator = new RawEvidenceSourceLocator(
                FixedBytes16.FromBytes(header.Slice(34, 16)),
                BinaryPrimitives.ReadUInt64BigEndian(header.Slice(50, 8)),
                sourceLength);
        }
        else if (!header.Slice(34, 28).SequenceEqual(new byte[28]))
        {
            throw new EvidenceCorruptionException("Evidence v2 absent locator fields must be zero.");
        }

        return new EvidenceRecordHeaderV2(
            (RawEvidenceSourceKind)sourceValue,
            BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)),
            BinaryPrimitives.ReadInt64BigEndian(header.Slice(16, 8)),
            keyId,
            ciphertextLength,
            locator);
    }
    public static byte[] Encrypt(
        RawEvidenceInput input,
        ulong rawOrdinal,
        ReadOnlySpan<byte> previousDigest,
        EvidenceKeyMaterial key,
        ReadOnlySpan<byte> nonce)
    {
        if (previousDigest.Length != DigestSize)
            throw new ArgumentException("Previous digest must be exactly 32 bytes.", nameof(previousDigest));
        ArgumentNullException.ThrowIfNull(key);
        if (nonce.Length != NonceSize)
            throw new ArgumentException("Nonce must be exactly 12 bytes.", nameof(nonce));

        var header = BuildHeader(input, rawOrdinal, key.KeyId);
        var plaintext = input.Payload.ToArray();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var associatedData = BuildAssociatedData(header, previousDigest);
        using (var aes = new AesGcm(key.KeyBytes, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var digest = ComputeDigest(previousDigest, header, ciphertext, tag);
        var frame = new byte[FrameOverhead + ciphertext.Length];
        var position = 0;
        header.CopyTo(frame, position);
        position += FixedHeaderSize;
        nonce.CopyTo(frame.AsSpan(position, NonceSize)); position += NonceSize;
        ciphertext.CopyTo(frame, position); position += ciphertext.Length;
        tag.CopyTo(frame, position); position += TagSize;
        previousDigest.CopyTo(frame.AsSpan(position, DigestSize)); position += DigestSize;
        digest.CopyTo(frame, position);
        return frame;
    }

    public static DecryptedEvidenceV2 Decrypt(
        ReadOnlySpan<byte> frame,
        ReadOnlySpan<byte> expectedPreviousDigest,
        EvidenceKeyMaterial key)
    {
        if (expectedPreviousDigest.Length != DigestSize)
            throw new ArgumentException("Previous digest must be exactly 32 bytes.", nameof(expectedPreviousDigest));
        ArgumentNullException.ThrowIfNull(key);
        if (frame.Length < FrameOverhead)
            throw new EvidenceCorruptionException("Evidence v2 frame is truncated.");

        var headerBytes = frame.Slice(0, FixedHeaderSize);
        var header = ParseHeader(headerBytes);
        if (header.KeyId != key.KeyId)
            throw new EvidenceCorruptionException($"Evidence key id {header.KeyId} is unavailable.");
        var ciphertextLength = checked((int)header.CiphertextLength);
        var expectedLength = checked(FrameOverhead + ciphertextLength);
        if (frame.Length != expectedLength)
            throw new EvidenceCorruptionException("Evidence v2 frame length is invalid.");
        var nonceOffset = FixedHeaderSize;
        var ciphertextOffset = nonceOffset + NonceSize;
        var tagOffset = ciphertextOffset + ciphertextLength;
        var previousOffset = tagOffset + TagSize;
        var digestOffset = previousOffset + DigestSize;
        var storedPrevious = frame.Slice(previousOffset, DigestSize);
        var storedDigest = frame.Slice(digestOffset, DigestSize);
        if (!storedPrevious.SequenceEqual(expectedPreviousDigest))
            throw new EvidenceCorruptionException("Evidence v2 previous digest does not match chain state.");

        var expectedDigest = ComputeDigest(
            expectedPreviousDigest,
            headerBytes,
            frame.Slice(ciphertextOffset, ciphertextLength),
            frame.Slice(tagOffset, TagSize));
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, expectedDigest))
            throw new EvidenceCorruptionException("Evidence v2 record digest mismatch.");

        var plaintext = new byte[ciphertextLength];
        var associatedData = BuildAssociatedData(headerBytes, expectedPreviousDigest);
        try
        {
            using var aes = new AesGcm(key.KeyBytes, TagSize);
            aes.Decrypt(
                frame.Slice(nonceOffset, NonceSize),
                frame.Slice(ciphertextOffset, ciphertextLength), frame.Slice(tagOffset, TagSize),
                plaintext,
                associatedData);
        }
        catch (CryptographicException ex)
        {
            throw new EvidenceCorruptionException($"Evidence v2 authentication failed: {ex.Message}");
        }

        return new DecryptedEvidenceV2(
            header,
            plaintext,
            FixedBytes32.FromBytes(storedPrevious),
            FixedBytes32.FromBytes(storedDigest));
    }

    public static byte[] ComputeDigest(
        ReadOnlySpan<byte> previousDigest,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(previousDigest);
        hash.AppendData(header);
        hash.AppendData(ciphertext);
        hash.AppendData(tag);
        return hash.GetHashAndReset();
    }
    private static byte[] BuildAssociatedData(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> previousDigest)
    {
        var associatedData = new byte[header.Length + previousDigest.Length];
        header.CopyTo(associatedData);
        previousDigest.CopyTo(associatedData.AsSpan(header.Length));
        return associatedData;
    }

    private static bool IsValidSourceKind(ushort value)
        => value is >= (ushort)RawEvidenceSourceKind.LocalJournal
            and <= (ushort)RawEvidenceSourceKind.Sample;
}
