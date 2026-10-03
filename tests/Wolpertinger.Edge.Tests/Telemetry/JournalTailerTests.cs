using System.Security.Cryptography;
using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Telemetry;

namespace Wolpertinger.Edge.Tests.Telemetry;

public sealed class JournalTailerTests
{
    [Fact]
    public void SourceIdUsesUpperInvariantBaseFileNameOnly()
    {
        const string fileName = "Journal.2026-09-11T120000.01.log";
        var left = JournalSourceId.FromFileName(Path.Combine("C:\\one", fileName));
        var right = JournalSourceId.FromFileName(Path.Combine("D:\\two", fileName));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(fileName.ToUpperInvariant()));

        Assert.Equal(left, right);
        Assert.Equal(expectedHash[..16], left.ToArray());
    }

    [Theory]
    [InlineData("\n", 4)]
    [InlineData("\r\n", 5)]
    public async Task EmitsPayloadWithoutNewlineAndLocatorConsumesExactSourceBytes(
        string newline,
        uint expectedLength)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Journal.2026-09-11T120000.01.log");
        await File.WriteAllTextAsync(path, "abc" + newline, new UTF8Encoding(false));

        var records = await CollectAsync(new JournalTailer(temp.Path).ReadAvailableAsync([]));
        var record = Assert.Single(records);
        Assert.Equal("abc", Encoding.UTF8.GetString(record.Payload));
        Assert.Equal(0UL, record.Locator.SourceOffset);
        Assert.Equal(expectedLength, record.Locator.SourceLength);
        Assert.Equal(JournalSourceId.FromFileName(path), record.Locator.SourceId);
    }

    [Fact]
    public async Task SameFrontierTimestampRetainsFileOrder()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Journal.2026-09-11T120100.01.log");
        var line1 = "{\"timestamp\":\"2026-09-11T12:01:00Z\",\"event\":\"One\"}";
        var line2 = "{\"timestamp\":\"2026-09-11T12:01:00Z\",\"event\":\"Two\"}";
        await File.WriteAllTextAsync(path, line1 + "\n" + line2 + "\n", new UTF8Encoding(false));

        var records = await CollectAsync(new JournalTailer(temp.Path).ReadAvailableAsync([]));

        Assert.Equal(2, records.Count);
        Assert.Equal(line1, Encoding.UTF8.GetString(records[0].Payload));
        Assert.Equal(line2, Encoding.UTF8.GetString(records[1].Payload));
        Assert.True(records[1].Locator.SourceOffset > records[0].Locator.SourceOffset);
    }

    [Fact]
    public async Task IncompleteTrailingLineIsHeldUntilCompleted()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Journal.2026-09-11T120200.01.log");
        await File.WriteAllTextAsync(path, "first\npartial", new UTF8Encoding(false));
        var tailer = new JournalTailer(temp.Path);

        var firstRead = await CollectAsync(tailer.ReadAvailableAsync([]));
        Assert.Single(firstRead);
        Assert.Equal("first", Encoding.UTF8.GetString(firstRead[0].Payload));

        await File.AppendAllTextAsync(path, "-done\n", new UTF8Encoding(false));
        var secondRead = await CollectAsync(tailer.ReadAvailableAsync([]));
        var completed = Assert.Single(secondRead);
        Assert.Equal("partial-done", Encoding.UTF8.GetString(completed.Payload));
        Assert.Equal((ulong)"first\n"u8.Length, completed.Locator.SourceOffset);
    }

    [Fact]
    public async Task ReopenFromRecoveredLocatorEmitsOnlyFollowingLine()
    {
        using var temp = new TempDirectory();
        var journalPath = Path.Combine(temp.Path, "Journal.2026-09-11T120300.01.log");
        await File.WriteAllTextAsync(journalPath, "first\n", new UTF8Encoding(false));
        var firstTailer = new JournalTailer(temp.Path);
        var firstRecord = Assert.Single(await CollectAsync(firstTailer.ReadAvailableAsync([])));

        var evidenceDir = Path.Combine(temp.Path, "evidence");
        var keyStore = new EvidenceKeyStore(
            Path.Combine(temp.Path, "evidence-key.bin"),
            new Base64TestProtector());
        await using (var log = await EncryptedSegmentedEvidenceLog.OpenAsync(evidenceDir, keyStore, 4096))
        {
            _ = await log.AppendAsync(new RawEvidenceInput(
                RawEvidenceSourceKind.LocalJournal,
                firstRecord.Payload,
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
                firstRecord.Locator));
        }

        await File.AppendAllTextAsync(journalPath, "second\n", new UTF8Encoding(false));
        var recovered = await EvidenceLogRecovery.RecoverAsync(evidenceDir, keyStore);
        var reopened = new JournalTailer(temp.Path);
        var next = Assert.Single(await CollectAsync(reopened.ReadAvailableAsync(recovered)));

        Assert.Equal("second", Encoding.UTF8.GetString(next.Payload));
        Assert.True(next.Locator.SourceOffset > firstRecord.Locator.SourceOffset);
    }

    [Fact]
    public async Task LaterJournalIsReadAfterCurrentFileIsFullyConsumed()
    {
        using var temp = new TempDirectory();
        var firstPath = Path.Combine(temp.Path, "Journal.2026-09-11T120400.01.log");
        await File.WriteAllTextAsync(firstPath, "old\n", new UTF8Encoding(false));
        var tailer = new JournalTailer(temp.Path);

        var oldRecord = Assert.Single(await CollectAsync(tailer.ReadAvailableAsync([])));
        Assert.Equal("old", Encoding.UTF8.GetString(oldRecord.Payload));

        var nextPath = Path.Combine(temp.Path, "Journal.2026-09-11T120500.01.log");
        await File.WriteAllTextAsync(nextPath, "new\n", new UTF8Encoding(false));
        var newRecord = Assert.Single(await CollectAsync(tailer.ReadAvailableAsync([])));

        Assert.Equal("new", Encoding.UTF8.GetString(newRecord.Payload));
        Assert.Equal(JournalSourceId.FromFileName(nextPath), newRecord.Locator.SourceId);
        Assert.Equal(0UL, newRecord.Locator.SourceOffset);
    }

    [Fact]
    public async Task UnknownRecoveredSourceIdFailsClosed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Journal.2026-09-11T120600.01.log");
        await File.WriteAllTextAsync(path, "line\n", new UTF8Encoding(false));
        var unknown = new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), 0, 5);
        var recovered = new[] { RecoveredLocalJournal(unknown) };

        await Assert.ThrowsAsync<EvidenceCorruptionException>(async () =>
            _ = await CollectAsync(new JournalTailer(temp.Path).ReadAvailableAsync(recovered)));
    }

    private static RecoveredRawEvidence RecoveredLocalJournal(RawEvidenceSourceLocator locator)
        => new(
            new EvidenceReference(0, 0, 0, 1),
            RawEvidenceSourceKind.LocalJournal,
            "x"u8.ToArray(),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            FixedBytes32.FromBytes(new byte[32]),
            FixedBytes32.FromBytes(SHA256.HashData("x"u8)),
            locator);

    private static async Task<List<JournalSourceRecord>> CollectAsync(
        IAsyncEnumerable<JournalSourceRecord> source)
    {
        var result = new List<JournalSourceRecord>();
        await foreach (var record in source)
        {
            result.Add(record);
        }

        return result;
    }

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
                "wolpertinger-journal-tailer-tests",
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
