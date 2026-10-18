namespace Palanca.Components.Common;

/// <summary>What the passkey script adds to a form: the credential, or a code for why there is none.</summary>
public sealed class PasskeyInput
{
    public string? CredentialJson { get; set; }
    public string? Error { get; set; }

    /// <summary>The message for an error code the script reported, worded for the page that shows it.</summary>
    public static string ErrorMessage(string? code, string? invalidOptions = null) => code switch
    {
        "cancelled" => "The passkey request was cancelled or timed out. Try again when your device is ready.",
        "unsupported" => "This browser cannot use passkeys. Update it, or use another browser.",
        "exists" => "This device or password manager already holds a passkey for Palanca. Register the new one somewhere else.",
        "options-400" when invalidOptions is not null => invalidOptions,
        "options-401" => "Your session has ended. Sign in again.",
        "options-409" => "You already have 10 passkeys. Remove one before adding another.",
        "options-429" => "Too many failed attempts from this address. Wait a minute, then try again.",
        _ => "The browser could not complete the passkey request. Try again.",
    };
}
