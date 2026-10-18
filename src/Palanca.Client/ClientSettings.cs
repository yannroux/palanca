using System.Globalization;
using System.Text;

namespace Palanca.Client;

/// <summary>The options the client runs with, after checking them once at startup.</summary>
internal sealed record ClientSettings(
    Uri FlagsAddress, string SdkKey, string Application, string Environment, TimeSpan PollInterval, TimeSpan StartupWait, TimeSpan StaleAfter)
{
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(5);

    // Leaves the SDK key out of ToString, so printing the settings never prints the secret (FLG-065).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"FlagsAddress = {FlagsAddress}, Application = {Application}, Environment = {Environment}, ");
        builder.Append(CultureInfo.InvariantCulture, $"PollInterval = {PollInterval}, StartupWait = {StartupWait}, StaleAfter = {StaleAfter}");
        return true;
    }
}

/// <summary>
/// The outcome of checking <see cref="PalancaOptions"/>. A missing or invalid required setting leaves
/// <see cref="Settings"/> null: the client then serves code defaults and never polls, because an application must
/// not fail to start over its flags. Durations out of range are raised to their minimum instead.
/// </summary>
internal sealed record ClientConfiguration(ClientSettings? Settings, IReadOnlyList<string> Problems, IReadOnlyList<string> Adjustments)
{
    public static ClientConfiguration From(PalancaOptions options)
    {
        var problems = new List<string>();
        var adjustments = new List<string>();

        Uri? flagsAddress = null;
        if (options.Endpoint is null)
        {
            problems.Add("Endpoint is missing");
        }
        else if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https"))
        {
            problems.Add("Endpoint must be an absolute http or https address");
        }
        else
        {
            // Resolved against a trailing slash, so a base address with a path keeps it.
            var endpoint = options.Endpoint.AbsoluteUri.EndsWith('/') ? options.Endpoint : new Uri(options.Endpoint.AbsoluteUri + "/");
            flagsAddress = new Uri(endpoint, "api/v1/flags");
        }

        var sdkKey = Required(options.SdkKey, "SdkKey", problems);
        var application = Required(options.Application, "Application", problems);
        var environment = Required(options.Environment, "Environment", problems);

        var pollInterval = options.PollInterval;
        if (pollInterval < ClientSettings.MinimumPollInterval)
        {
            adjustments.Add($"PollInterval {pollInterval} is under the minimum, so the client polls every {ClientSettings.MinimumPollInterval}");
            pollInterval = ClientSettings.MinimumPollInterval;
        }

        var startupWait = options.StartupWait;
        if (startupWait < TimeSpan.Zero)
        {
            adjustments.Add($"StartupWait {startupWait} is negative, so startup does not wait");
            startupWait = TimeSpan.Zero;
        }

        var staleAfter = options.StaleAfter;
        if (staleAfter < pollInterval)
        {
            adjustments.Add($"StaleAfter {staleAfter} is under the poll interval, so it is raised to {pollInterval}");
            staleAfter = pollInterval;
        }

        var settings = problems.Count == 0
            ? new ClientSettings(flagsAddress!, sdkKey!, application!, environment!, pollInterval, startupWait, staleAfter)
            : null;
        return new ClientConfiguration(settings, problems, adjustments);
    }

    // A key read from a mounted file may end with a line break.
    private static string? Required(string? value, string name, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{name} is missing");
            return null;
        }

        return value.Trim();
    }
}
