namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierOAuthOptions(
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri DecodeEndpoint,
    Uri CapiProfileEndpoint,
    string ClientId,
    Uri RedirectUri,
    string Scope,
    string Audience,
    Uri? ApplicationCallbackUri = null)
{
    public FrontierOAuthOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(AuthorizationEndpoint);
        ArgumentNullException.ThrowIfNull(TokenEndpoint);
        ArgumentNullException.ThrowIfNull(DecodeEndpoint);
        ArgumentNullException.ThrowIfNull(CapiProfileEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        ArgumentNullException.ThrowIfNull(RedirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(Audience);
        if (!RedirectUri.IsAbsoluteUri)
            throw new InvalidOperationException("Frontier redirect URI must be absolute.");
        if (ApplicationCallbackUri is { IsAbsoluteUri: false })
            throw new InvalidOperationException("Application callback URI must be absolute.");
        return this;
    }
}
