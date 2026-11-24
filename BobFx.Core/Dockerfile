# syntax=docker/dockerfile:1
FROM node:26-bookworm-slim@sha256:5df79989ed685e09f0a98019db4cebfb22fb9df1a53c0161664a011f7ff6b0f1 AS frontend
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY frontend/ ./
# The out dir is given here, so this stage does not depend on the repository layout that vite.config.ts assumes.
RUN npm run build -- --outDir /out --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY BobFx.Core/BobFx.Core.csproj BobFx.Core/
COPY BobFx.Core/packages.lock.json BobFx.Core/
# The package folder is a cache mount, not part of this layer: it can be empty while the layer is still cached.
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore BobFx.Core/BobFx.Core.csproj --locked-mode
COPY BobFx.Core/ BobFx.Core/
COPY --from=frontend /out/ BobFx.Core/wwwroot/ui/
# Declared after the restore, so a release version does not invalidate the restore layer.
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
# Publish restores again, still locked: the package cache mount may have been emptied since the restore layer was built.
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish BobFx.Core/BobFx.Core.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:RestoreLockedMode=true /p:UseAppHost=false /p:Version=$VERSION /p:SkipFrontendBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends --only-upgrade apt libapt-pkg6.0t64 && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN mkdir -p /app/data && chown $APP_UID:$APP_UID /app/data
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
