using System.Net;
using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Presentation.Contracts;
using static Wolpertinger.Integration.Tests.FsdJumpVerticalSliceTests;

namespace Wolpertinger.Integration.Tests;

public sealed class FrontierProfilePipelineTests
{
    [Fact]
    public async Task DurableFrontierProfileRunsThroughTrustedKernelAndPresentation()
    {
        var root = RepoRoot();
        var data = TempData();
        var paths = ProductRuntimePaths.ForLive(data);
        var pipeName = $"wolpertinger.frontier.{Guid.NewGuid():N}";
        var protector = new TestProtector();

        var body = await File.ReadAllBytesAsync(
            Path.Combine(root, "fixtures", "capi", "profile-r0-sanitized.json"));

        await using (var runtime = await ProductEdgeRuntime.OpenAsync(
                         paths,
                         Kernel(root),
                         protector,
                         pipeName))
        {
            await runtime.ProcessJournalRecordAsync(Record(
                """{"event":"Fileheader","part":1,"gameversion":"4.2.2.0","Odyssey":true}""",
                0));
            await runtime.ProcessJournalRecordAsync(Record(
                """{"event":"Commander","FID":"FTEST0001","Name":"CMDR TEST"}""",
                100));

            using var http = new HttpClient(new StaticHandler(body));
            var capi = runtime.CreateFrontierCapiClient(
                http,
                new Uri("https://companion.orerve.net/profile"));

            var snapshot = await capi.GetProfileAsync("test-access-token");
            Assert.True(snapshot.EvidenceReceipt.IsDurable);
            Assert.Equal(
                RawEvidenceSourceKind.FrontierApi,
                snapshot.EvidenceReceipt.SourceKind);

            await runtime.ProcessFrontierProfileSnapshotAsync(snapshot);

            var commander = Assert.IsType<CommanderVesselPresentation>(
                runtime.CurrentPresentation.CommanderVessel);
            Assert.Equal("REDACTED", commander.CommanderName);
            Assert.Equal(PresentationProvenance.FrontierApi, commander.Provenance);
            Assert.Equal(PresentationFreshness.Current, commander.Freshness);
            Assert.Equal(
                2UL,
                runtime.KernelDiagnostics.LastAgreedCursor?.EvidenceSequence);
        }

        var keyStore = new EvidenceKeyStore(paths.EvidenceKeyPath, protector);
        var recovered = await EvidenceLogRecovery.RecoverAsync(
            paths.EvidenceDirectory,
            keyStore);
        Assert.Equal(
            RawEvidenceSourceKind.FrontierApi,
            recovered[^1].SourceKind);
        Assert.Equal(body, recovered[^1].Payload);
    }

    private static JournalSourceRecord Record(string json, ulong offset)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return new JournalSourceRecord(
            payload,
            new RawEvidenceSourceLocator(
                FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"),
                offset,
                checked((uint)payload.Length + 1)));
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(
                new Uri("https://companion.orerve.net/profile"),
                request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    private sealed class TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }
}
