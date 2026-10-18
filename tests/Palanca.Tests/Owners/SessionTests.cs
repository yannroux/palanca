using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Palanca.Tests.Owners;

/// <summary>Session limits and revocation (FLG-047, FLG-048, AC-005, AC-024).</summary>
public sealed class SessionTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private readonly SoftwareAuthenticator _authenticator = new();
    private readonly DashboardClient _client;

    public SessionTests()
    {
        _factory = new PalancaFactory { Time = _time };
        _client = new DashboardClient(_factory);
    }

    [Fact]
    public async Task ASessionEndsAfterThirtyMinutesWithoutARequest()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, await StatusOfHomeAsync());

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Redirect, await StatusOfHomeAsync());
    }

    [Fact]
    public async Task ASessionEndsEightHoursAfterSignInEvenWhenInUse()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);

        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(8) - TimeSpan.FromMinutes(20); elapsed += TimeSpan.FromMinutes(20))
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await StatusOfHomeAsync());
        }

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Redirect, await StatusOfHomeAsync());
    }

    [Fact]
    public async Task RecoveryEndsEverySessionAndRevokesEveryPasskey()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);

        var token = await OwnerSetup.IssueLinkAsync(_factory, recover: true);

        Assert.Equal(HttpStatusCode.Redirect, await StatusOfHomeAsync());
        using (var oldPasskey = await _client.SignInAsync(_authenticator))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oldPasskey.StatusCode);
        }

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.OwnerPasskeys.CountAsync(TestContext.Current.CancellationToken));
            var record = await db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken);
            Assert.Equal((Palanca.Data.ChangeAction.OwnerRecovered, Palanca.Data.ChangeSource.Host, "1 passkey"), (record.Action, record.Source, record.OldValue));
        });

        // The new link registers a passkey that signs in again.
        using var recovered = new SoftwareAuthenticator();
        using (var enrolled = await _client.EnrollAsync(token, "Recovered", recovered))
        {
            Assert.Equal(HttpStatusCode.Redirect, enrolled.StatusCode);
        }

        using var signedIn = await _client.SignInAsync(recovered);
        Assert.Equal("/", DashboardClient.RedirectPath(signedIn));
    }

    [Fact]
    public async Task RemovingAPasskeyEndsOtherSessionsButNotThisOne()
    {
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        using (var added = await _client.AddPasskeyAsync(_authenticator, "Phone"))
        {
            Assert.Equal("/passkeys?status=added", DashboardClient.RedirectPath(added));
        }

        using var other = new DashboardClient(_factory);
        using (var otherSignIn = await other.SignInAsync(_authenticator, index: 0))
        {
            Assert.Equal(HttpStatusCode.Redirect, otherSignIn.StatusCode);
        }

        var page = await _client.GetPageAsync("/passkeys");
        using (var removed = await RemoveAsync(page, page.CredentialIds[1]))
        {
            Assert.Equal("/passkeys?status=removed", DashboardClient.RedirectPath(removed));
        }

        Assert.Equal(HttpStatusCode.OK, await StatusOfHomeAsync());
        using var otherHome = await other.Http.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, otherHome.StatusCode);
    }

    private async Task<HttpStatusCode> StatusOfHomeAsync()
    {
        using var response = await _client.Http.GetAsync("/", TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> RemoveAsync(DashboardClient.Page page, string credentialId) =>
        _client.PostFormAsync("/passkeys", page, $"passkey-{credentialId}", new() { ["CredentialId"] = credentialId, ["Action"] = "remove" });

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
