dotnet --info
dotnet restore .\NetRatel.sln
dotnet build   .\NetRatel.sln -c Release --no-restore
