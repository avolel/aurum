# Build context is the repo root so the solution's project references resolve.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only edit reuses the cached layer.
#
# Every project Aurum.Api references has to be copied here, not just Aurum.Api itself: restore
# walks ProjectReference and fails on a .csproj that is not in the build context. The test project
# is deliberately absent — nothing in the published output needs it, and pulling it in would put
# Testcontainers and the xunit runner into the restore for a production image.
COPY src/Aurum.Api/Aurum.Api.csproj src/Aurum.Api/
COPY src/Aurum.App.Application/Aurum.App.Application.csproj src/Aurum.App.Application/
COPY src/Aurum.App.Infrastructure.Data/Aurum.App.Infrastructure.Data.csproj src/Aurum.App.Infrastructure.Data/
COPY src/Aurum.App.Infrastructure.Pricing/Aurum.App.Infrastructure.Pricing.csproj src/Aurum.App.Infrastructure.Pricing/
COPY src/Aurum.App.SharedKernel/Aurum.App.SharedKernel.csproj src/Aurum.App.SharedKernel/
RUN dotnet restore src/Aurum.Api/Aurum.Api.csproj

COPY src/Aurum.Api/ src/Aurum.Api/
COPY src/Aurum.App.Application/ src/Aurum.App.Application/
COPY src/Aurum.App.Infrastructure.Data/ src/Aurum.App.Infrastructure.Data/
COPY src/Aurum.App.Infrastructure.Pricing/ src/Aurum.App.Infrastructure.Pricing/
COPY src/Aurum.App.SharedKernel/ src/Aurum.App.SharedKernel/
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
