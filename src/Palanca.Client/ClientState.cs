using System.Collections.Frozen;
using System.Net.Http.Headers;

namespace Palanca.Client;

/// <summary>One environment's flag values, as the service last sent them.</summary>
internal sealed record FlagSnapshot(long Revision, FrozenDictionary<string, bool> Flags, EntityTagHeaderValue? ETag);

/// <param name="Snapshot">The values reads are served from; null until the first successful sync.</param>
/// <param name="LastSuccess">When a sync last succeeded, including a sync that found no change.</param>
/// <param name="Problem">Why the last sync failed, or why the client does not sync; null after a success.</param>
internal sealed record SyncStatus(FlagSnapshot? Snapshot, DateTimeOffset? LastSuccess, string? Problem);

/// <summary>
/// What the client currently serves. Only the sync loop writes it, by swapping in a whole new status, so a read sees
/// one consistent status without locking.
/// </summary>
internal sealed class ClientState
{
    private SyncStatus _current;

    public ClientState(ClientConfiguration configuration) =>
        _current = new SyncStatus(null, null, configuration.Settings is null
            ? $"Palanca is not configured: {string.Join("; ", configuration.Problems)}"
            : "No sync has finished yet");

    public SyncStatus Current => Volatile.Read(ref _current);

    public void Set(SyncStatus status) => Volatile.Write(ref _current, status);
}
