using System.Buffers.Binary;
using System.Security.Cryptography;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Tests.Evidence;

public sealed class EvidenceRecordFormatV2Tests
{
    [Fact]
    public void HeaderFreezesEveryBigEndianOffsetAndLocator()
    {
        var locator = new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"),
            0x0102030405060708UL,
            0x11223344U);
        var input = new RawEvidenceInput(
            RawEvidenceSourceKind.Sample,
            new byte[257],
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_123_456),
            locator);
        var header = EvidenceRecordFormatV2.BuildHeader(
            input, 0x1020304050607080UL, 0x12345678U);

        Assert.Equal(64, EvidenceRecordFormatV2.FixedHeaderSize);
        Assert.Equal(156, EvidenceRecordFormatV2.FrameOverhead);
        Assert.Equal(12, EvidenceRecordFormatV2.NonceSize);
        Assert.Equal(16, EvidenceRecordFormatV2.TagSize);
        Assert.Equal(64, header.Length);
        Assert.Equal("WLEV"u8.ToArray(), header[..4]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2)));
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2)));
        Assert.Equal(0x1020304050607080UL,
            BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
        Assert.Equal(1_700_000_123_456,
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16, 8)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(24, 2)));
        Assert.Equal(0x12345678U,
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(26, 4)));
        Assert.Equal(257U,
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(30, 4)));
        Assert.Equal(locator.SourceId.ToArray(), header[34..50]);
        Assert.Equal(locator.SourceOffset,
            BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(50, 8)));
        Assert.Equal(locator.SourceLength,
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(58, 4)));
        Assert.Equal(new byte[2], header[62..64]);
        Assert.Equal(locator, EvidenceRecordFormatV2.ParseHeader(header).SourceLocator);
    }

    [Fact]
    public void SourceValuesAndAbsentLocatorAreStable()
    {
        var values = Enum.GetValues<RawEvidenceSourceKind>()
            .Select(value => (ushort)value)
            .ToArray();
        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6 }, values);

        var input = Input();
        Assert.Null(input.SourceLocator);
        var header = EvidenceRecordFormatV2.BuildHeader(input, 0, 1);
        Assert.Equal(new byte[2], header[24..26]);
        Assert.Equal(new byte[30], header[34..64]);
        Assert.Null(EvidenceRecordFormatV2.ParseHeader(header).SourceLocator);
    }

    [Fact]
    public void MalformedHeadersAreRejected()
    {
        var mutations = new (int Offset, byte Value)[]
        {
            (0, 0), (5, 3), (7, 0), (7, 7),
            (24, 1), (25, 2), (29, 0), (33, 0),
            (34, 1), (62, 1), (63, 1),
        };

        foreach (var mutation in mutations)
        {
            var header = EvidenceRecordFormatV2.BuildHeader(Input(), 0, 1);
            header[mutation.Offset] = mutation.Value;
            Assert.Throws<EvidenceCorruptionException>(
                () => EvidenceRecordFormatV2.ParseHeader(header));
        }
    }

    [Fact]
    public void ShortHeaderAndZeroLengthInputAreRejected()
    {
        Assert.Throws<EvidenceCorruptionException>(
            () => EvidenceRecordFormatV2.ParseHeader(new byte[63]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EvidenceRecordFormatV2.BuildHeader(
                Input() with { Payload = ReadOnlyMemory<byte>.Empty }, 0, 1));
    }

    [Fact]
    public void EncryptionMatchesAeadAndExactDigestFormula()
    {
        var key = Key();
        var previous = SHA256.HashData("previous"u8);
        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var frame = EvidenceRecordFormatV2.Encrypt(
            Input(), 7, previous, key, nonce);

        var header = frame[..64];
        var ciphertextLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            header.AsSpan(30, 4)));
        var ciphertextStart = EvidenceRecordFormatV2.FixedHeaderSize + EvidenceRecordFormatV2.NonceSize;
        var tagStart = ciphertextStart + ciphertextLength;
        var previousStart = tagStart + EvidenceRecordFormatV2.TagSize;
        var digestStart = previousStart + 32;

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key.KeyBytes, EvidenceRecordFormatV2.TagSize);
        var associated = header.Concat(previous).ToArray();
        aes.Decrypt(
            frame.AsSpan(64, 12),
            frame.AsSpan(ciphertextStart, ciphertextLength),
            frame.AsSpan(tagStart, 16),
            plaintext,
            associated);

        Assert.Equal(Input().Payload.ToArray(), plaintext);
        Assert.Equal(previous, frame.AsSpan(previousStart, 32).ToArray());
        var digestInput = previous
            .Concat(header)
            .Concat(frame.AsSpan(ciphertextStart, ciphertextLength).ToArray())
            .Concat(frame.AsSpan(tagStart, 16).ToArray())
            .ToArray();
        Assert.Equal(SHA256.HashData(digestInput), frame.AsSpan(digestStart, 32).ToArray());
        Assert.Equal(plaintext,
            EvidenceRecordFormatV2.Decrypt(frame, previous, key).Plaintext);
    }

    [Fact]
    public void EveryStoredFrameComponentIsAuthenticated()
    {
        var previous = SHA256.HashData("chain"u8);
        var key = Key();
        var nonce = Enumerable.Range(20, 12).Select(i => (byte)i).ToArray();
        var original = EvidenceRecordFormatV2.Encrypt(Input(), 3, previous, key, nonce);
        var header = EvidenceRecordFormatV2.ParseHeader(original.AsSpan(0, 64));
        var cipherStart = 64 + 12;
        var tagStart = cipherStart + checked((int)header.CiphertextLength);
        var previousStart = tagStart + 16;
        var digestStart = previousStart + 32;
        var offsets = new[] { 7, 64, cipherStart, tagStart, previousStart, digestStart };

        foreach (var offset in offsets)
        {
            var changed = original.ToArray();
            changed[offset] = unchecked((byte)(changed[offset] + 1));
            Assert.Throws<EvidenceCorruptionException>(
                () => EvidenceRecordFormatV2.Decrypt(changed, previous, key));
        }
    }
    private static EvidenceKeyMaterial Key()
        => new(7, Enumerable.Repeat((byte)3, 32).ToArray());

    private static RawEvidenceInput Input()
        => new(
            RawEvidenceSourceKind.FrontierApi,
            System.Text.Encoding.UTF8.GetBytes("abc"),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
}
