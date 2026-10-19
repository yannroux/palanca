using System.ComponentModel.DataAnnotations;

namespace Palanca.Data;

internal sealed class DatabaseOptions
{
    public const string SectionName = "Palanca:Database";

    /// <summary>The SQLite database file. A relative path is resolved against the content root.</summary>
    [Required]
    public string FilePath { get; set; } = "";

    /// <summary>Where the daily backups go, and the copy written before migrations change the database.</summary>
    [Required]
    public string BackupDirectory { get; set; } = "";

    /// <summary>How many days the daily backups are kept (FLG-061). Copies taken before a migration are kept.</summary>
    [Range(1, 3650)]
    public int BackupRetentionDays { get; set; } = 14;
}
