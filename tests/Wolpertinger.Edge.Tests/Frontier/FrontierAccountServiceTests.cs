using System.Net;
using System.Text;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Frontier;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Tests.Frontier;

public sealed class FrontierAccountServiceTests
{
    [Fact]
    public async Task CallbackPersistsRefreshCredentialAndPublishesConnected()
    {
        using var env = await TestEnvironment.CreateAsync();
        var state = await env.Service.ConnectAsync();
        var authState = ParseQuery(state.Query)["state"];

        await env.Service.HandleCallbackAsync(new Uri(
            $"wolpertinger://frontier/oauth?code=ok&state={authState}"));

        var stored = await env.TokenStore.LoadAsync();
        Assert.Equal("refresh-1", stored!.RefreshToken);
        Assert.Equal(FrontierAccountState.Connected, env.Service.Current.State);
        Assert.Equal(PresentationFreshness.Unknown, env.Service.Current.Freshness);
    }
    [Fact]
    public async Task FirstUnauthorizedRefreshesOnceThenProfileSucceeds()
    {
        using var env = await TestEnvironment.CreateAsync(
            profileStatuses: [HttpStatusCode.Unauthorized, HttpStatusCode.OK]);
        await env.SeedConnectedAsync();

        var result = await env.Service.RefreshProfileAsync();

        Assert.NotNull(result);
        Assert.Equal(1, env.RefreshTokenExchangeCount);
        Assert.Equal(2, env.ProfileRequestCount);
        Assert.Equal("refresh-2", (await env.TokenStore.LoadAsync())!.RefreshToken);
        Assert.Equal(FrontierAccountState.Connected, env.Service.Current.State);
        Assert.Equal(PresentationFreshness.Current, env.Service.Current.Freshness);
        Assert.NotNull(env.Service.Current.LastSuccessUnixMs);
    }

    [Fact]
    public async Task OAuthSecretsNeverEnterRawEvidenceSegments()
    {
        using var env = await TestEnvironment.CreateAsync(
            profileStatuses: [HttpStatusCode.Unauthorized, HttpStatusCode.OK]);
        await env.SeedConnectedAsync();
        Assert.NotNull(await env.Service.RefreshProfileAsync());

        var raw = Encoding.UTF8.GetString(env.ReadRawEvidenceBytes());
        var rawTokenStore = Encoding.UTF8.GetString(env.ReadRawTokenStoreBytes());
        foreach (var secret in new[] { "access-1", "refresh-1", "access-2", "refresh-2" })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, rawTokenStore, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SecondUnauthorizedRequiresReauthentication()
    {
        using var env = await TestEnvironment.CreateAsync(
            profileStatuses: [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized]);
        await env.SeedConnectedAsync();
        var result = await env.Service.RefreshProfileAsync();

        Assert.Null(result);
        Assert.Equal(1, env.RefreshTokenExchangeCount);
        Assert.Equal(2, env.ProfileRequestCount);
        Assert.Equal(FrontierAccountState.ReauthenticationRequired, env.Service.Current.State);
        Assert.NotEqual(PresentationFreshness.Current, env.Service.Current.Freshness);
    }

    [Fact]
    public async Task AutomaticRefreshHonorsSixtySecondCooldown()
    {
        using var env = await TestEnvironment.CreateAsync(profileStatuses: [HttpStatusCode.OK]);
        await env.SeedConnectedAsync();
        Assert.NotNull(await env.Service.RefreshProfileAsync());
        var calls = env.ProfileRequestCount;

        var skipped = await env.Service.RefreshProfileAsync(automatic: true);

        Assert.Null(skipped);
        Assert.Equal(calls, env.ProfileRequestCount);
    }

    [Fact]
    public async Task AutomaticFailuresUseBoundedRetrySchedule()
    {
        using var env = await TestEnvironment.CreateAsync(profileStatuses:
            [HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        await env.SeedConnectedAsync();
        Assert.Null(await env.Service.RefreshProfileAsync());
        Assert.Equal(1, env.ProfileRequestCount);

        env.Now += TimeSpan.FromSeconds(14);
        Assert.Null(await env.Service.RefreshProfileAsync(automatic: true));
        Assert.Equal(1, env.ProfileRequestCount);
        env.Now += TimeSpan.FromSeconds(1);
        Assert.Null(await env.Service.RefreshProfileAsync(automatic: true));
        Assert.Equal(2, env.ProfileRequestCount);

        env.Now += TimeSpan.FromSeconds(29);
        Assert.Null(await env.Service.RefreshProfileAsync(automatic: true));
        Assert.Equal(2, env.ProfileRequestCount);
        env.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(await env.Service.RefreshProfileAsync(automatic: true));
        Assert.Equal(3, env.ProfileRequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ServiceFailureMarksUnavailableWithoutThrowing(HttpStatusCode status)
    {
        using var env = await TestEnvironment.CreateAsync(profileStatuses: [status]);
        await env.SeedConnectedAsync();

        var result = await env.Service.RefreshProfileAsync();

        Assert.Null(result);
        Assert.Equal(FrontierAccountState.Unavailable, env.Service.Current.State);
        Assert.NotEqual(PresentationFreshness.Current, env.Service.Current.Freshness);
    }

    [Fact]
    public async Task DisconnectDeletesCredentialAndPublishesDisconnected()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.SeedConnectedAsync();

        await env.Service.DisconnectAsync();

        Assert.Null(await env.TokenStore.LoadAsync());
        Assert.Equal(FrontierAccountState.Disconnected, env.Service.Current.State);
        Assert.Equal(PresentationFreshness.Unknown, env.Service.Current.Freshness);
    }

    private static Dictionary<string, string> ParseQuery(string value)
        => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]),
                part => part.Length == 1 ? string.Empty : Uri.UnescapeDataString(part[1]));
    private sealed class TestEnvironment : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly EncryptedSegmentedEvidenceLog _evidence;
        private readonly Queue<HttpStatusCode> _profileStatuses;
        private readonly HttpClient _http;
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-12T20:40:00Z");

        private TestEnvironment(
            TempDirectory temp,
            EncryptedSegmentedEvidenceLog evidence,
            Queue<HttpStatusCode> profileStatuses,
            HttpClient http,
            FrontierTokenStore tokenStore,
            FrontierAccountService service)
        {
            _temp = temp;
            _evidence = evidence;
            _profileStatuses = profileStatuses;
            _http = http;
            TokenStore = tokenStore;
            Service = service;
        }

        public FrontierTokenStore TokenStore { get; }
        public FrontierAccountService Service { get; }
        public int RefreshTokenExchangeCount { get; private set; }
        public int ProfileRequestCount { get; private set; }
        public byte[] ReadRawTokenStoreBytes()
            => File.ReadAllBytes(Path.Combine(_temp.Path, "frontier-token.bin"));

        public byte[] ReadRawEvidenceBytes()
        {
            using var output = new MemoryStream();
            foreach (var path in Directory.GetFiles(
                         Path.Combine(_temp.Path, "evidence"), "evidence-*.wlev"))
            {
                using var input = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                input.CopyTo(output);
            }
            return output.ToArray();
        }
        public static async Task<TestEnvironment> CreateAsync(
            IEnumerable<HttpStatusCode>? profileStatuses = null)
        {
            var temp = new TempDirectory();
            var protector = new TestProtector();
            var keyStore = new EvidenceKeyStore(Path.Combine(temp.Path, "evidence-key.bin"), protector);
            var evidence = await EncryptedSegmentedEvidenceLog.OpenAsync(
                Path.Combine(temp.Path, "evidence"), keyStore);
            var statuses = new Queue<HttpStatusCode>(profileStatuses ?? [HttpStatusCode.OK]);
            TestEnvironment? env = null;
            var http = new HttpClient(new StubHandler(async request =>
                env is null
                    ? throw new InvalidOperationException("Test environment is not initialized.")
                    : await env.HandleAsync(request)));
            var browser = new RecordingBrowser();
            var oauth = new FrontierOAuthClient(Options(), http, browser);
            var store = new FrontierTokenStore(Path.Combine(temp.Path, "frontier-token.bin"), protector);
            var capi = new FrontierCapiClient(http, Options().CapiProfileEndpoint, evidence, () => env!.Now);
            var service = new FrontierAccountService(oauth, store, capi, () => env!.Now);
            env = new TestEnvironment(temp, evidence, statuses, http, store, service);
            return env;
        }

        public async Task SeedConnectedAsync()
        {
            var authorization = await Service.ConnectAsync();
            var state = ParseQuery(authorization.Query)["state"];
            await Service.HandleCallbackAsync(new Uri(
                $"wolpertinger://frontier/oauth?code=ok&state={state}"));
        }
        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            if (request.RequestUri == Options().TokenEndpoint)
            {
                var form = ParseQuery(await request.Content!.ReadAsStringAsync());
                if (form["grant_type"] == "refresh_token")
                {
                    RefreshTokenExchangeCount++;
                    return Json(HttpStatusCode.OK,
                        "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":3600}");
                }
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}");
            }

            Assert.Equal(Options().CapiProfileEndpoint, request.RequestUri);
            ProfileRequestCount++;
            var status = _profileStatuses.Count > 0
                ? _profileStatuses.Dequeue()
                : HttpStatusCode.OK;
            return status == HttpStatusCode.OK
                ? Json(status, "{\"commander\":\"Test\"}")
                : new HttpResponseMessage(status);
        }

        public void Dispose()
        {
            _http.Dispose();
            _evidence.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _temp.Dispose();
        }
    }
    private static FrontierOAuthOptions Options() => new(
        new Uri("https://auth.example/authorize"),
        new Uri("https://auth.example/token"),
        new Uri("https://auth.example/decode"),
        new Uri("https://capi.example/profile"),
        "wolpertinger-client",
        new Uri("wolpertinger://frontier/oauth"),
        "capi",
        "frontier");

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingBrowser : IExternalBrowser
    {
        public void Open(Uri uri) { }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
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
                "wolpertinger-frontier-account-tests",
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
