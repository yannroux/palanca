namespace Palanca.Client;

/// <summary>
/// Where the client reads flags from, and how often. Bind it from configuration (the <c>Palanca</c> section by
/// convention) or set it in code.
/// </summary>
public sealed class PalancaOptions
{
    /// <summary>The section name the client is usually configured under.</summary>
    public const string SectionName = "Palanca";

    /// <summary>The service's base address, such as <c>http://palanca:8080</c>. The client reads <c>api/v1/flags</c> under it.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The SDK key of the one environment this application reads.</summary>
    public string? SdkKey { get; set; }

    /// <summary>The application key the SDK key must belong to, such as <c>hogarhero</c>.</summary>
    public string? Application { get; set; }

    /// <summary>The environment key the SDK key must belong to, such as <c>production</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// How often to ask for changes. 30 seconds by default; anything under 5 seconds is raised to 5, and anything
    /// over an hour lowered to an hour.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long startup waits for the first flags before going on with the code defaults. 2 seconds by default, and
    /// at most 30 seconds.
    /// </summary>
    public TimeSpan StartupWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How old the flags may get before the health check reports Degraded. 5 minutes by default; anything under the
    /// poll interval is raised to it, and anything over a day lowered to a day.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);
}
