namespace Palanca.Client;

/// <summary>Reads flags from the client's in-memory copy.</summary>
public interface IPalancaFlags
{
    /// <summary>
    /// Whether the flag is on. Returns <paramref name="defaultValue"/> until the client has flags from the service,
    /// and for a key the service does not have. It never throws and never waits on the network.
    /// </summary>
    bool IsEnabled(string key, bool defaultValue);
}

/// <summary>
/// A flag an application reads, with the value it falls back to. Keep them in one static catalog, so each read names
/// its default once and unused flags are easy to find:
/// <code>
/// public static class Flags
/// {
///     public static readonly PalancaFlag NewSearch = new("new-search", DefaultValue: false);
/// }
///
/// if (flags.IsEnabled(Flags.NewSearch)) { ... }
/// </code>
/// </summary>
public sealed record PalancaFlag(string Key, bool DefaultValue);

/// <summary>Reads of catalog flags.</summary>
public static class PalancaFlagsExtensions
{
    /// <summary>
    /// Whether the catalog flag is on, falling back to its default. Like every read, it never throws: a null flag,
    /// which has no default to fall back to, reads as off.
    /// </summary>
    public static bool IsEnabled(this IPalancaFlags flags, PalancaFlag flag) =>
        flags is not null && flag is not null && flags.IsEnabled(flag.Key, flag.DefaultValue);
}
