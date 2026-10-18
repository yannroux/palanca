using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Palanca.Data;

namespace Palanca.Owners;

/// <summary>
/// The Identity user store for the owner account: user lookup, security stamp, and passkeys only.
/// Accounts are never created through Identity; the host command creates the owner (FLG-046).
/// </summary>
internal sealed class OwnerStore(PalancaDbContext db, TimeProvider timeProvider) :
    IUserSecurityStampStore<Owner>,
    IUserPasskeyStore<Owner>
{
    public Task<string> GetUserIdAsync(Owner user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(Owner user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email);

    public Task<string?> GetNormalizedUserNameAsync(Owner user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.ToUpperInvariant());

    // The email is the user name and never changes through Identity.
    public Task SetUserNameAsync(Owner user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetNormalizedUserNameAsync(Owner user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(Owner user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The owner account is created only by the host command.");

    public async Task<IdentityResult> UpdateAsync(Owner user, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public Task<IdentityResult> DeleteAsync(Owner user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The owner account cannot be deleted.");

    public async Task<Owner?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id) ? await db.Owners.FindAsync([id], cancellationToken) : null;

    public async Task<Owner?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        // One owner at most, so comparing in memory is simpler than a case-insensitive query.
        var owners = await db.Owners.ToListAsync(cancellationToken);
        return owners.FirstOrDefault(o => string.Equals(o.Email, normalizedUserName, StringComparison.OrdinalIgnoreCase));
    }

    public Task SetSecurityStampAsync(Owner user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(Owner user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.SecurityStamp);

    public async Task AddOrUpdatePasskeyAsync(Owner user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        var data = new OwnerPasskeyData(
            passkey.PublicKey, passkey.SignCount, passkey.Transports, passkey.IsUserVerified,
            passkey.IsBackupEligible, passkey.IsBackedUp, passkey.AttestationObject, passkey.ClientDataJson);

        var existing = await FindAsync(user, passkey.CredentialId, cancellationToken);
        if (existing is null)
        {
            db.OwnerPasskeys.Add(new OwnerPasskey
            {
                CredentialId = passkey.CredentialId,
                OwnerId = user.Id,
                Name = passkey.Name ?? "Passkey",
                Data = data,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            });
        }
        else
        {
            // An assertion result carries no name, so a sign-in never clears one.
            existing.Name = passkey.Name ?? existing.Name;
            existing.Data = data;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(Owner user, CancellationToken cancellationToken)
    {
        var passkeys = await db.OwnerPasskeys.Where(p => p.OwnerId == user.Id).ToListAsync(cancellationToken);
        return passkeys.ConvertAll(ToInfo);
    }

    public async Task<Owner?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        var passkey = await db.OwnerPasskeys.Include(p => p.Owner)
            .SingleOrDefaultAsync(p => p.CredentialId == credentialId, cancellationToken);
        return passkey?.Owner;
    }

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(Owner user, byte[] credentialId, CancellationToken cancellationToken)
    {
        var passkey = await FindAsync(user, credentialId, cancellationToken);
        return passkey is null ? null : ToInfo(passkey);
    }

    public async Task RemovePasskeyAsync(Owner user, byte[] credentialId, CancellationToken cancellationToken)
    {
        var passkey = await FindAsync(user, credentialId, cancellationToken);
        if (passkey is not null)
        {
            db.OwnerPasskeys.Remove(passkey);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        // The context belongs to the request scope.
    }

    private Task<OwnerPasskey?> FindAsync(Owner user, byte[] credentialId, CancellationToken cancellationToken) =>
        db.OwnerPasskeys.SingleOrDefaultAsync(p => p.OwnerId == user.Id && p.CredentialId == credentialId, cancellationToken);

    private static UserPasskeyInfo ToInfo(OwnerPasskey passkey) =>
        new(passkey.CredentialId, passkey.Data.PublicKey, new DateTimeOffset(passkey.CreatedAt, TimeSpan.Zero),
            passkey.Data.SignCount, passkey.Data.Transports, passkey.Data.IsUserVerified, passkey.Data.IsBackupEligible,
            passkey.Data.IsBackedUp, passkey.Data.AttestationObject, passkey.Data.ClientDataJson)
        {
            Name = passkey.Name,
        };
}
