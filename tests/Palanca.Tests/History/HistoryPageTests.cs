using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Flags;
using Palanca.History;
using Palanca.Tests.Owners;

namespace Palanca.Tests.History;

/// <summary>The history page (History, FLG-043, AC-006, AC-019's display).</summary>
public sealed partial class HistoryPageTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private static readonly ChangeActor Agent = new(ChangeSource.Api, "claude-staging");
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
        var writer = scope.ServiceProvider.GetRequiredService<FlagValueWriter>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await applications.CreateAsync("billing", "Billing", Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", [], Owner, Ct);
        await writer.SetAsync("hogarhero", "new-search", "staging", true, Owner, confirmed: false, Ct);
        await writer.SetAsync("hogarhero", "new-search", "production", true, Agent, confirmed: true, Ct);
    }

    [Fact]
    public async Task ChangesAreListedNewestFirstInWords()
    {
        var page = await _client.GetPageAsync("/history");

        var changes = Changes(page);
        Assert.Equal("Turned new-search on in production", changes[0]);
        Assert.Equal("Turned new-search on in staging", changes[1]);
        Assert.Equal("Created new-search", changes[2]);
        Assert.Contains("Issued an enrollment link for owner@example.com", changes);
        Assert.Contains("Registered the passkey Laptop", changes);
    }

    [Fact]
    public async Task AnAgentsChangeStandsOutWithItsTokenName()
    {
        var page = await _client.GetPageAsync("/history");

        Assert.Matches("<tr class=\"history-agent\">\\s*<td class=\"cell-time\">[^\\n]*\\n\\s*<td class=\"history-who\" data-label=\"Who\">\\s*<span>\\s*<span class=\"source source-api\">API</span>\\s*<span class=\"history-actor\">claude-staging</span>", page.Html);
        Assert.Single(AgentRowPattern().Matches(page.Html));
    }

    [Theory]
    [InlineData("?app=billing", new[] { "Registered the application Billing" })]
    [InlineData("?app=hogarhero&env=production", new[] { "Turned new-search on in production", "Added the protected environment production, named Production" })]
    [InlineData("?flag=new-search", new[] { "Turned new-search on in production", "Turned new-search on in staging", "Created new-search" })]
    [InlineData("?source=api", new[] { "Turned new-search on in production" })]
    [InlineData("?source=host", new[] { "Issued an enrollment link for owner@example.com" })]
    [InlineData("?app=+HogarHero+&env=Production", new[] { "Turned new-search on in production", "Added the protected environment production, named Production" })]
    public async Task FiltersNarrowTheHistory(string query, string[] expected)
    {
        var page = await _client.GetPageAsync($"/history{query}");

        Assert.Equal(expected, Changes(page));
    }

    [Theory]
    [InlineData("?before=abc")]
    [InlineData("?before=-1&source=bogus")]
    [InlineData("?app=%00&env=%20&flag=%E2%80%A6")]
    public async Task MalformedFiltersShowAnAnswerNotAnError(string query)
    {
        using var response = await _client.Http.GetAsync($"/history{query}", Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADescriptionChangeShowsWhatItWasAndWhatItBecame()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
            Assert.True((await flags.EditDescriptionAsync("hogarhero", "new-search", "New search results page\nwith filters", Owner, Ct)).Succeeded);
        }

        var page = await _client.GetPageAsync("/history?flag=new-search");

        Assert.Equal("Changed the description of new-search", Changes(page)[0]);
        Assert.Equal(["From “New search results page” to “New search results page\nwith filters”", "“New search results page”"], Details(page));
    }

    [Fact]
    public async Task APageBeyondTheOldestChangeSaysSo()
    {
        var page = await _client.GetPageAsync("/history?before=1");

        Assert.Contains("No older changes", page.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderChangesAreOnTheNextPage()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<FlagValueWriter>();
            for (var change = 0; change < HistoryQueries.PageSize; change++)
            {
                await writer.SetAsync("hogarhero", "new-search", "staging", change % 2 == 1, Owner, confirmed: false, Ct);
            }
        }

        var first = await _client.GetPageAsync("/history?flag=new-search");
        Assert.Equal(HistoryQueries.PageSize, Changes(first).Count);
        var older = OlderPattern().Match(first.Html).Groups[1].Value;
        Assert.StartsWith("/history?flag=new-search&amp;before=", older, StringComparison.Ordinal);

        var second = await _client.GetPageAsync(System.Net.WebUtility.HtmlDecode(older));
        Assert.Equal(["Turned new-search on in production", "Turned new-search on in staging", "Created new-search"], Changes(second));
        Assert.DoesNotMatch(OlderPattern(), second.Html);
    }

    [Fact]
    public async Task TheHistoryOffersNoWayToEditOrDeleteARecord()
    {
        var page = await _client.GetPageAsync("/history");

        // The only form that posts is sign-out, in the header; the filters use GET.
        Assert.Equal(["/sign-out"], [.. PostFormPattern().Matches(page.Html).Select(m => m.Groups[1].Value)]);
    }

    [Fact]
    public void EveryKindOfChangeHasItsOwnWording()
    {
        foreach (var action in Enum.GetValues<ChangeAction>())
        {
            var record = new ChangeRecord { Actor = "someone", Action = action, ApplicationKey = "app", EnvironmentKey = "env", FlagKey = "flag", OldValue = "old", NewValue = "new" };
            Assert.NotEqual(action.ToString(), ChangeWording.Describe(record));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<string> Changes(DashboardClient.Page page) =>
        [.. ChangePattern().Matches(page.Html).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))];

    [GeneratedRegex("<span class=\"history-sentence\">([^<]*)</span>", RegexOptions.None, "en-US")]
    private static partial Regex ChangePattern();

    private static List<string> Details(DashboardClient.Page page) =>
        [.. DetailPattern().Matches(page.Html).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))];

    [GeneratedRegex("<span class=\"history-detail\">([^<]*)</span>", RegexOptions.None, "en-US")]
    private static partial Regex DetailPattern();

    [GeneratedRegex("<tr class=\"history-agent\">", RegexOptions.None, "en-US")]
    private static partial Regex AgentRowPattern();

    [GeneratedRegex("<a class=\"button button-quiet\" href=\"(/history\\?[^\"]*)\">Older changes</a>", RegexOptions.None, "en-US")]
    private static partial Regex OlderPattern();

    [GeneratedRegex("<form method=\"post\" action=\"([^\"]*)\"", RegexOptions.None, "en-US")]
    private static partial Regex PostFormPattern();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authenticator.Dispose();
        await _factory.DisposeAsync();
    }
}
