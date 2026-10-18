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

namespace Palanca.Tests.Evaluation;

/// <summary>GET /api/v1/flags (FLG-020 to FLG-023, FLG-030 to FLG-032, AC-002, AC-004, AC-010, AC-020).</summary>
public sealed class EvaluationApiTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, OwnerSetup.Email);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
    private readonly PalancaFactory _factory;
    private string _staging = null!;
    private string _production = null!;
    private string _otherApplication = null!;

    public EvaluationApiTests() => _factory = new PalancaFactory { Time = _time };

    public async ValueTask InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<ApplicationCommands>();
        var flags = scope.ServiceProvider.GetRequiredService<FlagCommands>();
        var keys = scope.ServiceProvider.GetRequiredService<SdkKeyCommands>();
        await applications.CreateAsync("hogarhero", "HogarHero", Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct);
        await applications.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct);
        await flags.CreateAsync("hogarhero", "new-search", "New search results page", ["staging"], Owner, Ct);
        await flags.CreateAsync("hogarhero", "operator-denue-imports", "Operator-started DENUE imports", [], Owner, Ct);
        await applications.CreateAsync("billing", "Billing", Owner, Ct);
        await applications.CreateEnvironmentAsync("billing", "production", "Production", false, Owner, Ct);
        await flags.CreateAsync("billing", "invoice-pdf", "Invoices as PDF", ["production"], Owner, Ct);
        _staging = (await keys.CreateAsync("hogarhero", "staging", "hogarhero-web", Owner, Ct)).Key!;
        _production = (await keys.CreateAsync("hogarhero", "production", "hogarhero-web", Owner, Ct)).Key!;
        _otherApplication = (await keys.CreateAsync("billing", "production", "billing-web", Owner, Ct)).Key!;
    }

    [Fact]
    public async Task AKeyReadsItsEnvironmentsFlags()
    {
        using var response = await GetFlagsAsync(_staging);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("hogarhero", body.RootElement.GetProperty("application").GetString());
        Assert.Equal("staging", body.RootElement.GetProperty("environment").GetString());
        var revision = body.RootElement.GetProperty("revision").GetInt64();
        Assert.Equal($"\"{revision}\"", response.Headers.ETag?.Tag);
        Assert.Equal("""{"new-search":true,"operator-denue-imports":false}""", body.RootElement.GetProperty("flags").GetRawText());
    }

    [Fact]
    public async Task EachKeyReadsOnlyItsOwnEnvironmentAndApplication()
    {
        using var production = JsonDocument.Parse(await (await GetFlagsAsync(_production)).Content.ReadAsStringAsync(Ct));
        using var other = JsonDocument.Parse(await (await GetFlagsAsync(_otherApplication)).Content.ReadAsStringAsync(Ct));

        Assert.Equal("""{"new-search":false,"operator-denue-imports":false}""", production.RootElement.GetProperty("flags").GetRawText());
        Assert.Equal("billing", other.RootElement.GetProperty("application").GetString());
        Assert.Equal("""{"invoice-pdf":true}""", other.RootElement.GetProperty("flags").GetRawText());
    }

    [Fact]
    public async Task ArchivedFlagsAreLeftOut()
    {
        await RunAsync<FlagCommands>(c => c.ArchiveAsync("hogarhero", "new-search", Owner, Ct));

        using var body = JsonDocument.Parse(await (await GetFlagsAsync(_staging)).Content.ReadAsStringAsync(Ct));

        Assert.Equal("""{"operator-denue-imports":false}""", body.RootElement.GetProperty("flags").GetRawText());
    }

    [Fact]
    public async Task AnUnchangedPollIsNotModifiedUntilAValueChanges()
    {
        using var first = await GetFlagsAsync(_staging);
        var etag = first.Headers.ETag!;

        using (var unchanged = await GetFlagsAsync(_staging, etag))
        {
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal(etag, unchanged.Headers.ETag);
        }

        await RunWriterAsync("operator-denue-imports", "staging", enabled: true);

        using var changed = await GetFlagsAsync(_staging, etag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag);
        using (var production = await GetFlagsAsync(_production))
        {
            Assert.Equal(HttpStatusCode.NotModified, (await GetFlagsAsync(_production, production.Headers.ETag)).StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    [InlineData("Bearer palanca_sdk_short")]
    [InlineData("Bearer palanca_mt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Bearer palanca_sdk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AnythingButALiveKeyIsUnauthorizedWithoutSayingWhy(string? authorization)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task AManagementTokenIsUnauthorizedHere()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var (_, created) = await scope.ServiceProvider.GetRequiredService<ManagementTokenCommands>()
            .CreateAsync("agent", TokenPermission.ReadWrite, allApplications: true, [], 365, Owner, Ct);

        using var response = await GetFlagsAsync(created!.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AKeyWithARealPrefixButOtherCharactersIsUnauthorized()
    {
        // The prefix only finds candidates; the whole key's hash must match.
        var forged = _staging[..18] + new string('A', _staging.Length - 18);

        using var response = await GetFlagsAsync(forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARevokedKeyFailsOnItsNextRequest()
    {
        var id = await _factory.WithDbAsync(db => db.SdkKeys.Where(k => k.Environment.Key == "staging").Select(k => k.Id).SingleAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await GetFlagsAsync(_staging)).StatusCode);

        await RunAsync<SdkKeyCommands>(c => c.RevokeAsync("hogarhero", id, Owner, Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetFlagsAsync(_staging)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetFlagsAsync(_production)).StatusCode);
    }

    [Fact]
    public async Task ArchivingTheEnvironmentEndsItsKeys()
    {
        await RunAsync<ApplicationCommands>(c => c.ArchiveEnvironmentAsync("hogarhero", "staging", Owner, Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetFlagsAsync(_staging)).StatusCode);
    }

    [Fact]
    public async Task TheDashboardSessionAuthorizesNothingHere()
    {
        using var authenticator = new SoftwareAuthenticator();
        using var dashboard = new DashboardClient(_factory);
        await OwnerSetup.SignInAsync(_factory, dashboard, authenticator);

        using var response = await dashboard.Http.GetAsync("/api/v1/flags", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheEleventhFailureWithinAMinuteIsRefused()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetFlagsAsync("palanca_sdk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetFlagsAsync(_staging)).StatusCode);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await GetFlagsAsync(_staging)).StatusCode);
    }

    [Fact]
    public async Task LastUseIsWrittenAtMostOncePerMinute()
    {
        await GetFlagsAsync(_staging);
        var first = await LastUsedAsync();

        _time.Advance(TimeSpan.FromSeconds(30));
        await GetFlagsAsync(_staging);
        Assert.Equal(first, await LastUsedAsync());

        _time.Advance(TimeSpan.FromSeconds(31));
        await GetFlagsAsync(_staging);
        Assert.True(await LastUsedAsync() > first);
    }

    [Fact]
    public async Task ConsumersOnThePrivateNetworkUsePlainHttp()
    {
        using var client = _factory.CreateClient(new() { BaseAddress = new Uri("http://palanca:8080") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _staging);

        using var response = await client.GetAsync("/api/v1/flags", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheKeysHashIsStored()
    {
        var stored = await _factory.WithDbAsync(db => db.SdkKeys.SingleAsync(k => k.Environment.Key == "staging", Ct));

        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(_staging)), stored.Hash);
        Assert.StartsWith(stored.Prefix, _staging, StringComparison.Ordinal);
        Assert.Equal("palanca_sdk_".Length + 6, stored.Prefix.Length);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> GetFlagsAsync(string key, EntityTagHeaderValue? ifNoneMatch = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/flags");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private Task<DateTime?> LastUsedAsync() =>
        _factory.WithDbAsync(db => db.SdkKeys.Where(k => k.Environment.Key == "staging" && k.Environment.Application.Key == "hogarhero")
            .Select(k => k.LastUsedAt).SingleAsync(Ct));

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

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
