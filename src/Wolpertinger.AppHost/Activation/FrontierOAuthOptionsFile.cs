using System.Text.Json;
using System.Text.Json.Serialization;
using Wolpertinger.Edge.Frontier;

namespace Wolpertinger.AppHost.Activation;

public static class FrontierOAuthOptionsFile
{
    public const string FileName = "frontier-oauth.json";
    public const string RegisteredRedirectUri =
        "https://keilerhirsch.github.io/wolpertinger/oauth/callback/";
    public static readonly Uri ApplicationCallbackUri = new("wolpertinger://frontier/oauth");
    private const int MaximumSettingsBytes = 16_384;
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "clientId",
        "redirectUri",
        "scope",
        "audience",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static FrontierOAuthOptions? LoadIfPresent(string path)
    {
        var result = Read(path);
        return result.Status switch
        {
            FrontierOAuthOptionsFileStatus.Missing => null,
            FrontierOAuthOptionsFileStatus.Valid => result.Options,
            FrontierOAuthOptionsFileStatus.Invalid => throw InvalidSettings(),
            _ => throw new InvalidOperationException("Undefined Frontier OAuth configuration status."),
        };
    }

    public static FrontierOAuthOptionsFileReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Missing, null);
        }
        catch (IOException)
        {
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Invalid, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Invalid, null);
        }

        if (payload.Length is 0 or > MaximumSettingsBytes)
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Invalid, null);

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            ValidateProperties(document.RootElement);
            var settings = document.RootElement.Deserialize<Settings>(JsonOptions)
                ?? throw new InvalidDataException();
            if (!string.Equals(settings.RedirectUri, RegisteredRedirectUri, StringComparison.Ordinal))
                throw new InvalidDataException();

            var options = new FrontierOAuthOptions(
                FrontierOAuthEndpoints.Authorization,
                FrontierOAuthEndpoints.Token,
                FrontierOAuthEndpoints.Decode,
                FrontierOAuthEndpoints.CapiProfile,
                settings.ClientId,
                new Uri(settings.RedirectUri, UriKind.Absolute),
                settings.Scope,
                settings.Audience,
                ApplicationCallbackUri);
            return new FrontierOAuthOptionsFileReadResult(
                FrontierOAuthOptionsFileStatus.Valid,
                options.Validate());
        }
        catch (Exception error) when (error is JsonException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or UriFormatException
            or NotSupportedException)
        {
            return new FrontierOAuthOptionsFileReadResult(FrontierOAuthOptionsFileStatus.Invalid, null);
        }
    }

    private static void ValidateProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name) || !names.Add(property.Name))
                throw new InvalidDataException();
        }

        if (names.Count != AllowedProperties.Count)
            throw new InvalidDataException();
    }

    private static InvalidDataException InvalidSettings() =>
        new("Frontier OAuth settings are invalid; the callback consumer was not configured.");

    private sealed record Settings(
        string ClientId,
        string RedirectUri,
        string Scope,
        string Audience);
}

public enum FrontierOAuthOptionsFileStatus
{
    Missing,
    Valid,
    Invalid,
}

public sealed record FrontierOAuthOptionsFileReadResult(
    FrontierOAuthOptionsFileStatus Status,
    FrontierOAuthOptions? Options);
