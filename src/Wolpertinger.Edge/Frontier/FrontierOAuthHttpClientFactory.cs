namespace Wolpertinger.Edge.Frontier;

public static class FrontierOAuthHttpClientFactory
{
    public static HttpClient CreateClient()
        => new(CreateHandler(), disposeHandler: true);

    public static HttpClientHandler CreateHandler()
        => new() { AllowAutoRedirect = false };
}
