# Palanca

A small, self-hosted service for boolean feature flags, per application and per environment.
Palanca is Spanish for "lever".

## Build and test

Requires the .NET SDK pinned in `global.json`.

```sh
dotnet build Palanca.slnx
dotnet test --solution Palanca.slnx
```

The browser test drives Google Chrome (the stable channel) with a virtual passkey authenticator. Without Chrome it is skipped locally, and it fails on CI, where `CI` is set.

## Configuration

| Setting | Development | Container |
|---------|-------------|-----------|
| `Palanca:Dashboard:Origin` | `https://localhost:7180` | `https://palanca.hogarhero.com` |
| `Palanca:DataProtection:KeysDirectory` | `src/Palanca/.data/keys` | `/data/keys` |
| `Palanca:Network:TrustedProxies` | none | none; set it to Caddy's address |
| `Palanca:FailedAuthentication:PermitLimit` | 10 | 10 |

- **`Palanca:Dashboard:Origin`** is the exact origin browsers use. Passkeys are bound to its host, they are accepted only from this origin, and enrollment links point at it.
- **`Palanca:DataProtection:KeysDirectory`** holds the keys that protect session and antiforgery cookies, so sessions survive a restart.
- **`Palanca:Network:TrustedProxies`** lists the addresses or CIDR ranges allowed to report the client address (`X-Real-IP`) and the original scheme (`X-Forwarded-Proto`). With none listed, both headers are ignored.
  - Behind Caddy it must be set. The dashboard's antiforgery cookie is Secure-only, so ASP.NET Core fails the sign-in and enrollment pages with an error on any request it does not see as HTTPS. And without it, every client shares Caddy's address in the failed sign-in limit.
  - List Caddy's own address, or a network only Caddy and Palanca share. Any peer in the list can name the client address, so a container on a shared network could lock the owner out with failed attempts made in their name.
- **`Palanca:FailedAuthentication:PermitLimit`** is how many failed sign-in, enrollment, or evaluation API attempts one client address may make within a minute before further attempts get `429`. A public IPv6 client is counted by its /64 network; private IPv6 addresses, such as containers on a Docker network, one by one.

## Owner sign-in

The dashboard has one owner, who signs in with a passkey. There is no password and no sign-up page. The owner account is created and recovered only with a command run where the service runs, for example with Docker Compose:

```sh
docker compose exec palanca dotnet Palanca.dll owner enroll owner@example.com
docker compose exec palanca dotnet Palanca.dll owner recover
```

Each command prints a link that registers a passkey. The link works once and expires after 15 minutes. `owner recover` first revokes every passkey and ends every session. The owner can register up to 10 passkeys and manage them on the Passkeys page.

In development:

1. Trust the HTTPS development certificate once with `dotnet dev-certs https --trust`, so the browser opens the dashboard without a certificate warning.
2. Start the service with `dotnet run --project src/Palanca`.
3. Run `dotnet run --project src/Palanca -- owner enroll you@example.com`, and open the link it prints.

## Dashboard

- **Applications:** the home page lists them. Register one with a key and a name; the key never changes.
- **Environments:** add them in the application's Settings. Each one becomes a column of the board, in the order set there.
- **The board:** shows every flag as a row, with an ON/OFF tile per environment. Clicking a tile changes that value; values changed in the last 24 hours are ringed in yellow, and the latest value changes are listed beside the board.
- **Protected environments:** a protected environment, such as production, never starts serving a new value from the dashboard without a confirmation:
  - turning a flag on or off there asks first;
  - a new flag always starts off there;
  - restoring an archived flag that is on there asks first;
  - unprotecting the environment asks first.
- **Retiring a flag:** a flag starts off everywhere unless it is created with initial values. Archiving it moves it off the board, with its history kept; restoring brings it back with its values, and only an archived flag can be deleted.
- **History:** every change to applications, environments, flags, values, SDK keys, management tokens and the owner's passkeys is recorded, with when, where, and who made it: the owner in the dashboard, a host command, or a management token through the API. The History page lists the records newest first, 50 at a time, and filters them by application, environment, flag and source; a description change shows the old and the new text. Changes made through the API stand out with a solid plate and the token's name in bold. The database refuses any statement that edits, replaces or deletes a record.

## SDK keys and the evaluation API

An application reads one environment's flags with that environment's SDK key.

- **Getting a key:** create one on the application's SDK keys page. It is shown once, on the page the form leads to; Palanca stores only its SHA-256 hash and the start of the key, `palanca_sdk_` plus six characters.
- **Rotating keys:** an environment can have several keys. Add a new key, deploy it, then revoke the old one; a revoked key fails from its next request.

```http
GET /api/v1/flags
Authorization: Bearer palanca_sdk_...
```

```json
{
  "application": "hogarhero",
  "environment": "production",
  "revision": 42,
  "flags": { "new-search": false, "operator-denue-imports": true }
}
```

- **The response:** `200` with `ETag: "<revision>"` and `Cache-Control: no-store`. A request with `If-None-Match: "42"` while the revision is still 42 gets `304` with no body. Archived flags are left out.
- **Revisions:** they change whenever a value changes, and when a flag is created, archived or restored.
- **Failures:** a missing, unknown or revoked key gets `401` without saying which. Failed attempts count toward the per-address limit (`Palanca:FailedAuthentication:PermitLimit`), and beyond it requests get `429`.
- **Access:** consumers call the API over plain HTTP; it needs neither HTTPS nor the dashboard's cookies. It is meant for the private Docker networks, and the deployment's public route, which is outside this repository, does not expose it.

## Management tokens

Scripts and AI agents call the management API with a management token.

- **Creating a token:** on the Tokens page, give it a name, a permission, the applications it reaches and an expiry. It is shown once, on the page the form leads to; Palanca stores only its SHA-256 hash and the start of the token, `palanca_mgt_` plus six characters.
- **Permission:** a read-only token reads flags, values and history. A read-write token can also create flags, edit descriptions and set values in every environment, production included.
- **Applications:** either chosen applications, or all of them, including applications registered after the token.
- **Expiry:** 30, 90 or 180 days, or 12 months, which is the longest and the default.
- **Names:** the history records changes made with a token under its name, so two active tokens cannot share one.
- **Changing a token:** its permission, applications and expiry never change. Create a new token, then revoke the old one. Creating and revoking tokens is recorded in the history.

## Management API

The management API lives under `/api/manage/v1` and takes and returns JSON. Every request needs a management token, except the OpenAPI document at `/api/manage/v1/openapi.json`, which is public so agents can discover the contract. Any token reads; writing needs a read-write one.

```http
PUT /api/manage/v1/applications/hogarhero/flags/new-search/values/staging
Authorization: Bearer palanca_mgt_...
Content-Type: application/json

{ "enabled": true }
```

| Method and path | Does |
| --- | --- |
| `GET /applications` | Lists the applications in the token's scope, with their environments, protection and revision |
| `GET /applications/{app}/flags` | Lists the application's flags, with their description and, per environment, the value, when it last changed and who changed it |
| `GET /applications/{app}/flags/{flag}` | Returns one flag |
| `GET /applications/{app}/history` | Lists change records, newest first, 50 at a time. Filter with `environment` and `flag`; pass the response's `next` as `before` for the next page |
| `POST /applications/{app}/flags` | Creates a flag from `key`, `description` and optional `values`, such as `{ "staging": true }`. Environments it does not turn on start off. Answers `201` with the flag |
| `PATCH /applications/{app}/flags/{flag}` | Changes the flag's `description` |
| `PUT /applications/{app}/flags/{flag}/values/{env}` | Sets `enabled` in one environment and answers with the environment's revision after the request. Setting the current value again changes nothing |

- **Protected environments:** a read-write token creates flags that are on in, and sets values in, every environment, production included, without the confirmation the dashboard asks for. Every change is recorded in the history with the source API and the token's name.
- **No escalation:** no endpoint archives or deletes flags, or manages applications, environments, SDK keys or tokens.
- **Write limit:** each token may make 30 writes a minute (`Palanca:Management:WritesPerMinute`). Every write that passes the permission and scope checks counts, whether or not it changes anything. Beyond the limit, writes get `429` with `Retry-After`; reads are not limited.
- **Errors:** RFC 9457 problem details (`application/problem+json`).
  - `400`: an invalid key or description, an unknown environment in `values`, malformed JSON or a missing required property. `415`: a write whose `Content-Type` is not JSON, or missing.
  - `401`: the token is missing, unknown, revoked or expired, without saying which. Failed attempts count toward the per-address limit: after 10 failures within a minute, requests from that address get `429`, even with a valid token, until a minute has passed.
  - `403`: the token is read-only and the request writes, which is checked before anything else about a write, or the application is outside the token's scope. A token for chosen applications gets `403` for any other key, whether or not that application exists, and for one of its own once it is archived.
  - `404`: a token for every application asked for one that does not exist or is archived, a flag or environment that does not exist or is archived, or any other path or method.
  - `409`: a flag with that key already exists, archived or not.
- **Credentials:** only management tokens work here. SDK keys and the dashboard's session cookie get `401`; a management token opens no dashboard page and gets `401` from the evaluation API.
- **Archived items:** archived applications, environments and flags are left out of the lists; the history still shows the changes made to them.
- **Caching and last use:** responses carry `Cache-Control: no-store`. A token's last use is recorded at most once a minute and shown on the Tokens page.

### From an agent or a script

Give the agent a read-write token scoped to its application, and keep the token in a file only that user can read. The agent can learn the API from the OpenAPI document, then work with plain HTTP:

```sh
token="$(cat ~/.config/palanca/token)"
base=https://palanca.example.com/api/manage/v1

# Create a flag that is on in staging only.
curl -sS --fail-with-body -X POST "$base/applications/hogarhero/flags" \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
  -d '{"key":"provider-onboarding-v2","description":"Second version of provider onboarding","values":{"staging":true}}'

# Later, turn it on in production too.
curl -sS --fail-with-body -X PUT "$base/applications/hogarhero/flags/provider-onboarding-v2/values/production" \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d '{"enabled":true}'
```

Each consumer's client picks up a change at its next poll, about one poll interval later (30 seconds by default); see the Blazor Server note under *Client package*.

## Client package

.NET applications read flags with the `Palanca.Client` package. It keeps one environment's flags in memory, so reads never wait on the network, and the flag service being down never takes the application down. Its README, [`src/Palanca.Client/README.md`](src/Palanca.Client/README.md), covers every option; it is also the package's page on nuget.org.

```csharp
builder.Services.AddPalanca(builder.Configuration.GetSection("Palanca"));
builder.Services.AddHealthChecks().AddPalanca();

// flags is an injected IPalancaFlags; Flags.NewSearch is a PalancaFlag with its default.
if (flags.IsEnabled(Flags.NewSearch)) { ... }
```

- **Settings:** `Endpoint`, `SdkKey`, `Application` and `Environment` under `Palanca`. Every answer must be for that application and environment, so a key deployed to the wrong environment is refused and logged as an error.
- **Polling:** every 30 seconds by default, with the last revision, so an unchanged poll gets `304`. Startup waits for the first flags, 2 seconds at most by default, then goes on with the code defaults.
- **Failures:** the client keeps the flags it has, retries with backoff up to the poll interval, and logs one warning per outage.
- **Health and metrics:** the `palanca` check is Degraded, never Unhealthy, before the first flags arrive and once they are more than 5 minutes old (by default). Sync metrics are on the `Palanca.Client` meter.
- **Interactive Blazor Server:** a component that reads a flag in `OnInitialized{Async}` keeps that value for the life of the component. Re-renders and navigation within the same page don't re-read it. Navigating to another page or reloading re-creates that page's components, but components that stay on screen across navigation, such as parts of a layout, keep theirs. Read the flag while rendering, or in `OnParametersSet`, when it must follow changes.
- **Releases:** pushing a `client-v<version>` tag on a commit on `main` publishes the package to nuget.org (`.github/workflows/client-release.yml`), through nuget.org's Trusted Publishing.

## Database

Palanca stores everything in one SQLite file in WAL mode. At startup it applies pending migrations, and it first copies an existing database to the backup directory.

| Setting | Development | Container |
|---------|-------------|-----------|
| `Palanca:Database:FilePath` | `src/Palanca/.data/palanca.db` | `/data/palanca.db` |
| `Palanca:Database:BackupDirectory` | `src/Palanca/.data/backups` | `/data/backups` |
| `Palanca:Database:BackupRetentionDays` | 14 | 14 |

### Backups

Point the backup directory at a host directory rather than the data volume, so losing the volume does not lose the backups too. Then run the backup daily, from a timer on the host:

```sh
docker compose exec palanca dotnet Palanca.dll backup
```

- **`backup`** copies the live database with `VACUUM INTO`, checks the copy with `PRAGMA integrity_check`, and writes it as `palanca-<UTC time>.db` with a `sha256sum` checksum file beside it. Both are readable by the service's user only. It then deletes daily copies older than the retention. The copies taken before a migration are kept.
- **`backup verify <file>`** checks a copy against its checksum file, when there is one, and runs the integrity check. It prints the copy's latest migration and the row count of each table.
- **`restore <file>`** verifies a copy, then makes it the database. The database it replaces is renamed `palanca-replaced-<UTC time>.db`, with its WAL files, and kept. It refuses a copy from a newer version of Palanca. An older copy is migrated when the service next starts.

To restore, stop the service, then run the command in a container of its own on the same volumes:

```sh
docker compose stop palanca
docker compose run --rm palanca restore /backups/palanca-20261019T074000Z.db
docker compose start palanca
```

The Data Protection keys are not in the database. A restore onto a new volume signs everyone out, and the owner signs in again with a passkey.

To add a migration:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Palanca --output-dir Data/Migrations
```

## Health and metrics

These endpoints need no credential. They are meant for the private Docker networks only, so the deployment's public route must not expose them.

- **`GET /health/live`** answers while the process runs.
- **`GET /health/ready`** answers once the database can be read and written.
- **`GET /metrics`** serves the service's counters in the Prometheus text format. The totals are kept in memory, so they start again from zero when the service restarts, and a series appears once it has counted something.

| Metric | Labels | Counts |
|--------|--------|--------|
| `palanca_requests_total` | `area`: `dashboard`, `evaluation`, `management`, `health`, `metrics`; `status_class`: `1xx` to `5xx` | Requests answered. Errors are the `5xx` class, unhandled exceptions included. |
| `palanca_authentication_failures_total` | `credential`: `sdk_key`, `management_token`, `passkey`, `enrollment_link` | Failed authentications, the same ones the failed sign-in limit counts. |

Labels take only these values, so no key, token, path, or client address reaches a metric.

## Container image

```sh
docker build --file src/Palanca/Dockerfile --tag palanca .
docker run --rm --read-only --volume palanca-data:/data --publish 8080:8080 palanca
```

`/data` is the only writable path. Mount a volume there.

The image's health check runs `dotnet Palanca.dll healthcheck`, because the chiseled runtime image has no shell.

## License

MIT
