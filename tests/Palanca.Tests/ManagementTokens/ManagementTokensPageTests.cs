using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Tests.Owners;

namespace Palanca.Tests.ManagementTokens;

/// <summary>
/// The management tokens page: a new token is shown once and stored as a hash, its permission, scope and expiry are
/// fixed at creation, and creation and revocation are recorded (FLG-083, FLG-042).
/// </summary>
public sealed partial class ManagementTokensPageTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private readonly SoftwareAuthenticator _authenticator = new();
    private DashboardClient _client = null!;

    public ManagementTokensPageTests() => _factory = new PalancaFactory { Time = _time };

    public async ValueTask InitializeAsync()
    {
        _client = new DashboardClient(_factory);
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateAsync("second-app", "Second app", Owner, Ct);
    }

    [Fact]
    public async Task TheFormStartsAtTheLeastAccess()
    {
        var page = await _client.GetPageAsync("/tokens");

        Assert.Matches("value=\"ReadOnly\" checked", page.Html);
        Assert.DoesNotMatch("value=\"ReadWrite\" checked", page.Html);
        Assert.Matches("value=\"chosen\" checked", page.Html);
        Assert.DoesNotMatch("name=\"Input.Applications\" value=\"[^\"]+\" checked", page.Html);
        Assert.Matches("<option value=\"365\" selected", page.Html);
    }

    [Fact]
    public async Task ANewTokenIsShownOnceAndStoredOnlyAsItsHash()
    {
        using var response = await CreateAsync("claude-hogarhero", "ReadWrite", applications: ["hogarhero"]);

        // A redirect, so a reload cannot create a second token; its URL carries a one-time handle, not the token.
        var shown = DashboardClient.RedirectPath(response)!;
        Assert.StartsWith("/tokens?shown=", shown, StringComparison.Ordinal);
        Assert.DoesNotMatch(TokenPattern(), shown);
        using var first = await _client.Http.GetAsync(shown, Ct);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var token = Assert.Single(TokenPattern().Matches(await first.Content.ReadAsStringAsync(Ct))).Value;

        var again = await _client.GetPageAsync(shown);
        Assert.DoesNotContain(token, again.Html, StringComparison.Ordinal);
        Assert.Contains($"{token[..18]}…", again.Html, StringComparison.Ordinal);

        var stored = await _factory.WithDbAsync(db => db.ManagementTokens.Include(t => t.Applications).SingleAsync(Ct));
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(token)), stored.Hash);
        Assert.Equal(token[..18], stored.Prefix);
        Assert.Equal((TokenPermission.ReadWrite, false), (stored.Permission, stored.AllApplications));
        Assert.Equal(["hogarhero"], stored.Applications.Select(a => a.Key));
        Assert.Equal(stored.CreatedAt.AddDays(365), stored.ExpiresAt);

        var record = await LastRecordAsync();
        Assert.Equal((ChangeAction.ManagementTokenCreated, ChangeSource.Dashboard, OwnerSetup.Email), (record.Action, record.Source, record.Actor));
        Assert.Equal($"claude-hogarhero ({token[..18]}): read-write, hogarhero, expires {Day(stored.ExpiresAt)}", record.NewValue);
        Assert.Null(record.ApplicationKey);
    }

    [Fact]
    public async Task ATokenCanCoverEveryApplication()
    {
        // The chosen applications are ignored once every application is in scope.
        using var response = await CreateAsync("reader", "ReadOnly", scope: "all", applications: ["hogarhero"], lifetimeDays: "30");

        Assert.StartsWith("/tokens?shown=", DashboardClient.RedirectPath(response), StringComparison.Ordinal);
        var stored = await _factory.WithDbAsync(db => db.ManagementTokens.Include(t => t.Applications).SingleAsync(Ct));
        Assert.Equal((TokenPermission.ReadOnly, true), (stored.Permission, stored.AllApplications));
        Assert.Empty(stored.Applications);
        Assert.Equal(stored.CreatedAt.AddDays(30), stored.ExpiresAt);
        Assert.EndsWith($": read-only, all applications, expires {Day(stored.ExpiresAt)}", (await LastRecordAsync()).NewValue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  ", "ReadOnly", "chosen", "hogarhero", "365")]
    [InlineData("agent", "Admin", "chosen", "hogarhero", "365")]
    [InlineData("agent", "ReadOnly", "chosen", null, "365")]
    [InlineData("agent", "ReadOnly", "chosen", "no-such-app", "365")]
    [InlineData("agent", "ReadOnly", "chosen", "hogarhero", "400")]
    [InlineData("agent", "ReadOnly", "chosen", "hogarhero", "7")]
    public async Task ATokenNeedsANamePermissionScopeAndAnOfferedExpiry(string name, string permission, string scope, string? application, string lifetimeDays)
    {
        using var response = await CreateAsync(name, permission, scope, application is null ? [] : [application], lifetimeDays);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.ManagementTokens.CountAsync(Ct)));
    }

    [Fact]
    public async Task TwoActiveTokensCannotShareAName()
    {
        (await CreateAsync("agent", "ReadOnly", applications: ["hogarhero"])).Dispose();

        using var duplicate = await CreateAsync("agent", "ReadWrite", applications: ["second-app"]);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.ManagementTokens.CountAsync(Ct)));

        (await RevokeAsync(await _factory.WithDbAsync(db => db.ManagementTokens.Select(t => t.Id).SingleAsync(Ct)))).Dispose();
        using var reused = await CreateAsync("agent", "ReadWrite", applications: ["second-app"]);
        Assert.Equal(HttpStatusCode.Redirect, reused.StatusCode);
    }

    [Fact]
    public async Task RevokingATokenIsRecorded()
    {
        (await CreateAsync("agent", "ReadOnly", applications: ["hogarhero"])).Dispose();
        var stored = await _factory.WithDbAsync(db => db.ManagementTokens.SingleAsync(Ct));

        using var revoked = await RevokeAsync(stored.Id);

        Assert.Equal("/tokens?status=revoked", DashboardClient.RedirectPath(revoked));
        Assert.NotNull(await _factory.WithDbAsync(db => db.ManagementTokens.Select(t => t.RevokedAt).SingleAsync(Ct)));
        var record = await LastRecordAsync();
        Assert.Equal((ChangeAction.ManagementTokenRevoked, $"agent ({stored.Prefix})"), (record.Action, record.OldValue));
        var page = await _client.GetPageAsync("/tokens");
        Assert.Contains("Revoked just now", page.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"revoke-{stored.Id}", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredTokenIsListedAsExpired()
    {
        (await CreateAsync("agent", "ReadOnly", applications: ["hogarhero"], lifetimeDays: "30")).Dispose();
        var id = await _factory.WithDbAsync(db => db.ManagementTokens.Select(t => t.Id).SingleAsync(Ct));

        _time.Advance(TimeSpan.FromDays(31));
        // The session ended meanwhile.
        (await _client.SignInAsync(_authenticator)).Dispose();
        var page = await _client.GetPageAsync("/tokens");

        Assert.Contains("Expired yesterday", page.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"revoke-{id}", page.Html, StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> CreateAsync(
        string name, string permission, string scope = "chosen", string[]? applications = null, string lifetimeDays = "365")
    {
        var page = await _client.GetPageAsync("/tokens");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Input.Name", name),
            new("Input.Permission", permission),
            new("Input.Scope", scope),
            new("Input.LifetimeDays", lifetimeDays),
        };
        fields.AddRange((applications ?? []).Select(key => new KeyValuePair<string, string>("Input.Applications", key)));
        return await _client.PostFormAsync("/tokens", page, "create-token", fields);
    }

    private async Task<HttpResponseMessage> RevokeAsync(int id)
    {
        var page = await _client.GetPageAsync("/tokens");
        return await _client.PostFormAsync("/tokens", page, $"revoke-{id}", new() { ["TokenId"] = id.ToString(CultureInfo.InvariantCulture) });
    }

    private Task<ChangeRecord> LastRecordAsync() => _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(Ct));

    private static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("palanca_mgt_[A-Za-z0-9_-]{43}", RegexOptions.None, "en-US")]
    private static partial Regex TokenPattern();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
