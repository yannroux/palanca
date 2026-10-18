using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Palanca.Data;

namespace Palanca.Tests.Owners;

public sealed class EnrollmentTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private readonly SoftwareAuthenticator _authenticator = new();

    public EnrollmentTests() => _factory = new PalancaFactory { Time = _time };

    [Fact]
    public async Task TheLinkRegistersANamedPasskeyAndIsSpent()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);

        using var response = await client.EnrollAsync(token, "Work laptop", _authenticator);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?status=enrolled", DashboardClient.RedirectPath(response));
        await _factory.WithDbAsync(async db =>
        {
            var passkey = await db.OwnerPasskeys.Include(p => p.Owner).SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(("Work laptop", OwnerSetup.Email), (passkey.Name, passkey.Owner.Email));
            Assert.NotNull((await db.OwnerEnrollments.SingleAsync(TestContext.Current.CancellationToken)).UsedAt);

            var records = await db.ChangeRecords.OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Collection(
                records,
                issued => Assert.Equal((ChangeAction.OwnerEnrollmentIssued, ChangeSource.Host, "host"), (issued.Action, issued.Source, issued.Actor)),
                registered =>
                {
                    Assert.Equal((ChangeAction.PasskeyRegistered, ChangeSource.Dashboard), (registered.Action, registered.Source));
                    Assert.Equal((OwnerSetup.Email, "Work laptop"), (registered.Actor, registered.NewValue));
                });
        });
    }

    [Fact]
    public async Task TheLinkWorksOnce()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using (var client = new DashboardClient(_factory))
        using (var first = await client.EnrollAsync(token, "Laptop", _authenticator))
        {
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        }

        using var second = new DashboardClient(_factory);
        var page = await second.GetPageAsync("/enroll");
        using var options = await second.PostOptionsAsync("/enroll/options", page, new { token });

        Assert.Equal(HttpStatusCode.BadRequest, options.StatusCode);
    }

    [Fact]
    public async Task TheLinkExpiresAfterFifteenMinutes()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");

        _time.Advance(TimeSpan.FromMinutes(14));
        using (var before = await client.PostOptionsAsync("/enroll/options", page, new { token }))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using var after = await client.PostOptionsAsync("/enroll/options", page, new { token });
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
    }

    [Fact]
    public async Task ANewLinkReplacesAnUnusedOne()
    {
        var first = await OwnerSetup.IssueLinkAsync(_factory);
        var second = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");

        using var stale = await client.PostOptionsAsync("/enroll/options", page, new { token = first });
        using var current = await client.PostOptionsAsync("/enroll/options", page, new { token = second });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK), (stale.StatusCode, current.StatusCode));
    }

    [Fact]
    public async Task RegistrationRequiresTheLink()
    {
        // A ceremony started with a valid link cannot be finished under another token.
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");
        using var options = await client.PostOptionsAsync("/enroll/options", page, new { token });
        var credential = _authenticator.Create(await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var response = await client.PostFormAsync("/enroll", page, "enroll", new()
        {
            ["Input.Token"] = "not-the-token",
            ["Input.Name"] = "Laptop",
            ["Input.Credential.CredentialJson"] = credential,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.OwnerPasskeys.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AFailedAttemptNeverShowsTheTokenAgain()
    {
        var token = await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");

        using var response = await client.PostFormAsync("/enroll", page, "enroll", new()
        {
            ["Input.Token"] = token,
            ["Input.Name"] = "Laptop",
            ["Input.Credential.Error"] = "cancelled",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(token, html, StringComparison.Ordinal);
        Assert.Contains("Open the enrollment link again to retry.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEnrollmentFormIsLimitedToo()
    {
        await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            (await client.PostOptionsAsync("/enroll/options", page, new { token = "wrong" })).Dispose();
        }

        using var response = await client.PostFormAsync("/enroll", page, "enroll", new()
        {
            ["Input.Token"] = "wrong",
            ["Input.Name"] = "Laptop",
            ["Input.Credential.CredentialJson"] = "{}",
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task TheEleventhInvalidLinkWithinAMinuteIsRefused()
    {
        await OwnerSetup.IssueLinkAsync(_factory);
        using var client = new DashboardClient(_factory);
        var page = await client.GetPageAsync("/enroll");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var rejected = await client.PostOptionsAsync("/enroll/options", page, new { token = $"wrong-{attempt}" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using (var limited = await client.PostOptionsAsync("/enroll/options", page, new { token = "wrong" }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using var allowed = await client.PostOptionsAsync("/enroll/options", page, new { token = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
