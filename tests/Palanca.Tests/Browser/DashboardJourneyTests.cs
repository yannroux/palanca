using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Palanca.Data;
using Palanca.Flags;

namespace Palanca.Tests.Browser;

/// <summary>
/// The routine rollout in Chrome: register an application and its environments, create a flag, turn it on in
/// staging, then in production through the protected environment's confirmation.
/// </summary>
public sealed class DashboardJourneyTests : IAsyncLifetime
{
    private BrowserHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await BrowserHost.StartAsync();

    [Fact]
    public async Task TheOwnerRollsAFlagOutFromStagingToProduction()
    {
        var origin = _host.Origin;
        await using var context = await _host.NewContextAsync();
        var page = await context.NewPageAsync();
        var sockets = new List<string>();
        page.WebSocket += (_, socket) => sockets.Add(socket.Url);
        await BrowserHost.AddVirtualAuthenticatorAsync(context, page);
        await _host.SignInAsync(page);

        await page.GetByRole(AriaRole.Textbox, new() { Name = "Key", Exact = true }).FillAsync("hogarhero");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("HogarHero");
        await page.GetByRole(AriaRole.Button, new() { Name = "Register" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/apps/hogarhero/settings?status=created");

        await AddEnvironmentAsync(page, "staging", "Staging", isProtected: false);
        await AddEnvironmentAsync(page, "production", "Production", isProtected: true);

        await page.GotoAsync("/apps/hogarhero");
        await Assertions.Expect(page.GetByText("No flags yet")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "New flag" }).ClickAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Key", Exact = true }).FillAsync("new-search");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Description", Exact = true }).FillAsync("New search results page");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create flag" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/apps/hogarhero?status=created#flag-new-search");

        var staging = page.GetByRole(AriaRole.Button, new() { Name = "new-search is off in Staging. Turn it on." });
        await staging.ClickAsync();
        await page.WaitForURLAsync($"{origin}/apps/hogarhero?flipped=new-search~staging#flag-new-search");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "new-search is on in Staging. Turn it off." }))
            .ToHaveClassAsync("tile on flipped");

        await page.GetByRole(AriaRole.Button, new() { Name = "new-search is off in Production. Turn it on." }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Turn new-search on in Production?");
        await Assertions.Expect(dialog.Locator("dd")).ToHaveTextAsync(["HogarHero", "Production Protected", "new-search", "ON"]);
        await BrowserHost.ScreenshotAsync(page, "board-confirmation");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Turn on in Production" }).ClickAsync();
        await page.WaitForURLAsync($"{origin}/apps/hogarhero?flipped=new-search~production#flag-new-search");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "new-search is on in Production. Turn it off." })).ToBeVisibleAsync();

        var values = await _host.Factory.WithDbAsync(db => db.FlagValues.Where(v => v.Enabled).CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, values);

        // A staging key for the application, shown once.
        await page.GetByRole(AriaRole.Link, new() { Name = "SDK keys" }).ClickAsync();
        var stagingKeys = page.Locator("section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Staging", Exact = true }) });
        await stagingKeys.GetByLabel("New key name").FillAsync("hogarhero-web");
        await stagingKeys.GetByRole(AriaRole.Button, new() { Name = "Create key" }).ClickAsync();
        var secret = page.Locator("code.secret");
        await Assertions.Expect(secret).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^palanca_sdk_[A-Za-z0-9_-]{43}$"));
        await BrowserHost.ScreenshotAsync(page, "keys-created");
        await page.GotoAsync("/apps/hogarhero/keys");
        await Assertions.Expect(page.Locator("code.secret")).ToHaveCountAsync(0);
        await Assertions.Expect(stagingKeys.Locator("tbody tr")).ToHaveCountAsync(1);

        Assert.Empty(sockets);

        await CaptureReviewScreensAsync(page);
    }

    private static async Task AddEnvironmentAsync(IPage page, string key, string name, bool isProtected)
    {
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Key", Exact = true }).FillAsync(key);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync(name);
        if (isProtected)
        {
            await page.GetByRole(AriaRole.Checkbox, new() { Name = "Protected", Exact = true }).CheckAsync();
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Add environment" }).ClickAsync();
        await page.WaitForURLAsync("**/settings?status=environment-added");
    }

    // A fuller board for design review: more flags, older changes, and one made by an agent's token.
    private async Task CaptureReviewScreensAsync(IPage page)
    {
        if (Environment.GetEnvironmentVariable("PALANCA_SCREENSHOTS") is not { Length: > 0 })
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
            var writer = scope.ServiceProvider.GetRequiredService<FlagValueWriter>();
            var owner = new ChangeActor(ChangeSource.Dashboard, Owners.OwnerSetup.Email);
            (string Key, string Description, string[] On, int DaysAgo)[] seeds =
            [
                ("payout-weekly", "Weekly payouts for providers", ["staging", "production"], 21),
                ("chat-attachments", "File attachments in customer chat", ["staging"], 12),
                ("booking-reminders-sms", "Text reminder 24 hours before a booking", [], 9),
                ("map-view-results", "Show search results on a map", [], 6),
                ("quote-pdf-export", "Customers can export quotes as PDF", ["staging", "production"], 4),
                ("review-photos", "Customers can attach photos to reviews", ["staging"], 2),
                ("operator-denue-imports", "Operators can start DENUE prospect imports", ["staging"], 0),
            ];
            foreach (var (key, description, on, daysAgo) in seeds)
            {
                _host.Time.Offset = -TimeSpan.FromDays(daysAgo) - TimeSpan.FromHours(3);
                var created = await flags.CreateAsync("hogarhero", key, description, on, owner, ct);
                Assert.True(created.Succeeded, created.Message);
            }

            _host.Time.Offset = TimeSpan.Zero;

            Assert.True((await flags.CreateAsync("hogarhero", "provider-onboarding-v2", "Second version of provider onboarding", [], owner, ct)).Succeeded);
            await writer.SetAsync("hogarhero", "provider-onboarding-v2", "staging", true, new ChangeActor(ChangeSource.Api, "claude-staging"), confirmed: true, ct);
            Assert.True((await flags.CreateAsync("hogarhero", "legacy-search", "Old search page, replaced by new-search", [], owner, ct)).Succeeded);
            Assert.True((await flags.ArchiveAsync("hogarhero", "legacy-search", owner, ct)).Succeeded);
        }

        // A different URL from the current one, so the page really reloads; then the flip has time to finish.
        await page.GotoAsync("/apps/hogarhero?flipped=provider-onboarding-v2~staging");
        await page.WaitForTimeoutAsync(1000);
        await BrowserHost.ScreenshotAsync(page, "board");
        await page.SetViewportSizeAsync(390, 844);
        await BrowserHost.ScreenshotAsync(page, "board-mobile");
        await page.SetViewportSizeAsync(1440, 900);

        // With 40 flags, the header row stays pinned while the owner scrolls down the board.
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
            var owner = new ChangeActor(ChangeSource.Dashboard, Owners.OwnerSetup.Email);
            for (var number = 1; number <= 31; number++)
            {
                Assert.True((await flags.CreateAsync("hogarhero", $"experiment-{number:00}", $"Experiment {number}", [], owner, ct)).Succeeded);
            }
        }

        await page.GotoAsync("/apps/hogarhero");
        await page.Locator("#flag-experiment-30").ScrollIntoViewIfNeededAsync();
        var headerTop = await page.Locator(".board thead th").First.EvaluateAsync<double>("th => th.getBoundingClientRect().top");
        Assert.Equal(0, headerTop, precision: 0);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Environment.GetEnvironmentVariable("PALANCA_SCREENSHOTS")!, "board-scrolled.png") });

        foreach (var (path, name) in new[]
        {
            ("/", "home"),
            ("/apps/hogarhero/settings", "settings"),
            ("/apps/hogarhero/keys", "keys"),
            ("/apps/hogarhero/flags/new", "new-flag"),
            ("/apps/hogarhero/flags/quote-pdf-export", "flag"),
            ("/apps/hogarhero/confirm?flag=map-view-results&env=production&value=on", "confirm-page"),
        })
        {
            await page.GotoAsync(path);
            await BrowserHost.ScreenshotAsync(page, name);
        }

        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/");
        await BrowserHost.ScreenshotAsync(page, "home-mobile");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
