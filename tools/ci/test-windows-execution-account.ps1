# This probe provisions a synthetic account on a disposable GitHub-hosted VM.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'The execution account probe requires a disposable GitHub-hosted Windows runner.'
}

$project = 'src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj'
& dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$root = Join-Path $env:RUNNER_TEMP ('netratel-execution-account-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$runnerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls $root /inheritance:r /grant:r "*${runnerSid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict access to the disposable fixture.' }

$taskName = 'NetRatelSavedAccountTest'
$username = 'netratel-ci-exec'
try {
    $password = 'A9!' + [Guid]::NewGuid().ToString('N')
    $account = New-LocalUser -Name $username -Password (ConvertTo-SecureString $password -AsPlainText -Force)
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $account
    $policy = Join-Path $root 'user-rights.inf'
    & secedit /export /cfg $policy /areas USER_RIGHTS /quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not read disposable VM account rights.' }
    $lines = [System.Collections.Generic.List[string]]::new([string[]](Get-Content $policy))
    foreach ($right in @('SeBatchLogonRight', 'SeInteractiveLogonRight')) {
        $index = $lines.FindIndex([Predicate[string]]{ param($line) $line.StartsWith("$right =") })
        if ($index -ge 0) { $lines[$index] += ",*$($account.SID.Value)" }
        else { $lines.Insert($lines.IndexOf('[Privilege Rights]') + 1, "$right = *$($account.SID.Value)") }
    }
    $lines | Set-Content $policy -Encoding Unicode
    & secedit /configure /db (Join-Path $root 'user-rights.sdb') /cfg $policy /areas USER_RIGHTS /quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the synthetic account rights.' }

    @{
        Username = $username
        AccountId = $account.SID.Value
        Password = $password
        Dotnet = (Get-Command dotnet).Source
        Assembly = (Resolve-Path 'src/NetRatel/NetRatel.Tests/bin/Release/net10.0/NetRatel.Tests.dll').Path
    } | ConvertTo-Json | Set-Content (Join-Path $root 'fixture.json')
    $driver = Join-Path $root 'run.ps1'
    @'
$ErrorActionPreference = 'Stop'
$fixture = Get-Content (Join-Path $PSScriptRoot 'fixture.json') -Raw | ConvertFrom-Json
$env:NETRATEL_TEST_EXECUTION_USER = $fixture.Username
$env:NETRATEL_TEST_EXECUTION_ACCOUNT = $fixture.AccountId
$env:NETRATEL_TEST_EXECUTION_PASSWORD = $fixture.Password
& $fixture.Dotnet $fixture.Assembly -class NetRatel.Tests.Client.SavedAccountExecutionTests -noColor `
    -xml (Join-Path $PSScriptRoot 'identity.xml') *> (Join-Path $PSScriptRoot 'test.log')
$LASTEXITCODE | Set-Content (Join-Path $PSScriptRoot 'exit')
'@ | Set-Content $driver
    $action = New-ScheduledTaskAction -Execute (Get-Command pwsh).Source -Argument "-NoProfile -NonInteractive -File `"$driver`""
    $null = Register-ScheduledTask -TaskName $taskName -Action $action -User 'SYSTEM' -RunLevel Highest
    Start-ScheduledTask -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    $status = Join-Path $root 'exit'
    while (-not (Test-Path $status) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    $log = Join-Path $root 'test.log'
    if (Test-Path $log) { (Get-Content -Raw $log).Replace($password, '[redacted]') | Write-Host }
    if (-not (Test-Path $status) -or (Get-Content $status -Raw).Trim() -ne '0') {
        throw 'The synthetic Windows job and terminal identity probe failed.'
    }
    [xml]$receipt = Get-Content (Join-Path $root 'identity.xml') -Raw
    if ($receipt.assemblies.assembly.passed -ne '1' -or $receipt.assemblies.assembly.total -ne '1') {
        throw 'The native identity test did not execute successfully.'
    }
}
finally {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if (Get-LocalUser -Name $username -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $username }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
