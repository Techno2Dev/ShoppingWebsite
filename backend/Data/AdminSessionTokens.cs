using System.Security.Cryptography;
using System.Text;

namespace backend.Data;

public static class AdminSessionTokens
{
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    public static bool IsValidFormat(string token) => token.Length == 43 &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
