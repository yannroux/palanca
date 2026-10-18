using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Palanca.Data;
using Palanca.Flags;

namespace Palanca.Tests.Flags;

public sealed class FlagValueWriterTests : IAsyncLifetime
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, "owner@example.com");
    private readonly PalancaFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        await _factory.WithDbAsync(async db =>
        {
            var hogarHero = new Application { Key = "hogarhero", Name = "HogarHero" };
            hogarHero.Environments.Add(new AppEnvironment { Key = "staging", Name = "Staging", SortOrder = 0 });
            hogarHero.Environments.Add(new AppEnvironment { Key = "production", Name = "Production", SortOrder = 1, IsProtected = true });
            hogarHero.Flags.Add(new Flag { Key = "new-search", Description = "New search results page" });
            for (var i = 0; i < 20; i++)
            {
                hogarHero.Flags.Add(new Flag { Key = $"bulk-{i}", Description = "Concurrency test flag" });
            }

            var other = new Application { Key = "other-app", Name = "Other" };
            other.Environments.Add(new AppEnvironment { Key = "staging", Name = "Staging" });
            other.Flags.Add(new Flag { Key = "other-flag", Description = "Belongs to another application" });

            db.Applications.AddRange(hogarHero, other);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task TurningAFlagOnRaisesTheRevisionAndRecordsTheChange()
    {
        var result = await SetAsync("hogarhero", "new-search", "staging", enabled: true);

        Assert.Equal(new SetFlagValueResult(SetFlagValueOutcome.Changed, 1), result);
        await _factory.WithDbAsync(async db =>
        {
            var value = await db.FlagValues.SingleAsync(TestContext.Current.CancellationToken);
            Assert.True(value.Enabled);
            Assert.Equal(_time.GetUtcNow().UtcDateTime, value.UpdatedAt);
            Assert.Equal("owner@example.com", value.UpdatedBy);

            var record = await db.ChangeRecords.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChangeAction.FlagValueSet, record.Action);
            Assert.Equal(ChangeSource.Dashboard, record.Source);
            Assert.Equal("owner@example.com", record.Actor);
            Assert.Equal(_time.GetUtcNow().UtcDateTime, record.OccurredAt);
            Assert.Equal(("hogarhero", "staging", "new-search"), (record.ApplicationKey, record.EnvironmentKey, record.FlagKey));
            Assert.Equal(("off", "on"), (record.OldValue, record.NewValue));

            var revisions = await db.Environments
                .Where(e => e.Application.Key == "hogarhero")
                .ToDictionaryAsync(e => e.Key, e => e.Revision, TestContext.Current.CancellationToken);
            Assert.Equal(1, revisions["staging"]);
            Assert.Equal(0, revisions["production"]);
        });
    }

    [Fact]
    public async Task SettingTheCurrentValueChangesNothing()
    {
        Assert.Equal(new SetFlagValueResult(SetFlagValueOutcome.Unchanged, 0), await SetAsync("hogarhero", "new-search", "staging", enabled: false));
        await SetAsync("hogarhero", "new-search", "staging", enabled: true);

        var repeated = await SetAsync("hogarhero", "new-search", "staging", enabled: true);

        Assert.Equal(new SetFlagValueResult(SetFlagValueOutcome.Unchanged, 1), repeated);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TurningAFlagBackOffIsAChange()
    {
        await SetAsync("hogarhero", "new-search", "production", enabled: true);

        var result = await SetAsync("hogarhero", "new-search", "production", enabled: false);

        Assert.Equal(new SetFlagValueResult(SetFlagValueOutcome.Changed, 2), result);
        var last = await _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("on", "off"), (last.OldValue, last.NewValue));
    }

    [Fact]
    public async Task AProtectedEnvironmentNeedsAConfirmedChange()
    {
        var result = await _factory.WithDbAsync(db => new FlagValueWriter(db, _time)
            .SetAsync("hogarhero", "new-search", "production", true, Owner, confirmed: false, TestContext.Current.CancellationToken));

        Assert.Equal(new SetFlagValueResult(SetFlagValueOutcome.ConfirmationRequired, 0), result);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.FlagValues.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ConcurrentChangesEachRaiseTheRevision()
    {
        var results = await RunTogetherAsync(Enumerable.Range(0, 20).Select(i =>
            (Func<Task<SetFlagValueResult>>)(() => SetInOwnScopeAsync("hogarhero", $"bulk-{i}", "staging", enabled: true))));

        Assert.All(results, result => Assert.Equal(SetFlagValueOutcome.Changed, result.Outcome));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), results.Select(r => r.Revision).Order());
        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(20, await db.Environments.Where(e => e.Key == "staging" && e.Application.Key == "hogarhero")
                .Select(e => e.Revision).SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(20, await db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task ConcurrentIdenticalChangesApplyOnce()
    {
        var results = await RunTogetherAsync(Enumerable.Range(0, 10).Select(_ =>
            (Func<Task<SetFlagValueResult>>)(() => SetInOwnScopeAsync("hogarhero", "new-search", "staging", enabled: true))));

        Assert.Single(results, r => r.Outcome == SetFlagValueOutcome.Changed);
        Assert.Equal(9, results.Count(r => r.Outcome == SetFlagValueOutcome.Unchanged));
        Assert.All(results, r => Assert.Equal(1, r.Revision));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("missing-app", "new-search", "staging")]
    [InlineData("hogarhero", "missing-flag", "staging")]
    [InlineData("hogarhero", "new-search", "missing-environment")]
    [InlineData("hogarhero", "other-flag", "staging")]
    [InlineData("other-app", "new-search", "staging")]
    public async Task UnknownTargetsAreNotFound(string applicationKey, string flagKey, string environmentKey)
    {
        var result = await SetAsync(applicationKey, flagKey, environmentKey, enabled: true);

        Assert.Equal(SetFlagValueOutcome.NotFound, result.Outcome);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("application")]
    [InlineData("environment")]
    [InlineData("flag")]
    public async Task ArchivedTargetsAreNotFound(string archived)
    {
        await _factory.WithDbAsync(async db =>
        {
            var archivedAt = _time.GetUtcNow().UtcDateTime;
            var cancellationToken = TestContext.Current.CancellationToken;
            _ = archived switch
            {
                "application" => await db.Applications.Where(a => a.Key == "hogarhero")
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ArchivedAt, archivedAt), cancellationToken),
                "environment" => await db.Environments.Where(e => e.Key == "staging" && e.Application.Key == "hogarhero")
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.ArchivedAt, archivedAt), cancellationToken),
                _ => await db.Flags.Where(f => f.Key == "new-search")
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.ArchivedAt, archivedAt), cancellationToken),
            };
        });

        var result = await SetAsync("hogarhero", "new-search", "staging", enabled: true);

        Assert.Equal(SetFlagValueOutcome.NotFound, result.Outcome);
    }

    private Task<SetFlagValueResult> SetAsync(string applicationKey, string flagKey, string environmentKey, bool enabled) =>
        _factory.WithDbAsync(db => new FlagValueWriter(db, _time)
            .SetAsync(applicationKey, flagKey, environmentKey, enabled, Owner, confirmed: true, TestContext.Current.CancellationToken));

    // Microsoft.Data.Sqlite completes its async calls synchronously, so writers started in a loop
    // would run one after another. Separate threads released together make them overlap.
    private static async Task<T[]> RunTogetherAsync<T>(IEnumerable<Func<Task<T>>> writers)
    {
        var writerList = writers.ToList();
        using var start = new Barrier(writerList.Count);
        var tasks = writerList.Select(writer => Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return writer();
        }));
        return await Task.WhenAll(tasks);
    }

    // Each writer gets its own context and connection, as concurrent requests would.
    private async Task<SetFlagValueResult> SetInOwnScopeAsync(string applicationKey, string flagKey, string environmentKey, bool enabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<FlagValueWriter>();
        return await writer.SetAsync(applicationKey, flagKey, environmentKey, enabled, Owner, confirmed: true, TestContext.Current.CancellationToken);
    }
}
