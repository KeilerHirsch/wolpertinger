using System.Text;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Tests.Evidence;

public sealed class EvidenceKeyStoreTests
{
    [Fact]
    public async Task CreatesOneProtectedKeyAndReopensSameMaterial()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "evidence-key.bin");
        var protector = new Base64TestProtector();
        var store = new EvidenceKeyStore(path, protector);

        var first = await store.LoadOrCreateAsync();
        var again = await store.LoadOrCreateAsync();
        var reopened = await new EvidenceKeyStore(path, protector).LoadOrCreateAsync();

        Assert.NotEqual(0U, first.KeyId);
        Assert.Equal(32, first.KeyBytes.Length);
        Assert.Equal(first.KeyId, again.KeyId);
        Assert.Equal(first.KeyBytes, again.KeyBytes);
        Assert.Equal(first.KeyId, reopened.KeyId);
        Assert.Equal(first.KeyBytes, reopened.KeyBytes);

        var stored = await File.ReadAllBytesAsync(path);
        Assert.False(stored.AsSpan().IndexOf(first.KeyBytes) >= 0);
    }
    [Fact]
    public async Task ExistingKeyCanOnlyBeResolvedByItsStoredId()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "evidence-key.bin");
        var protector = new Base64TestProtector();
        var store = new EvidenceKeyStore(path, protector);
        var key = await store.LoadOrCreateAsync();

        Assert.Equal(key, await store.LoadAsync(key.KeyId));
        await Assert.ThrowsAsync<EvidenceCorruptionException>(
            () => store.LoadAsync(unchecked(key.KeyId + 1)));
    }

    [Fact]
    public async Task MissingOrMalformedKeyFileFailsClosedOnLoad()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "evidence-key.bin");
        var store = new EvidenceKeyStore(path, new Base64TestProtector());

        await Assert.ThrowsAsync<EvidenceCorruptionException>(() => store.LoadAsync(1));
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("not-a-key-file"));
        await Assert.ThrowsAsync<EvidenceCorruptionException>(() => store.LoadAsync(1));
    }
    [Fact]
    public void DpapiCurrentUserProtectorRoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var protector = new DpapiCurrentUserKeyProtector();
        var plaintext = Encoding.UTF8.GetBytes("wolpertinger-key-test");
        var protectedBytes = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedBytes);
        Assert.Equal(plaintext, protector.Unprotect(protectedBytes));
    }

    private sealed class Base64TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "wolpertinger-key-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
