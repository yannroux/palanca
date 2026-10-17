namespace Palanca.Data;

/// <summary>A product that consumes flags, such as HogarHero.</summary>
internal sealed class Application
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public List<AppEnvironment> Environments { get; } = [];
    public List<Flag> Flags { get; } = [];
}

/// <summary>
/// A deployment of an application, such as <c>staging</c> or <c>production</c>.
/// Named to avoid clashing with <see cref="System.Environment"/>.
/// </summary>
internal sealed class AppEnvironment
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public required string Key { get; set; }
    public required string Name { get; set; }
    public bool IsProtected { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Increases with every flag value change in this environment, and backs the evaluation API ETag.</summary>
    public long Revision { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>A named boolean switch that belongs to one application.</summary>
internal sealed class Flag
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public required string Key { get; set; }
    public required string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public List<FlagValue> Values { get; } = [];
}

/// <summary>
/// The state of one flag in one environment. A missing row means the flag is off there,
/// which is how a flag starts off in environments added after it.
/// </summary>
internal sealed class FlagValue
{
    public int FlagId { get; set; }
    public Flag Flag { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>A secret that grants read access to the flag values of exactly one environment.</summary>
internal sealed class SdkKey
{
    public int Id { get; set; }
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public required string Name { get; set; }

    /// <summary>The visible start of the key, used to find candidates before comparing hashes.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full key. The key itself is never stored.</summary>
    public required byte[] Hash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// An append-only record of one change. It keeps the keys of what it describes, without
/// foreign keys, so history survives the permanent deletion of an archived flag.
/// </summary>
internal sealed class ChangeRecord
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public ChangeSource Source { get; set; }
    public required string Actor { get; set; }
    public ChangeAction Action { get; set; }
    public int? ApplicationId { get; set; }
    public string? ApplicationKey { get; set; }
    public int? EnvironmentId { get; set; }
    public string? EnvironmentKey { get; set; }
    public int? FlagId { get; set; }
    public string? FlagKey { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

internal enum ChangeSource
{
    Dashboard,
    Api,
    Host,
}

internal enum ChangeAction
{
    FlagValueSet,
}
