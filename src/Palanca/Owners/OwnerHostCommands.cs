using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Owners;

/// <summary>The outcome of a host command: an enrollment token to print, or the reason it was refused.</summary>
internal sealed record OwnerCommandResult(string? Email, string? Token, string? Error)
{
    public static OwnerCommandResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// Creates and recovers the owner account. Only the container command calls these (FLG-046):
/// no HTTP endpoint creates an account or starts recovery.
/// </summary>
internal sealed class OwnerHostCommands(PalancaDbContext db, TimeProvider timeProvider)
{
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);
    public const string Actor = "host";

    public async Task<OwnerCommandResult> EnrollAsync(string email, CancellationToken cancellationToken)
    {
        email = email.Trim();
        if (!IsEmail(email))
        {
            return OwnerCommandResult.Refused($"'{email}' is not an email address.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var owner = await db.Owners.Include(o => o.Passkeys).SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (owner is null)
        {
            owner = new Owner { Id = Guid.CreateVersion7(), Email = email, SecurityStamp = NewSecurityStamp(), CreatedAt = now };
            db.Owners.Add(owner);
        }
        else if (!string.Equals(owner.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            return OwnerCommandResult.Refused($"Palanca already has an owner, {owner.Email}. To regain access, run 'owner recover'.");
        }
        else if (owner.Passkeys.Count > 0)
        {
            return OwnerCommandResult.Refused("The owner already has a passkey. Add more on the Passkeys page, or run 'owner recover' if every passkey is lost.");
        }

        var token = await IssueEnrollmentAsync(owner, now, cancellationToken);
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = now, Source = ChangeSource.Host, Actor = Actor, Action = ChangeAction.OwnerEnrollmentIssued, NewValue = owner.Email,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OwnerCommandResult(owner.Email, token, null);
    }

    /// <summary>
    /// Revokes every passkey and, by rotating the security stamp, every session, then issues a new enrollment link.
    /// </summary>
    public async Task<OwnerCommandResult> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var owner = await db.Owners.Include(o => o.Passkeys).SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return OwnerCommandResult.Refused("There is no owner yet. Run 'owner enroll <email>' first.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revoked = owner.Passkeys.Count;
        db.OwnerPasskeys.RemoveRange(owner.Passkeys);
        owner.SecurityStamp = NewSecurityStamp();
        var token = await IssueEnrollmentAsync(owner, now, cancellationToken);
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = now, Source = ChangeSource.Host, Actor = Actor, Action = ChangeAction.OwnerRecovered,
            OldValue = revoked == 1 ? "1 passkey" : $"{revoked} passkeys", NewValue = owner.Email,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OwnerCommandResult(owner.Email, token, null);
    }

    // One link at a time: issuing a new one invalidates any the owner has not used yet.
    private async Task<string> IssueEnrollmentAsync(Owner owner, DateTime now, CancellationToken cancellationToken)
    {
        await db.OwnerEnrollments.Where(e => e.OwnerId == owner.Id && e.UsedAt == null).ExecuteDeleteAsync(cancellationToken);
        var (token, hash) = EnrollmentTokens.Create();
        db.OwnerEnrollments.Add(new OwnerEnrollment { OwnerId = owner.Id, Hash = hash, ExpiresAt = now + EnrollmentLifetime });
        return token;
    }

    public static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    private static bool IsEmail(string value) =>
        value.Length <= PalancaDbContext.EmailMaxLength
        && MailAddress.TryCreate(value, out var address)
        && address.Address == value;
}
