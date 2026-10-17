using System.ComponentModel.DataAnnotations;

namespace Palanca.Data;

internal sealed class DatabaseOptions
{
    public const string SectionName = "Palanca:Database";

    /// <summary>The SQLite database file. A relative path is resolved against the content root.</summary>
    [Required]
    public string FilePath { get; set; } = "";

    /// <summary>Where a copy of the database is written before migrations change it.</summary>
    [Required]
    public string BackupDirectory { get; set; } = "";
}
