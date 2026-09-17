# --- Base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

# Stable app user; make creation idempotent so rebuilds don't fail
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
    fi

# DataProtection keys dir. DEV mounts this path as the durable cookie key ring.
ENV NetRatel_KEYS_DIR=/var/netratel/keys \
    NetRatel_HTTP_PORT=9111 \
    ASPNETCORE_URLS=http://+:9111 \
    ASPNETCORE_HTTP_PORTS=9111

# Prepare writable locations and set ownership
RUN mkdir -p /app /var/netratel/keys \
 && chown -R ${APP_USER}:${APP_USER} /app /var/netratel

WORKDIR /app
EXPOSE 9111 9111
USER ${APP_USER}

# --- Build/Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "Directory.Build.targets", "./"]
COPY ["src/NetRatel/NetRatel.Web/NetRatel.Web.csproj", "src/NetRatel/NetRatel.Web/"]
COPY ["src/NetRatel/NetRatel.ServiceDefaults/NetRatel.ServiceDefaults.csproj", "src/NetRatel/NetRatel.ServiceDefaults/"]
COPY ["src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj", "src/NetRatel/NetRatel.Shared/"]
RUN dotnet restore "./src/NetRatel/NetRatel.Web/NetRatel.Web.csproj"
COPY . .
WORKDIR "/src/src/NetRatel/NetRatel.Web"
RUN dotnet build "./NetRatel.Web.csproj" -c "$BUILD_CONFIGURATION" -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NetRatel.Web.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish /p:UseAppHost=false

# --- Final
FROM base AS final
WORKDIR /app
# If COPY --chown is not supported in your Docker, replace with plain COPY + RUN chown
COPY --chown=${APP_USER}:${APP_USER} --from=publish /app/publish .
COPY --chown=${APP_USER}:${APP_USER} docker/web/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh
ENTRYPOINT ["/app/docker-entrypoint.sh", "dotnet", "NetRatel.Web.dll"]
