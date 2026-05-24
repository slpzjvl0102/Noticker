using System.Security.Cryptography;
using System.Text;

namespace Noticker.Infrastructure;

// Token is decrypted once at startup and held in AppSettings (personal tool, acceptable).
public static class DpapiHelper
{
    public static string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
