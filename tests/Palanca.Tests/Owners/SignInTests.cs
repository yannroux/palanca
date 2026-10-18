using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Palanca.Tests.Owners;

public sealed class SignInTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private readonly SoftwareAuthenticator _authenticator = new();

    public SignInTests() => _factory = new PalancaFactory { Time = _time };

    [Fact]
    public async Task SigningInIssuesTheSessionCookie()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();

        using var response = await client.SignInAsync(_authenticator);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", DashboardClient.RedirectPath(response));
        var cookie = Assert.Single(DashboardClient.SetCookies(response), c => c.StartsWith("__Host-Palanca.Session=", StringComparison.Ordinal));
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).Select(a => a.ToLowerInvariant()).ToList();
        Assert.Equal(["path=/", "secure", "samesite=strict", "httponly"], attributes);

        using var home = await client.Http.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains(OwnerSetup.Email, await home.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasskeysMustVerifyTheUserAndBeDiscoverable()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");

        using var creation = await client.PostOptionsAsync("/enroll/options", page, new { token });
        using var request = await client.PostOptionsAsync("/sign-in/options", page);

        var creationOptions = JsonNode.Parse(await creation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("localhost", creationOptions["rp"]!["id"]!.GetValue<string>());
        Assert.Equal("required", creationOptions["authenticatorSelection"]!["userVerification"]!.GetValue<string>());
        Assert.Equal("required", creationOptions["authenticatorSelection"]!["residentKey"]!.GetValue<string>());
        var requestOptions = JsonNode.Parse(await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("required", requestOptions["userVerification"]!.GetValue<string>());
        Assert.Empty(requestOptions["allowCredentials"]?.AsArray() ?? []);
    }

    [Fact]
    public async Task AResponseWithoutUserVerificationIsRejected()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();
        _authenticator.UserVerifies = false;

        using var response = await client.SignInAsync(_authenticator);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(client.Cookie("__Host-Palanca.Session"));
    }

    [Fact]
    public async Task SessionKeysAreKeptInTheConfiguredDirectory()
    {
        using var client = new DashboardClient(_factory);

        await OwnerSetup.SignInAsync(_factory, client, _authenticator);

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_factory.DataDirectory, "keys"), "key-*.xml"));
    }

    [Fact]
    public async Task SigningInRecordsTheLastUseOfThePasskey()
    {
        using var client = new DashboardClient(_factory);

        await OwnerSetup.SignInAsync(_factory, client, _authenticator);

        var passkey = await _factory.WithDbAsync(db => db.OwnerPasskeys.SingleAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(passkey.LastUsedAt);
        Assert.Equal(1u, passkey.Data.SignCount);
    }

    [Fact]
    public async Task SigningInReturnsToTheRequestedPage()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();
        using var gate = await client.Http.GetAsync("/passkeys", TestContext.Current.CancellationToken);
        Assert.Equal("/sign-in?returnUrl=%2Fpasskeys", DashboardClient.RedirectPath(gate));

        using var response = await client.SignInAsync(_authenticator, DashboardClient.RedirectPath(gate)!);

        Assert.Equal("/passkeys", DashboardClient.RedirectPath(response));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task AReturnUrlOffTheSiteGoesHome(string returnUrl)
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();

        using var response = await client.SignInAsync(_authenticator, $"/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal("/", DashboardClient.RedirectPath(response));
    }

    [Theory]
    [InlineData("https://sub.localhost")]
    [InlineData("http://localhost")]
    [InlineData("https://localhost:8443")]
    [InlineData("https://evil.example")]
    public async Task APasskeyResponseFromAnotherOriginIsRejected(string origin)
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();
        _authenticator.Origin = origin;

        using var response = await client.SignInAsync(_authenticator);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(client.Cookie("__Host-Palanca.Session"));
    }

    [Fact]
    public async Task ARegistrationFromAnotherOriginIsRejected()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        _authenticator.Origin = "https://sub.localhost";

        using var response = await client.EnrollAsync(token, "Laptop", _authenticator);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.OwnerPasskeys.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TheEleventhFailedSignInWithinAMinuteIsRefused()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        (await client.EnrollAsync(token, "Laptop", _authenticator)).Dispose();
        _authenticator.Origin = "https://evil.example";

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var rejected = await client.SignInAsync(_authenticator);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        _authenticator.Origin = PalancaFactory.Origin;
        using (var limited = await client.SignInAsync(_authenticator))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using var allowed = await client.SignInAsync(_authenticator);
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
    }

    [Fact]
    public async Task SigningOutEndsTheSession()
    {
        using var client = new DashboardClient(_factory);
        await OwnerSetup.SignInAsync(_factory, client, _authenticator);
        var home = await client.GetPageAsync("/");

        using var response = await client.Http.PostAsync("/sign-out",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = home.FormToken }),
            TestContext.Current.CancellationToken);

        Assert.Equal("/sign-in?status=signed-out", DashboardClient.RedirectPath(response));
        using var after = await client.Http.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
