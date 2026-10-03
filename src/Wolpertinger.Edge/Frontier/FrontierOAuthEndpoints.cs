namespace Wolpertinger.Edge.Frontier;

public static class FrontierOAuthEndpoints
{
    public static readonly Uri Authorization = new("https://auth.frontierstore.net/auth");
    public static readonly Uri Token = new("https://auth.frontierstore.net/token");
    public static readonly Uri Decode = new("https://auth.frontierstore.net/decode");
    public static readonly Uri CapiProfile = new("https://companion.orerve.net/profile");
}
