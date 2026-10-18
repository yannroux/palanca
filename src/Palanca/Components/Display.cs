using System.Globalization;
using Palanca.Data;

namespace Palanca.Components;

/// <summary>How the dashboard words times and authors.</summary>
internal static class Display
{
    /// <summary>Changes newer than this stand out on the board as recent.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    public static bool IsRecent(DateTime? utc, DateTime now) => utc is { } at && now - at < RecentWindow;

    /// <summary>A time relative to <paramref name="now"/>, such as "12 min ago"; older than a month, the date.</summary>
    public static string Ago(DateTime utc, DateTime now)
    {
        var elapsed = now - utc;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 31 } => $"{(int)elapsed.TotalDays} days ago",
            _ => Date(utc),
        };
    }

    /// <summary>
    /// A departure board's time for a change: the clock time today, "Yest.", the weekday within a week, then the date.
    /// Times are UTC, as everywhere in the dashboard.
    /// </summary>
    public static string Clock(DateTime utc, DateTime now) => (now.Date - utc.Date).TotalDays switch
    {
        < 1 => utc.ToString("HH:mm", CultureInfo.InvariantCulture),
        < 2 => "Yest.",
        < 7 => utc.ToString("ddd", CultureInfo.InvariantCulture),
        _ => utc.ToString("d MMM", CultureInfo.InvariantCulture),
    };

    public static string Date(DateTime utc) => utc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Timestamp(DateTime utc) => utc.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The author of a value as the owner reads it: "you" for the owner, otherwise the management token's name,
    /// since the dashboard and the API are the only ways to change a value.
    /// </summary>
    public static string Author(string actor, string? ownerEmail) =>
        string.Equals(actor, ownerEmail, StringComparison.OrdinalIgnoreCase) ? "you" : $"token {actor}";

    public static string Author(ChangeSource source, string actor) => source switch
    {
        ChangeSource.Dashboard => "you",
        ChangeSource.Api => $"token {actor}",
        _ => actor,
    };

    public static string OnOff(bool enabled) => enabled ? "ON" : "OFF";
}
