#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $SourceRevision,
    [Parameter(Mandatory)][string] $ReceiptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $IsWindows) { throw 'The protected Windows archive smoke requires Windows.' }
if ($SourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'A verified 40-character source revision is required.' }
$archive = [IO.Path]::GetFullPath($ArchivePath)
$receipt = [IO.Path]::GetFullPath($ReceiptPath)
$powershell = (Get-Process -Id $PID).Path
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('NetRatel-Mcp-Smoke-' + [Guid]::NewGuid().ToString('N'))
$secure = $null
$rotated = $null
$captured = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[string]]::new()
$parentToken = [Environment]::GetEnvironmentVariable('NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN')
$parentTemp = $env:TEMP
$parentTmp = $env:TMP

function Assert-Smoke([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function New-Start([string] $Executable, [string[]] $Arguments, [hashtable] $Environment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('NETRATEL', [StringComparison]::OrdinalIgnoreCase)) { [void] $start.Environment.Remove($key) }
    }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    return $start
}

function Invoke-Local([string] $Executable, [string[]] $Arguments, [string] $InputText = '', [hashtable] $Environment = @{}) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-Start $Executable $Arguments $Environment
    try {
        Assert-Smoke ($process.Start()) 'Could not start a local archive fixture.'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        Assert-Smoke ($process.WaitForExit(30000)) 'A local archive fixture exceeded its deadline.'
        $result = @{ ExitCode = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
        Assert-Smoke (($result.Out.Length + $result.Error.Length) -le 65536) 'Local fixture output exceeded its bound.'
        $captured.Add($result.Out)
        $captured.Add($result.Error)
        return $result
    }
    finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill($false); [void] $process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Invoke-Launcher([string[]] $Arguments, [string] $InputText = '', [hashtable] $Environment = @{}) {
    return Invoke-Local $powershell (@('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $launcher) + $Arguments) $InputText $Environment
}

function Read-Protocol([Diagnostics.Process] $Process, [string] $Request) {
    $Process.StandardInput.WriteLine($Request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace($line) -and $line.Length -le 1048576) 'The archived MCP returned an empty or oversized protocol response.'
    $captured.Add($line)
    return $line | ConvertFrom-Json -AsHashtable
}

function Assert-PackagedIdentity([string] $Executable) {
    # These operations are local catalog reads. The reserved .invalid destination is never contacted.
    $config = Join-Path $fixture 'metadata-config.json'
    @{ ApiBaseUrl = 'https://metadata.example.invalid'; AuthenticationMode = 'IntegrationCredential'; CredentialPurpose = 'Api'; Instance = 'dev' } |
        ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-Start $Executable @()
    $process.StartInfo.Environment['NETRATEL_MCP_CONFIG'] = $config
    $process.StartInfo.Environment['NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN'] = 'synthetic-metadata-' + [Guid]::NewGuid().ToString('N')
    try {
        Assert-Smoke ($process.Start()) 'The archived MCP did not start.'
        [void] $process.StartInfo.Environment.Remove('NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN')
        $stderr = $process.StandardError.ReadToEndAsync()
        $initialize = Read-Protocol $process '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"windows-archive-smoke","version":"1"}}}'
        Assert-Smoke ($initialize.id -eq 1 -and $initialize.result.serverInfo.name -eq 'NetRatel.Mcp' -and $initialize.result.serverInfo.version -eq $Version) 'The archived initialize identity differs from the release.'
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $tools = Read-Protocol $process '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
        Assert-Smoke ($tools.id -eq 2 -and $tools.result.tools.Count -gt 0) 'The archived tool catalog is empty.'
        $status = Read-Protocol $process '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"status"}}}'
        $data = $status.result.structuredContent.data
        Assert-Smoke ($status.id -eq 3 -and $status.result.structuredContent.success -eq $true -and $data.identityScope -eq 'mcp_host' -and
            $data.sourceIdentityAvailable -eq $true -and $data.hostRelease.serviceName -eq 'NetRatel.Mcp' -and
            $data.hostRelease.productVersion -eq $Version -and $data.hostRelease.sourceRevision -ceq $SourceRevision -and
            $data.catalog.digest -cmatch '^[0-9a-f]{64}$' -and $data.nativeLoadedMetadata -eq 'not_observed') 'The archived status does not report the exact MCP release identity.'
        $checks.Add('packaged-initialize-catalog-status-source-identity')
        $process.StandardInput.Close()
        Assert-Smoke ($process.WaitForExit(15000)) 'The archived metadata process did not finish after stdin EOF.'
        Assert-Smoke ($process.ExitCode -eq 0) 'The archived metadata process failed.'
        $captured.Add($stderr.GetAwaiter().GetResult())
    }
    finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill($false); [void] $process.WaitForExit(5000) }
        $process.Dispose()
    }
}

try {
    [void] [IO.Directory]::CreateDirectory($fixture)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($identity)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $fixture -AclObject $acl
    $privateTemp = Join-Path $fixture 'temp'
    [void] [IO.Directory]::CreateDirectory($privateTemp)
    $unsafeDirectory = Join-Path $fixture 'replaceable'
    [void] [IO.Directory]::CreateDirectory($unsafeDirectory)
    $unsafeAcl = Get-Acl -LiteralPath $unsafeDirectory
    $unsafeAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-1-0'), 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $unsafeDirectory -AclObject $unsafeAcl
    $unsafeEnvironment = @{ TEMP = $unsafeDirectory; TMP = $unsafeDirectory }
    Expand-Archive -LiteralPath $archive -DestinationPath $fixture
    $distribution = Join-Path $fixture 'netratel-mcp-win-x64'
    $launcher = Join-Path $distribution 'http-mcp-windows.ps1'
    $executable = Join-Path $distribution 'NetRatel.Mcp.exe'
    $bridgeVersion = Invoke-Local $executable @('http-bridge', '--version')
    $reportedVersion = $bridgeVersion.Out.Trim()
    Assert-Smoke ($bridgeVersion.ExitCode -eq 0 -and ($reportedVersion -eq $Version -or $reportedVersion.StartsWith($Version + '+', [StringComparison]::Ordinal))) 'The archived HTTP bridge version differs from the release.'
    $help = Invoke-Local $executable @('http-bridge', '--help')
    Assert-Smoke ($help.ExitCode -eq 0 -and $help.Out.Contains('http-bridge')) 'The archived HTTP bridge help failed.'
    Assert-PackagedIdentity $executable
    $checks.Add('archive-version-help')

    $token = 'synthetic-windows-credential-' + [Guid]::NewGuid().ToString('N')
    $secure = ConvertTo-SecureString $token -AsPlainText -Force
    $credentialFiles = @((Join-Path $fixture 'secure.clixml'), (Join-Path $fixture 'credential.clixml'))
    $secure | Export-Clixml -LiteralPath $credentialFiles[0]
    [Management.Automation.PSCredential]::new('synthetic-account', $secure) | Export-Clixml -LiteralPath $credentialFiles[1]
    $originalHashes = @($credentialFiles | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    $originalAcls = @($credentialFiles | ForEach-Object { (Get-Acl -LiteralPath $_).Sddl })
    $fixtureAcl = (Get-Acl -LiteralPath $fixture).Sddl
    $distributionAcl = (Get-Acl -LiteralPath $distribution).Sddl
    $profiles = @()
    $launchEntries = @()
    for ($index = 0; $index -lt $credentialFiles.Count; $index++) {
        $profile = Join-Path $fixture "prepared-$index.json"
        $profiles += $profile
        $prepared = Invoke-Launcher @('-Prepare', '-Profile', $profile, '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $credentialFiles[$index],
            '-TemporaryDirectory', $privateTemp, '-StateDirectory', $fixture) '' $unsafeEnvironment
        Assert-Smoke ($prepared.ExitCode -eq 0) 'Preparation of a standard encrypted Windows credential failed.'
        $record = $prepared.Out | ConvertFrom-Json -AsHashtable
        $launchEntries += $record.launch
        Assert-Smoke ($record.state -eq 'prepared' -and $record.protectedStorage -eq 'windows-powershell-clixml' -and
            $record.consumerSwitch -eq 'pending' -and $record.consumerReconnect -eq 'pending' -and
            $record.nativeLoadedMetadata -eq 'not_observed' -and $record.effectiveAdmission -eq 'not_tested') 'The preparation receipt overstates client reconnect or effective access.'
        Assert-Smoke ($record.layout.installation -eq $distribution -and $record.layout.encryptedSource -eq $credentialFiles[$index] -and
            $record.layout.reference -eq $profile -and $record.layout.pendingStateDirectory -eq $fixture -and
            $record.layout.temporaryDirectory -eq $privateTemp -and $record.launch.args[-2] -eq '-TemporaryDirectory' -and
            $record.launch.args[-1] -eq $privateTemp) 'The preparation receipt does not reproduce the validated layout.'
        $manifest = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json -AsHashtable
        Assert-Smoke ($manifest.Count -eq 7 -and $manifest.credentialFileSha256 -ceq $originalHashes[$index].ToLowerInvariant()) 'The nonsecret manifest does not bind the encrypted source.'
        # Malformed stdin is handled locally by the actual archived bridge before any HTTP request.
        # Execute the returned entry unchanged, even with an unsafe ambient TEMP/TMP.
        $running = Invoke-Local $record.launch.command @($record.launch.args) "{broken`n" $unsafeEnvironment
        Assert-Smoke ($running.ExitCode -eq 0) 'The actual protected launcher failed its local protocol smoke.'
        $parseError = $running.Out.Trim() | ConvertFrom-Json -AsHashtable
        Assert-Smoke ($parseError.error.code -eq -32700 -and $parseError.ContainsKey('id') -and $null -eq $parseError.id) 'Malformed stdin did not return the expected protocol parse error.'
    }
    $checks.Add('securestring-and-pscredential-dpapi-prepare-run')
    $checks.Add('returned-launch-entry-reproduces-validated-temporary-directory')

    $unsafeProfile = Join-Path $fixture 'unsafe-temp.json'
    $unsafeTemp = Invoke-Launcher @('-Prepare', '-Profile', $unsafeProfile, '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $credentialFiles[0]) '' $unsafeEnvironment
    Assert-Smoke ($unsafeTemp.ExitCode -eq 2 -and $unsafeTemp.Error.Contains('stage=preparation; pathRole=temporary-directory') -and
        -not (Test-Path -LiteralPath $unsafeProfile)) 'An unsafe effective temporary directory did not fail preparation clearly.'
    foreach ($control in @(
        @{ Role = 'encrypted-source'; Profile = (Join-Path $fixture 'unsafe-source.json'); Source = (Join-Path $unsafeDirectory 'source.clixml') },
        @{ Role = 'reference'; Profile = (Join-Path $unsafeDirectory 'reference.json'); Source = $credentialFiles[0] }
    )) {
        if ($control.Role -eq 'encrypted-source') {
            Copy-Item -LiteralPath $credentialFiles[0] -Destination $control.Source
            $safeSourceAcl = Get-Acl -LiteralPath $credentialFiles[0]
            $safeSourceAcl.SetAccessRuleProtection($true, $true)
            Set-Acl -LiteralPath $control.Source -AclObject $safeSourceAcl
        }
        $denied = Invoke-Launcher @('-Prepare', '-Profile', $control.Profile, '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $control.Source, '-TemporaryDirectory', $privateTemp)
        Assert-Smoke ($denied.ExitCode -eq 2 -and $denied.Error.Contains('pathRole=' + $control.Role) -and
            -not (Test-Path -LiteralPath $control.Profile)) 'An unsafe encrypted source or reference ancestor was accepted.'
    }
    $unsafeStateProfile = Join-Path $fixture 'unsafe-state.json'
    $unsafeState = Invoke-Launcher @('-Prepare', '-Profile', $unsafeStateProfile, '-Resource', 'https://synthetic.example.invalid/mcp',
        '-CredentialFile', $credentialFiles[0], '-TemporaryDirectory', $privateTemp, '-StateDirectory', $unsafeDirectory)
    Assert-Smoke ($unsafeState.ExitCode -eq 2 -and $unsafeState.Error.Contains('pathRole=pending-state') -and
        -not (Test-Path -LiteralPath $unsafeStateProfile)) 'An unsafe pending-state directory was accepted.'
    $checks.Add('unsafe-temp-source-reference-and-state-preparation-rejected')

    $reparse = Join-Path $fixture 'temp-junction'
    [void] (New-Item -ItemType Junction -Path $reparse -Target $privateTemp)
    foreach ($invalidTemp in @($reparse, '\\synthetic.invalid\mcp')) {
        $invalid = Invoke-Launcher @('-Prepare', '-Profile', (Join-Path $fixture 'invalid-temp.json'), '-Resource', 'https://synthetic.example.invalid/mcp',
            '-CredentialFile', $credentialFiles[0], '-TemporaryDirectory', $invalidTemp)
        Assert-Smoke ($invalid.ExitCode -eq 2 -and $invalid.Error.Contains('pathRole=temporary-directory')) 'A network or reparse temporary path was accepted.'
    }
    Remove-Item -LiteralPath $reparse -Force
    $checks.Add('network-and-reparse-preparation-rejected')

    $temporaryAcl = Get-Acl -LiteralPath $privateTemp
    Set-Acl -LiteralPath $privateTemp -AclObject $unsafeAcl
    $changedTemp = Invoke-Local $launchEntries[0].command @($launchEntries[0].args) "{broken`n"
    Assert-Smoke ($changedTemp.ExitCode -eq 2 -and $changedTemp.Error.Contains('stage=launch; pathRole=temporary-directory')) 'Runtime did not revalidate the selected temporary path.'
    Set-Acl -LiteralPath $privateTemp -AclObject $temporaryAcl
    $checks.Add('runtime-revalidates-selected-temporary-directory')

    # The disposable installation's parent is deliberately replaceable by Everyone.
    $unsafeInstallation = Join-Path $unsafeDirectory 'installed'
    Copy-Item -LiteralPath $distribution -Destination $unsafeInstallation -Recurse
    Set-Acl -LiteralPath $unsafeInstallation -AclObject $acl
    $unsafeInstall = Invoke-Local $powershell @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $unsafeInstallation 'http-mcp-windows.ps1'),
        '-Profile', $profiles[0], '-TemporaryDirectory', $privateTemp)
    Assert-Smoke ($unsafeInstall.ExitCode -eq 2 -and $unsafeInstall.Error.Contains('pathRole=installation')) 'An unsafe installation ancestor was accepted.'
    Remove-Item -LiteralPath $unsafeInstallation -Recurse -Force
    $checks.Add('unsafe-installation-ancestor-rejected')

    $profileHash = (Get-FileHash -LiteralPath $profiles[0] -Algorithm SHA256).Hash
    $overwrite = Invoke-Launcher @('-Prepare', '-Profile', $profiles[0], '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $credentialFiles[0], '-TemporaryDirectory', $privateTemp)
    Assert-Smoke ($overwrite.ExitCode -eq 2 -and (Get-FileHash -LiteralPath $profiles[0] -Algorithm SHA256).Hash -ceq $profileHash) 'Preparation overwrote an existing profile.'
    $unsupportedSource = Join-Path $fixture 'unsupported.clixml'
    'unsupported-synthetic-object' | Export-Clixml -LiteralPath $unsupportedSource
    $unsupportedProfile = Join-Path $fixture 'unsupported.json'
    $unsupported = Invoke-Launcher @('-Prepare', '-Profile', $unsupportedProfile, '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $unsupportedSource, '-TemporaryDirectory', $privateTemp)
    Assert-Smoke ($unsupported.ExitCode -eq 2 -and -not (Test-Path -LiteralPath $unsupportedProfile)) 'An unsupported credential object was accepted.'
    $checks.Add('overwrite-and-unsupported-format-rejected')

    $staleSource = Join-Path $fixture 'rotation-control.clixml'
    Copy-Item -LiteralPath $credentialFiles[0] -Destination $staleSource
    $staleProfile = Join-Path $fixture 'rotation-control.json'
    $stalePrepared = Invoke-Launcher @('-Prepare', '-Profile', $staleProfile, '-Resource', 'https://synthetic.example.invalid/mcp', '-CredentialFile', $staleSource, '-TemporaryDirectory', $privateTemp)
    Assert-Smoke ($stalePrepared.ExitCode -eq 0) 'The stale-source control could not be prepared.'
    $staleHash = (Get-FileHash -LiteralPath $staleProfile -Algorithm SHA256).Hash
    $rotated = ConvertTo-SecureString ('synthetic-rotation-' + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $rotated | Export-Clixml -LiteralPath $staleSource # Intentional mutation of a separate disposable control copy.
    $stale = Invoke-Launcher @('-Profile', $staleProfile, '-TemporaryDirectory', $privateTemp) "{broken`n"
    Assert-Smoke ($stale.ExitCode -eq 2 -and (Get-FileHash -LiteralPath $staleProfile -Algorithm SHA256).Hash -ceq $staleHash) 'A changed encrypted source was silently consumed.'
    $checks.Add('stale-encrypted-source-rejected')

    $childScript = Join-Path $fixture 'synthetic-child.ps1'
    @'
param([string] $Mode, [string] $ExpectedHash)
$ErrorActionPreference = 'Stop'
$name = if ($Mode -eq 'requester') { 'NETRATEL_MCP_ACCESS_REQUESTER_TOKEN' } else { 'NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN' }
$other = if ($Mode -eq 'requester') { 'NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN' } else { 'NETRATEL_MCP_ACCESS_REQUESTER_TOKEN' }
$value = [Environment]::GetEnvironmentVariable($name)
if (-not $value -or [Environment]::GetEnvironmentVariable($other) -or $env:NETRATEL_SMOKE_PARENT_CONTROL) { exit 41 }
$actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant()
if ($actual -cne $ExpectedHash) { exit 42 }
$value = $null
$stdout = [Console]::OpenStandardOutput()
function Emit([string] $Value) { $bytes = [Text.Encoding]::UTF8.GetBytes($Value); $stdout.Write($bytes); $stdout.Flush() }
Emit ('{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"fixture","progress":1,"message":"café ☃","childPid":' + $PID + "}}`n")
if ($Mode -in @('exit', 'requester')) { exit 0 }
if ([Console]::ReadLine() -cne 'continue') { exit 43 }
if ($Mode -eq 'fault') {
    Emit "{`"fixture`":`"consumer-failure`"}`n"
    [void] [Console]::ReadLine()
    exit 0
}
Emit "{`"jsonrpc`":`"2.0`",`"id`":1,`"result`":{`"message`":`"café ☃`"}}`n"
$diagnostics = [Text.Encoding]::UTF8.GetBytes("fixture-error-µ`n")
$stderr = [Console]::OpenStandardError(); $stderr.Write($diagnostics); $stderr.Flush()
'@ | Set-Content -LiteralPath $childScript -Encoding utf8NoBOM
    Add-Type -Path @((Join-Path $distribution 'HttpMcpProcessRelay.cs'), (Join-Path $PSScriptRoot 'HttpMcpWindowsRelaySmoke.cs'))
    $tokenHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($token))).ToLowerInvariant()
    [NetRatel.Tests.ClientTools.HttpMcpWindowsRelaySmoke]::RunAsync($powershell, $childScript, $secure, $tokenHash).GetAwaiter().GetResult()
    $checks.Add('relay-early-notification-byte-fidelity')
    $checks.Add('relay-exit-with-stdin-open')
    $checks.Add('relay-consumer-failure-bounded-child-shutdown')
    $checks.Add('relay-requester-token-purpose-isolation')

    for ($index = 0; $index -lt $credentialFiles.Count; $index++) {
        Assert-Smoke ((Get-FileHash -LiteralPath $credentialFiles[$index] -Algorithm SHA256).Hash -ceq $originalHashes[$index]) 'The selected encrypted credential source changed.'
        Assert-Smoke ((Get-Acl -LiteralPath $credentialFiles[$index]).Sddl -ceq $originalAcls[$index]) 'An original encrypted source ACL changed.'
    }
    foreach ($text in $captured) { Assert-Smoke (-not $text.Contains($token, [StringComparison]::Ordinal)) 'Synthetic plaintext appeared in captured output.' }
    foreach ($file in Get-ChildItem -LiteralPath $fixture -Recurse -File) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        Assert-Smoke (-not [Text.Encoding]::UTF8.GetString($bytes).Contains($token, [StringComparison]::Ordinal) -and
            -not [Text.Encoding]::Unicode.GetString($bytes).Contains($token, [StringComparison]::Ordinal)) 'Synthetic plaintext was persisted in the fixture.'
    }
    Assert-Smoke (@(Get-ChildItem -LiteralPath $privateTemp -Filter 'netratel-http-client-*.json').Count -eq 0) 'The launcher left temporary client configuration behind.'
    Assert-Smoke ([Environment]::GetEnvironmentVariable('NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN') -ceq $parentToken) 'The parent credential environment changed.'
    Assert-Smoke ($env:TEMP -ceq $parentTemp -and $env:TMP -ceq $parentTmp -and
        (Get-Acl -LiteralPath $fixture).Sddl -ceq $fixtureAcl -and (Get-Acl -LiteralPath $distribution).Sddl -ceq $distributionAcl) 'Existing layout ACLs or parent temporary environment changed.'
    $checks.Add('encrypted-source-preserved-no-plaintext-or-temporary-profile')
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receipt))
    [ordered]@{ schemaVersion = 1; state = 'passed'; runtime = 'win-x64'; productVersion = $Version; sourceRevision = $SourceRevision;
        archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); checks = $checks.ToArray();
        protectedStorage = 'windows-powershell-clixml'; endpointMode = 'synthetic-local-only'; nativeConsumerReconnect = 'not_tested'; effectiveAdmission = 'not_tested' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
    Write-Output ('Windows MCP archive smoke passed: ' + $checks.Count + ' bounded synthetic checks.')
}
finally {
    if ($secure) { $secure.Dispose() }
    if ($rotated) { $rotated.Dispose() }
    if ([IO.Directory]::Exists($fixture)) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
