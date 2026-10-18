using System.Diagnostics.Metrics;

namespace Palanca.Client;

/// <summary>
/// The client's metrics (FLG-056), on the <see cref="MeterName"/> meter:
/// <list type="bullet">
/// <item><c>palanca.client.sync.last_success</c>: Unix time, in seconds, of the last successful sync. No value before the first.</item>
/// <item><c>palanca.client.sync.failures</c>: syncs that failed and kept the previous values, by <c>reason</c>: <c>unreachable</c>, <c>rejected</c>, <c>invalid</c> or <c>mismatch</c>.</item>
/// <item><c>palanca.client.snapshot.revision</c>: the revision of the flags being served. No value before the first sync.</item>
/// </list>
/// Logs use the same name as their category.
/// </summary>
public static class PalancaMetrics
{
    /// <summary>The meter's name, to pass to <c>AddMeter</c> when collecting the metrics.</summary>
    public const string MeterName = "Palanca.Client";
}

/// <summary>Why a sync failed, as the <c>reason</c> tag of the failure counter.</summary>
internal enum SyncFailure
{
    /// <summary>No answer: a connection error or a timeout.</summary>
    Unreachable,

    /// <summary>An answer other than 200 or 304, such as 401 for a revoked key.</summary>
    Rejected,

    /// <summary>A 200 whose body is not a flags document.</summary>
    Invalid,

    /// <summary>Flags for an application or environment other than the configured one.</summary>
    Mismatch,
}

internal sealed class ClientMetrics
{
    private readonly Counter<long> _failures;

    public ClientMetrics(IMeterFactory meterFactory, ClientState state)
    {
        var meter = meterFactory.Create(PalancaMetrics.MeterName);
        _failures = meter.CreateCounter<long>("palanca.client.sync.failures", "{failure}", "Syncs that failed and kept the previous flag values.");
        meter.CreateObservableGauge(
            "palanca.client.sync.last_success",
            () => state.Current.LastSuccess is { } lastSuccess ? [new Measurement<double>(lastSuccess.ToUnixTimeMilliseconds() / 1000d)] : Array.Empty<Measurement<double>>(),
            "s",
            "Unix time of the last successful sync.");
        meter.CreateObservableGauge(
            "palanca.client.snapshot.revision",
            () => state.Current.Snapshot is { } snapshot ? [new Measurement<long>(snapshot.Revision)] : Array.Empty<Measurement<long>>(),
            "{revision}",
            "Revision of the flag values being served.");
    }

    public void RecordFailure(SyncFailure failure)
    {
        try
        {
            _failures.Add(1, new KeyValuePair<string, object?>("reason", Reason(failure)));
        }
        catch (Exception)
        {
            // A listener's callback runs inside Add; one that throws must not change how the sync goes.
        }
    }

    private static string Reason(SyncFailure failure) => failure switch
    {
        SyncFailure.Unreachable => "unreachable",
        SyncFailure.Rejected => "rejected",
        SyncFailure.Invalid => "invalid",
        _ => "mismatch",
    };
}
