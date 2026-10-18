using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palanca.Applications;
using Palanca.Data;
using Palanca.Flags;

namespace Palanca.Tests.Applications;

/// <summary>Applications, environments, and flags (FLG-001 to FLG-003, FLG-010 to FLG-013, FLG-042).</summary>
public sealed class CatalogCommandsTests : IAsyncDisposable
{
    private static readonly ChangeActor Owner = new(ChangeSource.Dashboard, "owner@example.com");
    private readonly PalancaFactory _factory = new();

    [Theory]
    [InlineData("HogarHero")]
    [InlineData("1app")]
    [InlineData("my_app")]
    [InlineData("")]
    public async Task AnApplicationKeyFollowsTheKeyRules(string key)
    {
        var result = await RunAsync<ApplicationCommands>(c => c.CreateAsync(key, "Name", Owner, Ct));

        Assert.Equal(CommandStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AnApplicationKeyIsUnique()
    {
        await RunAsync<ApplicationCommands>(c => c.CreateAsync("hogarhero", "HogarHero", Owner, Ct));

        var result = await RunAsync<ApplicationCommands>(c => c.CreateAsync("hogarhero", "Again", Owner, Ct));

        Assert.Equal(CommandStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task EnvironmentsTakeTheNextColumnAndCanMove()
    {
        await SeedAsync();
        await RunAsync<ApplicationCommands>(c => c.CreateEnvironmentAsync("hogarhero", "qa", "QA", false, Owner, Ct));

        Assert.Equal(["staging", "production", "qa"], await EnvironmentOrderAsync());

        await RunAsync<ApplicationCommands>(c => c.MoveEnvironmentAsync("hogarhero", "qa", -1, Owner, Ct));
        Assert.Equal(["staging", "qa", "production"], await EnvironmentOrderAsync());

        await RunAsync<ApplicationCommands>(c => c.MoveEnvironmentAsync("hogarhero", "staging", -1, Owner, Ct));
        Assert.Equal(["staging", "qa", "production"], await EnvironmentOrderAsync());
    }

    [Fact]
    public async Task ProtectionChangesAreRecorded()
    {
        await SeedAsync();

        await RunAsync<ApplicationCommands>(c => c.SetProtectedAsync("hogarhero", "staging", true, Owner, Ct));

        var record = await LastRecordAsync();
        Assert.Equal((ChangeAction.EnvironmentProtectionChanged, "staging", "unprotected", "protected"),
            (record.Action, record.EnvironmentKey, record.OldValue, record.NewValue));
    }

    [Fact]
    public async Task ArchivingAnEnvironmentRevokesItsSdkKeys()
    {
        await SeedAsync();
        await _factory.WithDbAsync(async db =>
        {
            var staging = await db.Environments.SingleAsync(e => e.Key == "staging", Ct);
            db.SdkKeys.Add(new SdkKey { EnvironmentId = staging.Id, Name = "web", Prefix = "plk_test", Hash = new byte[32] });
            await db.SaveChangesAsync(Ct);
        });

        var result = await RunAsync<ApplicationCommands>(c => c.ArchiveEnvironmentAsync("hogarhero", "staging", Owner, Ct));

        Assert.True(result.Succeeded);
        await _factory.WithDbAsync(async db =>
        {
            Assert.NotNull((await db.SdkKeys.SingleAsync(Ct)).RevokedAt);
            var actions = await db.ChangeRecords.OrderBy(r => r.Id).Select(r => r.Action).ToListAsync(Ct);
            Assert.Equal([ChangeAction.EnvironmentArchived, ChangeAction.SdkKeyRevoked], actions[^2..]);
        });
    }

    [Fact]
    public async Task ArchivingAnApplicationRevokesTheKeysOfEveryEnvironment()
    {
        await SeedAsync();
        await _factory.WithDbAsync(async db =>
        {
            foreach (var environment in await db.Environments.ToListAsync(Ct))
            {
                db.SdkKeys.Add(new SdkKey { EnvironmentId = environment.Id, Name = environment.Key, Prefix = $"plk_{environment.Key}", Hash = new byte[32] });
            }

            await db.SaveChangesAsync(Ct);
        });

        await RunAsync<ApplicationCommands>(c => c.ArchiveAsync("hogarhero", Owner, Ct));

        await _factory.WithDbAsync(async db => Assert.All(await db.SdkKeys.ToListAsync(Ct), k => Assert.NotNull(k.RevokedAt)));
        Assert.Equal(CommandStatus.NotFound, (await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "late", "Too late", [], Owner, Ct))).Status);
    }

    [Fact]
    public async Task CreatingAFlagWithInitialValuesRecordsEachOneAndRaisesEveryRevision()
    {
        await SeedAsync();

        var result = await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search results page", ["staging"], Owner, Ct));

        Assert.True(result.Succeeded);
        await _factory.WithDbAsync(async db =>
        {
            var value = await db.FlagValues.Include(v => v.Environment).SingleAsync(Ct);
            Assert.Equal(("staging", true, "owner@example.com"), (value.Environment.Key, value.Enabled, value.UpdatedBy));
            Assert.All(await db.Environments.ToListAsync(Ct), e => Assert.Equal(1, e.Revision));
            var records = await db.ChangeRecords.Where(r => r.FlagKey == "new-search").OrderBy(r => r.Id).ToListAsync(Ct);
            Assert.Collection(
                records,
                created => Assert.Equal((ChangeAction.FlagCreated, "New search results page"), (created.Action, created.NewValue)),
                on => Assert.Equal((ChangeAction.FlagValueSet, "staging", "off", "on"), (on.Action, on.EnvironmentKey, on.OldValue, on.NewValue)));
        });
    }

    [Fact]
    public async Task AFlagStartsOffEverywhereByDefault()
    {
        await SeedAsync();

        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search results page", [], Owner, Ct));

        Assert.Equal(0, await _factory.WithDbAsync(db => db.FlagValues.CountAsync(Ct)));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.ChangeRecords.CountAsync(r => r.FlagKey == "new-search", Ct)));
    }

    [Fact]
    public async Task AFlagKeyIsUniqueEvenAmongArchivedFlags()
    {
        await SeedAsync();
        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "First", [], Owner, Ct));
        await RunAsync<FlagCommands>(c => c.ArchiveAsync("hogarhero", "new-search", Owner, Ct));

        var result = await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "Second", [], Owner, Ct));

        Assert.Equal(CommandStatus.Conflict, result.Status);
        Assert.Contains("Restore it instead", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialValuesMustNameExistingEnvironments()
    {
        await SeedAsync();

        var result = await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search", ["qa"], Owner, Ct));

        Assert.Equal(CommandStatus.Invalid, result.Status);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Flags.CountAsync(Ct)));
    }

    [Fact]
    public async Task ArchivingAndRestoringChangeTheApiResponseSoBothRaiseRevisions()
    {
        await SeedAsync();
        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search", [], Owner, Ct));

        await RunAsync<FlagCommands>(c => c.ArchiveAsync("hogarhero", "new-search", Owner, Ct));
        await RunAsync<FlagCommands>(c => c.RestoreAsync("hogarhero", "new-search", Owner, confirmed: false, Ct));

        await _factory.WithDbAsync(async db => Assert.All(await db.Environments.ToListAsync(Ct), e => Assert.Equal(3, e.Revision)));
        var actions = await _factory.WithDbAsync(db => db.ChangeRecords.Where(r => r.FlagKey == "new-search").OrderBy(r => r.Id).Select(r => r.Action).ToListAsync(Ct));
        Assert.Equal([ChangeAction.FlagCreated, ChangeAction.FlagArchived, ChangeAction.FlagRestored], actions);
    }

    [Fact]
    public async Task RestoringAFlagThatIsOnInAProtectedEnvironmentNeedsConfirmation()
    {
        await SeedAsync();
        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search", ["production"], Owner, Ct));
        await RunAsync<FlagCommands>(c => c.ArchiveAsync("hogarhero", "new-search", Owner, Ct));

        var unconfirmed = await RunAsync<FlagCommands>(c => c.RestoreAsync("hogarhero", "new-search", Owner, confirmed: false, Ct));
        Assert.Equal(CommandStatus.ConfirmationRequired, unconfirmed.Status);
        Assert.NotNull(await _factory.WithDbAsync(db => db.Flags.Select(f => f.ArchivedAt).SingleAsync(Ct)));

        var confirmed = await RunAsync<FlagCommands>(c => c.RestoreAsync("hogarhero", "new-search", Owner, confirmed: true, Ct));
        Assert.True(confirmed.Succeeded);
        Assert.Null(await _factory.WithDbAsync(db => db.Flags.Select(f => f.ArchivedAt).SingleAsync(Ct)));
    }

    [Fact]
    public async Task ReorderingIsRecorded()
    {
        await SeedAsync();

        await RunAsync<ApplicationCommands>(c => c.MoveEnvironmentAsync("hogarhero", "production", -1, Owner, Ct));

        var record = await LastRecordAsync();
        Assert.Equal((ChangeAction.EnvironmentMoved, "production", "column 2", "column 1"), (record.Action, record.EnvironmentKey, record.OldValue, record.NewValue));
    }

    [Fact]
    public async Task ADescriptionMayHaveLineBreaks()
    {
        await SeedAsync();

        var result = await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "First line\r\nSecond line", [], Owner, Ct));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("First line\nSecond line", await _factory.WithDbAsync(db => db.Flags.Select(f => f.Description).SingleAsync(Ct)));
    }

    [Fact]
    public async Task OnlyAnArchivedFlagCanBeDeletedAndItsHistoryStays()
    {
        await SeedAsync();
        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "New search", ["staging"], Owner, Ct));

        Assert.Equal(CommandStatus.Conflict, (await RunAsync<FlagCommands>(c => c.DeleteAsync("hogarhero", "new-search", Owner, Ct))).Status);

        await RunAsync<FlagCommands>(c => c.ArchiveAsync("hogarhero", "new-search", Owner, Ct));
        Assert.True((await RunAsync<FlagCommands>(c => c.DeleteAsync("hogarhero", "new-search", Owner, Ct))).Succeeded);

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.Flags.CountAsync(Ct));
            Assert.Equal(0, await db.FlagValues.CountAsync(Ct));
            var actions = await db.ChangeRecords.Where(r => r.FlagKey == "new-search").OrderBy(r => r.Id).Select(r => r.Action).ToListAsync(Ct);
            Assert.Equal([ChangeAction.FlagCreated, ChangeAction.FlagValueSet, ChangeAction.FlagArchived, ChangeAction.FlagDeleted], actions);
        });
    }

    [Fact]
    public async Task DescriptionChangesAreRecordedWithBothValues()
    {
        await SeedAsync();
        await RunAsync<FlagCommands>(c => c.CreateAsync("hogarhero", "new-search", "Old words", [], Owner, Ct));

        await RunAsync<FlagCommands>(c => c.EditDescriptionAsync("hogarhero", "new-search", "  New words  ", Owner, Ct));

        var record = await LastRecordAsync();
        Assert.Equal((ChangeAction.FlagDescriptionChanged, "Old words", "New words"), (record.Action, record.OldValue, record.NewValue));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedAsync()
    {
        Assert.True((await RunAsync<ApplicationCommands>(c => c.CreateAsync("hogarhero", "HogarHero", Owner, Ct))).Succeeded);
        Assert.True((await RunAsync<ApplicationCommands>(c => c.CreateEnvironmentAsync("hogarhero", "staging", "Staging", false, Owner, Ct))).Succeeded);
        Assert.True((await RunAsync<ApplicationCommands>(c => c.CreateEnvironmentAsync("hogarhero", "production", "Production", true, Owner, Ct))).Succeeded);
    }

    private async Task<CommandResult> RunAsync<T>(Func<T, Task<CommandResult>> command)
        where T : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await command(scope.ServiceProvider.GetRequiredService<T>());
    }

    private Task<List<string>> EnvironmentOrderAsync() =>
        _factory.WithDbAsync(db => db.Environments.Where(e => e.ArchivedAt == null).OrderBy(e => e.SortOrder).Select(e => e.Key).ToListAsync(Ct));

    private Task<ChangeRecord> LastRecordAsync() =>
        _factory.WithDbAsync(db => db.ChangeRecords.OrderBy(r => r.Id).LastAsync(Ct));

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
