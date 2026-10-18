using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Flags;
using Palanca.ManagementTokens;
using Palanca.SdkKeys;
using Palanca.Tests.Owners;

namespace Palanca.Tests.Management;

/// <summary>
/// Reading through the management API, and who may (FLG-080 to FLG-082, FLG-087, FLG-032; AC-016, AC-018, AC-020, AC-022).
/// </summary>
public sealed class ManagementReadTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private string _scoped = null!;
    private string _everything = null!;

    public ManagementReadTests() => _factory = new PalancaFactory { Time = _time };

    public async ValueTask InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", ["staging"], Owner, Ct);
        await flags.CreateAsync("hogarhero", "operator-denue-imports", "Operator-started DENUE imports", [], Owner, Ct);
        await flags.CreateAsync("hogarhero", "legacy-search", "Old search page", [], Owner, Ct);
        await flags.ArchiveAsync("hogarhero", "legacy-search", Owner, Ct);
        await applications.CreateAsync("billing", "Billing", Owner, Ct);
        await applications.CreateEnvironmentAsync("billing", "production", "Production", false, Owner, Ct);
        _scoped = await CreateTokenAsync("hogarhero-reader", TokenPermission.ReadOnly, all: false, ["hogarhero"]);
        _everything = await CreateTokenAsync("agent", TokenPermission.ReadWrite, all: true, []);
    }

    [Fact]
    public async Task ATokenListsTheApplicationsInItsScope()
    {
        await RunAsync<ApplicationCommands>(c => c.CreateAsync("later-app", "Registered later", Owner, Ct));

        using var scoped = await GetAsync("/api/manage/v1/applications", _scoped);
        using var everything = await GetAsync("/api/manage/v1/applications", _everything);

        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        Assert.True(scoped.Headers.CacheControl?.NoStore);
        using var scopedBody = await JsonAsync(scoped);
        var hogarHero = Assert.Single(scopedBody.RootElement.GetProperty("applications").EnumerateArray());
        Assert.Equal(("hogarhero", "HogarHero"), (hogarHero.GetProperty("key").GetString(), hogarHero.GetProperty("name").GetString()));
        Assert.Equal(
            ["staging:False", "production:True"],
            hogarHero.GetProperty("environments").EnumerateArray().Select(e => $"{e.GetProperty("key").GetString()}:{e.GetProperty("protected").GetBoolean()}"));
        Assert.True(hogarHero.GetProperty("environments")[0].GetProperty("revision").GetInt64() > 0);

        // A token for every application reaches ones registered after it.
        using var everythingBody = await JsonAsync(everything);
        Assert.Equal(["billing", "hogarhero", "later-app"], everythingBody.RootElement.GetProperty("applications").EnumerateArray().Select(a => a.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task FlagsShowTheirDescriptionAndValuePerEnvironment()
    {
        using var response = await GetAsync("/api/manage/v1/applications/hogarhero/flags", _scoped);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("hogarhero", body.RootElement.GetProperty("application").GetString());
        var flags = body.RootElement.GetProperty("flags");
        Assert.Equal(["new-search", "operator-denue-imports"], flags.EnumerateArray().Select(f => f.GetProperty("key").GetString()));
        var newSearch = flags[0];
        Assert.Equal("New search results page", newSearch.GetProperty("description").GetString());
        Assert.Equal(["staging", "production"], newSearch.GetProperty("values").EnumerateObject().Select(p => p.Name));
        var staging = newSearch.GetProperty("values").GetProperty("staging");
        Assert.True(staging.GetProperty("enabled").GetBoolean());
        Assert.Equal(OwnerSetup.Email, staging.GetProperty("updatedBy").GetString());
        var production = newSearch.GetProperty("values").GetProperty("production");
        Assert.False(production.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, production.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task OneFlagIsReadByItsKey()
    {
        using var found = await GetAsync("/api/manage/v1/applications/hogarhero/flags/new-search", _scoped);
        using var archived = await GetAsync("/api/manage/v1/applications/hogarhero/flags/legacy-search", _scoped);
        using var unknown = await GetAsync("/api/manage/v1/applications/hogarhero/flags/no-such-flag", _scoped);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using var body = await JsonAsync(found);
        Assert.Equal("new-search", body.RootElement.GetProperty("key").GetString());
        Assert.True(body.RootElement.GetProperty("values").GetProperty("staging").GetProperty("enabled").GetBoolean());
        await AssertProblemAsync(archived, HttpStatusCode.NotFound);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHistoryIsNewestFirstFilteredAndPaged()
    {
        for (var i = 0; i < 26; i++)
        {
            await RunWriterAsync("operator-denue-imports", "staging", enabled: i % 2 == 0);
            await RunWriterAsync("operator-denue-imports", "production", enabled: i % 2 == 0);
        }

        using var first = await JsonAsync(await GetAsync("/api/manage/v1/applications/hogarhero/history", _scoped));
        var changes = first.RootElement.GetProperty("changes");
        Assert.Equal(50, changes.GetArrayLength());
        var newest = changes[0];
        Assert.Equal(("dashboard", OwnerSetup.Email, "flagValueSet"), (newest.GetProperty("source").GetString(), newest.GetProperty("actor").GetString(), newest.GetProperty("action").GetString()));
        Assert.Equal(("production", "operator-denue-imports", "on", "off"), (
            newest.GetProperty("environment").GetString(), newest.GetProperty("flag").GetString(),
            newest.GetProperty("oldValue").GetString(), newest.GetProperty("newValue").GetString()));
        Assert.Equal("Turned operator-denue-imports off in production", newest.GetProperty("summary").GetString());
        var ids = changes.EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.OrderDescending(), ids);

        var next = first.RootElement.GetProperty("next").GetInt64();
        using var second = await JsonAsync(await GetAsync($"/api/manage/v1/applications/hogarhero/history?before={next}", _scoped));
        Assert.Contains(second.RootElement.GetProperty("changes").EnumerateArray(), c => c.GetProperty("action").GetString() == "flagCreated");
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next").ValueKind);

        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/hogarhero/history?before=abc", _scoped), HttpStatusCode.BadRequest);

        using var filtered = await JsonAsync(await GetAsync("/api/manage/v1/applications/hogarhero/history?flag=new-search&environment=staging", _scoped));
        var only = Assert.Single(filtered.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal("Turned new-search on in staging", only.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ATokenReachesOnlyTheApplicationsInItsScope()
    {
        await RunAsync<ApplicationCommands>(c => c.ArchiveAsync("billing", Owner, Ct));

        // A scoped token learns nothing about other applications, whether they exist or not.
        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/billing/flags", _scoped), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/no-such-app/flags", _scoped), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/billing/history", _scoped), HttpStatusCode.Forbidden);

        // A token for every application is told when one does not exist or is archived.
        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/no-such-app/flags", _everything), HttpStatusCode.NotFound);
        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications/billing/flags", _everything), HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    [InlineData("Bearer palanca_mgt_short")]
    [InlineData("Bearer palanca_mgt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AnythingButALiveTokenIsUnauthorizedWithoutSayingWhy(string? authorization)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/manage/v1/applications");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, Ct);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task AnSdkKeyIsUnauthorizedHere()
    {
        string key;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            key = (await scope.ServiceProvider.GetRequiredService<SdkKeyCommands>().CreateAsync("hogarhero", "staging", "web", Owner, Ct)).Key!;
        }

        await AssertUnauthorizedAsync(await GetAsync("/api/manage/v1/applications", key));
    }

    [Fact]
    public async Task ATokenWithARealPrefixButOtherCharactersIsUnauthorized()
    {
        // The prefix only finds candidates; the whole token's hash must match.
        var forged = _scoped[..18] + new string('A', _scoped.Length - 18);

        await AssertUnauthorizedAsync(await GetAsync("/api/manage/v1/applications", forged));
    }

    [Fact]
    public async Task ARevokedTokenFailsOnItsNextRequest()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/manage/v1/applications", _scoped)).StatusCode);
        var id = await _factory.WithDbAsync(db => db.ManagementTokens.Where(t => t.Name == "hogarhero-reader").Select(t => t.Id).SingleAsync(Ct));

        await RunAsync<ManagementTokenCommands>(c => c.RevokeAsync(id, Owner, Ct));

        await AssertUnauthorizedAsync(await GetAsync("/api/manage/v1/applications", _scoped));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/manage/v1/applications", _everything)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredTokenFailsOnItsNextRequest()
    {
        var shortLived = await CreateTokenAsync("short-lived", TokenPermission.ReadOnly, all: true, [], lifetimeDays: 30);
        _time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/manage/v1/applications", shortLived)).StatusCode);

        _time.Advance(TimeSpan.FromMinutes(1));

        await AssertUnauthorizedAsync(await GetAsync("/api/manage/v1/applications", shortLived));
    }

    [Fact]
    public async Task TheDashboardSessionAuthorizesNothingHere()
    {
        using var authenticator = new SoftwareAuthenticator();
        using var dashboard = new DashboardClient(_factory);
        await OwnerSetup.SignInAsync(_factory, dashboard, authenticator);
        Assert.Equal(HttpStatusCode.OK, (await dashboard.Http.GetAsync("/", Ct)).StatusCode);

        await AssertUnauthorizedAsync(await dashboard.Http.GetAsync("/api/manage/v1/applications", Ct));
    }

    [Fact]
    public async Task TheEleventhFailureWithinAMinuteIsRefused()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/manage/v1/applications", "palanca_mgt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
        }

        await AssertProblemAsync(await GetAsync("/api/manage/v1/applications", _scoped), HttpStatusCode.TooManyRequests);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/manage/v1/applications", _scoped)).StatusCode);
    }

    [Fact]
    public async Task LastUseIsWrittenAtMostOncePerMinute()
    {
        Assert.Null(await LastUsedAsync());
        await GetAsync("/api/manage/v1/applications", _scoped);
        var first = await LastUsedAsync();
        Assert.NotNull(first);

        _time.Advance(TimeSpan.FromSeconds(30));
        await GetAsync("/api/manage/v1/applications", _scoped);
        Assert.Equal(first, await LastUsedAsync());

        _time.Advance(TimeSpan.FromSeconds(31));
        await GetAsync("/api/manage/v1/applications", _scoped);
        Assert.True(await LastUsedAsync() > first);
    }

    [Theory]
    [InlineData("GET", "/api/manage/v1/tokens")]
    [InlineData("DELETE", "/api/manage/v1/applications/hogarhero/flags/new-search")]
    [InlineData("POST", "/api/manage/v1/applications/hogarhero/flags/new-search/archive")]
    public async Task OtherPathsNeedATokenAndAreNotFound(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var anonymous = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _everything);
        using var authenticated = await client.SendAsync(request, Ct);

        await AssertUnauthorizedAsync(anonymous);
        await AssertProblemAsync(authenticated, HttpStatusCode.NotFound);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Flags.CountAsync(f => f.Key == "new-search" && f.ArchivedAt == null, Ct)));
    }

    private async Task<string> CreateTokenAsync(string name, TokenPermission permission, bool all, string[] applications, int lifetimeDays = 365)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (result, created) = await scope.ServiceProvider.GetRequiredService<ManagementTokenCommands>()
            .CreateAsync(name, permission, all, applications, lifetimeDays, Owner, Ct);
        Assert.True(result.Succeeded, result.Message);
        return created!.Token;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("title").GetString()));
        }
    }

    private Task<DateTime?> LastUsedAsync() =>
        _factory.WithDbAsync(db => db.ManagementTokens.Where(t => t.Name == "hogarhero-reader").Select(t => t.LastUsedAt).SingleAsync(Ct));

    private async Task RunAsync<T>(Func<T, Task<CommandResult>> command)
        where T : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.True((await command(scope.ServiceProvider.GetRequiredService<T>())).Succeeded);
    }

    private async Task RunWriterAsync(string flagKey, string environmentKey, bool enabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FlagValueWriter>()
            .SetAsync("hogarhero", flagKey, environmentKey, enabled, Owner, confirmed: true, Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
