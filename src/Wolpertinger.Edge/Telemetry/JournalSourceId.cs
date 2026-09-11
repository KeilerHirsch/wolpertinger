using System.Security.Cryptography;
using System.Text;
using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Telemetry;

public static class JournalSourceId
{
    public static FixedBytes16 FromFileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Journal filename is required.", nameof(path));
        }

        var bytes = Encoding.UTF8.GetBytes(fileName.ToUpperInvariant());
        var hash = SHA256.HashData(bytes);
        return FixedBytes16.FromBytes(hash.AsSpan(0, 16));
    }
}
