param(
  [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Info($msg) { Write-Host "    $msg" -ForegroundColor Gray }
function Write-Change($msg) { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $repoRoot  # tools/ -> repo root
Set-Location $repoRoot

$prefixOld = 'NetRatel'
$prefixNew = 'NetRatel'

# Project directories explicitly mapped (top-level)
# Ordered list to control rename order (put Web last)
$projectMap = @(
  @{ From='NetRatel.Shared';          To='NetRatel.Shared' }
  @{ From='NetRatel.ServiceDefaults'; To='NetRatel.ServiceDefaults' }
  @{ From='NetRatel.Client';          To='NetRatel.Client' }
  @{ From='NetRatel.API';             To='NetRatel.API' }
  @{ From='NetRatel.AppHost';         To='NetRatel.AppHost' }
  @{ From='NetRatel.Tests';           To='NetRatel.Tests' }
  @{ From='NetRatel.Web';             To='NetRatel.Web' }
)

# tests/* subprojects: rename with generic prefix replacement
$testDirs = @(Get-ChildItem -Directory -Recurse -Path (Join-Path $repoRoot 'tests') -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$prefixOld*" })

Write-Step "Planning directory renames"
$dirRenames = @()
foreach ($item in $projectMap) {
  $from = Join-Path $repoRoot $item.From
  if (Test-Path $from) {
    $to = Join-Path $repoRoot $item.To
    $dirRenames += [pscustomobject]@{ From = $from; To = $to }
  }
}
foreach ($d in $testDirs) {
  $newName = $d.Name -replace "^$([regex]::Escape($prefixOld))", $prefixNew
  if ($newName -ne $d.Name) {
    $dirRenames += [pscustomobject]@{ From = $d.FullName; To = (Join-Path ($d.Parent.FullName) $newName) }
  }
}

foreach ($r in $dirRenames) { Write-Info ("{0} -> {1}" -f $r.From, $r.To) }
if (-not $dirRenames) { Write-Warn "No matching directories found to rename." }

$renamedSucceeded = New-Object System.Collections.Generic.HashSet[string]
if (-not $DryRun) {
  foreach ($r in $dirRenames) {
    if (Test-Path $r.To) { Write-Warn ("Target exists, skipping: {0}" -f $r.To); $null = $renamedSucceeded.Add($r.To); continue }
    Write-Change ("Renaming dir: {0} -> {1}" -f $r.From, $r.To)
    try {
      Rename-Item -Path $r.From -NewName (Split-Path -Leaf $r.To)
      $null = $renamedSucceeded.Add($r.To)
    }
    catch {
      Write-Warn ("Failed to rename dir: {0} -> {1} :: {2}" -f $r.From, $r.To, $_.Exception.Message)
    }
  }
}

Write-Step "Renaming .csproj files"
$csprojFiles = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.Name -like "$prefixOld*.csproj" }
foreach ($f in $csprojFiles) {
  $newName = $f.Name -replace "^$([regex]::Escape($prefixOld))", $prefixNew
  if ($newName -ne $f.Name) {
    Write-Info ("{0} -> {1}" -f $f.FullName, (Join-Path $f.DirectoryName $newName))
    if (-not $DryRun) {
      Rename-Item -LiteralPath $f.FullName -NewName $newName
    }
  }
}

# Helper: safe line-based replacements for C# files
function Replace-NamespaceAndUsings([string]$file) {
  $text = Get-Content -LiteralPath $file -Raw

  # namespace declarations (file-scoped and block)
  $text = [regex]::Replace($text, '(?m)^(\s*namespace\s+)' + [regex]::Escape($prefixOld) + '(?=[\.;\s\{])', '${1}' + $prefixNew)

  # using directives (global/static supported)
  $text = [regex]::Replace($text, '(?m)^(\s*using\s+(?:global\s+)?(?:static\s+)?)(?<!=\s*)' + [regex]::Escape($prefixOld) + '(?=[\.;\s])', '${1}' + $prefixNew)

  # Razor @using directives
  $text = [regex]::Replace($text, '(?m)^(\s*@using\s+)' + [regex]::Escape($prefixOld) + '(?=[\.;\s])', '${1}' + $prefixNew)

  # alias usings: using Alias = NetRatel...;
  $text = [regex]::Replace($text, '(?m)^(\s*using\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*)' + [regex]::Escape($prefixOld) + '(?=[\.;])', '${1}' + $prefixNew)

  # InternalsVisibleTo attribute: replace the prefix inside the string literal
  $pattern = '(?m)^(\s*\[assembly:\s*InternalsVisibleTo\(\s*\")' + [regex]::Escape($prefixOld)
  $replacement = '${1}' + $prefixNew
  $text = [regex]::Replace($text, $pattern, $replacement)

  Set-Content -LiteralPath $file -Value $text -NoNewline
}

# Helper: XML replacements for csproj
function Update-Csproj([string]$file) {
  $raw = Get-Content -LiteralPath $file -Raw
  $orig = $raw
  # Replace only when the tag exists
  $raw = [regex]::Replace($raw, '<AssemblyName>' + [regex]::Escape($prefixOld) + '(.*?)</AssemblyName>', '<AssemblyName>' + $prefixNew + '$1</AssemblyName>')
  $raw = [regex]::Replace($raw, '<RootNamespace>' + [regex]::Escape($prefixOld) + '(.*?)</RootNamespace>', '<RootNamespace>' + $prefixNew + '$1</RootNamespace>')
  $raw = [regex]::Replace($raw, '(<InternalsVisibleTo\s+Include=\")' + [regex]::Escape($prefixOld), '${1}' + $prefixNew)
  # Update project references: directories and csproj file names
  $raw = [regex]::Replace($raw, '(<ProjectReference\s+Include=\"[^\"]*)' + [regex]::Escape($prefixOld) + '(.*?)', { param($m) $m.Groups[1].Value + $prefixNew + $m.Groups[2].Value })
  # In case of <ProjectReference Include="..\Foo\NetRatel.Bar.csproj">
  $raw = [regex]::Replace($raw, '(<ProjectReference\s+Include=\"[^\"]*\\)' + [regex]::Escape($prefixOld) + '(\.csproj\")', '${1}' + $prefixNew + '${2}')
  if ($raw -ne $orig) { Set-Content -LiteralPath $file -Value $raw -NoNewline }
}

Write-Step "Updating .sln entries based on successful renames"
$slnPath = Join-Path $repoRoot 'NetRatel.sln'
if (Test-Path $slnPath) {
  $lines = Get-Content -LiteralPath $slnPath
  for ($i=0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^Project\("\{[^\}]+\}"\) = "([^"]+)",\s*"([^"]+)",\s*"\{[^\}]+\}"') {
      $projName = $matches[1]
      $projPath = $matches[2]
      $newName = $projName -replace "^$([regex]::Escape($prefixOld))", $prefixNew
      # Compute new path candidate by replacing prefix
      $newPathCandidate = $projPath -replace [regex]::Escape($prefixOld) + '\\', ($prefixNew + '\\') -replace [regex]::Escape($prefixOld) + '\.csproj$', ($prefixNew + '.csproj')
      $absNew = Join-Path $repoRoot $newPathCandidate
      $absOld = Join-Path $repoRoot $projPath
      $finalName = $projName
      $finalPath = $projPath
      if (Test-Path $absNew) { $finalName = $newName; $finalPath = $newPathCandidate }
      elseif (-not (Test-Path $absOld)) { Write-Warn "Neither old nor new path exist: $projPath" }
      # Rewrite line with possibly updated name/path
      $lines[$i] = [regex]::Replace($line, '=\s*\"[^\"]+\"\s*,\s*\"[^\"]+\"', ('= "' + $finalName + '", "' + $finalPath + '"'))
    }
  }
  Set-Content -LiteralPath $slnPath -Value ($lines -join "`r`n")
}
else { Write-Warn "Solution file not found: $slnPath" }

# Optionally rename solution file if all core directories were renamed
$coreTargets = @('NetRatel.API','NetRatel.AppHost','NetRatel.Client','NetRatel.ServiceDefaults','NetRatel.Shared','NetRatel.Tests','NetRatel.Web') | ForEach-Object { Join-Path $repoRoot $_ }
$allCoreRenamed = $true
foreach ($t in $coreTargets) { if (-not (Test-Path $t)) { $allCoreRenamed = $false } }
if ($allCoreRenamed) {
  $slnOld = Join-Path $repoRoot 'NetRatel.sln'
  $slnNew = Join-Path $repoRoot 'NetRatel.sln'
  if (Test-Path $slnOld) {
    if (Test-Path $slnNew) { Remove-Item $slnNew -Force }
    Rename-Item -LiteralPath $slnOld -NewName (Split-Path -Leaf $slnNew)
    Write-Change "Renamed solution file to NetRatel.sln"
  }
}

Write-Step "Updating .csproj contents"
$allCsproj = Get-ChildItem -Recurse -Filter *.csproj
foreach ($p in $allCsproj) {
  try { Update-Csproj -file $p.FullName } catch { Write-Warn "Failed to update csproj: $($p.FullName) :: $($_.Exception.Message)" }
}

Write-Step "Rewriting namespaces and usings in source files"
$codeGlobs = @('*.cs','*.razor','*.cshtml')
$codeFiles = @()
foreach ($g in $codeGlobs) { $codeFiles += Get-ChildItem -Recurse -Include $g -File }
$codeFiles = $codeFiles | Where-Object { $_.FullName -notmatch '\\.git\\' }
foreach ($f in $codeFiles) {
  $content = Get-Content -LiteralPath $f.FullName -Raw
  if ($content -match "(?m)^(\s*(namespace|using|global\s+using|\[assembly:|@using))") {
    try { Replace-NamespaceAndUsings -file $f.FullName } catch { Write-Warn "Failed to update: $($f.FullName) :: $($_.Exception.Message)" }
  }
}

Write-Step "Updating Dockerfiles and build scripts"
$textFiles = Get-ChildItem -Recurse -Include 'Dockerfile','*.yml','*.yaml','*.ps1','*.sh' -File
foreach ($tf in $textFiles) {
  $raw = Get-Content -LiteralPath $tf.FullName -Raw
  if ($raw -match [regex]::Escape($prefixOld + '.')) {
    $newRaw = $raw -replace [regex]::Escape($prefixOld) + '\.', ($prefixNew + '.')
    if ($newRaw -ne $raw) { Set-Content -LiteralPath $tf.FullName -Value $newRaw -NoNewline; Write-Change "Updated: $($tf.FullName)" }
  }
}

Write-Step "All done. Consider running: dotnet build ./NetRatel.sln ; dotnet test ./NetRatel.sln"
