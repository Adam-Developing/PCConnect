# =============================================================================
# PCConnect worker image.
#
# The scheduled half: command expiry, the reminder scheduler, the recurrence
# horizon, retention, and the continuous verification gates.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props PCConnect.sln ./
COPY src/PCConnect.Core/*.csproj src/PCConnect.Core/
COPY src/PCConnect.Infrastructure/*.csproj src/PCConnect.Infrastructure/
COPY src/PCConnect.Worker/*.csproj src/PCConnect.Worker/
RUN dotnet restore src/PCConnect.Worker/PCConnect.Worker.csproj

COPY src/ src/
RUN dotnet publish src/PCConnect.Worker/PCConnect.Worker.csproj \
        -c Release -o /app/publish --no-restore

# The ASP.NET Core runtime, not the base runtime: the worker publishes to the
# same SignalR hub type the API hosts, so it references Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache icu-libs tzdata \
    && addgroup -S pcconnect && adduser -S -G pcconnect pcconnect

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0

COPY --from=build --chown=pcconnect:pcconnect /app/publish .

USER pcconnect

ENTRYPOINT ["dotnet", "PCConnect.Worker.dll"]
