# Palanca

A small, self-hosted service for boolean feature flags, per application and per environment.
Palanca is Spanish for "lever".

## Build and test

Requires the .NET SDK pinned in `global.json`.

```sh
dotnet build Palanca.slnx
dotnet test --solution Palanca.slnx
```

## Container image

```sh
docker build --file src/Palanca/Dockerfile --tag palanca .
docker run --rm --publish 8080:8080 palanca
```

The image's health check runs `dotnet Palanca.dll healthcheck`, because the chiseled runtime image has no shell.

## License

MIT
