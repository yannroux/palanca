using System.Net;
using Palanca.Commands;

namespace Palanca.Tests.Owners;

internal static class OwnerSetup
{
    public const string Email = "owner@example.com";

    /// <summary>Runs the host command in-process and returns its output and exit code.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(PalancaFactory factory, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await OwnerCommand.RunAsync(factory.Services, args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs <c>owner enroll</c> (or <c>owner recover</c>) and returns the token from the printed link.</summary>
    public static async Task<string> IssueLinkAsync(PalancaFactory factory, bool recover = false)
    {
        var (exitCode, output, error) = recover
            ? await RunCommandAsync(factory, "recover")
            : await RunCommandAsync(factory, "enroll", Email);
        Assert.True(exitCode == 0, error);
        return output.Split('#')[1].Trim();
    }

    /// <summary>Creates the owner, registers a passkey, and signs <paramref name="client"/> in with it.</summary>
    public static async Task SignInAsync(PalancaFactory factory, DashboardClient client, SoftwareAuthenticator authenticator)
    {
        var token = await IssueLinkAsync(factory);
        using (var enrolled = await client.EnrollAsync(token, "Laptop", authenticator))
        {
            Assert.Equal(HttpStatusCode.Redirect, enrolled.StatusCode);
        }

        using var signedIn = await client.SignInAsync(authenticator);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", DashboardClient.RedirectPath(signedIn));
    }
}
