FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj \
    --configuration Release --output /out /p:UseAppHost=false

RUN apt-get update && apt-get install -y --no-install-recommends python3
RUN python3 tools/ci/generate-runtime-sbom.py --project-assets src/NetRatel/NetRatel.Migrations/obj/project.assets.json --distribution /out --name NetRatel.Migrations --version "$(dotnet msbuild src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj -nologo -getProperty:Version)" --output /out/runtime.spdx.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /out .
COPY LICENSE NOTICE /app/licenses/
ENTRYPOINT ["dotnet", "NetRatel.Migrations.dll"]
