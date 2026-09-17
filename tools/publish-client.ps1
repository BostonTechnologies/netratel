param(
  [ValidateSet("win-x64","linux-x64","osx-arm64")]
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"
$outDir = Join-Path $root ("artifacts/client/{0}" -f $Runtime)

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Publishing NetRatel.Client for $Runtime to $outDir"
dotnet publish $project -c Release -r $Runtime /p:PublishSingleFile=true /p:SelfContained=true /p:PublishTrimmed=false -o $outDir
