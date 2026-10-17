FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src
COPY .editorconfig Directory.Build.props Directory.Packages.props global.json ./
COPY src/Palanca/Palanca.csproj src/Palanca/
RUN dotnet restore src/Palanca/Palanca.csproj
COPY src/Palanca/ src/Palanca/
RUN dotnet publish src/Palanca/Palanca.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false

# Pinned to the same .NET patch as HogarHero. Chiseled: no shell, no package manager,
# non-root `app` user. The application files stay owned by root, so the process cannot change them.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "Palanca.dll", "healthcheck"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Palanca.dll"]
