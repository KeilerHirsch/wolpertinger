using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Frontier;

namespace Wolpertinger.Edge.Tests.Frontier;

public sealed class FrontierCapiClientTests
{
    [Fact]
    public void R0BoundsAreFrozen()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), FrontierCapiClient.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), FrontierAccountService.MinimumAutomaticRefreshInterval);
        Assert.Equal(
            new[] { 15, 30, 60, 120 },
            FrontierAccountService.RetryDelays.Select(value => (int)value.TotalSeconds));
    }

    [Fact]
    public async Task SuccessfulProfileIsDurableFrontierEvidenceBeforeReturn()
    {
        using var temp = new TempDirectory();
        var protector = new TestProtector();
        var keyStore = new EvidenceKeyStore(Path.Combine(temp.Path, "evidence-key.bin"), protector);
        await using var evidence = await EncryptedSegmentedEvidenceLog.OpenAsync(
            Path.Combine(temp.Path, "evidence"), keyStore);
        var body = Encoding.UTF8.GetBytes("{\"commander\":\"Test\"}");
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("https://capi.example/profile"), request.RequestUri);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "access-secret"), request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
        }));
        var observed = DateTimeOffset.Parse("2026-09-12T20:30:00Z");
        var client = new FrontierCapiClient(
            http,
            new Uri("https://capi.example/profile"),
            evidence,
            () => observed);

        var result = await client.GetProfileAsync("access-secret");
        await evidence.DisposeAsync();

        Assert.True(result.EvidenceReceipt.IsDurable);
        Assert.Equal(RawEvidenceSourceKind.FrontierApi, result.EvidenceReceipt.SourceKind);
        Assert.Equal(body, result.ResponseBytes);
        Assert.Equal(observed, result.ResponseObservedUtc);
        Assert.Equal(new Uri("https://capi.example/profile"), result.SourceUri);
        var recovered = await EvidenceLogRecovery.RecoverAsync(
            Path.Combine(temp.Path, "evidence"), keyStore);
        var item = Assert.Single(recovered);
        Assert.Equal(RawEvidenceSourceKind.FrontierApi, item.SourceKind);
        Assert.Equal(body, item.Payload);
    }

    [Fact]
    public async Task NonSuccessWritesNoEvidence()
    {
        using var temp = new TempDirectory();
        var protector = new TestProtector();
        var keyStore = new EvidenceKeyStore(Path.Combine(temp.Path, "evidence-key.bin"), protector);
        var evidenceDirectory = Path.Combine(temp.Path, "evidence");
        await using var evidence = await EncryptedSegmentedEvidenceLog.OpenAsync(evidenceDirectory, keyStore);
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = new FrontierCapiClient(
            http,
            new Uri("https://capi.example/profile"),
            evidence);

        var error = await Assert.ThrowsAsync<FrontierCapiException>(() =>
            client.GetProfileAsync("access-secret"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        await evidence.DisposeAsync();
        Assert.Empty(await EvidenceLogRecovery.RecoverAsync(evidenceDirectory, keyStore));
    }
    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class TestProtector : IEvidenceKeyProtector
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
                "wolpertinger-capi-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
