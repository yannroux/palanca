namespace Palanca;

internal enum CommandStatus
{
    Done,
    Invalid,
    NotFound,
    Conflict,

    /// <summary>The change affects a protected environment and was not confirmed (FLG-014); nothing changed.</summary>
    ConfirmationRequired,
}

/// <summary>The outcome of a dashboard command, with a message for the page when it did not succeed.</summary>
internal sealed record CommandResult(CommandStatus Status, string? Message = null)
{
    public static CommandResult Done { get; } = new(CommandStatus.Done);

    public bool Succeeded => Status == CommandStatus.Done;

    public static CommandResult Invalid(string message) => new(CommandStatus.Invalid, message);

    public static CommandResult NotFound(string message) => new(CommandStatus.NotFound, message);

    public static CommandResult Conflict(string message) => new(CommandStatus.Conflict, message);

    public static CommandResult ConfirmationRequired(string message) => new(CommandStatus.ConfirmationRequired, message);
}
