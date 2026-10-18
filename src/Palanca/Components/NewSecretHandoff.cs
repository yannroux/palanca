using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Palanca.Components;

/// <summary>
/// Carries a new SDK key or management token from the create request to the page it redirects to, so a reload cannot
/// create a second one. The secret waits in memory under a random handle, which is what the redirect URL carries, for
/// at most a minute, and is handed out once.
/// </summary>
internal sealed class NewSecretHandoff(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public string Hold<T>(T secret)
        where T : class
    {
        var handle = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        cache.Set(CacheKey<T>(handle), secret, Lifetime);
        return handle;
    }

    /// <returns>The secret held under <paramref name="handle"/>, once; afterwards, null.</returns>
    public T? Take<T>(string? handle)
        where T : class
    {
        if (string.IsNullOrEmpty(handle) || !cache.TryGetValue(CacheKey<T>(handle), out T? secret))
        {
            return null;
        }

        cache.Remove(CacheKey<T>(handle));
        return secret;
    }

    private static string CacheKey<T>(string handle) => $"palanca:new-secret:{typeof(T).Name}:{handle}";
}
