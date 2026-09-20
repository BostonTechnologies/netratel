FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj \
    --configuration Release --output /out /p:UseAppHost=false

RUN apt-get update && apt-get install -y --no-install-recommends python3
RUN python3 tools/ci/generate-runtime-sbom.py --project-assets src/NetRatel/NetRatel.Migrations/obj/project.assets.json --distribution /out --name NetRatel.Migrations --version "$(dotnet msbuild src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj -nologo -getProperty:Version)" --output /out/runtime.spdx.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
ARG APP_USER=app
ARG APP_UID=2000
ARG APP_GID=2000
RUN set -eux; \
    if ! getent group "${APP_USER}" >/dev/null; then \
        groupadd -g "${APP_GID}" "${APP_USER}" || groupadd "${APP_USER}"; \
    fi; \
    if ! id -u "${APP_USER}" >/dev/null 2>&1; then \
        useradd -u "${APP_UID}" -g "${APP_USER}" -s /usr/sbin/nologin -m "${APP_USER}" || \
        useradd -g "${APP_USER}" -s /usr/sbin/nologin -m "${APP_USER}"; \
    fi; \
    mkdir -p /var/netratel/sqlite; \
    chown -R "${APP_USER}:${APP_USER}" /var/netratel
WORKDIR /app
COPY --from=build /out .
COPY LICENSE NOTICE /app/licenses/
USER ${APP_USER}
ENTRYPOINT ["dotnet", "NetRatel.Migrations.dll"]
