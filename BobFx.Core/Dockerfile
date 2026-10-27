# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
WORKDIR /src
COPY global.json ./
COPY BobFx.Core/BobFx.Core.csproj BobFx.Core/
COPY BobFx.Core/packages.lock.json BobFx.Core/
RUN dotnet restore BobFx.Core/BobFx.Core.csproj --locked-mode
COPY BobFx.Core/ BobFx.Core/
RUN dotnet publish BobFx.Core/BobFx.Core.csproj --no-restore -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 BOBFX_HEALTH_URL=http://127.0.0.1:8080/health/ready
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail --silent --show-error --max-time 3 -H "Host: ${AllowedHosts%%;*}" "$BOBFX_HEALTH_URL" || exit 1

FROM base AS final
ARG VERSION=0.0.0-local
ARG REVISION=local
LABEL org.opencontainers.image.title="BobFX" org.opencontainers.image.version=$VERSION org.opencontainers.image.revision=$REVISION org.opencontainers.image.source="https://github.com/SCarlsen7757/BobFX"
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
ENTRYPOINT ["dotnet", "BobFx.Core.dll"]
