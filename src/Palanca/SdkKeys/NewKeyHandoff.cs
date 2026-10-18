using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Palanca.SdkKeys;

internal sealed record NewKey(string ApplicationKey, string Name, string EnvironmentName, string Key);

/// <summary>
/// Carries a new SDK key from the create request to the page it redirects to, so a reload cannot create a second
/// key. The key waits in memory under a random handle, which is what the redirect URL carries, for at most a minute,
/// and is handed out once.
/// </summary>
internal sealed class NewKeyHandoff(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public string Hold(NewKey key)
    {
        var handle = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        cache.Set(CacheKey(handle), key, Lifetime);
        return handle;
    }

    /// <returns>The key held under <paramref name="handle"/> for <paramref name="applicationKey"/>, once; afterwards, null.</returns>
    public NewKey? Take(string? handle, string applicationKey)
    {
        if (string.IsNullOrEmpty(handle) || !cache.TryGetValue(CacheKey(handle), out NewKey? key) || key is null)
        {
            return null;
        }

        cache.Remove(CacheKey(handle));
        return key.ApplicationKey == applicationKey ? key : null;
    }

    private static string CacheKey(string handle) => $"palanca:new-sdk-key:{handle}";
}
