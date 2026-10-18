# Palanca.Client

The .NET client for [Palanca](https://github.com/yannroux/palanca), a small self-hosted service for on/off feature flags. The client keeps one environment's flags in memory and serves every read from there. If the service is down, the application keeps its last values, or the defaults in its code, so the flag service never takes the application down with it.

## Set up

Create an SDK key for the environment in the Palanca dashboard, then configure the client:

```json
{
  "Palanca": {
    "Endpoint": "http://palanca:8080",
    "SdkKey": "palanca_sdk_…",
    "Application": "hogarhero",
    "Environment": "production"
  }
}
```

```csharp
builder.Services.AddPalanca(builder.Configuration.GetSection("Palanca"));
builder.Services.AddHealthChecks().AddPalanca();
```

`AddPalanca(options => { ... })` sets the same options in code. Keep the SDK key out of source control, for example in a mounted secret or an environment variable such as `Palanca__SdkKey`.

| Option | Default | Meaning |
| --- | --- | --- |
| `Endpoint` | required | The service's base address. The client reads `api/v1/flags` under it. |
| `SdkKey` | required | The key of the one environment this application reads. |
| `Application`, `Environment` | required | The application and environment that key must belong to. |
| `PollInterval` | `00:00:30` | How often to ask for changes, between 5 seconds and 1 hour. |
| `StartupWait` | `00:00:02` | How long startup waits for the first flags before going on with code defaults, at most 30 seconds. |
| `StaleAfter` | `00:05:00` | How old the flags may get before the health check reports Degraded, between the poll interval and 1 day. |

Write durations as `hh:mm:ss`: a plain number such as `30` means days. A duration out of range is brought within it, with a warning.

If a required option is missing or invalid, or any option cannot be read, the application still starts: the client logs an error, serves code defaults and reports Degraded.

## Read flags

Keep the flags an application uses in one catalog, with their defaults:

```csharp
public static class Flags
{
    public static readonly PalancaFlag NewSearch = new("new-search", DefaultValue: false);
}
```

Then inject `IPalancaFlags` and read:

```csharp
if (flags.IsEnabled(Flags.NewSearch))
{
    // ...
}
```

`IsEnabled` is synchronous, never throws and never waits on the network. It returns the default until the first flags arrive, and for a key the service does not have. The first read of a key the service does not have logs a warning, for up to about 1,000 distinct keys.

In interactive Blazor Server, a component that reads a flag in `OnInitialized` keeps that value for as long as the component lives. Read it while rendering, or in `OnParametersSet`, when it must follow changes.

## When things go wrong

- **The service is unreachable or refuses the key:** the client keeps the flags it has, retries after 1, 2, 4… seconds up to the poll interval, and logs one warning per outage.
- **The key belongs to another environment:** the client keeps the flags it has, or the code defaults, and logs an error that names both environments.
- **No answer:** a sync that gets no complete answer within 10 seconds fails like any other.
- **Health:** the `palanca` check is Healthy while the flags are fresh, and Degraded when they are older than `StaleAfter` or have not arrived yet. It never reports Unhealthy.

## Metrics

On the `Palanca.Client` meter:

- `palanca.client.sync.last_success`: Unix time, in seconds, of the last successful sync;
- `palanca.client.sync.failures`: failed syncs, tagged `reason` (`unreachable`, `rejected`, `invalid` or `mismatch`);
- `palanca.client.snapshot.revision`: the revision of the flags being served.

Logs use the `Palanca.Client` category. They never contain the SDK key.

## Its HTTP client

The client retries on its own schedule and logs once per outage. So handlers that an application adds to every `HttpClient` through `ConfigureHttpClientDefaults`, such as a resilience handler, are left out of the client's own. That includes service discovery and header propagation added that way, so give `Endpoint` an address the application reaches directly. A primary handler configured that way, for a proxy say, is kept. The factory's per-request logs are removed, so a quiet poll writes nothing.
