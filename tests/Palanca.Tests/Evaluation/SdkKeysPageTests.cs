using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Evaluation;

/// <summary>The SDK keys page: a new key is shown once and revocation is recorded (FLG-020 to FLG-022, FLG-042).</summary>
public sealed partial class SdkKeysPageTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly PalancaFactory _factory = new();
    private readonly SoftwareAuthenticator _authenticator = new();
    private DashboardClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _client = new DashboardClient(_factory);
        await OwnerSetup.SignInAsync(_factory, _client, _authenticator);
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
    }

    [Fact]
    public async Task ANewKeyIsShownOnceAndWorks()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/keys");

        using var response = await _client.PostFormAsync("/apps/hogarhero/keys", page, "create-staging", new()
        {
            ["EnvironmentKey"] = "staging",
            ["KeyName"] = "hogarhero-web",
        });

        // A redirect, so a reload cannot create a second key; its URL carries a one-time handle, not the key.
        var shown = DashboardClient.RedirectPath(response)!;
        Assert.StartsWith("/apps/hogarhero/keys?shown=", shown, StringComparison.Ordinal);
        Assert.DoesNotMatch(KeyPattern(), shown);
        using var first = await _client.Http.GetAsync(shown, Ct);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var key = Assert.Single(KeyPattern().Matches(await first.Content.ReadAsStringAsync(Ct))).Value;

        var again = await _client.GetPageAsync(shown);
        Assert.DoesNotContain(key, again.Html, StringComparison.Ordinal);
        Assert.Contains($"{key[..18]}…", again.Html, StringComparison.Ordinal);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.SdkKeys.CountAsync(Ct)));

        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/flags", Ct)).StatusCode);

        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(Ct));
        Assert.Equal((ChangeAction.SdkKeyCreated, "staging", $"hogarhero-web ({key[..18]})"), (record.Action, record.EnvironmentKey, record.NewValue));
    }

    [Fact]
    public async Task RevokingAKeyIsRecordedAndEndsIt()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/keys");
        using var created = await _client.PostFormAsync("/apps/hogarhero/keys", page, "create-staging", new() { ["EnvironmentKey"] = "staging", ["KeyName"] = "old" });
        var key = KeyPattern().Match((await _client.GetPageAsync(DashboardClient.RedirectPath(created)!)).Html).Value;
        var id = await _factory.WithDbAsync(db => db.SdkKeys.Select(k => k.Id).SingleAsync(Ct));

        var listed = await _client.GetPageAsync("/apps/hogarhero/keys");
        using var revoked = await _client.PostFormAsync("/apps/hogarhero/keys", listed, $"revoke-{id}", new() { ["KeyId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Assert.Equal("/apps/hogarhero/keys?status=revoked", DashboardClient.RedirectPath(revoked));
        Assert.Equal(ChangeAction.SdkKeyRevoked, (await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(Ct))).Action);
        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/flags", Ct)).StatusCode);
        Assert.Contains("Revoked", (await _client.GetPageAsync("/apps/hogarhero/keys")).Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyNeedsAName()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/keys");

        using var response = await _client.PostFormAsync("/apps/hogarhero/keys", page, "create-staging", new() { ["EnvironmentKey"] = "staging", ["KeyName"] = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.SdkKeys.CountAsync(Ct)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("palanca_sdk_[A-Za-z0-9_-]{43}", RegexOptions.None, "en-US")]
    private static partial Regex KeyPattern();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
