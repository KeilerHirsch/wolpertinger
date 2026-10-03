using Wolpertinger.AppHost.Activation;

namespace Wolpertinger.AppHost.Tests;

public sealed class FrontierOAuthOptionsFileTests
{
    [Fact]
    public void LoadsOnlyPublicOAuthOptionsAndUsesBuiltInFrontierEndpoints()
    {
        using var file = new TemporaryOptionsFile("""
            {
              "clientId": "synthetic-client-id",
              "redirectUri": "https://keilerhirsch.github.io/wolpertinger/oauth/callback/",
              "scope": "auth capi",
              "audience": "frontier"
            }
            """);

        var options = FrontierOAuthOptionsFile.LoadIfPresent(file.Path);

        Assert.NotNull(options);
        Assert.Equal(
            "https://keilerhirsch.github.io/wolpertinger/oauth/callback/",
            options!.RedirectUri.AbsoluteUri);
        Assert.Equal(
            new Uri("wolpertinger://frontier/oauth"),
            options.ApplicationCallbackUri);
        Assert.Equal(new Uri("https://auth.frontierstore.net/auth"), options.AuthorizationEndpoint);
        Assert.Equal(new Uri("https://auth.frontierstore.net/token"), options.TokenEndpoint);
        Assert.Equal(new Uri("https://auth.frontierstore.net/decode"), options.DecodeEndpoint);
        Assert.Equal(new Uri("https://companion.orerve.net/profile"), options.CapiProfileEndpoint);
    }

    [Fact]
    public void RejectsUnknownPropertiesInsteadOfLoadingSharedKeyMaterialOrEndpointOverrides()
    {
        using var file = new TemporaryOptionsFile("""
            {
              "clientId": "synthetic-client-id",
              "redirectUri": "https://keilerhirsch.github.io/wolpertinger/oauth/callback/",
              "scope": "auth capi",
              "audience": "frontier",
              "sharedKey": "synthetic-value",
              "tokenEndpoint": "https://attacker.invalid/token"
            }
            """);

        var error = Assert.Throws<InvalidDataException>(
            () => FrontierOAuthOptionsFile.LoadIfPresent(file.Path));

        Assert.Equal(
            "Frontier OAuth settings are invalid; the callback consumer was not configured.",
            error.Message);
        Assert.DoesNotContain("synthetic-value", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.invalid", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMissingConfigurationWithoutExposingValues()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"wolpertinger-missing-oauth-{Guid.NewGuid():N}.json");

        var result = FrontierOAuthOptionsFile.Read(path);

        Assert.Equal(FrontierOAuthOptionsFileStatus.Missing, result.Status);
        Assert.Null(result.Options);
    }

    [Fact]
    public void RejectsRedirectUriThatDoesNotExactlyMatchTheRegisteredEndpoint()
    {
        using var file = new TemporaryOptionsFile(ValidJson.Replace(
            "https://keilerhirsch.github.io/wolpertinger/oauth/callback/",
            "https://keilerhirsch.github.io/wolpertinger/oauth/callback",
            StringComparison.Ordinal));

        var result = FrontierOAuthOptionsFile.Read(file.Path);

        Assert.Equal(FrontierOAuthOptionsFileStatus.Invalid, result.Status);
        Assert.Null(result.Options);
    }

    [Fact]
    public void RejectsEndpointOverridesRegardlessOfScheme()
    {
        using var file = new TemporaryOptionsFile(ValidJson.Replace(
            "\"clientId\": \"synthetic-client-id\",",
            "\"authorizationEndpoint\": \"http://attacker.invalid/auth\",\n  \"clientId\": \"synthetic-client-id\",",
            StringComparison.Ordinal));

        var result = FrontierOAuthOptionsFile.Read(file.Path);

        Assert.Equal(FrontierOAuthOptionsFileStatus.Invalid, result.Status);
        Assert.Null(result.Options);
    }

    [Fact]
    public void RejectsDuplicateFieldsInsteadOfAcceptingAmbiguousValues()
    {
        using var file = new TemporaryOptionsFile(ValidJson.Replace(
            "\"clientId\": \"synthetic-client-id\",",
            "\"clientId\": \"synthetic-client-id\",\n  \"clientId\": \"other-client-id\",",
            StringComparison.Ordinal));

        var result = FrontierOAuthOptionsFile.Read(file.Path);

        Assert.Equal(FrontierOAuthOptionsFileStatus.Invalid, result.Status);
        Assert.Null(result.Options);
    }

    private const string ValidJson = """
        {
          "clientId": "synthetic-client-id",
          "redirectUri": "https://keilerhirsch.github.io/wolpertinger/oauth/callback/",
          "scope": "auth capi",
          "audience": "frontier"
        }
        """;

    private sealed class TemporaryOptionsFile : IDisposable
    {
        public TemporaryOptionsFile(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wolpertinger-frontier-options-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
