ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.101
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.1-noble-chiseled
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/PCConnect.Api/PCConnect.Api.csproj --locked-mode
RUN dotnet publish src/PCConnect.Api/PCConnect.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /out .
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "PCConnect.Api.dll"]
