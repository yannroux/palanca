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
- **`Palanca:FailedAuthentication:PermitLimit`** is how many failed sign-in or enrollment attempts one client address may make within a minute before further attempts get `429`. An IPv6 client is counted by its /64 network.

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
- **Change records:** every change to applications, environments, flags and values is recorded with who made it.

## Database

Palanca stores everything in one SQLite file in WAL mode. At startup it applies pending migrations, and it first copies an existing database to the backup directory.

| Setting | Development | Container |
|---------|-------------|-----------|
| `Palanca:Database:FilePath` | `src/Palanca/.data/palanca.db` | `/data/palanca.db` |
| `Palanca:Database:BackupDirectory` | `src/Palanca/.data/backups` | `/data/backups` |

To add a migration:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Palanca --output-dir Data/Migrations
```

## Container image

```sh
docker build --file src/Palanca/Dockerfile --tag palanca .
docker run --rm --read-only --volume palanca-data:/data --publish 8080:8080 palanca
```

`/data` is the only writable path. Mount a volume there.

The image's health check runs `dotnet Palanca.dll healthcheck`, because the chiseled runtime image has no shell.

## License

MIT
