using System.Security.Cryptography;
using System.Text;

namespace backend.Data;

public static class OrderPaymentSecurity
{
    public static string CreateAccessToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string HashAccessToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
