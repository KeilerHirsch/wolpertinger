using System.Net;
using System.Security.Cryptography;
using System.Text;
using Wolpertinger.Edge.Frontier;

namespace Wolpertinger.Edge.Tests.Frontier;

public sealed class FrontierOAuthClientTests
{
    [Fact]
    public async Task BeginAuthorizationUsesIndependentPkceAndState()
    {
        var browser = new RecordingBrowser();
        using var http = new HttpClient(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException())));
        var client = new FrontierOAuthClient(Options(), http, browser);

        var authorization = await client.BeginAuthorizationAsync();
        Assert.Equal(authorization, browser.LastOpened);

        var query = ParseQuery(authorization.Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Options().ClientId, query["client_id"]);
        Assert.Equal(Options().RedirectUri.ToString(), query["redirect_uri"]);
        Assert.Equal(Options().Scope, query["scope"]);
        Assert.DoesNotContain("=", query["code_challenge"]);
        Assert.DoesNotContain("=", query["state"]);
        Assert.NotEqual(query["code_challenge"], query["state"]);
    }
    [Fact]
    public async Task CallbackExchangesSameVerifierAndIsConsumedOnce()
    {
        var browser = new RecordingBrowser();
        Dictionary<string, string>? tokenForm = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            Assert.Equal(Options().TokenEndpoint, request.RequestUri);
            tokenForm = ParseQuery(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK,
                "{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600}");
        }));
        var client = new FrontierOAuthClient(Options(), http, browser);
        var authorization = await client.BeginAuthorizationAsync();
        var authorizationQuery = ParseQuery(authorization.Query);
        var state = authorizationQuery["state"];

        var result = await client.HandleCallbackAsync(
            new Uri($"{Options().RedirectUri}?code=abc123&state={state}"));

        Assert.Equal("access-secret", result.AccessToken);
        Assert.Equal("refresh-secret", result.RefreshToken);
        Assert.NotNull(tokenForm);
        Assert.Equal("authorization_code", tokenForm!["grant_type"]);
        Assert.Equal("abc123", tokenForm["code"]);
        Assert.Equal(Options().RedirectUri.ToString(), tokenForm["redirect_uri"]);
        Assert.Equal(Options().ClientId, tokenForm["client_id"]);
        var verifier = tokenForm["code_verifier"];
        Assert.Equal(32, Base64UrlDecode(verifier).Length);
        Assert.Equal(authorizationQuery["code_challenge"],
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        Assert.Equal(32, Base64UrlDecode(state).Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.HandleCallbackAsync(
                new Uri($"{Options().RedirectUri}?code=again&state={state}")));
    }

    [Theory]
    [InlineData("?error=access_denied&state={0}")]
    [InlineData("?state={0}")]
    [InlineData("?code=abc&state=wrong")]
    public async Task CallbackErrorsMissingCodeAndStateMismatchFailClosed(string template)
    {
        var browser = new RecordingBrowser();
        var called = false;
        using var http = new HttpClient(new StubHandler(_ => { called = true; return Json(HttpStatusCode.OK, "{}"); }));
        var client = new FrontierOAuthClient(Options(), http, browser);
        var authorization = await client.BeginAuthorizationAsync();
        var state = ParseQuery(authorization.Query)["state"];
        var callback = new Uri($"{Options().RedirectUri}{string.Format(template, state)}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.HandleCallbackAsync(callback));
        Assert.False(called);
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

    private static Dictionary<string, string> ParseQuery(string value)
        => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length == 1 ? string.Empty : Uri.UnescapeDataString(part[1]));

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingBrowser : IExternalBrowser
    {
        public Uri? LastOpened { get; private set; }
        public void Open(Uri uri) => LastOpened = uri;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request))) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request);
    }
}
