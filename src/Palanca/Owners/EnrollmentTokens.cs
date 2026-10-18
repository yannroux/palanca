using System.Buffers.Text;
using System.Security.Cryptography;

namespace Palanca.Owners;

/// <summary>The secret in an enrollment link: 256 random bits, base64url-encoded, stored only as its SHA-256 hash.</summary>
internal static class EnrollmentTokens
{
    private const int TokenBytes = 32;

    public static (string Token, byte[] Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return (Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }

    /// <returns>The hash of <paramref name="token"/>, or null when it cannot be a token.</returns>
    public static byte[]? Hash(string? token)
    {
        // TryDecodeFromChars throws on malformed input, so check the shape first.
        if (string.IsNullOrEmpty(token) || !Base64Url.IsValid(token, out var length) || length != TokenBytes)
        {
            return null;
        }

        return SHA256.HashData(Base64Url.DecodeFromChars(token));
    }
}
