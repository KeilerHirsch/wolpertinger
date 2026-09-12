namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierOAuthOptions(
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri DecodeEndpoint,
    Uri CapiProfileEndpoint,
    string ClientId,
    Uri RedirectUri,
    string Scope,
    string Audience)
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
        return this;
    }
}
