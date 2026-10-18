using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Palanca.Data;

internal sealed class PalancaDbContext(DbContextOptions<PalancaDbContext> options) : DbContext(options)
{
    private const int NameMaxLength = 200;
    private const int TextMaxLength = 1000;
    public const int EmailMaxLength = 254;
    public const int PasskeyNameMaxLength = 64;

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<AppEnvironment> Environments => Set<AppEnvironment>();
    public DbSet<Flag> Flags => Set<Flag>();
    public DbSet<FlagValue> FlagValues => Set<FlagValue>();
    public DbSet<SdkKey> SdkKeys => Set<SdkKey>();
    public DbSet<ManagementToken> ManagementTokens => Set<ManagementToken>();
    public DbSet<ChangeRecord> ChangeRecords => Set<ChangeRecord>();
    public DbSet<Owner> Owners => Set<Owner>();
    public DbSet<OwnerPasskey> OwnerPasskeys => Set<OwnerPasskey>();
    public DbSet<OwnerEnrollment> OwnerEnrollments => Set<OwnerEnrollment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type. Store UTC and read it back as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Application>(application =>
        {
            application.ToTable(table => table.HasCheckConstraint("CK_Applications_Key", Keys.CheckConstraintSql(nameof(Application.Key))));
            application.Property(a => a.Key).HasMaxLength(Keys.MaxLength);
            application.Property(a => a.Name).HasMaxLength(NameMaxLength);
            application.HasIndex(a => a.Key).IsUnique();
        });

        modelBuilder.Entity<AppEnvironment>(environment =>
        {
            environment.ToTable("Environments", table => table.HasCheckConstraint("CK_Environments_Key", Keys.CheckConstraintSql(nameof(AppEnvironment.Key))));
            environment.Property(e => e.Key).HasMaxLength(Keys.MaxLength);
            environment.Property(e => e.Name).HasMaxLength(NameMaxLength);
            environment.HasIndex(e => new { e.ApplicationId, e.Key }).IsUnique();
            environment.HasOne(e => e.Application).WithMany(a => a.Environments).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Flag>(flag =>
        {
            flag.ToTable(table => table.HasCheckConstraint("CK_Flags_Key", Keys.CheckConstraintSql(nameof(Flag.Key))));
            flag.Property(f => f.Key).HasMaxLength(Keys.MaxLength);
            flag.Property(f => f.Description).HasMaxLength(TextMaxLength);
            flag.HasIndex(f => new { f.ApplicationId, f.Key }).IsUnique();
            flag.HasOne(f => f.Application).WithMany(a => a.Flags).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FlagValue>(value =>
        {
            value.HasKey(v => new { v.FlagId, v.EnvironmentId });
            value.Property(v => v.UpdatedBy).HasMaxLength(TextMaxLength);
            value.HasOne(v => v.Flag).WithMany(f => f.Values).OnDelete(DeleteBehavior.Cascade);
            value.HasOne(v => v.Environment).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SdkKey>(key =>
        {
            key.Property(k => k.Name).HasMaxLength(NameMaxLength);
            key.Property(k => k.Prefix).HasMaxLength(32);
            key.HasIndex(k => k.Prefix);
            key.HasOne(k => k.Environment).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManagementToken>(token =>
        {
            token.Property(t => t.Name).HasMaxLength(NameMaxLength);
            token.Property(t => t.Prefix).HasMaxLength(32);
            token.Property(t => t.Permission).HasConversion<string>().HasMaxLength(16);
            token.HasIndex(t => t.Prefix);
            // Applications are archived, never deleted, so a token's scope keeps naming them.
            token.HasMany(t => t.Applications).WithMany().UsingEntity(
                "ManagementTokenApplications",
                application => application.HasOne(typeof(Application)).WithMany().HasForeignKey("ApplicationId").OnDelete(DeleteBehavior.Restrict),
                scopedToken => scopedToken.HasOne(typeof(ManagementToken)).WithMany().HasForeignKey("TokenId").OnDelete(DeleteBehavior.Cascade));
        });

        modelBuilder.Entity<ChangeRecord>(record =>
        {
            record.Property(r => r.Source).HasConversion<string>().HasMaxLength(16);
            record.Property(r => r.Action).HasConversion<string>().HasMaxLength(64);
            record.Property(r => r.Actor).HasMaxLength(TextMaxLength);
            record.Property(r => r.ApplicationKey).HasMaxLength(Keys.MaxLength);
            record.Property(r => r.EnvironmentKey).HasMaxLength(Keys.MaxLength);
            record.Property(r => r.FlagKey).HasMaxLength(Keys.MaxLength);
            record.Property(r => r.OldValue).HasMaxLength(TextMaxLength);
            record.Property(r => r.NewValue).HasMaxLength(TextMaxLength);
            record.HasIndex(r => r.ApplicationId);
        });

        modelBuilder.Entity<Owner>(owner =>
        {
            owner.Property(o => o.Email).HasMaxLength(EmailMaxLength);
            owner.Property(o => o.SecurityStamp).HasMaxLength(64);
            owner.HasIndex(o => o.Email).IsUnique();
        });

        modelBuilder.Entity<OwnerPasskey>(passkey =>
        {
            passkey.HasKey(p => p.CredentialId);
            passkey.Property(p => p.CredentialId).HasMaxLength(1023);
            passkey.Property(p => p.Name).HasMaxLength(PasskeyNameMaxLength);
            passkey.Property(p => p.Data).HasConversion<PasskeyDataConverter>();
            passkey.HasOne(p => p.Owner).WithMany(o => o.Passkeys).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OwnerEnrollment>(enrollment =>
        {
            enrollment.Property(e => e.Hash).HasMaxLength(32);
            enrollment.HasIndex(e => e.Hash).IsUnique();
            enrollment.HasOne(e => e.Owner).WithMany().OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class PasskeyDataConverter() : ValueConverter<OwnerPasskeyData, string>(
        value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
        value => JsonSerializer.Deserialize<OwnerPasskeyData>(value, JsonSerializerOptions.Web)!);
}
