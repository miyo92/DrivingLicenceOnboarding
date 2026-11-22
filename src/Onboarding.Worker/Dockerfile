# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json DrivingLicenceOnboarding.slnx ./
COPY src/Onboarding.Core/Onboarding.Core.csproj src/Onboarding.Core/
COPY src/Onboarding.Infrastructure/Onboarding.Infrastructure.csproj src/Onboarding.Infrastructure/
COPY src/Onboarding.Worker/Onboarding.Worker.csproj src/Onboarding.Worker/
RUN dotnet restore src/Onboarding.Worker/Onboarding.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Onboarding.Worker/Onboarding.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl is used by the compose healthcheck; the runtime image does not ship it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/photos \
    && chown "$APP_UID" /data/photos
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV PhotoStorage__RootPath=/data/photos
ENTRYPOINT ["dotnet", "Onboarding.Worker.dll"]
