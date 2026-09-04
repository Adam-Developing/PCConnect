# =============================================================================
# PCConnect API image.
#
# Multi-stage: the SDK never reaches the runtime image, the app runs as a
# non-root user, and the migration tool ships alongside so `migrate` and `api`
# are the same artefact at the same version.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore on the project files alone, so a source-only change reuses the layer.
COPY Directory.Build.props Directory.Packages.props PCConnect.sln ./
COPY src/PCConnect.Core/*.csproj src/PCConnect.Core/
COPY src/PCConnect.Infrastructure/*.csproj src/PCConnect.Infrastructure/
COPY src/PCConnect.Api/*.csproj src/PCConnect.Api/
COPY src/PCConnect.DbMigrator/*.csproj src/PCConnect.DbMigrator/
RUN dotnet restore src/PCConnect.Api/PCConnect.Api.csproj

COPY src/ src/
COPY db/ db/
RUN dotnet publish src/PCConnect.Api/PCConnect.Api.csproj \
        -c Release -o /app/publish --no-restore \
    && dotnet publish src/PCConnect.DbMigrator/PCConnect.DbMigrator.csproj \
        -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# wget for the healthcheck; ICU because reminder scheduling is timezone-aware
# and InvariantGlobalization would break every IANA lookup (S2-07).
RUN apk add --no-cache wget icu-libs tzdata \
    && addgroup -S pcconnect && adduser -S -G pcconnect pcconnect

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

COPY --from=build --chown=pcconnect:pcconnect /app/publish .

USER pcconnect
EXPOSE 8080

ENTRYPOINT ["dotnet", "PCConnect.Api.dll"]
