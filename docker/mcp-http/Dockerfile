FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
ARG APP_USER=netratel-mcp
ARG APP_UID=10001
ARG APP_GID=10001

RUN set -eux; \
    groupadd --system --gid "${APP_GID}" "${APP_USER}"; \
    useradd --system --uid "${APP_UID}" --gid "${APP_GID}" --home-dir /app --shell /usr/sbin/nologin "${APP_USER}"; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
ENV ASPNETCORE_URLS=http://+:9224 \
    DOTNET_EnableDiagnostics=0
EXPOSE 9224
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:9224/health/live || exit 1

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Build.targets ./
COPY src/NetRatel/NetRatel.AgentClient/NetRatel.AgentClient.csproj src/NetRatel/NetRatel.AgentClient/
COPY src/NetRatel/NetRatel.Mcp.Core/NetRatel.Mcp.Core.csproj src/NetRatel/NetRatel.Mcp.Core/
COPY src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj src/NetRatel/NetRatel.Mcp.Http/
COPY src/NetRatel/NetRatel.Cli/NetRatel.Cli.csproj src/NetRatel/NetRatel.Cli/
COPY src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj src/NetRatel/NetRatel.Mcp/
COPY src/NetRatel/NetRatel.Setup/NetRatel.Setup.csproj src/NetRatel/NetRatel.Setup/
COPY src/NetRatel/NetRatel.ServiceDefaults/NetRatel.ServiceDefaults.csproj src/NetRatel/NetRatel.ServiceDefaults/
COPY src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj src/NetRatel/NetRatel.Shared/
RUN dotnet restore src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj
RUN dotnet restore src/NetRatel/NetRatel.Cli/NetRatel.Cli.csproj && \
    dotnet restore src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj

COPY . .
RUN dotnet publish src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj \
      --configuration "${BUILD_CONFIGURATION}" \
      --no-restore \
      --output /out \
      /p:UseAppHost=false
RUN dotnet publish src/NetRatel/NetRatel.Cli/NetRatel.Cli.csproj \
      --configuration "${BUILD_CONFIGURATION}" --no-restore --output /out/tooling/cli /p:UseAppHost=false && \
    dotnet publish src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj \
      --configuration "${BUILD_CONFIGURATION}" --no-restore --output /out/tooling/stdio /p:UseAppHost=false

# Operators copy this matching launcher to the Docker host and run it with bash.
# Include it in /out before generating the runtime distribution inventory.
COPY --chmod=0644 release/setup-http-mcp.sh /out/setup-http-mcp.sh
RUN apt-get update && apt-get install -y --no-install-recommends python3
RUN python3 tools/ci/generate-runtime-sbom.py \
      --project-assets src/NetRatel/NetRatel.Mcp.Http/obj/project.assets.json \
      --project-assets src/NetRatel/NetRatel.Cli/obj/project.assets.json \
      --project-assets src/NetRatel/NetRatel.Mcp/obj/project.assets.json \
      --distribution /out --distribution /out/tooling/cli --distribution /out/tooling/stdio \
      --distribution-root /out \
      --name NetRatel.Mcp.Http \
      --version "$(dotnet msbuild src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj -nologo -getProperty:Version)" \
      --output /out/runtime.spdx.json

FROM base AS final
WORKDIR /app
COPY --from=build --chown=netratel-mcp:netratel-mcp /out ./
COPY --chown=netratel-mcp:netratel-mcp LICENSE NOTICE /app/licenses/
COPY --chown=netratel-mcp:netratel-mcp docker/mcp-http/docker-entrypoint.sh /app/docker-entrypoint.sh
# Docker initializes a fresh named setup volume from this owned directory.
# The serving process and setup tooling both retain the dedicated non-root user.
RUN mkdir -p /setup-output /var/lib/netratel/clients && \
    chown netratel-mcp:netratel-mcp /setup-output /var/lib/netratel/clients && \
    chmod 700 /setup-output /var/lib/netratel/clients && chmod 755 /app/docker-entrypoint.sh
USER netratel-mcp
ENTRYPOINT ["/app/docker-entrypoint.sh"]
