using System.Net;
using System.Net.Http.Headers;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierProfileSnapshot(
    RawEvidenceReceipt EvidenceReceipt,
    byte[] ResponseBytes,
    DateTimeOffset ResponseObservedUtc,
    Uri SourceUri);

public sealed class FrontierCapiException : IOException
{
    public FrontierCapiException(HttpStatusCode statusCode)
        : base($"Frontier CAPI request failed with HTTP {(int)statusCode}.")
        => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}

public sealed class FrontierCapiClient
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly Uri _profileEndpoint;
    private readonly EncryptedSegmentedEvidenceLog _evidence;
    private readonly Func<DateTimeOffset> _clock;

    public FrontierCapiClient(
        HttpClient http,
        Uri profileEndpoint,
        EncryptedSegmentedEvidenceLog evidence,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _profileEndpoint = profileEndpoint ?? throw new ArgumentNullException(nameof(profileEndpoint));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FrontierProfileSnapshot> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _profileEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new FrontierCapiException(response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (bytes.Length == 0)
            throw new InvalidDataException("Frontier CAPI profile response is empty.");

        var observed = _clock();
        var receipt = await _evidence.AppendAsync(
            new RawEvidenceInput(
                RawEvidenceSourceKind.FrontierApi,
                bytes,
                observed),
            cancellationToken).ConfigureAwait(false);
        if (!receipt.IsDurable)
            throw new InvalidDataException("Frontier CAPI evidence was not durably committed.");

        return new FrontierProfileSnapshot(
            receipt,
            bytes,
            observed,
            _profileEndpoint);
    }
}
