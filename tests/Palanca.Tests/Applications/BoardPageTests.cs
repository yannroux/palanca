using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Flags;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Applications;

/// <summary>The dashboard pages: the board's toggles and confirmations, flags, applications, and environments.</summary>
public sealed class BoardPageTests : IAsyncLifetime
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
        var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", [], Owner, Ct);
        await flags.CreateAsync("hogarhero", "operator-denue-imports", "Operators can start DENUE imports", ["staging"], Owner, Ct);
    }

    [Fact]
    public async Task TheBoardShowsEachValueAsATile()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");

        Assert.Contains("value=\"operator-denue-imports|staging|off\" class=\"tile on\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("value=\"new-search|staging|on\" class=\"tile\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("popovertarget=\"toggle_new-search_production\"", page.Html, StringComparison.Ordinal);
        Assert.Contains("Turn <span class=\"key-literal\">new-search</span> on in Production?", page.Html, StringComparison.Ordinal);
        Assert.Contains("<dt>Application</dt><dd>HogarHero</dd>", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATileTogglesItsValueAndRecordsOneChangeByTheOwner()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");
        var before = await RecordCountAsync();
        var sent = DateTime.UtcNow;

        using var response = await _client.PostFormAsync("/apps/hogarhero", page, "board", new() { ["Toggle"] = "new-search|staging|on" });
        var answered = DateTime.UtcNow;

        Assert.Equal("/apps/hogarhero?flipped=new-search~staging", DashboardClient.RedirectPath(response));
        Assert.Equal("#flag-new-search", response.Headers.Location?.Fragment);
        Assert.True(await ValueAsync("new-search", "staging"));
        Assert.Equal(before + 1, await RecordCountAsync());
        var record = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(Ct));
        Assert.Equal((ChangeAction.FlagValueSet, ChangeSource.Dashboard, OwnerSetup.Email), (record.Action, record.Source, record.Actor));
        Assert.Equal(("hogarhero", "staging", "new-search", "off", "on"),
            (record.ApplicationKey, record.EnvironmentKey, record.FlagKey, record.OldValue, record.NewValue));
        Assert.InRange(record.OccurredAt, sent, answered);

        var after = await _client.GetPageAsync(DashboardClient.RedirectPath(response)!);
        Assert.Contains("value=\"new-search|staging|off\" class=\"tile on flipped\"", after.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtectedToggleWithoutConfirmationGoesToTheConfirmationPage()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");

        using var response = await _client.PostFormAsync("/apps/hogarhero", page, "board", new() { ["Toggle"] = "new-search|production|on" });

        Assert.Equal("/apps/hogarhero/confirm?flag=new-search&env=production&value=on", DashboardClient.RedirectPath(response));
        Assert.False(await ValueAsync("new-search", "production"));

        var confirm = await _client.GetPageAsync(DashboardClient.RedirectPath(response)!);
        Assert.Contains("Turn new-search on in Production?", confirm.Html, StringComparison.Ordinal);
        Assert.Contains("HogarHero", confirm.Html, StringComparison.Ordinal);
        using var confirmed = await _client.PostFormAsync(DashboardClient.RedirectPath(response)!, confirm, "confirm", []);

        Assert.Equal("/apps/hogarhero?flipped=new-search~production", DashboardClient.RedirectPath(confirmed));
        Assert.True(await ValueAsync("new-search", "production"));
    }

    [Fact]
    public async Task TheDialogsConfirmationChangesAProtectedValue()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");
        var before = await RecordCountAsync();

        using var response = await _client.PostFormAsync("/apps/hogarhero", page, "board", new() { ["Toggle"] = "new-search|production|on|confirmed" });

        Assert.Equal("/apps/hogarhero?flipped=new-search~production", DashboardClient.RedirectPath(response));
        Assert.True(await ValueAsync("new-search", "production"));
        Assert.Equal(before + 1, await RecordCountAsync());
    }

    [Fact]
    public async Task AnUnreadableToggleChangesNothing()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");

        using var response = await _client.PostFormAsync("/apps/hogarhero", page, "board", new() { ["Toggle"] = "new-search|production|on|forged" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ValueAsync("new-search", "production"));
    }

    [Fact]
    public async Task SettingTheCurrentValueAgainNeitherFlipsNorRecords()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero");
        var before = await RecordCountAsync();

        using var response = await _client.PostFormAsync("/apps/hogarhero", page, "board", new() { ["Toggle"] = "operator-denue-imports|staging|on" });

        Assert.Equal("/apps/hogarhero", DashboardClient.RedirectPath(response));
        Assert.Equal(before, await RecordCountAsync());
    }

    [Fact]
    public async Task TheFilterKeepsMatchingFlags()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero?q=DENUE");

        Assert.Contains("operator-denue-imports", page.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/apps/hogarhero/flags/new-search\"", page.Html, StringComparison.Ordinal);

        var none = await _client.GetPageAsync("/apps/hogarhero?q=nothing-like-this");
        Assert.Contains("No flag matches", none.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedFlagsCanBeRestoredOrDeletedFromTheBoard()
    {
        var flagPage = await _client.GetPageAsync("/apps/hogarhero/flags/new-search");
        using (var archived = await _client.PostFormAsync("/apps/hogarhero/flags/new-search", flagPage, "lifecycle", new() { ["Action"] = "archive" }))
        {
            Assert.Equal("/apps/hogarhero?status=archived", DashboardClient.RedirectPath(archived));
        }

        var board = await _client.GetPageAsync("/apps/hogarhero");
        Assert.Contains("Archived flags", board.Html, StringComparison.Ordinal);
        Assert.Contains("value=\"restore|new-search\"", board.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"new-search|staging|on\"", board.Html, StringComparison.Ordinal);

        using (var restored = await _client.PostFormAsync("/apps/hogarhero", board, "board", new() { ["FlagAction"] = "restore|new-search" }))
        {
            Assert.Equal("/apps/hogarhero?status=restored", DashboardClient.RedirectPath(restored));
        }

        using var notArchived = await _client.PostFormAsync("/apps/hogarhero", board, "board", new() { ["FlagAction"] = "delete|new-search" });
        Assert.Equal(HttpStatusCode.BadRequest, notArchived.StatusCode);
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Flags.CountAsync(Ct)));
    }

    [Fact]
    public async Task RestoringAFlagThatProductionServesNeedsTheDialogsConfirmation()
    {
        await _factory.WithDbAsync(async db =>
        {
            var flag = await db.Flags.SingleAsync(f => f.Key == "operator-denue-imports", Ct);
            var production = await db.Environments.SingleAsync(e => e.Key == "production", Ct);
            db.FlagValues.Add(new FlagValue { FlagId = flag.Id, EnvironmentId = production.Id, Enabled = true, UpdatedBy = OwnerSetup.Email });
            flag.ArchivedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        });
        var board = await _client.GetPageAsync("/apps/hogarhero");
        Assert.Contains("popovertarget=\"restore_operator-denue-imports\"", board.Html, StringComparison.Ordinal);

        using (var unconfirmed = await _client.PostFormAsync("/apps/hogarhero", board, "board", new() { ["FlagAction"] = "restore|operator-denue-imports" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        }

        Assert.True(await _factory.WithDbAsync(db => db.Flags.AnyAsync(f => f.Key == "operator-denue-imports" && f.ArchivedAt != null, Ct)));
        using var confirmed = await _client.PostFormAsync("/apps/hogarhero", board, "board", new() { ["FlagAction"] = "restore|operator-denue-imports|confirmed" });
        Assert.Equal("/apps/hogarhero?status=restored", DashboardClient.RedirectPath(confirmed));
    }

    [Fact]
    public async Task ANewFlagCannotStartOnInAProtectedEnvironment()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/flags/new");
        Assert.Contains("value=\"production\" disabled", page.Html, StringComparison.Ordinal);

        using var response = await _client.PostFormAsync("/apps/hogarhero/flags/new", page, "new-flag", new()
        {
            ["Input.Key"] = "quote-pdf-export",
            ["Input.Description"] = "Customers can export quotes as PDF",
            ["Input.On"] = "production",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await _factory.WithDbAsync(db => db.Flags.AnyAsync(f => f.Key == "quote-pdf-export", Ct)));
    }

    [Fact]
    public async Task ANewFlagStartsWithTheChosenValues()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/flags/new");

        using var response = await _client.PostFormAsync("/apps/hogarhero/flags/new", page, "new-flag", new()
        {
            ["Input.Key"] = "quote-pdf-export",
            ["Input.Description"] = "Customers can export quotes as PDF",
            ["Input.On"] = "staging",
        });

        Assert.Equal("/apps/hogarhero?status=created", DashboardClient.RedirectPath(response));
        Assert.True(await ValueAsync("quote-pdf-export", "staging"));
        Assert.False(await ValueAsync("quote-pdf-export", "production"));
    }

    [Fact]
    public async Task ADuplicateFlagKeyIsRefusedWithAReason()
    {
        var page = await _client.GetPageAsync("/apps/hogarhero/flags/new");

        using var response = await _client.PostFormAsync("/apps/hogarhero/flags/new", page, "new-flag", new()
        {
            ["Input.Key"] = "new-search",
            ["Input.Description"] = "Again",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already has a flag with the key new-search", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteringAnApplicationLeadsToItsEnvironments()
    {
        var home = await _client.GetPageAsync("/");

        using var response = await _client.PostFormAsync("/", home, "new-application", new() { ["Input.Key"] = "billing", ["Input.Name"] = "Billing" });

        Assert.Equal("/apps/billing/settings?status=created", DashboardClient.RedirectPath(response));
        var settings = await _client.GetPageAsync("/apps/billing/settings?status=created");
        Assert.Contains("Billing is registered.", settings.Html, StringComparison.Ordinal);

        using var added = await _client.PostFormAsync("/apps/billing/settings", settings, "new-environment", new()
        {
            ["NewEnvironment.Key"] = "production",
            ["NewEnvironment.Name"] = "Production",
            ["NewEnvironment.Protected"] = "true",
        });
        Assert.Equal("/apps/billing/settings?status=environment-added", DashboardClient.RedirectPath(added));
        Assert.True(await _factory.WithDbAsync(db => db.Environments.AnyAsync(e => e.Key == "production" && e.Application.Key == "billing" && e.IsProtected, Ct)));
    }

    [Fact]
    public async Task EnvironmentSettingsReorderProtectAndArchive()
    {
        var settings = await _client.GetPageAsync("/apps/hogarhero/settings");

        (await _client.PostFormAsync("/apps/hogarhero/settings", settings, "environment-production", new() { ["EnvironmentKey"] = "production", ["EnvironmentAction"] = "left" })).Dispose();
        (await _client.PostFormAsync("/apps/hogarhero/settings", settings, "environment-staging", new() { ["EnvironmentKey"] = "staging", ["EnvironmentAction"] = "protect" })).Dispose();
        (await _client.PostFormAsync("/apps/hogarhero/settings", settings, "environment-staging", new() { ["EnvironmentKey"] = "staging", ["EnvironmentAction"] = "archive" })).Dispose();

        var environments = await _factory.WithDbAsync(db => db.Environments.OrderBy(e => e.SortOrder).ToListAsync(Ct));
        Assert.Equal(["production", "staging"], environments.Select(e => e.Key));
        Assert.True(environments[1].IsProtected);
        Assert.NotNull(environments[1].ArchivedAt);

        var board = await _client.GetPageAsync("/apps/hogarhero");
        Assert.DoesNotContain("|staging|", board.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownApplicationIsNotFound()
    {
        using var response = await _client.Http.GetAsync("/apps/missing", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> RecordCountAsync() => _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(Ct));

    private Task<bool> ValueAsync(string flagKey, string environmentKey) =>
        _factory.WithDbAsync(db => db.FlagValues.AnyAsync(v => v.Flag.Key == flagKey && v.Environment.Key == environmentKey && v.Enabled, Ct));

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
