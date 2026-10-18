using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// Changing flags through the management API (FLG-082, FLG-084 to FLG-086, FLG-089; AC-016, AC-017, AC-019 to AC-021).
/// </summary>
public sealed class ManagementWriteTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private string _writer = null!;
    private int _writerId;
    private string _reader = null!;
    private string _billingWriter = null!;
    private string _stagingKey = null!;

    public ManagementWriteTests() => _factory = new PalancaFactory { Time = _time };

    public async ValueTask InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", ["staging"], Owner, Ct);
        await flags.CreateAsync("hogarhero", "legacy-search", "Old search page", [], Owner, Ct);
        await flags.ArchiveAsync("hogarhero", "legacy-search", Owner, Ct);
        await applications.CreateAsync("billing", "Billing", Owner, Ct);
        await applications.CreateEnvironmentAsync("billing", "production", "Production", false, Owner, Ct);
        _stagingKey = (await scope.ServiceProvider.GetRequiredService<SdkKeyCommands>().CreateAsync("hogarhero", "staging", "web", Owner, Ct)).Key!;
        _writer = await CreateTokenAsync("claude-hogarhero", TokenPermission.ReadWrite, ["hogarhero"]);
        _writerId = await _factory.WithDbAsync(db => db.ManagementTokens.Where(t => t.Name == "claude-hogarhero").Select(t => t.Id).SingleAsync(Ct));
        _reader = await CreateTokenAsync("hogarhero-reader", TokenPermission.ReadOnly, ["hogarhero"]);
        _billingWriter = await CreateTokenAsync("billing-writer", TokenPermission.ReadWrite, ["billing"]);
    }

    [Fact]
    public async Task AReadWriteTokenCreatesAFlagThatConsumersSee()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags", _writer,
            new { key = "new-onboarding", description = "Second version of provider onboarding", values = new { staging = true, production = true } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/manage/v1/applications/hogarhero/flags/new-onboarding", response.Headers.Location?.OriginalString);
        using var body = await JsonAsync(response);
        Assert.Equal("new-onboarding", body.RootElement.GetProperty("key").GetString());
        var production = body.RootElement.GetProperty("values").GetProperty("production");
        Assert.True(production.GetProperty("enabled").GetBoolean());
        Assert.Equal("claude-hogarhero", production.GetProperty("updatedBy").GetString());

        // Each change names the token, and is recorded as coming through the API (FLG-084, AC-019).
        var records = await RecordsAsync("new-onboarding");
        Assert.Equal(
            [(ChangeAction.FlagCreated, (string?)null), (ChangeAction.FlagValueSet, "staging"), (ChangeAction.FlagValueSet, "production")],
            records.Select(r => (r.Action, r.EnvironmentKey)));
        Assert.All(records, r => Assert.Equal((ChangeSource.Api, "claude-hogarhero", (int?)_writerId), (r.Source, r.Actor, r.TokenId)));

        // The staging consumer's next poll serves it.
        using var evaluation = _factory.CreateClient();
        evaluation.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _stagingKey);
        using var flags = JsonDocument.Parse(await evaluation.GetStringAsync("/api/v1/flags", Ct));
        Assert.True(flags.RootElement.GetProperty("flags").GetProperty("new-onboarding").GetBoolean());
    }

    [Fact]
    public async Task EnvironmentsTheValuesDoNotTurnOnStartOff()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags", _writer,
            new { key = "quote-pdf-export", description = "Quotes as PDF", values = new { staging = false } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.All(body.RootElement.GetProperty("values").EnumerateObject(), value => Assert.False(value.Value.GetProperty("enabled").GetBoolean()));
        Assert.Equal([ChangeAction.FlagCreated], (await RecordsAsync("quote-pdf-export")).Select(r => r.Action));
    }

    [Theory]
    [InlineData("new-search")]
    [InlineData("legacy-search")]
    public async Task AnExistingKeyConflicts(string key)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags", _writer, new { key, description = "Again" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("New Search", "Valid description", null)]
    [InlineData("valid-key", "", null)]
    [InlineData("valid-key", null, null)]
    [InlineData("valid-key", "Valid description", "qa")]
    public async Task ACreateNeedsAValidKeyDescriptionAndKnownEnvironments(string? key, string? description, string? environment)
    {
        var flagCount = await _factory.WithDbAsync(db => db.Flags.CountAsync(Ct));
        object values = environment is null ? new Dictionary<string, bool>() : new Dictionary<string, bool> { [environment] = false };

        using var response = await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags", _writer, new { key, description, values });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(flagCount, await _factory.WithDbAsync(db => db.Flags.CountAsync(Ct)));
    }

    [Fact]
    public async Task ADescriptionCanChange()
    {
        using var response = await SendAsync(HttpMethod.Patch, "/api/manage/v1/applications/hogarhero/flags/new-search", _writer, new { description = "Search results v2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("Search results v2", body.RootElement.GetProperty("description").GetString());
        var record = (await RecordsAsync("new-search"))[^1];
        Assert.Equal(
            (ChangeAction.FlagDescriptionChanged, ChangeSource.Api, "claude-hogarhero", (int?)_writerId),
            (record.Action, record.Source, record.Actor, record.TokenId));

        await AssertProblemAsync(await SendAsync(HttpMethod.Patch, "/api/manage/v1/applications/hogarhero/flags/legacy-search", _writer, new { description = "x" }), HttpStatusCode.NotFound);
        await AssertProblemAsync(await SendAsync(HttpMethod.Patch, "/api/manage/v1/applications/hogarhero/flags/no-such-flag", _writer, new { description = "x" }), HttpStatusCode.NotFound);
        await AssertProblemAsync(await SendAsync(HttpMethod.Patch, "/api/manage/v1/applications/hogarhero/flags/new-search", _writer, new { description = " " }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AReadWriteTokenSetsAValueInProductionAndRepeatingItChangesNothing()
    {
        var before = await RevisionAsync("production");

        using var response = await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _writer, new { enabled = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = await JsonAsync(response))
        {
            Assert.Equal(("hogarhero", "production", "new-search", true), (
                body.RootElement.GetProperty("application").GetString(), body.RootElement.GetProperty("environment").GetString(),
                body.RootElement.GetProperty("flag").GetString(), body.RootElement.GetProperty("enabled").GetBoolean()));
            Assert.Equal(before + 1, body.RootElement.GetProperty("revision").GetInt64());
        }

        var record = (await RecordsAsync("new-search"))[^1];
        Assert.Equal((ChangeAction.FlagValueSet, "production", "off", "on", ChangeSource.Api, (int?)_writerId), (
            record.Action, record.EnvironmentKey, record.OldValue, record.NewValue, record.Source, record.TokenId));

        var count = (await RecordsAsync("new-search")).Count;
        using var again = await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _writer, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var body = await JsonAsync(again))
        {
            Assert.Equal(before + 1, body.RootElement.GetProperty("revision").GetInt64());
        }

        Assert.Equal(count, (await RecordsAsync("new-search")).Count);
    }

    [Theory]
    [InlineData("/api/manage/v1/applications/hogarhero/flags/no-such-flag/values/staging")]
    [InlineData("/api/manage/v1/applications/hogarhero/flags/new-search/values/qa")]
    [InlineData("/api/manage/v1/applications/hogarhero/flags/legacy-search/values/staging")]
    public async Task AValueForAnUnknownFlagOrEnvironmentIsNotFound(string path)
    {
        await AssertProblemAsync(await SendAsync(HttpMethod.Put, path, _writer, new { enabled = true }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AReadOnlyTokenIsForbiddenEveryWriteAndNothingChanges()
    {
        var records = await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(Ct));
        var revision = await RevisionAsync("production");

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags", _reader, new { key = "sneaky", description = "x" }), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await SendAsync(HttpMethod.Patch, "/api/manage/v1/applications/hogarhero/flags/new-search", _reader, new { description = "x" }), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _reader, new { enabled = true }), HttpStatusCode.Forbidden);

        Assert.Equal(records, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(Ct)));
        Assert.Equal(revision, await RevisionAsync("production"));
        Assert.False(await _factory.WithDbAsync(db => db.FlagValues.AnyAsync(v => v.Environment.Key == "production" && v.Enabled, Ct)));
    }

    [Fact]
    public async Task AWriteOutsideTheScopeIsForbidden()
    {
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _billingWriter, new { enabled = true }),
            HttpStatusCode.Forbidden);

        Assert.False(await _factory.WithDbAsync(db => db.FlagValues.AnyAsync(v => v.Environment.Key == "production" && v.Enabled, Ct)));
    }

    [Fact]
    public async Task TheThirtyFirstWriteWithinAMinuteIsRefused()
    {
        for (var write = 0; write < 30; write++)
        {
            using var response = await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _writer, new { enabled = write % 2 == 0 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var records = await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(Ct));
        using (var refused = await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _writer, new { enabled = true }))
        {
            var retryAfter = refused.Headers.RetryAfter?.Delta;
            await AssertProblemAsync(refused, HttpStatusCode.TooManyRequests);
            Assert.InRange(retryAfter!.Value, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
        }

        Assert.Equal(records, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(Ct)));

        // Reading and other tokens are not limited, and a minute later the token writes again.
        using (var read = await SendAsync(HttpMethod.Get, "/api/manage/v1/applications/hogarhero/flags", _writer))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (var other = await SendAsync(HttpMethod.Post, "/api/manage/v1/applications/billing/flags", _billingWriter, new { key = "invoice-pdf", description = "Invoices as PDF" }))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using var later = await SendAsync(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production", _writer, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Theory]
    [InlineData("text/plain", "{\"enabled\":true}", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json", "{", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "null", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{\"enabled\":\"yes\"}", HttpStatusCode.BadRequest)]
    [InlineData("application/json", "{}", HttpStatusCode.BadRequest)]
    public async Task AMalformedBodyIsAProblemAndChangesNothing(string contentType, string content, HttpStatusCode status)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/manage/v1/applications/hogarhero/flags/new-search/values/production")
        {
            Content = new StringContent(content, Encoding.UTF8, contentType),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _writer);

        await AssertProblemAsync(await client.SendAsync(request, Ct), status);
        Assert.False(await _factory.WithDbAsync(db => db.FlagValues.AnyAsync(v => v.Environment.Key == "production" && v.Enabled, Ct)));
    }

    [Fact]
    public async Task AWriteWithoutATokenIsUnauthorizedEvenWithAMalformedBody()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/manage/v1/applications/hogarhero/flags")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, Ct);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Flags.CountAsync(f => f.Application.Key == "hogarhero", Ct)));
    }

    [Theory]
    [InlineData("POST", "/api/manage/v1/tokens")]
    [InlineData("DELETE", "/api/manage/v1/tokens/1")]
    [InlineData("POST", "/api/manage/v1/tokens/1/revoke")]
    [InlineData("POST", "/api/manage/v1/applications/hogarhero/flags/new-search/archive")]
    [InlineData("DELETE", "/api/manage/v1/applications/hogarhero/flags/new-search")]
    [InlineData("DELETE", "/api/manage/v1/applications/hogarhero/flags/legacy-search")]
    [InlineData("POST", "/api/manage/v1/applications")]
    [InlineData("POST", "/api/manage/v1/applications/hogarhero/environments")]
    [InlineData("POST", "/api/manage/v1/applications/hogarhero/keys")]
    public async Task NoCallCreatesOrRevokesATokenOrArchivesOrDeletesAFlag(string method, string path)
    {
        var before = await CatalogCountsAsync();

        using var response = await SendAsync(new HttpMethod(method), path, _writer, new { name = "escalated", key = "escalated" });

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(before, await CatalogCountsAsync());
        Assert.Equal(3, await _factory.WithDbAsync(db => db.ManagementTokens.CountAsync(t => t.RevokedAt == null, Ct)));
        Assert.Equal(
            ["legacy-search:archived", "new-search:active"],
            await _factory.WithDbAsync(db => db.Flags.Where(f => f.Application.Key == "hogarhero").OrderBy(f => f.Key)
                .Select(f => f.Key + (f.ArchivedAt == null ? ":active" : ":archived")).ToListAsync(Ct)));
    }

    // Applications, environments, SDK keys and change records, which no escalation may add to.
    private Task<(int, int, int, int)> CatalogCountsAsync() =>
        _factory.WithDbAsync(async db => (
            await db.Applications.CountAsync(Ct), await db.Environments.CountAsync(Ct), await db.SdkKeys.CountAsync(Ct), await db.ChangeRecords.CountAsync(Ct)));

    private async Task<string> CreateTokenAsync(string name, TokenPermission permission, string[] applications)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (result, created) = await scope.ServiceProvider.GetRequiredService<ManagementTokenCommands>()
            .CreateAsync(name, permission, allApplications: false, applications, 365, Owner, Ct);
        Assert.True(result.Succeeded, result.Message);
        return created!.Token;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }

    private Task<List<ChangeRecord>> RecordsAsync(string flagKey) =>
        _factory.WithDbAsync(db => db.ChangeRecords.Where(r => r.FlagKey == flagKey).OrderBy(r => r.Id).ToListAsync(Ct));

    private Task<long> RevisionAsync(string environmentKey) =>
        _factory.WithDbAsync(db => db.Environments.Where(e => e.Key == environmentKey && e.Application.Key == "hogarhero").Select(e => e.Revision).SingleAsync(Ct));

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            Assert.True(status == response.StatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("title").GetString()));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
