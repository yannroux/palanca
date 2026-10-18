using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Options;
using Palanca.Security;

namespace Palanca.Management;

internal sealed class ManagementOptions
{
    public const string SectionName = "Palanca:Management";

    /// <summary>Writes one token may make within one minute before further writes get 429 (FLG-086).</summary>
    [Range(1, 1000)]
    public int WritesPerMinute { get; set; } = 30;
}

/// <summary>
/// Counts each management token's writes over a sliding minute (FLG-086). Every write a token is allowed to attempt
/// counts, whether or not it changes anything, so retrying a rejected request does not get around the limit.
/// </summary>
internal sealed class ManagementWriteLimiter(TimeProvider timeProvider, IOptions<ManagementOptions> options)
{
    private readonly SlidingWindowLog _writes = new(timeProvider);

    public int WritesPerMinute => options.Value.WritesPerMinute;

    /// <returns>Whether the token may write now; if not, <paramref name="retryAfter"/> is how long until it may.</returns>
    public bool TryAcquire(int tokenId, out TimeSpan retryAfter) =>
        _writes.TryAdd(tokenId.ToString(CultureInfo.InvariantCulture), WritesPerMinute, out retryAfter);
}
