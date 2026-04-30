using System;
using System.Security.Cryptography;
using System.Text;

namespace Cleario.Services
{
    public static class SecureStorageService
    {
        private const string Prefix = "dpapi:v1:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Cleario.SecureStorage.v1");

        public static bool IsProtected(string? value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string Protect(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.StartsWith(Prefix, StringComparison.Ordinal))
                return value;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(value);
                var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(encryptedBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (!value.StartsWith(Prefix, StringComparison.Ordinal))
                return value;

            try
            {
                var encryptedText = value.Substring(Prefix.Length);
                var encryptedBytes = Convert.FromBase64String(encryptedText);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
