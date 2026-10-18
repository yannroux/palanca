using System.Buffers.Text;
using System.Security.Cryptography;

namespace Palanca.Security;

/// <summary>
/// Bearer secrets: a marker followed by 256 random bits in base64url. The marker tells the kinds apart and lets secret
/// scanners recognize them; the stored prefix adds the first six random characters so the dashboard can tell secrets
/// apart and the APIs can find candidates. Only a secret's SHA-256 hash is stored (FLG-021, FLG-083).
/// </summary>
internal sealed class BearerSecrets
{
    /// <summary>SDK keys, which read one environment's flags.</summary>
    public static readonly BearerSecrets SdkKeys = new("palanca_sdk_");

    /// <summary>Management tokens, which call the management API.</summary>
    public static readonly BearerSecrets ManagementTokens = new("palanca_mgt_");

    private const int RandomBytes = 32;
    private const int VisibleRandomCharacters = 6;

    private BearerSecrets(string marker) => Marker = marker;

    public string Marker { get; }

    public (string Secret, string Prefix, byte[] Hash) Create()
    {
        var secret = Marker + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));
        return (secret, PrefixOf(secret), Hash(secret));
    }

    /// <returns>The prefix and hash of <paramref name="secret"/>, or null when it cannot be a secret of this kind.</returns>
    public (string Prefix, byte[] Hash)? Parse(string? secret)
    {
        if (secret is null || !secret.StartsWith(Marker, StringComparison.Ordinal)
            || !Base64Url.IsValid(secret.AsSpan(Marker.Length), out var length) || length != RandomBytes)
        {
            return null;
        }

        return (PrefixOf(secret), Hash(secret));
    }

    /// <summary>The credential in a request's <c>Authorization: Bearer</c> header, or null when it has none.</summary>
    public static string? FromRequest(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string Scheme = "Bearer ";
        return authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? authorization[Scheme.Length..].Trim() : null;
    }

    private string PrefixOf(string secret) => secret[..(Marker.Length + VisibleRandomCharacters)];

    private static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(secret));
}
