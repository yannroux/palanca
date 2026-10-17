# Palanca

A small, self-hosted service for boolean feature flags, per application and per environment.
Palanca is Spanish for "lever".

## Build and test

Requires the .NET SDK pinned in `global.json`.

```sh
dotnet build Palanca.slnx
dotnet test --solution Palanca.slnx
```

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
