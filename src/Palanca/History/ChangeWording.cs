using Palanca.Data;

namespace Palanca.History;

/// <summary>A change record as one sentence in the history, from what the record itself keeps.</summary>
internal static class ChangeWording
{
    public static string Describe(ChangeRecord record) => record.Action switch
    {
        ChangeAction.FlagValueSet => $"Turned {record.FlagKey} {record.NewValue} in {record.EnvironmentKey}",
        ChangeAction.ApplicationCreated => $"Registered the application {record.NewValue}",
        ChangeAction.ApplicationRenamed => $"Renamed the application from {record.OldValue} to {record.NewValue}",
        ChangeAction.ApplicationArchived => "Archived the application",
        ChangeAction.EnvironmentCreated when record.NewValue?.EndsWith(ProtectedSuffix, StringComparison.Ordinal) == true =>
            $"Added the protected environment {record.EnvironmentKey}, named {record.NewValue[..^ProtectedSuffix.Length]}",
        ChangeAction.EnvironmentCreated => $"Added the environment {record.EnvironmentKey}, named {record.NewValue}",
        ChangeAction.EnvironmentRenamed => $"Renamed {record.EnvironmentKey} from {record.OldValue} to {record.NewValue}",
        ChangeAction.EnvironmentMoved => $"Moved {record.EnvironmentKey} from {record.OldValue} to {record.NewValue}",
        ChangeAction.EnvironmentProtectionChanged => $"Made {record.EnvironmentKey} {record.NewValue}",
        ChangeAction.EnvironmentArchived => $"Archived the environment {record.EnvironmentKey}",
        ChangeAction.FlagCreated => $"Created {record.FlagKey}",
        ChangeAction.FlagDescriptionChanged => $"Changed the description of {record.FlagKey}",
        ChangeAction.FlagArchived => $"Archived {record.FlagKey}",
        ChangeAction.FlagRestored => $"Restored {record.FlagKey}",
        ChangeAction.FlagDeleted => $"Deleted {record.FlagKey} for good",
        ChangeAction.SdkKeyCreated => $"Created the SDK key {record.NewValue} for {record.EnvironmentKey}",
        ChangeAction.SdkKeyRevoked => $"Revoked the SDK key {record.OldValue} of {record.EnvironmentKey}",
        ChangeAction.OwnerEnrollmentIssued => $"Issued an enrollment link for {record.NewValue}",
        ChangeAction.OwnerRecovered => $"Recovered the owner account, revoking {record.OldValue}",
        ChangeAction.PasskeyRegistered => $"Registered the passkey {record.NewValue}",
        ChangeAction.PasskeyRenamed => $"Renamed the passkey {record.OldValue} to {record.NewValue}",
        ChangeAction.PasskeyRemoved => $"Removed the passkey {record.OldValue}",
        _ => record.Action.ToString(),
    };

    /// <summary>The text a change replaced or set, where the sentence alone cannot carry it.</summary>
    public static string? Detail(ChangeRecord record) => record.Action switch
    {
        ChangeAction.FlagCreated when !string.IsNullOrEmpty(record.NewValue) => $"“{record.NewValue}”",
        ChangeAction.FlagDescriptionChanged => $"From {Quoted(record.OldValue)} to {Quoted(record.NewValue)}",
        _ => null,
    };

    public static string Source(ChangeSource source) => source switch
    {
        ChangeSource.Dashboard => "Dashboard",
        ChangeSource.Api => "API",
        ChangeSource.Host => "Host command",
        _ => source.ToString(),
    };

    // How ApplicationCommands records a protected environment's name.
    private const string ProtectedSuffix = " (protected)";

    private static string Quoted(string? text) => string.IsNullOrEmpty(text) ? "no description" : $"“{text}”";
}
