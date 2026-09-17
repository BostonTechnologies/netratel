FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj \
    --configuration Release --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "NetRatel.Migrations.dll"]
