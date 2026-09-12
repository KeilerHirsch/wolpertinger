using System.Text.Json;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierTokenSet(
    string RefreshToken,
    DateTimeOffset? AccessTokenExpiresUtc)
{
    public override string ToString()
        => $"FrontierTokenSet {{ RefreshToken = [REDACTED], AccessTokenExpiresUtc = {AccessTokenExpiresUtc:O} }}";
}

public sealed class FrontierTokenStore
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly IEvidenceKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FrontierTokenStore(string path, IEvidenceKeyProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public async Task SaveAsync(
        FrontierTokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens.RefreshToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = new StoredTokenEnvelope(
                FormatVersion,
                tokens.RefreshToken,
                tokens.AccessTokenExpiresUtc);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            try
            {
                byte[] protectedBytes;
                try { protectedBytes = _protector.Protect(plaintext); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException("Frontier token protection failed.");
                }
                if (protectedBytes.Length == 0)
                    throw new InvalidOperationException("Frontier token protection returned no data.");
                await WriteProtectedAtomicallyAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<FrontierTokenSet?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return null;
            byte[] protectedBytes;
            try { protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidDataException("Frontier token store cannot be read.");
            }
            if (protectedBytes.Length == 0)
                throw new InvalidDataException("Frontier token store is empty.");

            byte[] plaintext;
            try { plaintext = _protector.Unprotect(protectedBytes); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidDataException("Frontier token store cannot be unprotected.");
            }
            return ParseEnvelope(plaintext);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally { _gate.Release(); }
    }

    private static FrontierTokenSet ParseEnvelope(byte[] plaintext)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<StoredTokenEnvelope>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Frontier token envelope is missing.");
            if (envelope.Version != FormatVersion)
                throw new InvalidDataException("Frontier token envelope version is unsupported.");
            if (string.IsNullOrWhiteSpace(envelope.RefreshToken))
                throw new InvalidDataException("Frontier token envelope is missing credential material.");
            return new FrontierTokenSet(envelope.RefreshToken, envelope.AccessTokenExpiresUtc);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Frontier token envelope is malformed.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task WriteProtectedAtomicallyAsync(
        byte[] protectedBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record StoredTokenEnvelope(
        int Version,
        string RefreshToken,
        DateTimeOffset? AccessTokenExpiresUtc);
}
