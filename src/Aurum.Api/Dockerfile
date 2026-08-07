# Build context is the repo root so the solution's project references resolve.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only edit reuses the cached layer.
COPY src/Aurum.Api/Aurum.Api.csproj src/Aurum.Api/
RUN dotnet restore src/Aurum.Api/Aurum.Api.csproj

COPY src/Aurum.Api/ src/Aurum.Api/
RUN dotnet publish src/Aurum.Api/Aurum.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# wget backs the compose healthcheck; the aspnet image ships neither it nor curl.
RUN apt-get update \
    && apt-get install --no-install-recommends -y wget \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Aurum.Api.dll"]
