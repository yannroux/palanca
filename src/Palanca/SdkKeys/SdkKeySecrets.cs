using System.Buffers.Text;
using System.Security.Cryptography;

namespace Palanca.SdkKeys;

/// <summary>
/// SDK keys: <c>palanca_sdk_</c> followed by 256 random bits in base64url. The prefix lets secret scanners recognize
/// a key; the stored prefix adds the first six random characters so the dashboard can tell keys apart and the API
/// can find candidates. Only the key's SHA-256 hash is stored (FLG-021).
/// </summary>
internal static class SdkKeySecrets
{
    public const string Marker = "palanca_sdk_";
    private const int RandomBytes = 32;
    private const int VisibleRandomCharacters = 6;

    public static (string Key, string Prefix, byte[] Hash) Create()
    {
        var key = Marker + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));
        return (key, PrefixOf(key), Hash(key));
    }

    /// <returns>The prefix and hash of <paramref name="key"/>, or null when it cannot be an SDK key.</returns>
    public static (string Prefix, byte[] Hash)? Parse(string? key)
    {
        if (key is null || !key.StartsWith(Marker, StringComparison.Ordinal)
            || !Base64Url.IsValid(key.AsSpan(Marker.Length), out var length) || length != RandomBytes)
        {
            return null;
        }

        return (PrefixOf(key), Hash(key));
    }

    private static string PrefixOf(string key) => key[..(Marker.Length + VisibleRandomCharacters)];

    private static byte[] Hash(string key) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(key));
}
