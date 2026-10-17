namespace Palanca.Data;

/// <summary>
/// Keys for applications, environments, and flags: lowercase letters, digits, and hyphens,
/// starting with a letter, at most 64 characters, for example <c>operator-denue-imports</c>.
/// </summary>
internal static class Keys
{
    public const int MaxLength = 64;

    /// <summary>The same rule as a SQLite check constraint, so the database refuses a bad key too.</summary>
    public static string CheckConstraintSql(string column) =>
        $"length(\"{column}\") BETWEEN 1 AND {MaxLength} AND \"{column}\" GLOB '[a-z]*' AND \"{column}\" NOT GLOB '*[^a-z0-9-]*'";
}
