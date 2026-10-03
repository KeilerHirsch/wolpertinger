using System.Security.Cryptography;
using System.Text;

namespace Wolpertinger.Product.Contracts;

public static class ProductActivationEndpoint
{
    public const string ProductId = "KeilerHirsch.WOLPERTINGER.R0";

    public static string PipeName(string productId = ProductId)
        => $"WOLPERTINGER.{UserDigest(productId)}.activation";

    public static string MutexName(string productId = ProductId)
        => $"Local\\WOLPERTINGER.{UserDigest(productId)}.instance";

    private static string UserDigest(string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        var userKey = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{productId}|{userKey}")))[..24];
    }
}
