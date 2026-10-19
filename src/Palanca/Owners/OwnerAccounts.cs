using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;
using Palanca.Metrics;
using Palanca.Security;

namespace Palanca.Owners;

internal enum SignInOutcome
{
    SignedIn,
    Rejected,
    Expired,
    Limited,
}

internal enum PasskeyChangeOutcome
{
    Done,
    Rejected,
    Expired,
    LimitReached,
    NotFound,
    LastPasskey,
}

/// <summary>The dashboard's side of the owner account: sign-in, enrollment, and passkey management.</summary>
internal sealed class OwnerAccounts(
    PalancaDbContext db,
    UserManager<Owner> userManager,
    SignInManager<Owner> signInManager,
    FailedAuthenticationLimiter limiter,
    TimeProvider timeProvider)
{
    public const int MaxPasskeys = 10;

    public async Task<SignInOutcome> SignInAsync(string credentialJson, IPAddress? clientAddress)
    {
        if (limiter.IsLimited(clientAddress))
        {
            return SignInOutcome.Limited;
        }

        PasskeyAssertionResult<Owner> result;
        try
        {
            result = await signInManager.PerformPasskeyAssertionAsync(credentialJson);
        }
        catch (InvalidOperationException)
        {
            // The ceremony cookie is missing or spent: the attempt took too long or was replayed.
            return SignInOutcome.Expired;
        }

        if (!result.Succeeded)
        {
            limiter.RecordFailure(clientAddress, FailedCredential.Passkey);
            return SignInOutcome.Rejected;
        }

        var now = timeProvider.GetUtcNow();
        await userManager.AddOrUpdatePasskeyAsync(result.User, result.Passkey);
        await db.OwnerPasskeys.Where(p => p.CredentialId == result.Passkey.CredentialId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.LastUsedAt, now.UtcDateTime));
        await signInManager.SignInAsync(result.User, OwnerSession.NewProperties(now), authenticationMethod: "passkey");
        return SignInOutcome.SignedIn;
    }

    /// <summary>Finds the unexpired, unused enrollment that <paramref name="token"/> belongs to.</summary>
    public async Task<OwnerEnrollment?> FindEnrollmentAsync(string? token, CancellationToken cancellationToken)
    {
        var hash = EnrollmentTokens.Hash(token);
        if (hash is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.OwnerEnrollments.Include(e => e.Owner)
            .SingleOrDefaultAsync(e => e.Hash == hash && e.UsedAt == null && e.ExpiresAt > now, cancellationToken);
    }

    public static PasskeyUserEntity UserEntity(Owner owner) =>
        new() { Id = owner.Id.ToString(), Name = owner.Email, DisplayName = owner.Email };

    /// <summary>
    /// Registers the passkey from a creation ceremony that was started for <paramref name="owner"/>, and spends
    /// <paramref name="enrollment"/> when there is one, in the same transaction as its change record (FLG-042).
    /// </summary>
    public async Task<PasskeyChangeOutcome> AddPasskeyAsync(
        Owner owner, string credentialJson, string name, OwnerEnrollment? enrollment, CancellationToken cancellationToken)
    {
        PasskeyAttestationResult result;
        try
        {
            result = await signInManager.PerformPasskeyAttestationAsync(credentialJson);
        }
        catch (InvalidOperationException)
        {
            return PasskeyChangeOutcome.Expired;
        }

        // The ceremony state names the account; it must be the one this request acts for.
        if (!result.Succeeded || result.UserEntity.Id != owner.Id.ToString())
        {
            return PasskeyChangeOutcome.Rejected;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (enrollment is not null)
        {
            var spent = await db.OwnerEnrollments
                .Where(e => e.Id == enrollment.Id && e.UsedAt == null && e.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(e => e.UsedAt, now), cancellationToken);
            if (spent == 0)
            {
                return PasskeyChangeOutcome.Expired;
            }
        }

        if (await db.OwnerPasskeys.CountAsync(p => p.OwnerId == owner.Id, cancellationToken) >= MaxPasskeys)
        {
            return PasskeyChangeOutcome.LimitReached;
        }

        result.Passkey.Name = name;
        await userManager.AddOrUpdatePasskeyAsync(owner, result.Passkey);
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = now, Source = ChangeSource.Dashboard, Actor = owner.Email, Action = ChangeAction.PasskeyRegistered, NewValue = name,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PasskeyChangeOutcome.Done;
    }

    public async Task<PasskeyChangeOutcome> RenamePasskeyAsync(Owner owner, byte[] credentialId, string name, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var passkey = await db.OwnerPasskeys.SingleOrDefaultAsync(p => p.OwnerId == owner.Id && p.CredentialId == credentialId, cancellationToken);
        if (passkey is null)
        {
            return PasskeyChangeOutcome.NotFound;
        }

        if (passkey.Name != name)
        {
            db.ChangeRecords.Add(new ChangeRecord
            {
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime, Source = ChangeSource.Dashboard, Actor = owner.Email,
                Action = ChangeAction.PasskeyRenamed, OldValue = passkey.Name, NewValue = name,
            });
            passkey.Name = name;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PasskeyChangeOutcome.Done;
    }

    /// <summary>
    /// Removes a passkey other than the last one and rotates the security stamp, which ends every session on its
    /// next request (FLG-047). The caller refreshes its own session afterwards.
    /// </summary>
    public async Task<PasskeyChangeOutcome> RemovePasskeyAsync(Owner owner, byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var passkeys = await db.OwnerPasskeys.Where(p => p.OwnerId == owner.Id)
            .Select(p => new { p.CredentialId, p.Name })
            .ToListAsync(cancellationToken);
        var passkey = passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
        if (passkey is null)
        {
            return PasskeyChangeOutcome.NotFound;
        }

        if (passkeys.Count == 1)
        {
            return PasskeyChangeOutcome.LastPasskey;
        }

        await userManager.RemovePasskeyAsync(owner, credentialId);
        await userManager.UpdateSecurityStampAsync(owner);
        db.ChangeRecords.Add(new ChangeRecord
        {
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime, Source = ChangeSource.Dashboard, Actor = owner.Email,
            Action = ChangeAction.PasskeyRemoved, OldValue = passkey.Name,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PasskeyChangeOutcome.Done;
    }

    /// <summary>Trims a passkey name and checks it is 1 to 64 printable characters.</summary>
    public static string? NormalizePasskeyName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > PalancaDbContext.PasskeyNameMaxLength || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }
}
