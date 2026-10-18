using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Tests.Data;

public sealed class SchemaTests(PalancaFactory factory) : IClassFixture<PalancaFactory>
{
    [Theory]
    [InlineData("hogarhero")]
    [InlineData("operator-denue-imports")]
    [InlineData("a1-b2")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task DatabaseAcceptsWellFormedKeys(string key)
    {
        await factory.WithDbAsync(async db =>
        {
            db.Applications.Add(new Application { Key = key, Name = "Accepted" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("HogarHero")]
    [InlineData("1app")]
    [InlineData("-app")]
    [InlineData("my_app")]
    [InlineData("my app")]
    [InlineData("café")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task DatabaseRefusesMalformedKeys(string key)
    {
        await factory.WithDbAsync(async db =>
        {
            db.Applications.Add(new Application { Key = key, Name = "Refused" });

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
            Assert.Contains("CK_Applications_Key", exception.InnerException?.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ChangeRecordsCannotBeEditedOrDeleted()
    {
        await using var isolated = new PalancaFactory();
        await isolated.WithDbAsync(async db =>
        {
            db.ChangeRecords.Add(new ChangeRecord { Source = ChangeSource.Dashboard, Actor = "owner@example.com", Action = ChangeAction.FlagValueSet });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await isolated.WithDbAsync(async db =>
        {
            var update = await Assert.ThrowsAsync<SqliteException>(() =>
                db.ChangeRecords.ExecuteUpdateAsync(setter => setter.SetProperty(r => r.Actor, "someone-else"), TestContext.Current.CancellationToken));
            var delete = await Assert.ThrowsAsync<SqliteException>(() => db.ChangeRecords.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

            // REPLACE deletes the old row without firing delete triggers, so it is refused on insert.
            var replace = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE "Copy" AS SELECT * FROM "ChangeRecords";
                UPDATE "Copy" SET "Actor" = 'someone-else';
                INSERT OR REPLACE INTO "ChangeRecords" SELECT * FROM "Copy";
                """, TestContext.Current.CancellationToken));

            Assert.Contains("append-only", update.Message, StringComparison.Ordinal);
            Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
            Assert.Contains("append-only", replace.Message, StringComparison.Ordinal);
            Assert.Equal("owner@example.com", (await db.ChangeRecords.SingleAsync(TestContext.Current.CancellationToken)).Actor);
        });
    }

    [Fact]
    public async Task DatesAreReadBackAsUtc()
    {
        await using var isolated = new PalancaFactory();
        var createdAt = new DateTime(2026, 10, 17, 12, 30, 0, DateTimeKind.Utc);
        await isolated.WithDbAsync(async db =>
        {
            db.Applications.Add(new Application { Key = "hogarhero", Name = "HogarHero", CreatedAt = createdAt, ArchivedAt = createdAt });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var application = await isolated.WithDbAsync(db => db.Applications.SingleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(createdAt, application.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, application.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, application.ArchivedAt?.Kind);
    }
}
