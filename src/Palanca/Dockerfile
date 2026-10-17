FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src
COPY .editorconfig Directory.Build.props Directory.Packages.props global.json ./
COPY src/Palanca/Palanca.csproj src/Palanca/
# Restore for Release: the EF Core design-time package is referenced only in Debug.
# Target linux-x64 only, so the image carries one platform's native SQLite library instead of 22.
RUN dotnet restore src/Palanca/Palanca.csproj --runtime linux-x64 -p:Configuration=Release
COPY src/Palanca/ src/Palanca/
RUN dotnet publish src/Palanca/Palanca.csproj \
    --configuration Release --runtime linux-x64 --self-contained false --no-restore --output /out /p:UseAppHost=false
# The runtime image has no shell to create the data directory, so it is made here.
RUN mkdir /data

# Pinned to the same .NET patch as HogarHero. Chiseled: no shell, no package manager,
# non-root `app` user. The application files stay owned by root, so the process cannot change them.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /out/ ./
# The only writable path: mount a volume here. Docker copies this ownership into a new named volume.
COPY --from=build --chown=$APP_UID:$APP_UID /data /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "Palanca.dll", "healthcheck"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Palanca.dll"]
