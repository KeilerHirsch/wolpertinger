using System.Text;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Frontier;

namespace Wolpertinger.Edge.Tests.Frontier;

public sealed class FrontierTokenStoreTests
{
    [Fact]
    public async Task SaveLoadDeleteUsesOnlyProtectedBytes()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "secrets", "frontier-token.bin");
        var protector = new XorProtector(0xA5);
        var store = new FrontierTokenStore(path, protector);
        var tokens = new FrontierTokenSet(
            "refresh-secret-literal",
            DateTimeOffset.Parse("2026-09-12T20:00:00Z"));

        await store.SaveAsync(tokens);
        Assert.True(File.Exists(path));
        var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("refresh-secret-literal", text, StringComparison.Ordinal);
        Assert.Equal(tokens, await store.LoadAsync());

        await store.DeleteAsync();
        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public void TokenRecordsRedactSecretsFromToString()
    {
        var tokens = new FrontierTokenSet("refresh-do-not-print", null);
        var session = new FrontierOAuthSession(
            "access-do-not-print",
            "refresh-do-not-print",
            null);

        Assert.DoesNotContain("refresh-do-not-print", tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access-do-not-print", session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-do-not-print", session.ToString(), StringComparison.Ordinal);
    }

    private sealed class XorProtector(byte key) : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);
        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => Transform(protectedBytes);

        private byte[] Transform(ReadOnlySpan<byte> input)
        {
            var output = input.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= key;
            return output;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-frontier-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
