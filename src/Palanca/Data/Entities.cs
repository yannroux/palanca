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

/// <summary>What a management token may do with the flags of the applications in its scope (FLG-082).</summary>
internal enum TokenPermission
{
    /// <summary>Read flags, values, and history.</summary>
    ReadOnly,

    /// <summary>Also create flags, edit descriptions, and set values in every environment, production included.</summary>
    ReadWrite,
}

/// <summary>
/// A secret that lets a script or AI agent call the management API. Its permission and application scope are fixed at
/// creation, and it expires after at most 12 months (FLG-083).
/// </summary>
internal sealed class ManagementToken
{
    public const int MaxLifetimeDays = 365;

    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The visible start of the token, used to find candidates before comparing hashes.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full token. The token itself is never stored.</summary>
    public required byte[] Hash { get; set; }

    public TokenPermission Permission { get; set; }

    /// <summary>Whether the token covers every application, including ones registered after it. Otherwise it covers <see cref="Applications"/>.</summary>
    public bool AllApplications { get; set; }

    public List<Application> Applications { get; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
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

    /// <summary>The management token that made an API change, which tells apart tokens that share a name.</summary>
    public int? TokenId { get; set; }

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
    ApplicationCreated,
    ApplicationRenamed,
    ApplicationArchived,
    EnvironmentCreated,
    EnvironmentRenamed,
    EnvironmentMoved,
    EnvironmentProtectionChanged,
    EnvironmentArchived,
    FlagCreated,
    FlagDescriptionChanged,
    FlagArchived,
    FlagRestored,
    FlagDeleted,
    SdkKeyCreated,
    SdkKeyRevoked,
    OwnerEnrollmentIssued,
    OwnerRecovered,
    PasskeyRegistered,
    PasskeyRenamed,
    PasskeyRemoved,
    ManagementTokenCreated,
    ManagementTokenRevoked,
}

/// <summary>The one person who signs in to the dashboard. Created and recovered only from the host (FLG-046).</summary>
internal sealed class Owner
{
    public Guid Id { get; set; }
    public required string Email { get; set; }

    /// <summary>Changes when a passkey is removed or the owner is recovered, which ends every other session (FLG-047).</summary>
    public required string SecurityStamp { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<OwnerPasskey> Passkeys { get; } = [];
}

/// <summary>A passkey registered to the owner, as ASP.NET Core Identity's passkey store sees it.</summary>
internal sealed class OwnerPasskey
{
    public required byte[] CredentialId { get; set; }
    public Guid OwnerId { get; set; }
    public Owner Owner { get; set; } = null!;
    public required string Name { get; set; }
    public required OwnerPasskeyData Data { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>The rest of Identity's passkey record, stored as JSON.</summary>
internal sealed record OwnerPasskeyData(
    byte[] PublicKey,
    uint SignCount,
    string[]? Transports,
    bool IsUserVerified,
    bool IsBackupEligible,
    bool IsBackedUp,
    byte[] AttestationObject,
    byte[] ClientDataJson);

/// <summary>A single-use enrollment link issued by the host command. Only the hash of its token is stored.</summary>
internal sealed class OwnerEnrollment
{
    public int Id { get; set; }
    public Guid OwnerId { get; set; }
    public Owner Owner { get; set; } = null!;

    /// <summary>SHA-256 of the token in the link.</summary>
    public required byte[] Hash { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
