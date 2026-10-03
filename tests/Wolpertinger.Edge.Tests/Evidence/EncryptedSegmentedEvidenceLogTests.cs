using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Tests.Evidence;

public sealed class EncryptedSegmentedEvidenceLogTests
{
    [Fact]
    public async Task AppendEncryptsPayloadAndRecoveryRestoresLocator()
    {
        using var temp = new TempDirectory();
        var store = KeyStore(temp.Path);
        var payload = Encoding.UTF8.GetBytes("SECRET-R0-PAYLOAD-MARKER");
        var locator = new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("102132435465768798A9BACBDCEDFE0F"), 1234, (uint)payload.Length);
        RawEvidenceReceipt receipt;

        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(
            Path.Combine(temp.Path, "evidence"), store, 4096))
        {
            receipt = await log.AppendAsync(new RawEvidenceInput(
                RawEvidenceSourceKind.FrontierApi,
                payload,
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
                locator));
        }

        var bytes = await File.ReadAllBytesAsync(Path.Combine(
            temp.Path, "evidence", "evidence-00000000.wlev"));
        Assert.True(bytes.AsSpan().IndexOf(payload) < 0);
        var recovered = await EvidenceLogRecovery.RecoverAsync(
            Path.Combine(temp.Path, "evidence"), store);
        var record = Assert.Single(recovered);
        Assert.Equal(payload, record.Payload);
        Assert.Equal(locator, record.SourceLocator);
        Assert.Equal(receipt.EvidenceDigest, record.EvidenceDigest);
        Assert.True(receipt.IsDurable);
    }

    [Fact]
    public async Task RotatesSegmentsAndReopenContinuesOrdinalAndChain()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "evidence");
        var store = KeyStore(temp.Path);
        var firstPayload = Encoding.UTF8.GetBytes("first-v2-record");
        var secondPayload = Encoding.UTF8.GetBytes("second-v2-record");
        RawEvidenceReceipt first;

        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(
            directory, store, EvidenceRecordFormatV2.FrameOverhead + firstPayload.Length + 1))
        {
            first = await log.AppendAsync(Input(firstPayload, 1));
        }

        RawEvidenceReceipt second;
        await using (var reopened = await EncryptedSegmentedEvidenceLog.OpenAsync(
            directory, store, EvidenceRecordFormatV2.FrameOverhead + firstPayload.Length + 1))
        {
            second = await reopened.AppendAsync(Input(secondPayload, 2));
        }

        Assert.Equal(0UL, first.Reference.RawOrdinal);
        Assert.Equal(1UL, second.Reference.RawOrdinal);
        Assert.Equal(0U, first.Reference.SegmentNumber);
        Assert.Equal(1U, second.Reference.SegmentNumber);

        var recovered = await EvidenceLogRecovery.RecoverAsync(directory, store);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(first.EvidenceDigest, recovered[1].PreviousDigest);
        Assert.Equal(second.EvidenceDigest, recovered[1].EvidenceDigest);
    }

    [Fact]
    public async Task IncompleteFinalTailIsTruncatedToLastValidFrame()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "evidence");
        var store = KeyStore(temp.Path);
        RawEvidenceReceipt receipt;
        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(directory, store, 4096))
        {
            receipt = await log.AppendAsync(Input(Encoding.UTF8.GetBytes("complete"), 1));
        }

        var path = Path.Combine(directory, "evidence-00000000.wlev");
        await using (var tail = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await tail.WriteAsync(Encoding.UTF8.GetBytes("partial-tail"));
        }
        var recovered = await EvidenceLogRecovery.RecoverAsync(directory, store);
        Assert.Single(recovered);
        Assert.Equal(receipt.Reference.FrameLength, new FileInfo(path).Length);
    }

    [Fact]
    public async Task CompleteCorruptionFailsClosedWithoutTailTruncation()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "evidence");
        var store = KeyStore(temp.Path);
        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(directory, store, 4096))
        {
            _ = await log.AppendAsync(Input(Encoding.UTF8.GetBytes("record-one"), 1));
            _ = await log.AppendAsync(Input(Encoding.UTF8.GetBytes("record-two"), 2));
        }

        var path = Path.Combine(directory, "evidence-00000000.wlev");
        var originalLength = new FileInfo(path).Length;
        var bytes = await File.ReadAllBytesAsync(path);
        var firstHeader = EvidenceRecordFormatV2.ParseHeader(bytes.AsSpan(0, 64));
        var firstTagOffset = 64 + 12 + checked((int)firstHeader.CiphertextLength);
        bytes[firstTagOffset] = unchecked((byte)(bytes[firstTagOffset] + 1));
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<EvidenceCorruptionException>(
            () => EvidenceLogRecovery.RecoverAsync(directory, store));
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }
    [Fact]
    public async Task V2WriterRotatesAfterExistingV1AndRecoveryReadsBoth()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "evidence");
        await using (var v1 = await SegmentedEvidenceLog.OpenAsync(directory, 4096))
        {
            _ = await v1.AppendAsync(Input(Encoding.UTF8.GetBytes("legacy-v1"), 1));
        }

        var store = KeyStore(temp.Path);
        RawEvidenceReceipt v2Receipt;
        await using (var v2 = await EncryptedSegmentedEvidenceLog.OpenAsync(directory, store, 4096))
        {
            v2Receipt = await v2.AppendAsync(Input(Encoding.UTF8.GetBytes("encrypted-v2"), 2));
        }

        Assert.Equal(1U, v2Receipt.Reference.SegmentNumber);
        var recovered = await EvidenceLogRecovery.RecoverAsync(directory, store);
        Assert.Equal(2, recovered.Count);
        Assert.Equal("legacy-v1", Encoding.UTF8.GetString(recovered[0].Payload));
        Assert.Equal("encrypted-v2", Encoding.UTF8.GetString(recovered[1].Payload));
        Assert.Equal(recovered[0].EvidenceDigest, recovered[1].PreviousDigest);
    }

    [Fact]
    public async Task V2RecoveryRequiresExistingMatchingKey()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "evidence");
        var store = KeyStore(temp.Path);
        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(directory, store, 4096))
        {
            _ = await log.AppendAsync(Input(Encoding.UTF8.GetBytes("needs-key"), 1));
        }

        File.Delete(Path.Combine(temp.Path, "evidence-key.bin"));
        var missing = KeyStore(temp.Path);
        await Assert.ThrowsAsync<EvidenceCorruptionException>(
            () => EvidenceLogRecovery.RecoverAsync(directory, missing));
    }

    private static EvidenceKeyStore KeyStore(string root)
        => new(Path.Combine(root, "evidence-key.bin"), new Base64TestProtector());

    private static RawEvidenceInput Input(byte[] payload, long millisecond)
        => new(
            RawEvidenceSourceKind.LocalJournal,
            payload,
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + millisecond));

    private sealed class Base64TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-encrypted-evidence-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
