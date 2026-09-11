using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.Integration.Tests;

public sealed class FsdJumpVerticalSliceTests
{
    [Fact]
    public async Task RealFixtureProducesOneAuthoritativeJumpOutput()
    {
        var root = RepoRoot(); var data = TempData(); var kernel = Kernel(root);
        await using var runner = await VerticalSliceRunner.OpenAsync(data, kernel);
        foreach (var line in File.ReadLines(Path.Combine(root, "fixtures", "journal", "live-v4-fsdjump-session.jsonl")))
            await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(line));

        var output = Assert.Single(runner.Outputs);
        Assert.Equal("Jump complete: W. Grantler NX-42 - 55.359 ly, fuel 27.123 t.", output.Text);
        Assert.Equal(2UL, output.Cursor.EvidenceSequence);
        Assert.NotNull(runner.FinalStateDigest);
        Assert.Equal(runner.FinalStateDigest, output.StateDigest);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WOLPERTINGER.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    internal static string Kernel(string root)
    {
        var path = Path.Combine(root, "kernel", "bin", "wolpertinger_kernel_main.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Build trusted kernel before integration tests.", path);
    }
    internal static string TempData() { var p = Path.Combine(Path.GetTempPath(), "wolpertinger-int", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }

    [Fact]
    public async Task ProductRuntimeReopensTrustedStateAndContinuesWithV2Evidence()
    {
        var root = RepoRoot();
        var data = TempData();
        var paths = ProductRuntimePaths.ForLive(data);
        var kernel = Kernel(root);
        var protector = new TestProtector();
        var pipeName = $"wolpertinger.integration.{Guid.NewGuid():N}";
        var lines = File.ReadLines(Path.Combine(root, "fixtures", "journal", "live-v4-fsdjump-session.jsonl")).ToArray();
        var records = ToJournalRecords(lines).ToArray();

        FixedBytes32 firstDigest;
        ObservationCursor firstCursor;
        await using (var runtime = await ProductEdgeRuntime.OpenAsync(paths, kernel, protector, pipeName))
        {
            foreach (var record in records) await runtime.ProcessJournalRecordAsync(record);
            Assert.Single(runtime.Outputs);
            firstDigest = runtime.FinalStateDigest!.Value;
            firstCursor = runtime.KernelDiagnostics.LastAgreedCursor!.Value;
            Assert.Equal(2UL, firstCursor.EvidenceSequence);
        }

        await using (var reopened = await ProductEdgeRuntime.OpenAsync(paths, kernel, protector, pipeName))
        {
            Assert.Equal(KernelSupervisorLifecycle.Synchronized, reopened.KernelDiagnostics.Lifecycle);
            Assert.Equal(firstDigest, reopened.FinalStateDigest);
            Assert.Equal(firstCursor, reopened.KernelDiagnostics.LastAgreedCursor);
            var next = ToJournalRecord(lines[^1], records[^1].Locator.SourceOffset + records[^1].Locator.SourceLength);
            await reopened.ProcessJournalRecordAsync(next);
            Assert.Equal(3UL, reopened.KernelDiagnostics.LastAgreedCursor!.Value.EvidenceSequence);
            Assert.Single(reopened.Outputs);
        }
    }

    private static IEnumerable<JournalSourceRecord> ToJournalRecords(IEnumerable<string> lines)
    {
        ulong offset = 0;
        foreach (var line in lines)
        {
            var record = ToJournalRecord(line, offset);
            offset += record.Locator.SourceLength;
            yield return record;
        }
    }

    private static JournalSourceRecord ToJournalRecord(string line, ulong offset)
    {
        var payload = Encoding.UTF8.GetBytes(line);
        return new JournalSourceRecord(payload, new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"), offset, checked((uint)payload.Length + 1)));
    }

    private sealed class TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }
}
