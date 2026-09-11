using System.Security.Cryptography;

namespace Wolpertinger.Edge.Evidence;

public sealed class DpapiCurrentUserKeyProtector : IEvidenceKeyProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI evidence protection requires Windows.");
        return ProtectedData.Protect(
            plaintext.ToArray(),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI evidence protection requires Windows.");
        return ProtectedData.Unprotect(
            protectedBytes.ToArray(),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
    }
}
