[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $receiptRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $ReceiptPath = Join-Path $receiptRoot 'netratel-windows-hosted-prerequisites.json'
}

$script:checks = [ordered]@{
    windows_runner = 'not_run'
    verified_vendor_assets = 'not_run'
    x64_postgresql_execution = 'not_run'
    fresh_postgresql_migrations = 'not_run'
    actual_api_bootstrap_listener = 'not_run'
    native_traefik_trusted_https = 'not_run'
    cleanup = 'not_run'
}
$script:currentCheck = $null
$script:failureCode = $null
$script:cleanup = [ordered]@{
    postgresql_stopped = $true
    api_process_stopped = $true
    traefik_process_stopped = $true
    local_user_removed = $true
    hosts_entry_removed = $true
    trust_certificates_removed = $true
    task_files_removed = $true
    captured_processes_stopped = $true
}
$script:failureDetails = [System.Collections.Generic.List[object]]::new()
$script:trackedProcesses = [System.Collections.Generic.List[object]]::new()
$script:pendingCapturedProcesses = [System.Collections.Generic.List[object]]::new()
$script:postgresInitialized = $false
$script:postgresStartAttempted = $false
$script:postgresIdentity = $null
$script:postgresIdentityDiagnostic = $null
$script:postgresCredentialDisposed = $false
$script:localUserCreated = $false
$script:postgresCredential = $null
$script:securePassword = $null
$script:taskRoot = $null
$script:apiProcess = $null
$script:traefikProcess = $null
$script:httpClient = $null
$script:hostsPath = $null
$script:hostsBlock = $null
$script:rootCertificateThumbprint = $null
$script:serverCertificateThumbprint = $null
$script:rootCertificate = $null
$script:serverCertificate = $null
$script:currentOperation = 'runner_preflight'
$script:lastProcessExitCode = $null
$script:safeFailureCodes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
[void]$script:safeFailureCodes.UnionWith([string[]]@(
    'administrator_runner_required', 'api_bootstrap_created_operational_identity', 'api_bootstrap_isolation_mismatch',
    'api_bootstrap_liveness_mismatch', 'api_descriptor_not_unconfigured', 'api_process_exited_before_listener',
    'api_publish_output_missing', 'api_setup_status_unavailable', 'arm64_runner_required', 'bootstrap_state_not_fresh',
    'checked_out_source_does_not_match_test_merge_sha', 'database_test_user_is_elevated', 'github_hosted_runner_required',
    'github_runner_paths_required', 'https_api_not_in_unconfigured_bootstrap_state', 'https_bootstrap_liveness_mismatch',
    'https_route_timeout', 'listener_process_exited', 'owned_hosts_entry_not_found', 'owned_process_start_failed', 'owned_trust_certificate_removal_unverified',
    'postgres_command_line_directory_contract_failed', 'postgres_owned_process_identity_changed', 'postgres_pid_file_data_directory_mismatch', 'postgres_pid_file_invalid',
    'postgres_process_identity_mismatch', 'postgres_running_identity_unavailable', 'postgres_start_not_observed',
    'postgres_started_identity_unavailable', 'postgres_status_unknown', 'postgres_stop_ownership_unavailable',
    'postgres_stopped_status_conflicts_with_live_owned_process', 'postgres_stopped_status_conflicts_with_tracked_process',
    'postgresql_migrations_not_observed', 'postgresql_version_mismatch', 'process_exit_nonzero',
    'process_output_policy_not_allowed', 'process_output_pipe_timeout', 'process_start_failed', 'process_timeout',
    'product_version_resolution_failed',
    'repository_checkout_path_mismatch', 'runner_temp_required', 'source_provenance_missing',
    'task_root_ownership_check_failed', 'traefik_version_or_architecture_mismatch', 'vendor_archive_digest_mismatch',
    'vendor_archive_layout_unexpected', 'windows_11_required', 'windows_required'
))
$script:operatingSystem = 'unknown'
$script:sourceSha = 'unknown'
$script:testMergeSha = 'unknown'
$script:productVersion = 'unknown'
$script:postgresVerified = $false
$script:traefikVerified = $false
$script:osArchitecture = 'unknown'
$script:traefikPort = 443

function Add-SafeFailureDetail {
    param(
        [Parameter(Mandatory)][string]$Check,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$ExceptionCategory,
        [Nullable[int]]$ExitCode,
        [string]$SafeCode
    )
    $detail = [ordered]@{
        check = $Check
        operation = $Operation
        exceptionCategory = $ExceptionCategory
    }
    if ($null -ne $ExitCode) { $detail.exitCode = [int]$ExitCode }
    if (-not [string]::IsNullOrWhiteSpace($SafeCode) -and $script:safeFailureCodes.Contains($SafeCode)) { $detail.safeCode = $SafeCode }
    $script:failureDetails.Add($detail)
}

function Add-CleanupFailure {
    param([Parameter(Mandatory)][string]$Operation, [Parameter(Mandatory)][string]$ExceptionCategory)
    Add-SafeFailureDetail -Check 'cleanup' -Operation $Operation -ExceptionCategory $ExceptionCategory -ExitCode $null
    if ($null -eq $script:failureCode) { $script:failureCode = 'owned_resource_cleanup' }
}

function Invoke-QualificationCheck {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)
    $script:currentCheck = $Name
    $script:currentOperation = "check:$Name"
    $script:lastProcessExitCode = $null
    $script:checks[$Name] = 'running'
    Write-Host "Running hosted prerequisite check: $Name"
    try {
        $null = & $Action
        $script:checks[$Name] = 'passed'
    }
    catch {
        $script:checks[$Name] = 'failed'
        $script:failureCode = $Name
        $safeCode = $_.Exception.GetBaseException().Message
        Add-SafeFailureDetail -Check $Name -Operation $script:currentOperation -ExceptionCategory $_.Exception.GetBaseException().GetType().Name -ExitCode $script:lastProcessExitCode -SafeCode $safeCode
        throw [InvalidOperationException]::new('A hosted prerequisite check failed.')
    }
    finally { $script:currentCheck = $null }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
}

function Assert-LoopbackPortFree {
    param([Parameter(Mandatory)][int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() } finally { $listener.Stop() }
}

function Get-ProcessStartInfo {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [string]$WorkingDirectory = $script:taskRoot,
        [System.Collections.IDictionary]$Environment = @{},
        [PSCredential]$Credential
    )
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $info.Environment[[string]$name] = [string]$Environment[$name] }
    if ($null -ne $Credential) {
        $info.UserName = $Credential.UserName
        $info.Domain = '.'
        $info.Password = $Credential.Password
        $info.LoadUserProfile = $true
    }
    return $info
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [int]$TimeoutSeconds = 180,
        [string]$WorkingDirectory = $script:taskRoot,
        [System.Collections.IDictionary]$Environment = @{},
        [PSCredential]$Credential,
        [switch]$AllowNonZeroExitCode,
        [switch]$PgCtlStartWithInheritedOutputPipes
    )
    $skipOutputPipeDrain = $false
    if ($PgCtlStartWithInheritedOutputPipes) {
        $expectedPgCtl = [System.IO.Path]::GetFullPath($script:pgCtlExe)
        $actualExecutable = [System.IO.Path]::GetFullPath($FilePath)
        $logArgumentIndex = [Array]::IndexOf([string[]]$ArgumentList, '-l')
        $hasPrivateServerLog = $logArgumentIndex -ge 0 -and
            $logArgumentIndex + 1 -lt $ArgumentList.Length -and
            -not [string]::IsNullOrWhiteSpace($ArgumentList[$logArgumentIndex + 1])
        if ($hasPrivateServerLog -and -not [string]::IsNullOrWhiteSpace($script:pgLog)) {
            $expectedServerLog = [System.IO.Path]::GetFullPath($script:pgLog)
            $actualServerLog = [System.IO.Path]::GetFullPath($ArgumentList[$logArgumentIndex + 1])
            $hasPrivateServerLog = $actualServerLog -ieq $expectedServerLog
        }
        else { $hasPrivateServerLog = $false }
        $hasWait = [Array]::IndexOf([string[]]$ArgumentList, '-w') -ge 0
        $hasSilent = [Array]::IndexOf([string[]]$ArgumentList, '-s') -ge 0
        if ($actualExecutable -ine $expectedPgCtl -or
            $ArgumentList.Length -eq 0 -or $ArgumentList[-1] -cne 'start' -or
            -not $hasPrivateServerLog -or -not $hasWait -or -not $hasSilent) {
            throw 'process_output_policy_not_allowed'
        }
        $skipOutputPipeDrain = $true
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = Get-ProcessStartInfo -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -Environment $Environment -Credential $Credential
    $script:currentOperation = "process:$([System.IO.Path]::GetFileName($FilePath))"
    $script:lastProcessExitCode = $null
    $keepProcessHandle = $false
    $processId = $null
    $processStartTicks = $null
    $outputTasks = @()
    try {
        if (-not $process.Start()) { throw 'process_start_failed' }
        $processId = $process.Id
        $processStartTicks = $process.StartTime.ToUniversalTime().Ticks
        # On Windows pg_ctl start launches a shell that inherits these redirected handles.
        # Its -l target is the private server log and -s suppresses routine pg_ctl output, but
        # the daemon can keep pipe handles open after pg_ctl exits. Avoid creating EOF readers
        # only for this validated start; Process.Dispose closes the parent pipes with no reader
        # tasks left running. The bounded parent exit and subsequent status/PID checks still apply.
        if (-not $skipOutputPipeDrain) {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $outputTasks = @($stdoutTask, $stderrTask)
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            $exited = $process.WaitForExit(10000)
            $streamsClosed = $skipOutputPipeDrain -or [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$outputTasks, 10000)
            if (-not $exited -or -not $streamsClosed) {
                $keepProcessHandle = $true
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add([pscustomobject]@{
                    Role = "captured:$([System.IO.Path]::GetFileName($FilePath))"
                    Process = $process
                    ProcessId = $processId
                    StartTimeTicks = $processStartTicks
                    OutputTasks = $outputTasks
                })
            }
            throw 'process_timeout'
        }
        $streamsClosed = $skipOutputPipeDrain -or [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$outputTasks, 10000)
        if (-not $streamsClosed) {
            try { $process.Kill($true) } catch { }
            $exited = $process.WaitForExit(10000)
            $streamsClosed = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$outputTasks, 10000)
            if (-not $exited -or -not $streamsClosed) {
                $keepProcessHandle = $true
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add([pscustomobject]@{
                    Role = "captured:$([System.IO.Path]::GetFileName($FilePath))"
                    Process = $process
                    ProcessId = $processId
                    StartTimeTicks = $processStartTicks
                    OutputTasks = $outputTasks
                })
            }
            throw 'process_output_pipe_timeout'
        }
        if ($skipOutputPipeDrain) {
            $stdout = ''
            $stderr = ''
        }
        else {
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
        }
        $script:lastProcessExitCode = $process.ExitCode
        if ($process.ExitCode -ne 0 -and -not $AllowNonZeroExitCode) { throw 'process_exit_nonzero' }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $stdout; StandardError = $stderr }
    }
    finally {
        if (-not $keepProcessHandle) { $process.Dispose() }
    }
}

function Start-OwnedProcess {
    param(
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [System.Collections.IDictionary]$Environment = @{}
    )
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = Get-ProcessStartInfo -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -Environment $Environment
    if (-not $process.Start()) { $process.Dispose(); throw 'owned_process_start_failed' }
    $record = [pscustomobject]@{
        Role = $Role
        Process = $process
        ProcessId = $process.Id
        StartTimeTicks = $process.StartTime.ToUniversalTime().Ticks
        OutputTasks = @($process.StandardOutput.ReadToEndAsync(), $process.StandardError.ReadToEndAsync())
    }
    $script:trackedProcesses.Add($record)
    if ($Role -eq 'api') { $script:cleanup.api_process_stopped = $false }
    elseif ($Role -eq 'traefik') { $script:cleanup.traefik_process_stopped = $false }
    return $record
}

function Stop-OwnedProcess {
    param([Parameter(Mandatory)][object]$Record)
    $process = $Record.Process
    try {
        $process.Refresh()
        if (-not $process.HasExited) {
            if ($process.Id -ne $Record.ProcessId -or $process.StartTime.ToUniversalTime().Ticks -ne $Record.StartTimeTicks) { return $false }
            $process.Kill($true)
            if (-not $process.WaitForExit(15000)) { return $false }
        }
        if ($Record.PSObject.Properties.Name -contains 'OutputTasks' -and $Record.OutputTasks.Count -gt 0) {
            if (-not [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$Record.OutputTasks, 10000)) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Assert-ArchiveSha256 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Expected) { throw 'vendor_archive_digest_mismatch' }
}

function Get-CanonicalWindowsPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Get-PostgresDataDirectoryFromCommandLine {
    param([AllowNull()][string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }

    # pg_ctl on Windows emits postgres.exe followed by a quoted -D value. Parse that
    # argument instead of searching the command line for a path substring: PostgreSQL
    # canonicalizes the value and may use forward slashes on Windows.
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $CommandLine,
        '^(?:"[^"]+"|\S+)\s+-D\s+(?:"(?<quoted>[^"\r\n]+)"|(?<unquoted>[^\s"\r\n]+))(?=\s|$)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) { return $null }

    $directory = if ($match.Groups['quoted'].Success) { $match.Groups['quoted'].Value } else { $match.Groups['unquoted'].Value }
    try { return Get-CanonicalWindowsPath ($directory.Replace('/', '\')) }
    catch { return $null }
}

function Test-PostgresDataDirectoryCommandLine {
    param(
        [AllowNull()][string]$CommandLine,
        [Parameter(Mandatory)][string]$ExpectedDataDirectory
    )
    $actualDataDirectory = Get-PostgresDataDirectoryFromCommandLine -CommandLine $CommandLine
    if ([string]::IsNullOrWhiteSpace($actualDataDirectory)) { return $false }
    return $actualDataDirectory -ieq (Get-CanonicalWindowsPath $ExpectedDataDirectory)
}

function Assert-PostgresDataDirectoryCommandLineContract {
    $expectedDataDirectory = 'C:\NetRatel qualification\postgres data'
    $compactDataDirectory = 'C:\NetRatel\data'
    $quotedExecutable = '"C:\PostgreSQL binaries\pgsql\bin\postgres.exe"'
    $cases = @(
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D `"C:/NetRatel qualification/postgres data`" -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $true },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D `"C:\NetRatel qualification\postgres data`" -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $true },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D C:/NetRatel/data -p 55432"; ExpectedDataDirectory = $compactDataDirectory; Expected = $true },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D C:/NetRatel/postgres-data -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D `"C:/NetRatel qualification/postgres data-old`" -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false },
        [pscustomobject]@{ CommandLine = "$quotedExecutable --config `"C:/NetRatel qualification/postgres data`""; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false },
        [pscustomobject]@{ CommandLine = ''; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false },
        [pscustomobject]@{ CommandLine = "$quotedExecutable -D `"C:/NetRatel qualification/postgres data -p 55432"; ExpectedDataDirectory = $expectedDataDirectory; Expected = $false }
    )
    foreach ($case in $cases) {
        if ((Test-PostgresDataDirectoryCommandLine -CommandLine $case.CommandLine -ExpectedDataDirectory $case.ExpectedDataDirectory) -ne $case.Expected) {
            throw 'postgres_command_line_directory_contract_failed'
        }
    }
}

function Get-PostgresIdentityFromPidFile {
    $pidFile = Join-Path $script:pgDataDirectory 'postmaster.pid'
    if (-not (Test-Path -LiteralPath $pidFile -PathType Leaf)) { return $null }
    $lines = [System.IO.File]::ReadAllLines($pidFile)
    if ($lines.Length -lt 2 -or $lines[0] -notmatch '^[1-9][0-9]*$' -or [string]::IsNullOrWhiteSpace($lines[1])) {
        throw 'postgres_pid_file_invalid'
    }
    $processId = [int]$lines[0]
    $reportedDataDirectory = Get-CanonicalWindowsPath $lines[1]
    $expectedDataDirectory = Get-CanonicalWindowsPath $script:pgDataDirectory
    if ($reportedDataDirectory -ine $expectedDataDirectory) { throw 'postgres_pid_file_data_directory_mismatch' }

    return Get-PostgresIdentityByProcessId -ProcessId $processId
}

function Get-PostgresIdentityByProcessId {
    param([Parameter(Mandatory)][int]$ProcessId)
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
    if ($null -eq $process) { return $null }
    $expectedExecutable = Get-CanonicalWindowsPath $script:postgresExe
    $expectedDataDirectory = Get-CanonicalWindowsPath $script:pgDataDirectory
    $executablePathPresent = -not [string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)
    $executablePathMatchesExpected = $false
    if ($executablePathPresent) {
        try { $executablePathMatchesExpected = (Get-CanonicalWindowsPath ([string]$process.ExecutablePath)) -ieq $expectedExecutable }
        catch { $executablePathMatchesExpected = $false }
    }
    $commandLinePresent = -not [string]::IsNullOrWhiteSpace([string]$process.CommandLine)
    $commandLineDataDirectoryMatches = $false
    if ($commandLinePresent) {
        $commandLineDataDirectoryMatches = Test-PostgresDataDirectoryCommandLine -CommandLine ([string]$process.CommandLine) -ExpectedDataDirectory $expectedDataDirectory
    }
    $creationDatePresent = $null -ne $process.CreationDate
    if (-not $executablePathMatchesExpected -or -not $commandLineDataDirectoryMatches -or -not $creationDatePresent) {
        if ($null -eq $script:postgresIdentityDiagnostic) {
            $script:postgresIdentityDiagnostic = [ordered]@{
                processFound = $true
                executablePathPresent = [bool]$executablePathPresent
                executablePathMatchesExpected = [bool]$executablePathMatchesExpected
                commandLinePresent = [bool]$commandLinePresent
                commandLineDataDirectoryMatches = [bool]$commandLineDataDirectoryMatches
                creationDatePresent = [bool]$creationDatePresent
            }
        }
        throw 'postgres_process_identity_mismatch'
    }
    return [pscustomobject]@{
        ProcessId = $ProcessId
        StartTimeTicks = $process.CreationDate.ToUniversalTime().Ticks
        ExecutablePath = $expectedExecutable
        DataDirectory = $expectedDataDirectory
    }
}

function Assert-SamePostgresIdentity {
    param([Parameter(Mandatory)][object]$Expected)
    $current = Get-PostgresIdentityFromPidFile
    if ($null -eq $current -or $current.ProcessId -ne $Expected.ProcessId -or
        $current.StartTimeTicks -ne $Expected.StartTimeTicks -or
        $current.ExecutablePath -ine $Expected.ExecutablePath -or
        $current.DataDirectory -ine $Expected.DataDirectory) {
        throw 'postgres_owned_process_identity_changed'
    }
    return $current
}

function Get-PostgresCtlStatus {
    return Invoke-CapturedProcess -FilePath $script:pgCtlExe -ArgumentList @('-D', $script:pgDataDirectory, '-t', '10', 'status') -TimeoutSeconds 20 -WorkingDirectory $script:pgBin -Credential $script:postgresCredential -AllowNonZeroExitCode
}

function Confirm-PostgresStopped {
    $status = Get-PostgresCtlStatus
    if ($status.ExitCode -eq 0) {
        $identity = Get-PostgresIdentityFromPidFile
        if ($null -eq $identity) { throw 'postgres_running_identity_unavailable' }
        if ($null -ne $script:postgresIdentity) { $null = Assert-SamePostgresIdentity -Expected $script:postgresIdentity }
        else { $script:postgresIdentity = $identity }
        $null = Invoke-CapturedProcess -FilePath $script:pgCtlExe -ArgumentList @('-D', $script:pgDataDirectory, '-m', 'fast', '-w', '-t', '30', 'stop') -TimeoutSeconds 45 -WorkingDirectory $script:pgBin -Credential $script:postgresCredential
        $status = Get-PostgresCtlStatus
    }

    if ($status.ExitCode -eq 3) {
        $pidFile = Join-Path $script:pgDataDirectory 'postmaster.pid'
        if (Test-Path -LiteralPath $pidFile -PathType Leaf) {
            $identity = Get-PostgresIdentityFromPidFile
            if ($null -ne $identity) { throw 'postgres_stopped_status_conflicts_with_live_owned_process' }
        }
        if ($null -ne $script:postgresIdentity) {
            $identity = Get-PostgresIdentityByProcessId -ProcessId $script:postgresIdentity.ProcessId
            if ($null -ne $identity) {
                throw 'postgres_stopped_status_conflicts_with_tracked_process'
            }
        }
        $script:cleanup.postgresql_stopped = $true
        return
    }
    throw 'postgres_status_unknown'
}

function Add-DirectoryAccessRule {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Security.Principal.SecurityIdentifier]$Identity,
        [Parameter(Mandatory)][System.Security.AccessControl.FileSystemRights]$Rights,
        [System.Security.AccessControl.InheritanceFlags]$Inheritance = [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]$Propagation = [System.Security.AccessControl.PropagationFlags]::None
    )
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($Identity, $Rights, $Inheritance, $Propagation, [System.Security.AccessControl.AccessControlType]::Allow)
    [void]$acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
}

function Protect-TaskRoot {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $acl.SetAccessRuleProtection($true, $false)
    $inheritable = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $allowedSids = @(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    )
    foreach ($sid in $allowedSids) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($sid, [System.Security.AccessControl.FileSystemRights]::FullControl, $inheritable, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.SetAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
}

function Add-OwnedHostsEntry {
    param([Parameter(Mandatory)][string]$HostName, [Parameter(Mandatory)][string]$Marker)
    $contents = [System.IO.File]::ReadAllBytes($script:hostsPath)
    $text = [System.Text.Encoding]::ASCII.GetString($contents)
    $separator = if ($contents.Length -gt 0 -and $text[$text.Length - 1] -ne [char]10) { [string][char]13 + [string][char]10 } else { '' }
    $blockText = $separator + '127.0.0.1 ' + $HostName + ' # ' + $Marker + [string][char]13 + [string][char]10
    $block = [System.Text.Encoding]::ASCII.GetBytes($blockText)
    $stream = [System.IO.File]::Open($script:hostsPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try { $stream.Write($block, 0, $block.Length) } finally { $stream.Dispose() }
    $script:hostsBlock = $block
    $script:cleanup.hosts_entry_removed = $false
}

function Remove-OwnedHostsEntry {
    if ($null -eq $script:hostsBlock -or -not [System.IO.File]::Exists($script:hostsPath)) { return $false }
    $contents = [System.IO.File]::ReadAllBytes($script:hostsPath)
    $block = [byte[]]$script:hostsBlock
    $match = -1
    for ($offset = 0; $offset -le $contents.Length - $block.Length; $offset++) {
        $same = $true
        for ($index = 0; $index -lt $block.Length; $index++) {
            if ($contents[$offset + $index] -ne $block[$index]) { $same = $false; break }
        }
        if ($same) { $match = $offset; break }
    }
    if ($match -lt 0) { return $false }
    $remaining = [byte[]]::new($contents.Length - $block.Length)
    if ($match -gt 0) { [Array]::Copy($contents, 0, $remaining, 0, $match) }
    $after = $match + $block.Length
    if ($after -lt $contents.Length) { [Array]::Copy($contents, $after, $remaining, $match, $contents.Length - $after) }
    [System.IO.File]::WriteAllBytes($script:hostsPath, $remaining)
    $script:hostsBlock = $null
    return $true
}

function Wait-ForHttpsResponse {
    param([Parameter(Mandatory)][System.Net.Http.HttpClient]$Client, [Parameter(Mandatory)][string]$Uri, [int]$TimeoutSeconds = 45)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:apiProcess.Process.HasExited -or $script:traefikProcess.Process.HasExited) { throw 'listener_process_exited' }
        try {
            $response = $Client.GetAsync($Uri).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) {
                    return [pscustomobject]@{
                        StatusCode = [int]$response.StatusCode
                        Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    }
                }
            }
            finally { $response.Dispose() }
        }
        catch [System.Net.Http.HttpRequestException] { }
        catch [System.Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 300
    }
    throw 'https_route_timeout'
}

function Remove-TrustedCertificate {
    param([Parameter(Mandatory)][string]$StoreName, [Parameter(Mandatory)][string]$Thumbprint)
    $storePath = "Cert:\CurrentUser\$StoreName"
    $matches = @(Get-ChildItem -LiteralPath $storePath -ErrorAction Stop | Where-Object { $_.Thumbprint -eq $Thumbprint })
    foreach ($certificate in $matches) {
        if ($StoreName -eq 'My') {
            Remove-Item -LiteralPath $certificate.PSPath -Force -DeleteKey -ErrorAction Stop
        }
        else {
            Remove-Item -LiteralPath $certificate.PSPath -Force -ErrorAction Stop
        }
    }
    $remaining = @(Get-ChildItem -LiteralPath $storePath -ErrorAction Stop | Where-Object { $_.Thumbprint -eq $Thumbprint })
    if ($remaining.Count -gt 0) { throw 'owned_trust_certificate_removal_unverified' }
}

function Write-QualificationReceipt {
    $receipt = [ordered]@{
        schemaVersion = 1
        scope = 'windows-hosted-prerequisites'
        integratedAcceptance = 'not_run'
        runner = [ordered]@{
            operatingSystem = $script:operatingSystem
            operatingSystemArchitecture = [string]$script:osArchitecture
            processArchitecture = [string][System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
        }
        provenance = [ordered]@{
            sourceSha = $script:sourceSha
            testMergeSha = $script:testMergeSha
            productVersion = $script:productVersion
        }
        assets = [ordered]@{
            postgresql = [ordered]@{
                version = '17.11-4'
                sha256 = 'b9424ee7bc60b52450ff910a3630225df32e633f3cb29c1d126d9299d59aea28'
                verifiedBeforeExecution = $script:postgresVerified
            }
            traefik = [ordered]@{
                version = '3.7.13'
                architecture = 'windows-arm64'
                sha256 = 'b742323dc327e4a6eed0659b52109fb180ab48bc228075790401a669a11aa8c3'
                verifiedBeforeExecution = $script:traefikVerified
            }
        }
        checks = $script:checks
        cleanup = $script:cleanup
        postgresIdentityDiagnostic = $script:postgresIdentityDiagnostic
        failedCheck = $script:failureCode
        failureDetails = @($script:failureDetails)
    }
    $parent = Split-Path -Parent $ReceiptPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    [System.IO.File]::WriteAllText($ReceiptPath, (ConvertTo-Json -InputObject $receipt -Depth 8) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

try {
    Invoke-QualificationCheck 'windows_runner' {
        if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
            throw 'github_hosted_runner_required'
        }
        if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or [string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) { throw 'github_runner_paths_required' }
        if ([System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\') -cne [System.IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\')) { throw 'repository_checkout_path_mismatch' }
        if ($env:NETRATEL_REVIEW_SOURCE_SHA -notmatch '^[0-9a-fA-F]{40}$' -or $env:NETRATEL_REVIEW_TEST_MERGE_SHA -notmatch '^[0-9a-fA-F]{40}$') { throw 'source_provenance_missing' }
        $script:sourceSha = $env:NETRATEL_REVIEW_SOURCE_SHA.ToLowerInvariant()
        $script:testMergeSha = $env:NETRATEL_REVIEW_TEST_MERGE_SHA.ToLowerInvariant()
        $git = (Get-Command git -ErrorAction Stop).Source
        $checkoutSha = (Invoke-CapturedProcess -FilePath $git -ArgumentList @('-C', $RepositoryRoot, 'rev-parse', 'HEAD') -TimeoutSeconds 15 -WorkingDirectory $RepositoryRoot).StandardOutput.Trim().ToLowerInvariant()
        if ($checkoutSha -cne $script:testMergeSha) { throw 'checked_out_source_does_not_match_test_merge_sha' }
        $python = Get-Command python -ErrorAction SilentlyContinue
        $pythonArguments = @()
        if ($null -eq $python) {
            $python = Get-Command py -ErrorAction Stop
            $pythonArguments = @('-3')
        }
        $versionScript = Join-Path $RepositoryRoot 'tools/ci/product-version.py'
        $versionEnvironment = @{ 'NETRATEL_RELEASE_SOURCE_ROOT' = $RepositoryRoot }
        $script:productVersion = (Invoke-CapturedProcess -FilePath $python.Source -ArgumentList ($pythonArguments + @($versionScript)) -TimeoutSeconds 30 -WorkingDirectory $RepositoryRoot -Environment $versionEnvironment).StandardOutput.Trim()
        if ($script:productVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'product_version_resolution_failed' }
        if (-not $IsWindows) { throw 'windows_required' }
        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        if ($null -ne $os) { $script:operatingSystem = "$($os.Caption) $($os.Version)" }
        if ($os.Caption -notmatch 'Windows 11' -or [version]$os.Version -lt [version]'10.0.22000') { throw 'windows_11_required' }
        $script:osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        if ($script:osArchitecture -ne [System.Runtime.InteropServices.Architecture]::Arm64 -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [System.Runtime.InteropServices.Architecture]::Arm64) {
            throw 'arm64_runner_required'
        }
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'administrator_runner_required' }
        Assert-PostgresDataDirectoryCommandLineContract
        if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'runner_temp_required' }
        $script:hostsPath = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
        Assert-LoopbackPortFree -Port $script:traefikPort
        $runId = [Guid]::NewGuid().ToString('N')
        $script:taskRoot = Join-Path $env:RUNNER_TEMP "netratel-hosted-prereq-$runId"
        [System.IO.Directory]::CreateDirectory($script:taskRoot) | Out-Null
        $script:cleanup.task_files_removed = $false
        $script:currentOperation = 'protect-owned-task-directory'
        Protect-TaskRoot -Path $script:taskRoot
        $script:hostname = "netratel-prereq-$runId.invalid"
        $script:apiPort = Get-FreeLoopbackPort
        do { $script:postgresPort = Get-FreeLoopbackPort } while ($script:postgresPort -eq $script:apiPort)
    }

    Invoke-QualificationCheck 'verified_vendor_assets' {
        $downloads = Join-Path $script:taskRoot 'downloads'
        $script:postgresExtract = Join-Path $script:taskRoot 'postgresql'
        $traefikExtract = Join-Path $script:taskRoot 'traefik'
        [System.IO.Directory]::CreateDirectory($downloads) | Out-Null
        [System.IO.Directory]::CreateDirectory($script:postgresExtract) | Out-Null
        [System.IO.Directory]::CreateDirectory($traefikExtract) | Out-Null
        $postgresArchive = Join-Path $downloads 'postgresql-17.11-4-windows-x64-binaries.zip'
        $traefikArchive = Join-Path $downloads 'traefik_v3.7.13_windows_arm64.zip'
        Invoke-WebRequest -Uri 'https://get.enterprisedb.com/postgresql/postgresql-17.11-4-windows-x64-binaries.zip' -OutFile $postgresArchive -TimeoutSec 240
        Assert-ArchiveSha256 -Path $postgresArchive -Expected 'b9424ee7bc60b52450ff910a3630225df32e633f3cb29c1d126d9299d59aea28'
        $script:postgresVerified = $true
        Expand-Archive -LiteralPath $postgresArchive -DestinationPath $script:postgresExtract
        Invoke-WebRequest -Uri 'https://github.com/traefik/traefik/releases/download/v3.7.13/traefik_v3.7.13_windows_arm64.zip' -OutFile $traefikArchive -TimeoutSec 180
        Assert-ArchiveSha256 -Path $traefikArchive -Expected 'b742323dc327e4a6eed0659b52109fb180ab48bc228075790401a669a11aa8c3'
        $script:traefikVerified = $true
        Expand-Archive -LiteralPath $traefikArchive -DestinationPath $traefikExtract

        # The EDB ZIP also includes pgAdmin's private psql.exe. Use the PostgreSQL
        # server tools from the canonical core bin directory instead of requiring
        # executable basenames to be unique across the entire vendor archive.
        $script:pgBin = Join-Path $script:postgresExtract 'pgsql\bin'
        $script:initdbExe = Join-Path $script:pgBin 'initdb.exe'
        $script:postgresExe = Join-Path $script:pgBin 'postgres.exe'
        $script:pgCtlExe = Join-Path $script:pgBin 'pg_ctl.exe'
        $script:createdbExe = Join-Path $script:pgBin 'createdb.exe'
        $script:psqlExe = Join-Path $script:pgBin 'psql.exe'
        $postgresTools = @($script:initdbExe, $script:postgresExe, $script:pgCtlExe, $script:createdbExe, $script:psqlExe)
        $missingPostgresTools = @($postgresTools | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
        $traefik = @(Get-ChildItem -LiteralPath $traefikExtract -Filter 'traefik.exe' -File -Recurse)
        if ($missingPostgresTools.Count -ne 0 -or $traefik.Count -ne 1) {
            throw 'vendor_archive_layout_unexpected'
        }
        $script:traefikExe = $traefik[0].FullName
        $script:traefikWorkingDirectory = $traefik[0].DirectoryName
        $script:postgresVersion = (Invoke-CapturedProcess -FilePath $script:postgresExe -ArgumentList @('--version') -TimeoutSeconds 15 -WorkingDirectory $script:pgBin).StandardOutput.Trim()
        if ($script:postgresVersion -notmatch '^postgres \(PostgreSQL\) 17\.11\b') { throw 'postgresql_version_mismatch' }
        $script:traefikVersion = (Invoke-CapturedProcess -FilePath $script:traefikExe -ArgumentList @('version') -TimeoutSeconds 15 -WorkingDirectory $script:traefikWorkingDirectory).StandardOutput
        if ($script:traefikVersion -notmatch 'Version:\s+3\.7\.13' -or $script:traefikVersion -notmatch 'OS/Arch:\s+windows/arm64') { throw 'traefik_version_or_architecture_mismatch' }
    }

    Invoke-QualificationCheck 'x64_postgresql_execution' {
        Import-Module Microsoft.PowerShell.LocalAccounts -ErrorAction Stop
        $accountName = 'nrdb' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $randomBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $plainPassword = [Convert]::ToBase64String($randomBytes) + 'Aa1!'
        $securePassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force
        $script:securePassword = $securePassword
        [Array]::Clear($randomBytes, 0, $randomBytes.Length)
        $plainPassword = $null
        $script:localUser = New-LocalUser -Name $accountName -Password $securePassword -Description 'Temporary hosted database qualification account' -AccountNeverExpires
        $script:localUserCreated = $true
        $script:cleanup.local_user_removed = $false
        $administrators = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop | ForEach-Object { $_.SID.Value })
        if ($administrators -contains $script:localUser.SID.Value) { throw 'database_test_user_is_elevated' }

        Add-DirectoryAccessRule -Path $script:taskRoot -Identity $script:localUser.SID -Rights ([System.Security.AccessControl.FileSystemRights]::Traverse)
        Add-DirectoryAccessRule -Path $script:postgresExtract -Identity $script:localUser.SID -Rights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) -Inheritance ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit)
        $dataDirectory = Join-Path $script:taskRoot 'postgresql-data'
        [System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
        Add-DirectoryAccessRule -Path $dataDirectory -Identity $script:localUser.SID -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl) -Inheritance ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit)

        $script:postgresCredential = [PSCredential]::new($accountName, $securePassword)
        $script:pgDataDirectory = $dataDirectory
        $init = Invoke-CapturedProcess -FilePath $script:initdbExe -ArgumentList @("--pgdata=$dataDirectory", '--username=postgres', '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--no-instructions') -TimeoutSeconds 180 -WorkingDirectory $script:pgBin -Environment @{ TEMP = $dataDirectory; TMP = $dataDirectory } -Credential $script:postgresCredential
        $script:postgresInitialized = $true
        $script:pgLog = Join-Path $dataDirectory 'postgresql-server.log'
        $serverOptions = "-h 127.0.0.1 -p $($script:postgresPort) -c listen_addresses=127.0.0.1"
        $script:postgresStartAttempted = $true
        $script:cleanup.postgresql_stopped = $false
        $null = Invoke-CapturedProcess -FilePath $script:pgCtlExe -ArgumentList @('-D', $dataDirectory, '-l', $script:pgLog, '-o', $serverOptions, '-s', '-w', '-t', '45', 'start') -TimeoutSeconds 75 -WorkingDirectory $script:pgBin -Environment @{ TEMP = $dataDirectory; TMP = $dataDirectory } -Credential $script:postgresCredential -PgCtlStartWithInheritedOutputPipes
        $status = Get-PostgresCtlStatus
        if ($status.ExitCode -ne 0) { throw 'postgres_start_not_observed' }
        $script:postgresIdentity = Get-PostgresIdentityFromPidFile
        if ($null -eq $script:postgresIdentity) { throw 'postgres_started_identity_unavailable' }
        $null = Invoke-CapturedProcess -FilePath $script:createdbExe -ArgumentList @('-h', '127.0.0.1', '-p', [string]$script:postgresPort, '-U', 'postgres', 'netratel_prerequisites') -TimeoutSeconds 30 -WorkingDirectory $script:pgBin
        $script:pgConnectionString = "Host=127.0.0.1;Port=$($script:postgresPort);Database=netratel_prerequisites;Username=postgres;Timeout=5;Command Timeout=60;Include Error Detail=false;Pooling=false"
    }

    Invoke-QualificationCheck 'fresh_postgresql_migrations' {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $migrationProject = Join-Path $RepositoryRoot 'src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj'
        $migrationEnvironment = @{ 'ConnectionStrings__NetRatelDb' = $script:pgConnectionString }
        $null = Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @('run', '--project', $migrationProject, '--configuration', 'Release', '--verbosity', 'quiet') -TimeoutSeconds 900 -WorkingDirectory $RepositoryRoot -Environment $migrationEnvironment
        $query = 'SELECT CASE WHEN to_regclass(''public."Agents"'') IS NOT NULL AND to_regclass(''public."Tenants"'') IS NOT NULL AND to_regclass(''public."__EFMigrationsHistory"'') IS NOT NULL THEN ''migrated'' ELSE ''missing'' END'
        $queryResult = Invoke-CapturedProcess -FilePath $script:psqlExe -ArgumentList @('-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-h', '127.0.0.1', '-p', [string]$script:postgresPort, '-U', 'postgres', '-d', 'netratel_prerequisites', '-c', $query) -TimeoutSeconds 30 -WorkingDirectory $script:pgBin
        if ($queryResult.StandardOutput.Trim() -ne 'migrated') { throw 'postgresql_migrations_not_observed' }

        $apiProject = Join-Path $RepositoryRoot 'src/NetRatel/NetRatel.API/NetRatel.API.csproj'
        $script:apiPublishDirectory = Join-Path $script:taskRoot 'api-publish'
        $null = Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @('publish', $apiProject, '--configuration', 'Release', '--no-self-contained', '--output', $script:apiPublishDirectory, '--verbosity', 'quiet') -TimeoutSeconds 1200 -WorkingDirectory $RepositoryRoot
        $script:apiDll = Join-Path $script:apiPublishDirectory 'NetRatel.API.dll'
        if (-not (Test-Path -LiteralPath $script:apiDll -PathType Leaf)) { throw 'api_publish_output_missing' }
    }

    Invoke-QualificationCheck 'actual_api_bootstrap_listener' {
        $stateDirectory = Join-Path $script:taskRoot 'bootstrap-state'
        $keyDirectory = Join-Path $script:taskRoot 'data-protection-keys'
        [System.IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
        [System.IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
        if (Test-Path -LiteralPath (Join-Path $stateDirectory 'descriptor.json')) { throw 'bootstrap_state_not_fresh' }
        $apiEnvironment = @{
            'ASPNETCORE_ENVIRONMENT' = 'Production'
            'ConnectionStrings__NetRatelDb' = $script:pgConnectionString
            'Bootstrap__StateDirectory' = $stateDirectory
            'NetRatel_HTTP_PORT' = [string]$script:apiPort
            'DataProtection__KeysDirectory' = $keyDirectory
            'Branding__SiteUrl' = "https://$($script:hostname)"
        }
        $script:apiProcess = Start-OwnedProcess -Role 'api' -FilePath (Get-Command dotnet).Source -ArgumentList @($script:apiDll) -WorkingDirectory $script:apiPublishDirectory -Environment $apiEnvironment
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseProxy = $false
        $script:httpClient = [System.Net.Http.HttpClient]::new($handler)
        $script:httpClient.Timeout = [TimeSpan]::FromSeconds(4)

        $live = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($script:apiProcess.Process.HasExited) { throw 'api_process_exited_before_listener' }
            try {
                $response = $script:httpClient.GetAsync("http://127.0.0.1:$($script:apiPort)/health/live").GetAwaiter().GetResult()
                try {
                    if ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) {
                        $live = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                        break
                    }
                }
                finally { $response.Dispose() }
            }
            catch [System.Net.Http.HttpRequestException] { }
            catch [System.Threading.Tasks.TaskCanceledException] { }
            Start-Sleep -Milliseconds 300
        }
        if ($null -eq $live -or $live.status -ne 'alive' -or $live.lifecycle -ne 'bootstrap') { throw 'api_bootstrap_liveness_mismatch' }

        $statusResponse = $script:httpClient.GetAsync("http://127.0.0.1:$($script:apiPort)/api/v2/setup/status").GetAwaiter().GetResult()
        try {
            if ($statusResponse.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_setup_status_unavailable' }
            $setupStatus = $statusResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        }
        finally { $statusResponse.Dispose() }
        $descriptorPath = Join-Path $stateDirectory 'descriptor.json'
        if (-not $setupStatus.setupRequired -or $setupStatus.isReady -or $setupStatus.state -ne 0 -or
            -not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
            throw 'api_bootstrap_isolation_mismatch'
        }
        $descriptor = [System.IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
        if ($descriptor.state -ne 0) { throw 'api_descriptor_not_unconfigured' }
        $emptyStateQuery = 'SELECT (SELECT count(*) FROM "Agents")::text || ''|'' || (SELECT count(*) FROM "Tenants")::text || ''|'' || (SELECT count(*) FROM "LocalUsers")::text'
        $emptyState = Invoke-CapturedProcess -FilePath $script:psqlExe -ArgumentList @('-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-h', '127.0.0.1', '-p', [string]$script:postgresPort, '-U', 'postgres', '-d', 'netratel_prerequisites', '-c', $emptyStateQuery) -TimeoutSeconds 30 -WorkingDirectory $script:pgBin
        if ($emptyState.StandardOutput.Trim() -ne '0|0|0') { throw 'api_bootstrap_created_operational_identity' }
    }

    Invoke-QualificationCheck 'native_traefik_trusted_https' {
        $script:rootCertificate = New-SelfSignedCertificate -Subject "CN=NetRatel hosted qualification CA $([Guid]::NewGuid().ToString('N'))" -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddHours(4) -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
        $script:rootCertificateThumbprint = $script:rootCertificate.Thumbprint
        $script:cleanup.trust_certificates_removed = $false
        $script:serverCertificate = New-SelfSignedCertificate -Subject "CN=$($script:hostname)" -DnsName $script:hostname -Signer $script:rootCertificate -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature, KeyEncipherment -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddHours(2) -TextExtension @('2.5.29.19={critical}{text}ca=0', '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        $script:serverCertificateThumbprint = $script:serverCertificate.Thumbprint

        $certificatePath = Join-Path $script:taskRoot 'tls-certificate.pem'
        $privateKeyPath = Join-Path $script:taskRoot 'tls-private-key.pem'
        [System.IO.File]::WriteAllText($certificatePath, $script:serverCertificate.ExportCertificatePem(), [System.Text.UTF8Encoding]::new($false))
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($script:serverCertificate)
        try { [System.IO.File]::WriteAllText($privateKeyPath, $rsa.ExportPkcs8PrivateKeyPem(), [System.Text.UTF8Encoding]::new($false)) }
        finally { $rsa.Dispose() }

        $rootCertificatePath = Join-Path $script:taskRoot 'qualification-ca.cer'
        [System.IO.File]::WriteAllBytes($rootCertificatePath, $script:rootCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $script:currentOperation = 'trust-only-owned-root-certificate'
        Import-Certificate -FilePath $rootCertificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
        $script:currentOperation = 'append-exact-owned-hosts-entry'
        Add-OwnedHostsEntry -HostName $script:hostname -Marker "netratel-hosted-prerequisite-$($script:rootCertificateThumbprint.ToLowerInvariant())"

        $dynamicConfigPath = Join-Path $script:taskRoot 'traefik-dynamic.json'
        $staticConfigPath = Join-Path $script:taskRoot 'traefik-static.json'
        $dynamicConfig = [ordered]@{
            http = [ordered]@{
                routers = [ordered]@{ api = [ordered]@{ rule = 'Host("' + $script:hostname + '")'; entryPoints = @('websecure'); tls = [ordered]@{}; service = 'actual-api' } }
                services = [ordered]@{ 'actual-api' = [ordered]@{ loadBalancer = [ordered]@{ servers = @([ordered]@{ url = "http://127.0.0.1:$($script:apiPort)" }) } } }
            }
            tls = [ordered]@{ certificates = @([ordered]@{ certFile = $certificatePath; keyFile = $privateKeyPath }) }
        }
        $staticConfig = [ordered]@{
            entryPoints = [ordered]@{ websecure = [ordered]@{ address = "127.0.0.1:$($script:traefikPort)" } }
            providers = [ordered]@{ file = [ordered]@{ filename = $dynamicConfigPath; watch = $false } }
            log = [ordered]@{ level = 'ERROR' }
        }
        [System.IO.File]::WriteAllText($dynamicConfigPath, (ConvertTo-Json -InputObject $dynamicConfig -Depth 12), [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($staticConfigPath, (ConvertTo-Json -InputObject $staticConfig -Depth 6), [System.Text.UTF8Encoding]::new($false))
        $script:traefikProcess = Start-OwnedProcess -Role 'traefik' -FilePath $script:traefikExe -ArgumentList @("--configFile=$staticConfigPath") -WorkingDirectory $script:traefikWorkingDirectory

        $status = Wait-ForHttpsResponse -Client $script:httpClient -Uri "https://$($script:hostname)/api/v2/setup/status"
        $setupStatus = $status.Body | ConvertFrom-Json
        if (-not $setupStatus.setupRequired -or $setupStatus.isReady) { throw 'https_api_not_in_unconfigured_bootstrap_state' }
        $live = Wait-ForHttpsResponse -Client $script:httpClient -Uri "https://$($script:hostname)/health/live" -TimeoutSeconds 10
        $liveStatus = $live.Body | ConvertFrom-Json
        if ($liveStatus.status -ne 'alive' -or $liveStatus.lifecycle -ne 'bootstrap') { throw 'https_bootstrap_liveness_mismatch' }
    }
}
catch {
    if ([string]::IsNullOrWhiteSpace($script:failureCode)) {
        $script:failureCode = if ($script:currentCheck) { $script:currentCheck } else { 'qualification_setup' }
        if ($script:currentCheck) { $script:checks[$script:currentCheck] = 'failed' }
        $safeCode = $_.Exception.GetBaseException().Message
        Add-SafeFailureDetail -Check $script:failureCode -Operation $script:currentOperation -ExceptionCategory $_.Exception.GetBaseException().GetType().Name -ExitCode $script:lastProcessExitCode -SafeCode $safeCode
    }
}
finally {
    $cleanupFailed = $false
    if ($null -ne $script:httpClient) {
        try { $script:httpClient.Dispose() }
        catch { $cleanupFailed = $true; Add-CleanupFailure -Operation 'dispose-http-client' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name }
    }

    $script:cleanup.captured_processes_stopped = $true
    foreach ($record in @($script:pendingCapturedProcesses)) {
        if (-not (Stop-OwnedProcess -Record $record)) {
            $cleanupFailed = $true
            $script:cleanup.captured_processes_stopped = $false
            Add-CleanupFailure -Operation $record.Role -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { $record.Process.Dispose() }
    }

    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -eq 'traefik' })) {
        $script:cleanup.traefik_process_stopped = Stop-OwnedProcess -Record $record
        if (-not $script:cleanup.traefik_process_stopped) {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'stop-owned-traefik' -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { $record.Process.Dispose() }
    }
    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -eq 'api' })) {
        $script:cleanup.api_process_stopped = Stop-OwnedProcess -Record $record
        if (-not $script:cleanup.api_process_stopped) {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'stop-owned-api' -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { $record.Process.Dispose() }
    }

    if ($script:postgresStartAttempted) {
        try {
            if (-not $script:postgresInitialized -or $null -eq $script:pgCtlExe -or $null -eq $script:postgresCredential) {
                throw 'postgres_stop_ownership_unavailable'
            }
            $script:currentOperation = 'stop-owned-postgresql-instance'
            Confirm-PostgresStopped
        }
        catch {
            $script:cleanup.postgresql_stopped = $false
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'stop-owned-postgresql' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }

    if ($script:cleanup.api_process_stopped -and $script:cleanup.traefik_process_stopped) {
        try {
            if ($null -ne $script:hostsBlock) {
                $script:currentOperation = 'remove-exact-owned-hosts-entry'
                $script:cleanup.hosts_entry_removed = Remove-OwnedHostsEntry
                if (-not $script:cleanup.hosts_entry_removed) { throw 'owned_hosts_entry_not_found' }
            }
        }
        catch {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'remove-owned-hosts-entry' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }

        try {
            if ($script:rootCertificateThumbprint) {
                $script:currentOperation = 'remove-owned-root-certificate-and-key'
                Remove-TrustedCertificate -StoreName 'Root' -Thumbprint $script:rootCertificateThumbprint
                Remove-TrustedCertificate -StoreName 'My' -Thumbprint $script:rootCertificateThumbprint
            }
            if ($script:serverCertificateThumbprint) {
                $script:currentOperation = 'remove-owned-server-certificate-and-key'
                Remove-TrustedCertificate -StoreName 'My' -Thumbprint $script:serverCertificateThumbprint
            }
            $script:cleanup.trust_certificates_removed = $true
        }
        catch {
            $script:cleanup.trust_certificates_removed = $false
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'remove-owned-certificates' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }
    else {
        if ($null -ne $script:hostsBlock) { $script:cleanup.hosts_entry_removed = $false }
        if ($script:rootCertificateThumbprint -or $script:serverCertificateThumbprint) { $script:cleanup.trust_certificates_removed = $false }
        $cleanupFailed = $true
        Add-CleanupFailure -Operation 'retain-host-routing-and-certificates' -ExceptionCategory 'ProxyProcessNotProvenStopped'
    }

    if ($null -ne $script:securePassword -and -not $script:postgresCredentialDisposed) {
        try {
            $script:securePassword.Dispose()
            $script:postgresCredentialDisposed = $true
            $script:securePassword = $null
            $script:postgresCredential = $null
        }
        catch {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'dispose-temporary-database-credential' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }

    if ($null -ne $script:rootCertificate) { $script:rootCertificate.Dispose() }
    if ($null -ne $script:serverCertificate) { $script:serverCertificate.Dispose() }

    if ($script:localUserCreated -and $script:cleanup.postgresql_stopped -and $script:cleanup.captured_processes_stopped) {
        try {
            $script:currentOperation = 'remove-exact-temporary-database-user'
            Remove-LocalUser -SID $script:localUser.SID -ErrorAction Stop
            $script:cleanup.local_user_removed = $true
        }
        catch {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'remove-temporary-database-user' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }
    elseif ($script:localUserCreated) {
        $cleanupFailed = $true
        Add-CleanupFailure -Operation 'retain-temporary-database-user' -ExceptionCategory 'DatabaseOrChildProcessNotProvenStopped'
    }

    $canRemoveTaskRoot = $null -ne $script:taskRoot -and $script:cleanup.postgresql_stopped -and $script:cleanup.api_process_stopped -and $script:cleanup.traefik_process_stopped -and $script:cleanup.captured_processes_stopped -and $script:cleanup.hosts_entry_removed -and $script:cleanup.trust_certificates_removed -and (-not $script:localUserCreated -or $script:cleanup.local_user_removed)
    if ($canRemoveTaskRoot) {
        try {
            $expectedParent = [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
            $actualParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $script:taskRoot)).TrimEnd('\')
            if ($actualParent -cne $expectedParent -or (Split-Path -Leaf $script:taskRoot) -notmatch '^netratel-hosted-prereq-[0-9a-f]{32}$') { throw 'task_root_ownership_check_failed' }
            Remove-Item -LiteralPath $script:taskRoot -Recurse -Force
            $script:cleanup.task_files_removed = $true
        }
        catch {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'remove-owned-task-directory' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }
    elseif ($null -ne $script:taskRoot) {
        $cleanupFailed = $true
        Add-CleanupFailure -Operation 'retain-owned-task-directory' -ExceptionCategory 'OwnedResourceStillActiveOrUnknown'
    }

    if ($cleanupFailed -and $null -eq $script:failureCode) { $script:failureCode = 'owned_resource_cleanup' }
    $script:checks.cleanup = if ($cleanupFailed) { 'failed' } else { 'passed' }
    try { Write-QualificationReceipt }
    catch {
        $script:failureCode = 'receipt_write_failed'
        Add-SafeFailureDetail -Check 'receipt' -Operation 'write-sanitized-json-receipt' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name -ExitCode $null -SafeCode 'receipt_write_failed'
    }
}

if ($script:failureCode) {
    Write-Error "Windows hosted prerequisite qualification failed at check '$($script:failureCode)'. Detailed process output and temporary configuration are not uploaded."
    exit 1
}
Write-Host 'Windows hosted prerequisite qualification passed. This receipt covers prerequisites only; integrated acceptance was not run.'
