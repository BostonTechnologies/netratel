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

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Build.targets ./
COPY src/NetRatel/NetRatel.AgentClient/NetRatel.AgentClient.csproj src/NetRatel/NetRatel.AgentClient/
COPY src/NetRatel/NetRatel.Mcp.Core/NetRatel.Mcp.Core.csproj src/NetRatel/NetRatel.Mcp.Core/
COPY src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj src/NetRatel/NetRatel.Mcp.Http/
COPY src/NetRatel/NetRatel.ServiceDefaults/NetRatel.ServiceDefaults.csproj src/NetRatel/NetRatel.ServiceDefaults/
COPY src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj src/NetRatel/NetRatel.Shared/
RUN dotnet restore src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj

COPY . .
RUN dotnet publish src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj \
      --configuration "${BUILD_CONFIGURATION}" \
      --no-restore \
      --output /out \
      /p:UseAppHost=false

RUN apt-get update && apt-get install -y --no-install-recommends python3
RUN python3 tools/ci/generate-runtime-sbom.py --project-assets src/NetRatel/NetRatel.Mcp.Http/obj/project.assets.json --distribution /out --name NetRatel.Mcp.Http --version "$(dotnet msbuild src/NetRatel/NetRatel.Mcp.Http/NetRatel.Mcp.Http.csproj -nologo -getProperty:Version)" --output /out/runtime.spdx.json

FROM base AS final
WORKDIR /app
COPY --from=build --chown=netratel-mcp:netratel-mcp /out ./
COPY --chown=netratel-mcp:netratel-mcp LICENSE NOTICE /app/licenses/
USER netratel-mcp
ENTRYPOINT ["dotnet", "NetRatel.Mcp.Http.dll"]
