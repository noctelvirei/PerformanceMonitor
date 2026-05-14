using System.Security.Cryptography;
using System.Text;

namespace PerformanceMonitor.CentralRepository.Security;

public static class LocalSecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PerformanceMonitor.Headless.v1");

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SQL passwords can only be saved from Settings on Windows.");
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return "";
        }

        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedValue;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SQL passwords saved from Settings can only be read on Windows.");
        }

        var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
}
