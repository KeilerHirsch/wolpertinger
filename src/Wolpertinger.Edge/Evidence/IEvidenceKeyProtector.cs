namespace Wolpertinger.Edge.Evidence;

public interface IEvidenceKeyProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

public sealed class EvidenceKeyMaterial : IEquatable<EvidenceKeyMaterial>
{
    private readonly byte[] _keyBytes;

    public EvidenceKeyMaterial(uint keyId, byte[] keyBytes)
    {
        if (keyId == 0) throw new ArgumentOutOfRangeException(nameof(keyId));
        ArgumentNullException.ThrowIfNull(keyBytes);
        if (keyBytes.Length != 32)
            throw new ArgumentException("Evidence key must be exactly 32 bytes.", nameof(keyBytes));
        KeyId = keyId;
        _keyBytes = keyBytes.ToArray();
    }

    public uint KeyId { get; }
    public byte[] KeyBytes => _keyBytes.ToArray();
    public bool Equals(EvidenceKeyMaterial? other)
        => other is not null && KeyId == other.KeyId && _keyBytes.AsSpan().SequenceEqual(other._keyBytes);
    public override bool Equals(object? obj) => Equals(obj as EvidenceKeyMaterial);
    public override int GetHashCode() => KeyId.GetHashCode();
    internal void Clear() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(_keyBytes);
}
