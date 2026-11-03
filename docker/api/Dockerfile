# --- Base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

# The .NET base image's app identity is UID/GID 1654. Keep it stable so
# existing rc.5 volumes remain readable and mounted owner-only keys work.
ARG APP_USER=app
ARG APP_UID=1654
ARG APP_GID=1654
RUN test "$(id -u "${APP_USER}")" = "${APP_UID}" \
 && test "$(id -g "${APP_USER}")" = "${APP_GID}"

# ASP.NET's runtime image does not include the Kerberos GSSAPI native library,
# but the API's negotiated-authentication path loads it at runtime.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

ENV NetRatel_KEYS_DIR=/var/netratel/keys \
    NetRatel_HTTP_PORT=9222

# Prepare writable locations and set ownership
RUN mkdir -p /app /var/netratel/keys && chown -R ${APP_USER}:${APP_USER} /app /var/netratel

WORKDIR /app
EXPOSE 9222 9223
USER ${APP_USER}

# --- Build/Publish unchanged ...
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ARG PublicSourceRevision
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "Directory.Build.targets", "./"]
COPY ["src/NetRatel/NetRatel.API/NetRatel.API.csproj", "src/NetRatel/NetRatel.API/"]
COPY ["src/NetRatel/NetRatel.Application/NetRatel.Application.csproj", "src/NetRatel/NetRatel.Application/"]
COPY ["src/NetRatel/NetRatel.Infrastructure/NetRatel.Infrastructure.csproj", "src/NetRatel/NetRatel.Infrastructure/"]
COPY ["src/NetRatel/NetRatel.ServiceDefaults/NetRatel.ServiceDefaults.csproj", "src/NetRatel/NetRatel.ServiceDefaults/"]
COPY ["src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj", "src/NetRatel/NetRatel.Shared/"]
COPY ["src/NetRatel/NetRatel.AgentGateway.Contracts/NetRatel.AgentGateway.Contracts.csproj", "src/NetRatel/NetRatel.AgentGateway.Contracts/"]
COPY ["src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj", "src/NetRatel/NetRatel.Akka/"]
RUN dotnet restore "./src/NetRatel/NetRatel.API/NetRatel.API.csproj"
COPY . .
WORKDIR "/src/src/NetRatel/NetRatel.API"
RUN dotnet build "./NetRatel.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NetRatel.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN apt-get update && apt-get install -y --no-install-recommends python3
RUN python3 /src/tools/ci/generate-runtime-sbom.py --project-assets obj/project.assets.json --distribution /app/publish --name NetRatel.API --version "$(dotnet msbuild NetRatel.API.csproj -nologo -getProperty:Version)" --output /app/publish/runtime.spdx.json

# Optional linux/amd64 diagnostic target. The production final target remains
# the last stage and uses the existing noble base until acceptance is complete.
# Follow the tool/checksum used by Microsoft's noble-chiseled-extra image;
# Canonical's slice supplies the GSSAPI dependency and copyright closure.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS chisel
ARG TARGETARCH
RUN test "$TARGETARCH" = amd64 \
 && curl --fail --show-error --location --output /tmp/chisel.tar.gz \
      https://github.com/canonical/chisel/releases/download/v1.5.0/chisel_v1.5.0_linux_amd64.tar.gz \
 && echo 'f8ba3f3d04165b50d91aae7eb3bcaca2a714f1877d2af005989617937cf2e375ab30d4a50b1308263e61a61ecb79de5c  /tmp/chisel.tar.gz' | sha384sum -c - \
 && tar -xzf /tmp/chisel.tar.gz -C /usr/bin \
 && mkdir -p /rootfs \
 && chisel cut --release ubuntu-24.04 --root /rootfs base-files_chisel libgssapi-krb5-2_libs \
 && install -d -m 0755 -o 1654 -g 1654 /rootfs/app /rootfs/var/netratel /rootfs/var/netratel/keys

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS chiseled-candidate
ENV NetRatel_KEYS_DIR=/var/netratel/keys \
    NetRatel_HTTP_PORT=9222
COPY --from=chisel /rootfs /
WORKDIR /app
COPY --chown=1654:1654 --from=publish /app/publish .
COPY --chown=1654:1654 --chmod=755 docker/api/docker-entrypoint.sh /app/docker-entrypoint.sh
COPY --chown=1654:1654 LICENSE NOTICE /app/licenses/
EXPOSE 9222 9223
USER 1654:1654
ENTRYPOINT ["dotnet", "NetRatel.API.dll"]

# --- Final (default production target)
FROM base AS final
WORKDIR /app
# If your Docker supports it, keep --chown; otherwise chown after COPY
COPY --chown=${APP_USER}:${APP_USER} --from=publish /app/publish .
COPY --chown=${APP_USER}:${APP_USER} docker/api/docker-entrypoint.sh /app/docker-entrypoint.sh
COPY --chown=${APP_USER}:${APP_USER} LICENSE NOTICE /app/licenses/
RUN chmod +x /app/docker-entrypoint.sh
# RUN chown -R ${APP_USER}:${APP_USER} /app   # <- use this if COPY --chown not supported
ENTRYPOINT ["/app/docker-entrypoint.sh", "dotnet", "NetRatel.API.dll"]
