using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Palanca.Tests.Owners;

/// <summary>Every dashboard route needs an owner session, except sign-in and enrollment (FLG-040, AC-005).</summary>
public sealed class DashboardGateTests(PalancaFactory factory) : IClassFixture<PalancaFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/passkeys")]
    public async Task APageWithoutASessionSendsTheBrowserToSignIn(string path)
    {
        using var client = new DashboardClient(factory);

        using var response = await client.Http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/sign-in?returnUrl={Uri.EscapeDataString(path)}", DashboardClient.RedirectPath(response));
    }

    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/enroll")]
    [InlineData("/health/live")]
    [InlineData("/css/palanca.css")]
    [InlineData("/js/passkeys.js")]
    public async Task SignInEnrollmentHealthAndAssetsNeedNoSession(string path)
    {
        using var client = new DashboardClient(factory);

        using var response = await client.Http.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AFormPostWithoutASessionChangesNothing()
    {
        using var client = new DashboardClient(factory);
        var page = await client.GetPageAsync("/sign-in");
        var countBefore = await factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken));

        using var response = await client.PostFormAsync("/passkeys", page, "passkey-AAAA", new() { ["CredentialId"] = "AAAA", ["Action"] = "remove" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(countBefore, await factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ANavigationPostWithoutASessionIsSentToSignIn()
    {
        using var client = new DashboardClient(factory);
        var page = await client.GetPageAsync("/sign-in");
        client.Http.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");

        using var response = await client.PostFormAsync("/passkeys", page, "add-passkey", new() { ["Input.Name"] = "Phone" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in?returnUrl=", DashboardClient.RedirectPath(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasskeyOptionsWithoutASessionAreRefused()
    {
        using var client = new DashboardClient(factory);
        var page = await client.GetPageAsync("/sign-in");

        using var response = await client.PostOptionsAsync("/passkeys/options", page);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/sign-in/options")]
    [InlineData("/enroll/options")]
    public async Task OptionsRequestsNeedTheAntiforgeryHeader(string path)
    {
        using var client = new DashboardClient(factory);
        await client.GetPageAsync("/sign-in");

        using var response = await client.Http.PostAsync(path, new StringContent("{}", null, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignOutNeedsTheAntiforgeryToken()
    {
        using var client = new DashboardClient(factory);

        using var response = await client.Http.PostAsync("/sign-out", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task TheSignInPageOffersOnlyAPasskey()
    {
        using var client = new DashboardClient(factory);

        var page = await client.GetPageAsync("/sign-in");

        Assert.DoesNotContain("type=\"password\"", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("type=\"email\"", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operation=\"request\"", page.Html, StringComparison.Ordinal);
    }
}
