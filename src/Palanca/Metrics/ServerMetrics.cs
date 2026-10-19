using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Palanca.Metrics;

/// <summary>The kind of credential a failed authentication presented, as the <c>credential</c> tag.</summary>
internal enum FailedCredential
{
    /// <summary>A missing, unknown, or revoked SDK key on the evaluation API.</summary>
    SdkKey,

    /// <summary>A missing, unknown, revoked, or expired management token on the management API.</summary>
    ManagementToken,

    /// <summary>A passkey response that sign-in rejected.</summary>
    Passkey,

    /// <summary>An unknown, used, or expired enrollment link.</summary>
    EnrollmentLink,
}

/// <summary>
/// The service's metrics (FLG-064), on the <see cref="MeterName"/> meter:
/// <list type="bullet">
/// <item><c>palanca.requests</c>: requests answered, by <c>area</c> (<c>dashboard</c>, <c>evaluation</c>, <c>management</c>,
/// <c>health</c>, or <c>metrics</c>) and <c>status_class</c> (<c>1xx</c> to <c>5xx</c>). Errors are the <c>5xx</c> class, which
/// includes unhandled exceptions.</item>
/// <item><c>palanca.authentication.failures</c>: failed authentications, by <c>credential</c>: <c>sdk_key</c>,
/// <c>management_token</c>, <c>passkey</c>, or <c>enrollment_link</c>.</item>
/// </list>
/// Every tag value comes from these fixed sets, so no credential, path, or address can reach a metric (AC-011).
/// The running totals are served in the Prometheus text format at <c>/metrics</c>.
/// </summary>
internal sealed class ServerMetrics : IDisposable
{
    public const string MeterName = "Palanca";

    private static readonly string[] StatusClasses = ["1xx", "2xx", "3xx", "4xx", "5xx"];

    private readonly Counter<long> _requests;
    private readonly Counter<long> _authenticationFailures;
    private readonly MeterListener _listener = new();

    // Per counter, the running total of each combination of tag values, keyed by its Prometheus labels.
    private readonly ConcurrentDictionary<Instrument, ConcurrentDictionary<string, StrongBox<long>>> _totals = new();

    public ServerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("palanca.requests", "{request}", "Requests answered, by area and status class.");
        _authenticationFailures = meter.CreateCounter<long>(
            "palanca.authentication.failures", "{failure}", "Failed authentications, by the kind of credential presented.");

        // Only this meter instance: another host in the same process, such as a test's, has its own.
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == meter)
            {
                _totals.TryAdd(instrument, new ConcurrentDictionary<string, StrongBox<long>>(StringComparer.Ordinal));
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(Accumulate);
        _listener.Start();
    }

    public void RecordRequest(PathString path, int statusCode)
    {
        try
        {
            _requests.Add(1, Tag("area", Area(path)), Tag("status_class", StatusClass(statusCode)));
        }
        catch (Exception)
        {
            // A listener's callback runs inside Add; one that throws must not fail the request it counts.
        }
    }

    public void RecordAuthenticationFailure(FailedCredential credential)
    {
        try
        {
            _authenticationFailures.Add(1, Tag("credential", Credential(credential)));
        }
        catch (Exception)
        {
            // As above: counting must not change how authentication answers.
        }
    }

    /// <summary>Every counter and its totals in the Prometheus text exposition format, version 0.0.4.</summary>
    public string ToPrometheusText()
    {
        var text = new StringBuilder();
        foreach (var (instrument, series) in _totals.OrderBy(entry => entry.Key.Name, StringComparer.Ordinal))
        {
            var name = instrument.Name.Replace('.', '_') + "_total";
            text.Append("# HELP ").Append(name).Append(' ').Append(instrument.Description).Append('\n');
            text.Append("# TYPE ").Append(name).Append(" counter\n");
            foreach (var (labels, total) in series.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(name).Append('{').Append(labels).Append("} ")
                    .Append(Interlocked.Read(ref total.Value).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    internal static string Area(PathString path) =>
        path.StartsWithSegments("/api/v1") ? "evaluation"
        : path.StartsWithSegments("/api/manage") ? "management"
        : path.StartsWithSegments("/health") ? "health"
        : path.StartsWithSegments(MetricsEndpoint.Path) ? "metrics"
        : "dashboard";

    internal static string Credential(FailedCredential credential) => credential switch
    {
        FailedCredential.SdkKey => "sdk_key",
        FailedCredential.ManagementToken => "management_token",
        FailedCredential.Passkey => "passkey",
        _ => "enrollment_link",
    };

    private static string StatusClass(int statusCode) =>
        statusCode is >= 100 and < 600 ? StatusClasses[(statusCode / 100) - 1] : "5xx";

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    private void Accumulate(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (_totals.TryGetValue(instrument, out var series))
        {
            var total = series.GetOrAdd(Labels(tags), _ => new StrongBox<long>());
            Interlocked.Add(ref total.Value, value);
        }
    }

    private static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var sorted = tags.ToArray();
        Array.Sort(sorted, (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return string.Join(',', sorted.Select(tag => $"{tag.Key}=\"{Escape(Convert.ToString(tag.Value, CultureInfo.InvariantCulture))}\""));
    }

    // The values are fixed strings today; escaping keeps the output parseable if one ever holds a quote.
    private static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
