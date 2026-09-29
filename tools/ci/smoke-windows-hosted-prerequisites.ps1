[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $receiptRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $ReceiptPath = Join-Path $receiptRoot 'netratel-windows-hosted-onboarding.json'
}

$script:checks = [ordered]@{
    windows_runner = 'not_run'
    verified_vendor_assets = 'not_run'
    x64_postgresql_execution = 'not_run'
    fresh_postgresql_migrations = 'not_run'
    actual_api_bootstrap_listener = 'not_run'
    native_traefik_trusted_https = 'not_run'
    initialized_production_local_api = 'not_run'
    verified_candidate_client_archive = 'not_run'
    client_installation_baseline = 'not_run'
    actual_web_listener = 'not_run'
    hosted_main_onboarding = 'not_run'
    hosted_web_directory = 'not_run'
    owned_client_cleanup = 'not_run'
    cleanup = 'not_run'
}
$script:currentCheck = $null
$script:failureCode = $null
$script:cleanup = [ordered]@{
    postgresql_stopped = $true
    api_process_stopped = $true
    traefik_process_stopped = $true
    web_process_stopped = $true
    local_user_removed = $true
    hosts_entry_removed = $true
    trust_certificates_removed = $true
    task_files_removed = $true
    captured_processes_stopped = $true
    client_service_removed = $true
    updater_service_removed = $true
    client_default_directories_removed = $true
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
$script:webProcess = $null
$script:httpClient = $null
$script:publicApiClient = $null
$script:apiEnvironment = $null
$script:webPublishDirectory = $null
$script:webDll = $null
$script:apiGatewayPort = $null
$script:publicOrigin = $null
$script:handoffRoot = $null
$script:passwordFile = $null
$script:candidateArchive = $null
$script:candidateSha256 = $null
$script:identityReceiptPath = $null
$script:controlRequestPath = $null
$script:controlAckPath = $null
$script:safeEvidenceDirectory = $null
$script:evidenceExportDirectory = $null
$script:apiLocalCookie = $null
$script:initializedTenantId = $null
$script:serviceInstallRoot = $null
$script:serviceStateRoot = $null
$script:serviceCredentialFiles = @()
$script:serviceLogDirectories = @()
$script:baselineServiceAbsent = $false
$script:baselineDirectoriesAbsent = $false
$script:clientInstallationAttempted = $false
$script:mainTrxPath = $null
$script:webTrxPath = $null
$script:mainTestCounts = $null
$script:webTestCounts = $null
$script:integratedAcceptance = 'not_run'
$script:initialAcceptance = 'not_run'
$script:mandatoryLaterAcceptance = 'not_run'
$script:seenControlRequestIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:lastControlSequence = 0
$script:lastControlRequestId = $null
$script:lastControlAction = $null
$script:controlActionFailures = [System.Collections.Generic.List[string]]::new()
$script:apiProbeClient = $null
$script:contextPath = $null
$script:apiPublishDirectory = $null
$script:apiDll = $null
$script:mainTestModule = $null
$script:webTestModule = $null
$script:testResultsDirectory = $null
$script:agentId = $null
$script:operatorEmail = $null
$script:operatorDisplayName = $null
$script:tenantName = $null
$script:traefikStaticConfigPath = $null
$script:traefikDynamicConfigPath = $null
$script:serverCertificatePemPath = $null
$script:serverPrivateKeyPemPath = $null
$script:localMachineRootCertificateImported = $false
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
    'api_not_ready', 'api_publish_output_missing', 'api_setup_status_unavailable', 'arm64_runner_required', 'bootstrap_state_not_fresh',
    'checked_out_source_does_not_match_test_merge_sha', 'database_test_user_is_elevated', 'github_hosted_runner_required',
    'github_runner_paths_required', 'https_api_not_in_unconfigured_bootstrap_state', 'https_bootstrap_liveness_mismatch',
    'candidate_archive_commit_mismatch', 'candidate_archive_manifest_invalid', 'candidate_archive_missing',
    'client_default_directory_preexists', 'client_service_preexists', 'control_ack_write_failed', 'control_action_failed', 'control_request_invalid',
    'hosted_browser_missing', 'hosted_main_test_failed', 'hosted_main_test_missing_or_zero', 'hosted_web_test_failed', 'hosted_web_test_missing_or_zero',
    'https_route_timeout', 'initialized_local_auth_failed', 'initialized_tenant_unavailable', 'listener_process_exited',
    'local_machine_trust_failed', 'owned_hosts_entry_not_found', 'owned_process_start_failed', 'owned_trust_certificate_removal_unverified',
    'postgres_command_line_directory_contract_failed', 'postgres_owned_process_identity_changed', 'postgres_pid_file_data_directory_mismatch', 'postgres_pid_file_invalid',
    'postgres_process_identity_mismatch', 'postgres_running_identity_unavailable', 'postgres_start_not_observed',
    'postgres_started_identity_unavailable', 'postgres_status_unknown', 'postgres_stop_ownership_unavailable',
    'postgres_stopped_status_conflicts_with_live_owned_process', 'postgres_stopped_status_conflicts_with_tracked_process',
    'postgresql_migrations_not_observed', 'postgresql_version_mismatch', 'process_exit_nonzero',
    'process_output_policy_not_allowed', 'process_output_pipe_timeout', 'process_start_failed', 'process_timeout',
    'product_version_resolution_failed', 'receipt_write_failed',
    'repository_checkout_path_mismatch', 'runner_temp_required', 'safe_evidence_export_failed', 'source_provenance_missing',
    'task_root_ownership_check_failed', 'traefik_version_or_architecture_mismatch', 'vendor_archive_digest_mismatch',
    'vendor_archive_layout_unexpected', 'windows_11_required', 'windows_required'
))
$script:boundedDrainTypeName = 'NetRatel.Ci.BoundedStreamDrain'
if ($null -eq ($script:boundedDrainTypeName -as [type])) {
    Add-Type -TypeDefinition @'
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Ci
{
    public static class BoundedStreamDrain
    {
        public static Task<string> Start(StreamReader reader, int maximumCharacters) => DrainAsync(reader, maximumCharacters);

        private static async Task<string> DrainAsync(StreamReader reader, int maximumCharacters)
        {
            var retained = new StringBuilder(Math.Min(maximumCharacters, 4096));
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                var remaining = maximumCharacters - retained.Length;
                if (remaining > 0)
                    retained.Append(buffer, 0, Math.Min(remaining, read));
            }

            return retained.ToString();
        }
    }
}
'@ -ErrorAction Stop
}
$script:operatingSystem = 'unknown'
$script:sourceSha = 'unknown'
$script:testMergeSha = 'unknown'
$script:productVersion = 'unknown'
$script:postgresVerified = $false
$script:traefikVerified = $false
$script:osArchitecture = 'unknown'
$script:traefikPort = 443
$script:scope = 'windows-hosted-onboarding'

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
    Write-Host "Running Windows hosted onboarding check: $Name"
    try {
        Write-QualificationCheckpoint
        $null = & $Action
        $script:checks[$Name] = 'passed'
        Write-QualificationCheckpoint
    }
    catch {
        $script:checks[$Name] = 'failed'
        $exceptionCode = $_.Exception.GetBaseException().Message
        $script:failureCode = if ($exceptionCode -eq 'receipt_write_failed') { 'receipt_write_failed' } else { $Name }
        $safeCode = $_.Exception.GetBaseException().Message
        Add-SafeFailureDetail -Check $Name -Operation $script:currentOperation -ExceptionCategory $_.Exception.GetBaseException().GetType().Name -ExitCode $script:lastProcessExitCode -SafeCode $safeCode
        throw [InvalidOperationException]::new('A hosted prerequisite check failed.')
    }
    finally { $script:currentCheck = $null }
}

function Set-QualificationOperation {
    param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name)
    $script:currentOperation = $Name
    Write-QualificationCheckpoint
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

function Wait-CapturedOutputDrain {
    param([Parameter(Mandatory)][AllowEmptyCollection()][System.Threading.Tasks.Task[]]$Tasks, [Parameter(Mandatory)][int]$TimeoutMilliseconds)
    if ($Tasks.Count -eq 0) { return $true }
    try { return [System.Threading.Tasks.Task]::WaitAll($Tasks, $TimeoutMilliseconds) }
    catch [System.AggregateException] {
        foreach ($task in $Tasks) {
            if ($task.IsFaulted -and $null -ne $task.Exception) { $null = $task.Exception }
        }
        return $false
    }
    catch [System.Threading.Tasks.TaskCanceledException] { return $false }
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
    $processDisposed = $false
    $processStarted = $false
    $processId = $null
    $processStartTicks = $null
    $outputTasks = @()
    try {
        if (-not $process.Start()) { throw 'process_start_failed' }
        $processStarted = $true
        $processId = $process.Id
        $processStartTicks = $process.StartTime.ToUniversalTime().Ticks
        # On Windows pg_ctl start launches a shell that inherits these redirected handles.
        # Its -l target is the private server log and -s suppresses routine pg_ctl output, but
        # the daemon can keep pipe handles open after pg_ctl exits. Avoid creating EOF readers
        # only for this validated start; Process.Dispose closes the parent pipes with no reader
        # tasks left running. The bounded parent exit and subsequent status/PID checks still apply.
        if (-not $skipOutputPipeDrain) {
            $stdoutTask = [NetRatel.Ci.BoundedStreamDrain]::Start($process.StandardOutput, 65536)
            $outputTasks = @($stdoutTask)
            $stderrTask = [NetRatel.Ci.BoundedStreamDrain]::Start($process.StandardError, 65536)
            $outputTasks += $stderrTask
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            $exited = $process.WaitForExit(10000)
            $streamsClosed = $skipOutputPipeDrain -or (Wait-CapturedOutputDrain -Tasks ([System.Threading.Tasks.Task[]]$outputTasks) -TimeoutMilliseconds 10000)
            if (-not $exited -or -not $streamsClosed) {
                $keepProcessHandle = $true
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add([pscustomobject]@{
                    Role = "captured:$([System.IO.Path]::GetFileName($FilePath))"
                    Process = $process
                    ProcessId = $processId
                    StartTimeTicks = $processStartTicks
                    OutputTasks = $outputTasks
                    StandardOutput = ''
                    StandardError = ''
                    Stopped = $false
                    Disposed = $false
                    ExitCode = $null
                })
            }
            throw 'process_timeout'
        }
        $streamsClosed = $skipOutputPipeDrain -or (Wait-CapturedOutputDrain -Tasks ([System.Threading.Tasks.Task[]]$outputTasks) -TimeoutMilliseconds 10000)
        if (-not $streamsClosed) {
            try { $process.Kill($true) } catch { }
            $exited = $process.WaitForExit(10000)
            $streamsClosed = Wait-CapturedOutputDrain -Tasks ([System.Threading.Tasks.Task[]]$outputTasks) -TimeoutMilliseconds 10000
            if (-not $exited -or -not $streamsClosed) {
                $keepProcessHandle = $true
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add([pscustomobject]@{
                    Role = "captured:$([System.IO.Path]::GetFileName($FilePath))"
                    Process = $process
                    ProcessId = $processId
                    StartTimeTicks = $processStartTicks
                    OutputTasks = $outputTasks
                    StandardOutput = ''
                    StandardError = ''
                    Stopped = $false
                    Disposed = $false
                    ExitCode = $null
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
    catch {
        $primaryError = $_
        if ($processStarted -and -not $keepProcessHandle) {
            $record = [pscustomobject]@{
                Role = "captured:$([System.IO.Path]::GetFileName($FilePath))"
                Process = $process
                ProcessId = $processId
                StartTimeTicks = $processStartTicks
                OutputTasks = $outputTasks
                StandardOutput = ''
                StandardError = ''
                Stopped = $false
                Disposed = $false
                ExitCode = $null
            }
            try {
                if (Stop-OwnedProcess -Record $record) {
                    Dispose-OwnedProcess -Record $record
                    $processDisposed = $true
                }
                else {
                    $script:cleanup.captured_processes_stopped = $false
                    $script:pendingCapturedProcesses.Add($record)
                    $keepProcessHandle = $true
                }
            }
            catch {
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add($record)
                $keepProcessHandle = $true
            }
        }
        throw $primaryError
    }
    finally {
        if (-not $keepProcessHandle -and -not $processDisposed) { $process.Dispose() }
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
        OutputTasks = @(
            [NetRatel.Ci.BoundedStreamDrain]::Start($process.StandardOutput, 65536),
            [NetRatel.Ci.BoundedStreamDrain]::Start($process.StandardError, 65536))
        StandardOutput = ''
        StandardError = ''
        Stopped = $false
        Disposed = $false
        ExitCode = $null
    }
    $script:trackedProcesses.Add($record)
    if ($Role -eq 'api') { $script:cleanup.api_process_stopped = $false }
    elseif ($Role -eq 'traefik') { $script:cleanup.traefik_process_stopped = $false }
    elseif ($Role -eq 'web') { $script:cleanup.web_process_stopped = $false }
    return $record
}

function Stop-OwnedProcess {
    param([Parameter(Mandatory)][object]$Record)
    if ($Record.Stopped) { return $true }
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
            $Record.StandardOutput = $Record.OutputTasks[0].GetAwaiter().GetResult()
            $Record.StandardError = $Record.OutputTasks[1].GetAwaiter().GetResult()
        }
        if ($Record.PSObject.Properties.Name -contains 'ExitCode' -and $null -eq $Record.ExitCode) {
            $Record.ExitCode = $process.ExitCode
        }
        $Record.Stopped = $true
        return $true
    }
    catch { return $false }
}

function Dispose-OwnedProcess {
    param([Parameter(Mandatory)][object]$Record)
    if (-not $Record.Disposed) {
        $Record.Process.Dispose()
        $Record.Disposed = $true
    }
}

function Test-OwnedProcessRunning {
    param([AllowNull()][object]$Record)
    if ($null -eq $Record -or $Record.Stopped) { return $false }
    try {
        if ($Record.Disposed) { throw 'listener_process_exited' }
        $Record.Process.Refresh()
        return -not $Record.Process.HasExited
    }
    catch { throw 'listener_process_exited' }
}

function Assert-ArchiveSha256 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Expected) { throw 'vendor_archive_digest_mismatch' }
}

function Assert-PathHasNoReparsePoints {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $canonicalPath.StartsWith($canonicalRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'task_root_ownership_check_failed'
    }
    $relative = $canonicalPath.Substring($canonicalRoot.Length + 1)
    $current = $canonicalRoot
    foreach ($part in $relative.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'task_root_ownership_check_failed' }
        }
    }
}

function Assert-DirectoryTreeHasNoReparsePoints {
    param([Parameter(Mandatory)][string]$Path)
    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'task_root_ownership_check_failed'
    }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop) {
        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'task_root_ownership_check_failed' }
    }
}

function Assert-ProtectedDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $trusted = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sid in @([System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-32-544', 'S-1-5-18')) {
        [void]$trusted.Add($sid)
    }
    $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected -or -not $trusted.Contains($ownerSid)) { throw 'task_root_ownership_check_failed' }
    foreach ($rule in $acl.Access) {
        if (-not $trusted.Contains($rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value) -or
            $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
            throw 'task_root_ownership_check_failed'
        }
    }
}

function Assert-HandoffPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root, [switch]$MustExist, [switch]$Directory)
    $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $canonicalPath.StartsWith($canonicalRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'task_root_ownership_check_failed'
    }
    Assert-PathHasNoReparsePoints -Path $canonicalPath -Root $canonicalRoot
    if ($MustExist -and -not (Test-Path -LiteralPath $canonicalPath)) { throw 'task_root_ownership_check_failed' }
    if (Test-Path -LiteralPath $canonicalPath) {
        $item = Get-Item -LiteralPath $canonicalPath -Force -ErrorAction Stop
        if ($Directory -and -not $item.PSIsContainer) { throw 'task_root_ownership_check_failed' }
        if (-not $Directory -and $item.PSIsContainer) { throw 'task_root_ownership_check_failed' }
    }
    return $canonicalPath
}

function Write-AtomicJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][object]$Value)
    Assert-HandoffPath -Path $Path -Root $script:handoffRoot | Out-Null
    $temporaryPath = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $json = ConvertTo-Json -InputObject $Value -Depth 8 -Compress
        [System.IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        Assert-HandoffPath -Path $temporaryPath -Root $script:handoffRoot -MustExist | Out-Null
        if (Test-Path -LiteralPath $Path) { [System.IO.File]::Replace($temporaryPath, $Path, $null, $true) }
        else { [System.IO.File]::Move($temporaryPath, $Path) }
    }
    finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue } }
}

function Read-BoundedHandoffJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][int]$MaximumBytes)
    if ($MaximumBytes -lt 1 -or $MaximumBytes -gt 1048576) { throw 'control_request_invalid' }
    Assert-HandoffPath -Path $Path -Root $script:handoffRoot -MustExist | Out-Null
    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        if ($stream.Length -gt $MaximumBytes) { throw 'control_request_invalid' }
        $buffer = [byte[]]::new([Math]::Min(4096, $MaximumBytes + 1))
        $memory = [System.IO.MemoryStream]::new([Math]::Min($MaximumBytes + 1, 65536))
        try {
            while ($true) {
                $remaining = $MaximumBytes + 1 - [int]$memory.Length
                if ($remaining -le 0) { throw 'control_request_invalid' }
                $read = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $remaining))
                if ($read -eq 0) { break }
                $memory.Write($buffer, 0, $read)
                if ($memory.Length -gt $MaximumBytes) { throw 'control_request_invalid' }
            }
            $memory.Position = 0
            $reader = [System.IO.StreamReader]::new($memory, [System.Text.UTF8Encoding]::new($false, $true), $true, 1024, $true)
            try { $json = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
            return ConvertFrom-Json -InputObject $json -ErrorAction Stop
        }
        finally { $memory.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Copy-FileStreamingWithSha256 {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    Assert-HandoffPath -Path $Destination -Root $script:handoffRoot | Out-Null
    $sourceStream = [System.IO.File]::Open($Source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    $destinationStream = $null
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $destinationStream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $buffer = [byte[]]::new(1024 * 1024)
        while (($read = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $destinationStream.Write($buffer, 0, $read)
            $hash.AppendData($buffer, 0, $read)
        }
        $destinationStream.Flush($true)
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        $sourceStream.Dispose()
        $hash.Dispose()
    }
}

function Assert-ClientArchive {
    param([Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$ArtifactDirectory)
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw 'candidate_archive_missing' }
    $version = [regex]::Escape($script:productVersion)
    $archiveName = "netratel-client-$($script:productVersion)-win-x64.zip"
    $sumsPath = Join-Path $ArtifactDirectory 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) { throw 'candidate_archive_digest_mismatch' }
    $sumLine = [System.IO.File]::ReadLines($sumsPath) | Where-Object { $_ -match "^(?<sha>[0-9a-fA-F]{64})\s+\*?$([regex]::Escape($archiveName))$" } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($sumLine)) { throw 'candidate_archive_digest_mismatch' }
    $expectedHash = [regex]::Match($sumLine, '^[0-9a-fA-F]{64}').Value.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) { throw 'candidate_archive_digest_mismatch' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $manifestEntry = $null
        $exeEntry = $null
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.StartsWith('/') -or $name -match '(^|/)\.\.?(/|$)' -or $name.Contains(':') -or -not $names.Add($name)) {
                throw 'candidate_archive_manifest_invalid'
            }
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -eq 0xA000) { throw 'candidate_archive_manifest_invalid' }
            if ($name -eq 'netratel-client-win-x64/netratel-client-manifest.json') { $manifestEntry = $entry }
            if ($name -eq 'netratel-client-win-x64/NetRatel.Client.exe') { $exeEntry = $entry }
        }
        if ($null -eq $manifestEntry -or $null -eq $exeEntry -or $manifestEntry.Length -gt 65536 -or $exeEntry.Length -le 0) {
            throw 'candidate_archive_manifest_invalid'
        }
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open(), [System.Text.Encoding]::UTF8, $true, 4096, $false)
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
        finally { $reader.Dispose() }
        if ($manifest.schema -cne 'netratel.client.manifest.v1' -or $manifest.product -cne 'NetRatel.Client' -or
            $manifest.executable -cne 'NetRatel.Client.exe' -or $manifest.runtimeId -cne 'win-x64' -or
            $manifest.version -cne $script:productVersion -or $manifest.commitSha -notmatch '^[0-9a-fA-F]{40}$') {
            throw 'candidate_archive_manifest_invalid'
        }
        if ($manifest.commitSha.ToLowerInvariant() -cne $script:testMergeSha) { throw 'candidate_archive_commit_mismatch' }
    }
    finally { $archive.Dispose() }

    $script:candidateArchive = Join-Path $script:handoffRoot 'netratel-client-win-x64.zip'
    $copiedHash = Copy-FileStreamingWithSha256 -Source $ArchivePath -Destination $script:candidateArchive
    if ($copiedHash -cne $actualHash) { throw 'candidate_archive_digest_mismatch' }
    $script:candidateSha256 = $copiedHash
}

function New-PrivateHandoff {
    $script:handoffRoot = Join-Path $script:taskRoot 'handoff'
    [System.IO.Directory]::CreateDirectory($script:handoffRoot) | Out-Null
    Protect-TaskRoot -Path $script:handoffRoot
    Assert-ProtectedDirectory -Path $script:handoffRoot
    $script:passwordFile = Join-Path $script:handoffRoot 'operator-password.txt'
    $passwordBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(36)
    try { $password = 'N' + [Convert]::ToBase64String($passwordBytes) + '7!a' }
    finally { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
    [System.IO.File]::WriteAllText($script:passwordFile, $password, [System.Text.UTF8Encoding]::new($false))
    $password = $null
    $script:identityReceiptPath = Join-Path $script:handoffRoot 'identity-receipt.json'
    $script:controlRequestPath = Join-Path $script:handoffRoot 'phase-request.json'
    $script:controlAckPath = Join-Path $script:handoffRoot 'phase-ack.json'
    $script:safeEvidenceDirectory = Join-Path $script:handoffRoot 'safe-evidence'
    [System.IO.Directory]::CreateDirectory($script:safeEvidenceDirectory) | Out-Null
    Assert-HandoffPath -Path $script:passwordFile -Root $script:handoffRoot -MustExist | Out-Null
    Assert-HandoffPath -Path $script:safeEvidenceDirectory -Root $script:handoffRoot -MustExist -Directory | Out-Null
    foreach ($path in @($script:identityReceiptPath, $script:controlRequestPath, $script:controlAckPath)) {
        if (Test-Path -LiteralPath $path) { throw 'control_request_invalid' }
    }
    $exportBase = Join-Path $env:RUNNER_TEMP "netratel-hosted-evidence-$($script:runId)"
    if (Test-Path -LiteralPath $exportBase) { throw 'task_root_ownership_check_failed' }
    [System.IO.Directory]::CreateDirectory($exportBase) | Out-Null
    Protect-TaskRoot -Path $exportBase
    Assert-ProtectedDirectory -Path $exportBase
    $script:evidenceExportDirectory = $exportBase
}

function New-StableAgentSigningKey {
    $script:agentSigningKeyPath = Join-Path $script:taskRoot 'agent-signing-key.pem'
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
    try { [System.IO.File]::WriteAllText($script:agentSigningKeyPath, $ecdsa.ExportPkcs8PrivateKeyPem(), [System.Text.UTF8Encoding]::new($false)) }
    finally { $ecdsa.Dispose() }
}

function New-ApiEnvironment {
    return @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ConnectionStrings__NetRatelDb' = $script:pgConnectionString
        'Bootstrap__StateDirectory' = $script:bootstrapStateDirectory
        'DataProtection__KeysDirectory' = $script:dataProtectionDirectory
        'DataProtection__ApplicationName' = 'NetRatel'
        'AgentAuth__PrivateKeyPath' = $script:agentSigningKeyPath
        'AgentAuth__Issuer' = $script:publicOrigin
        'AgentAuth__Audience' = 'netratel-agent'
        'NetRatel_HTTP_PORT' = [string]$script:apiPort
        'NetRatelAkka__GatewayGrpcPort' = [string]$script:apiGatewayPort
        'Branding__SiteUrl' = $script:publicOrigin
        'StorageOptions__RootPath' = $script:storageRoot
        'ClientArtifacts__StorageRoot' = $script:artifactStorageRoot
    }
}

function Start-OwnedApi {
    if (Test-OwnedProcessRunning -Record $script:apiProcess) { return }
    if ($null -ne $script:apiProcess -and -not $script:apiProcess.Stopped) {
        if (-not (Stop-OwnedProcess -Record $script:apiProcess)) { throw 'listener_process_exited' }
        Dispose-OwnedProcess -Record $script:apiProcess
    }
    $script:apiProcess = Start-OwnedProcess -Role 'api' -FilePath (Get-Command dotnet).Source -ArgumentList @($script:apiDll) -WorkingDirectory $script:apiPublishDirectory -Environment $script:apiEnvironment
    $script:cleanup.api_process_stopped = $false
}

function Stop-OwnedApi {
    if ($null -ne $script:apiProcess -and -not $script:apiProcess.Stopped) {
        if (-not (Stop-OwnedProcess -Record $script:apiProcess)) { throw 'listener_process_exited' }
        $script:cleanup.api_process_stopped = $true
        Dispose-OwnedProcess -Record $script:apiProcess
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { Assert-LoopbackPortFree -Port $script:apiPort; return }
        catch { Start-Sleep -Milliseconds 200 }
    }
    throw 'listener_process_exited'
}

function Start-OwnedTraefik {
    if (Test-OwnedProcessRunning -Record $script:traefikProcess) { throw 'listener_process_exited' }
    $script:traefikProcess = Start-OwnedProcess -Role 'traefik' -FilePath $script:traefikExe -ArgumentList @("--configFile=$script:traefikStaticConfigPath") -WorkingDirectory $script:traefikWorkingDirectory
    $script:cleanup.traefik_process_stopped = $false
}

function Wait-ForProductionApiReady {
    param([int]$TimeoutSeconds = 90)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-OwnedProcessRunning -Record $script:apiProcess) -or -not (Test-OwnedProcessRunning -Record $script:traefikProcess)) {
            throw 'listener_process_exited'
        }
        try {
            $response = $script:httpClient.GetAsync("$($script:publicOrigin)/api/v2/setup/status").GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) {
                    $status = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    $descriptorPath = Join-Path $script:bootstrapStateDirectory 'descriptor.json'
                    $persistedReady = $false
                    if (Test-Path -LiteralPath $descriptorPath -PathType Leaf) {
                        try { $persistedReady = ([int]((Get-Content -LiteralPath $descriptorPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop).state) -eq 2) }
                        catch { $persistedReady = $false }
                    }
                    if ($persistedReady -and $status.isReady -and -not $status.setupRequired -and [int]$status.state -eq 2) {
                        if ($null -eq $script:publicApiClient) { return }
                        $identity = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/api/v2/local-auth/me' -Body $null
                        try {
                            if ($identity.StatusCode -eq [System.Net.HttpStatusCode]::ServiceUnavailable) { Start-Sleep -Milliseconds 500; continue }
                            if ($identity.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_not_ready' }
                            $principal = $identity.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                            if (-not [string]::Equals([string]$principal.email, $script:operatorEmail, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'api_not_ready' }
                        }
                        finally { $identity.Dispose() }
                        $tenantResponse = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/api/v2/access/tenants' -Body $null
                        try {
                            if ($tenantResponse.StatusCode -eq [System.Net.HttpStatusCode]::ServiceUnavailable) { Start-Sleep -Milliseconds 500; continue }
                            if ($tenantResponse.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_not_ready' }
                            $tenants = @($tenantResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
                            if (-not ($tenants | Where-Object { [int]$_.tenantId -eq $script:initializedTenantId })) { throw 'api_not_ready' }
                        }
                        finally { $tenantResponse.Dispose() }
                        $directory = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path "/api/v2/client-presence/?tenantId=$($script:initializedTenantId)&limit=1" -Body $null
                        try {
                            if ($directory.StatusCode -eq [System.Net.HttpStatusCode]::ServiceUnavailable) { Start-Sleep -Milliseconds 500; continue }
                            if ($directory.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_not_ready' }
                        }
                        finally { $directory.Dispose() }

                        $readiness = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/health/ready' -Body $null
                        try {
                            if ($readiness.StatusCode -eq [System.Net.HttpStatusCode]::ServiceUnavailable) { Start-Sleep -Milliseconds 500; continue }
                            if ($readiness.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_not_ready' }
                            $health = $readiness.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                            if ($health.status -cne 'ready') { throw 'api_not_ready' }
                        }
                        finally { $readiness.Dispose() }
                        return
                    }
                }
            }
            finally { $response.Dispose() }
        }
        catch [System.Net.Http.HttpRequestException] { }
        catch [System.Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 500
    }
    throw 'api_not_ready'
}

function Send-ApiJson {
    param([Parameter(Mandatory)][System.Net.Http.HttpClient]$Client, [Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, [AllowNull()][object]$Body)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$($script:publicOrigin)$Path")
    if ($null -ne $Body) {
        $json = ConvertTo-Json -InputObject $Body -Depth 6 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    }
    try { return $Client.SendAsync($request).GetAwaiter().GetResult() }
    finally { $request.Dispose() }
}

function Assert-InitializedLocalIdentity {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $handler.UseCookies = $true
    $script:publicApiClient = [System.Net.Http.HttpClient]::new($handler)
    $script:publicApiClient.Timeout = [TimeSpan]::FromSeconds(10)
    $login = Send-ApiJson -Client $script:publicApiClient -Method 'POST' -Path '/api/v2/local-auth/login' -Body @{ email = $script:operatorEmail; password = [System.IO.File]::ReadAllText($script:passwordFile); rememberMe = $false }
    try {
        if ($login.StatusCode -ne [System.Net.HttpStatusCode]::NoContent) { throw 'initialized_local_auth_failed' }
    }
    finally { $login.Dispose() }
    $me = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/api/v2/local-auth/me' -Body $null
    try {
        if ($me.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'initialized_local_auth_failed' }
        $identity = $me.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if (-not [string]::Equals([string]$identity.email, $script:operatorEmail, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'initialized_local_auth_failed' }
    }
    finally { $me.Dispose() }
    $tenants = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/api/v2/access/tenants' -Body $null
    try {
        if ($tenants.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'initialized_tenant_unavailable' }
        $tenantItems = @($tenants.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
        if ($tenantItems.Count -ne 1 -or [int]$tenantItems[0].tenantId -le 0) { throw 'initialized_tenant_unavailable' }
        $script:initializedTenantId = [int]$tenantItems[0].tenantId
    }
    finally { $tenants.Dispose() }

    $directory = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path "/api/v2/client-presence/?tenantId=$($script:initializedTenantId)&online=false&limit=100" -Body $null
    try {
        if ($directory.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'initialized_tenant_unavailable' }
        $directoryResult = $directory.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if (@($directoryResult.items).Count -ne 0) { throw 'initialized_tenant_unavailable' }
    }
    finally { $directory.Dispose() }
}

function Assert-TestResults {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$SafeFailureCode)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw $SafeFailureCode }
    try { [xml]$document = [System.IO.File]::ReadAllText($Path) }
    catch { throw $SafeFailureCode }
    $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) { throw $SafeFailureCode }
    $counterNames = @(
        'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive',
        'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning',
        'completed', 'inProgress', 'pending')
    if (@($counterNames | Where-Object { -not $counters.HasAttribute($_) }).Count -ne 0) { throw $SafeFailureCode }
    $result = [ordered]@{
        total = [int]$counters.GetAttribute('total')
        executed = [int]$counters.GetAttribute('executed')
        passed = [int]$counters.GetAttribute('passed')
        failed = [int]$counters.GetAttribute('failed')
        notExecuted = [int]$counters.GetAttribute('notExecuted')
        error = [int]$counters.GetAttribute('error')
        timeout = [int]$counters.GetAttribute('timeout')
        aborted = [int]$counters.GetAttribute('aborted')
        completed = [int]$counters.GetAttribute('completed')
        inconclusive = [int]$counters.GetAttribute('inconclusive')
        passedButRunAborted = [int]$counters.GetAttribute('passedButRunAborted')
        notRunnable = [int]$counters.GetAttribute('notRunnable')
        disconnected = [int]$counters.GetAttribute('disconnected')
        warning = [int]$counters.GetAttribute('warning')
        inProgress = [int]$counters.GetAttribute('inProgress')
        pending = [int]$counters.GetAttribute('pending')
    }
    $summary = $document.SelectSingleNode("//*[local-name()='ResultSummary']")
    if ($null -eq $summary -or $summary.GetAttribute('outcome') -cne 'Completed' -or
        $null -ne $document.SelectSingleNode("//*[local-name()='RunInfo']")) { throw $SafeFailureCode }
    $unitResults = $document.SelectNodes("//*[local-name()='UnitTestResult']")
    if ($result.total -ne 1 -or $result.executed -ne 1 -or $result.passed -ne 1 -or
        $result.notExecuted -ne 0 -or $result.failed -ne 0 -or $result.error -ne 0 -or
        $result.timeout -ne 0 -or $result.aborted -ne 0 -or $result.inconclusive -ne 0 -or
        $result.passedButRunAborted -ne 0 -or $result.notRunnable -ne 0 -or
        $result.disconnected -ne 0 -or $result.warning -ne 0 -or
        $result.completed -ne 0 -or $result.inProgress -ne 0 -or $result.pending -ne 0 -or $unitResults.Count -ne 1 -or
        $unitResults[0].GetAttribute('outcome') -cne 'Passed') {
        throw $SafeFailureCode
    }
    return $result
}

function Read-HostedIdentityReceipt {
    if (-not (Test-Path -LiteralPath $script:identityReceiptPath -PathType Leaf)) { throw 'hosted_main_test_failed' }
    Assert-HandoffPath -Path $script:identityReceiptPath -Root $script:handoffRoot -MustExist | Out-Null
    try { $receipt = Read-BoundedHandoffJson -Path $script:identityReceiptPath -MaximumBytes 32768 }
    catch { throw 'hosted_main_test_failed' }
    $requiredProperties = @(
        'schemaVersion', 'scope', 'runId', 'sourceSha', 'testMergeSha', 'productVersion',
        'agentId', 'tenantId', 'connectionEpoch', 'connectionId', 'heartbeatSequence',
        'localSystem', 'sessionZero', 'currentServiceProcessMatched', 'freshnessVerified', 'onlineDirectoryVerified')
    $parsedAgentId = [Guid]::Empty
    $parsedConnectionId = [Guid]::Empty
    if ($receipt -isnot [System.Management.Automation.PSCustomObject] -or
        @($receipt.PSObject.Properties).Count -ne $requiredProperties.Count -or
        @($requiredProperties | Where-Object { $receipt.PSObject.Properties.Name -notcontains $_ }).Count -ne 0 -or
        $receipt.schemaVersion -ne 1 -or
        $receipt.scope -ne 'integrated-onboarding' -or $receipt.runId -ne $script:runId -or
        $receipt.sourceSha -ne $script:sourceSha -or $receipt.testMergeSha -ne $script:testMergeSha -or
        $receipt.productVersion -ne $script:productVersion -or [int]$receipt.tenantId -ne $script:initializedTenantId -or
        -not [Guid]::TryParse([string]$receipt.agentId, [ref]$parsedAgentId) -or $parsedAgentId -eq [Guid]::Empty -or
        [long]$receipt.connectionEpoch -le 0 -or -not [Guid]::TryParse([string]$receipt.connectionId, [ref]$parsedConnectionId) -or $parsedConnectionId -eq [Guid]::Empty -or
        [long]$receipt.heartbeatSequence -lt 2 -or -not $receipt.localSystem -or -not $receipt.sessionZero -or
        -not $receipt.currentServiceProcessMatched -or -not $receipt.freshnessVerified -or -not $receipt.onlineDirectoryVerified) {
        throw 'hosted_main_test_failed'
    }
    $script:agentId = $parsedAgentId
}

function Get-TrackedService {
    param([Parameter(Mandatory)][string]$Name)
    return Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop
}

function Get-ServiceExecutableFromImage {
    param([Parameter(Mandatory)][string]$ImagePath)
    $match = [System.Text.RegularExpressions.Regex]::Match($ImagePath, '^\s*"(?<exe>[^"]+\.exe)"(?:\s+--service)?\s*$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) { return $null }
    try { return Get-CanonicalWindowsPath $match.Groups['exe'].Value }
    catch { return $null }
}

function Get-ServiceProcessIdentity {
    param([Parameter(Mandatory)][object]$Service, [Parameter(Mandatory)][string]$ExpectedExecutable)
    if ([int]$Service.ProcessId -le 0) { return $null }
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $([int]$Service.ProcessId)" -ErrorAction Stop
    if ($null -eq $process) { throw 'client_service_ownership_unavailable' }
    $expectedPath = Get-CanonicalWindowsPath $ExpectedExecutable
    $commandMatch = [System.Text.RegularExpressions.Regex]::Match([string]$process.CommandLine,
        '^\s*"(?<exe>[^"]+\.exe)"\s+--service\s*$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath) -or
        (Get-CanonicalWindowsPath ([string]$process.ExecutablePath)) -ine $expectedPath -or
        -not $commandMatch.Success -or (Get-CanonicalWindowsPath $commandMatch.Groups['exe'].Value) -ine $expectedPath) {
        throw 'client_service_ownership_unavailable'
    }
    if ($null -eq $process.CreationDate) { throw 'client_service_ownership_unavailable' }
    return [pscustomobject]@{
        ProcessId = [int]$process.ProcessId
        StartTimeTicks = $process.CreationDate.ToUniversalTime().Ticks
        ExecutablePath = $expectedPath
    }
}

function Assert-ServiceRegistrationOwned {
    param([Parameter(Mandatory)][object]$Service, [Parameter(Mandatory)][string]$ExpectedExecutable)
    $expectedPath = Get-CanonicalWindowsPath $ExpectedExecutable
    $registeredPath = Get-ServiceExecutableFromImage -ImagePath ([string]$Service.PathName)
    if ($null -eq $registeredPath -or $registeredPath -ine $expectedPath -or
        -not [string]::Equals([string]$Service.StartName, 'LocalSystem', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'client_service_ownership_unavailable'
    }
    return Get-ServiceProcessIdentity -Service $Service -ExpectedExecutable $expectedPath
}

function Wait-ServiceStopped {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSeconds = 45)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $service = Get-TrackedService -Name $Name
        if ($null -eq $service -or $service.State -eq 'Stopped') { return $service }
        Start-Sleep -Milliseconds 250
    }
    throw 'client_service_stop_failed'
}

function Assert-OldServiceProcessExited {
    param([AllowNull()][object]$Identity)
    if ($null -eq $Identity) { return }
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($Identity.ProcessId)" -ErrorAction Stop
    if ($null -ne $process -and $null -ne $process.CreationDate -and
        $process.CreationDate.ToUniversalTime().Ticks -eq $Identity.StartTimeTicks -and
        -not [string]::IsNullOrWhiteSpace([string]$process.ExecutablePath) -and
        (Get-CanonicalWindowsPath ([string]$process.ExecutablePath)) -ieq $Identity.ExecutablePath) {
        throw 'client_service_stop_failed'
    }
}

function Remove-OwnedClientService {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$ExpectedExecutable, [switch]$Required)
    $service = Get-TrackedService -Name $Name
    if ($null -eq $service) {
        if ($Required) { throw 'client_service_ownership_unavailable' }
        return
    }
    $identity = Assert-ServiceRegistrationOwned -Service $service -ExpectedExecutable $ExpectedExecutable
    if ($service.State -ne 'Stopped') {
        if ($service.State -ne 'Running') { throw 'client_service_ownership_unavailable' }
        Stop-Service -Name $Name -Force -ErrorAction Stop
        $service = Wait-ServiceStopped -Name $Name
        if ($null -eq $service) { throw 'client_service_ownership_unavailable' }
    }
    Assert-OldServiceProcessExited -Identity $identity
    $sc = (Get-Command sc.exe -ErrorAction Stop).Source
    $null = Invoke-CapturedProcess -FilePath $sc -ArgumentList @('delete', $Name) -TimeoutSeconds 20 -WorkingDirectory $script:taskRoot
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-TrackedService -Name $Name)) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'client_service_remove_failed'
}

function Assert-CleanClientInstallationBaseline {
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $common = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $script:serviceInstallRoot = Join-Path $programFiles 'NetRatel\Client'
    $script:serviceStateRoot = Join-Path $common 'NetRatel\update'
    $credentialDirectory = Join-Path $common 'NetRatel'
    $script:serviceCredentialFiles = @(
        (Join-Path $credentialDirectory 'agent.dat'),
        (Join-Path $credentialDirectory '.netratel-credential-machine-id'))
    $script:serviceLogDirectories = @(
        (Join-Path $credentialDirectory 'logs'),
        (Join-Path $credentialDirectory 'Client\logs'))
    $script:baselineServiceAbsent = ($null -eq (Get-TrackedService -Name 'NetRatel.Client')) -and ($null -eq (Get-TrackedService -Name 'NetRatel.Update'))
    if (-not $script:baselineServiceAbsent) { throw 'client_service_preexists' }
    $paths = @($script:serviceInstallRoot, $script:serviceStateRoot) + $script:serviceCredentialFiles + $script:serviceLogDirectories
    $script:baselineDirectoryStates = [ordered]@{}
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) { throw 'client_default_directory_preexists' }
        $script:baselineDirectoryStates[[System.IO.Path]::GetFullPath($path)] = $false
    }
    $script:baselineDirectoriesAbsent = $true
}

function Remove-OwnedDefaultDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $canonical = [System.IO.Path]::GetFullPath($Path)
    if (-not $script:baselineDirectoryStates.Contains($canonical) -or $script:baselineDirectoryStates[$canonical]) {
        throw 'client_default_directory_preexists'
    }
    if (-not (Test-Path -LiteralPath $canonical)) { return }
    $item = Get-Item -LiteralPath $canonical -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'client_default_directory_preexists'
    }
    foreach ($child in Get-ChildItem -LiteralPath $canonical -Force -Recurse -ErrorAction Stop) {
        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'client_default_directory_preexists' }
    }
    Remove-Item -LiteralPath $canonical -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $canonical) { throw 'client_default_directory_preexists' }
}

function Remove-OwnedClientInstallation {
    if (-not $script:baselineServiceAbsent -or -not $script:baselineDirectoriesAbsent) { throw 'client_service_ownership_unavailable' }
    $client = Get-TrackedService -Name 'NetRatel.Client'
    if ($null -ne $client -or $script:initialAcceptance -eq 'completed') {
        $expectedExe = Join-Path (Join-Path (Join-Path $script:serviceInstallRoot 'versions') $script:productVersion) 'NetRatel.Client.exe'
        Remove-OwnedClientService -Name 'NetRatel.Client' -ExpectedExecutable $expectedExe -Required:($script:initialAcceptance -eq 'completed')
    }
    $updater = Get-TrackedService -Name 'NetRatel.Update'
    if ($null -ne $updater) {
        $expectedUpdater = Join-Path (Join-Path (Join-Path $script:serviceInstallRoot 'versions') $script:productVersion) 'NetRatel.Update.exe'
        Remove-OwnedClientService -Name 'NetRatel.Update' -ExpectedExecutable $expectedUpdater
    }
    $script:cleanup.client_service_removed = $null -eq (Get-TrackedService -Name 'NetRatel.Client')
    $script:cleanup.updater_service_removed = $null -eq (Get-TrackedService -Name 'NetRatel.Update')
    if (-not $script:cleanup.client_service_removed -or -not $script:cleanup.updater_service_removed) { throw 'client_service_remove_failed' }

    foreach ($file in $script:serviceCredentialFiles) {
        if (Test-Path -LiteralPath $file) {
            $item = Get-Item -LiteralPath $file -Force -ErrorAction Stop
            if ($item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'client_default_directory_preexists' }
            Remove-Item -LiteralPath $file -Force -ErrorAction Stop
        }
    }
    $directories = @($script:serviceInstallRoot, $script:serviceStateRoot) + $script:serviceLogDirectories
    foreach ($directory in $directories | Sort-Object { $_.Length } -Descending) { Remove-OwnedDefaultDirectory -Path $directory }
    $script:cleanup.client_default_directories_removed = $true
}

function Set-IntegratedTraefikConfiguration {
    param([switch]$IncludeWeb)
    $apiRule = 'Host(`' + $script:hostname + '`) && (PathPrefix(`/api`) || PathPrefix(`/health`) || PathPrefix(`/clients/install`) || PathPrefix(`/hubs`) || PathPrefix(`/connect/token`))'
    $gatewayRule = 'Host(`' + $script:hostname + '`) && PathPrefix(`/netratel.gateway.v1.`)'
    $routers = [ordered]@{
        api = [ordered]@{ rule = $apiRule; priority = 200; entryPoints = @('websecure'); tls = [ordered]@{}; service = 'actual-api' }
        gateway = [ordered]@{ rule = $gatewayRule; priority = 1000; entryPoints = @('websecure'); tls = [ordered]@{}; service = 'actual-gateway' }
        web = [ordered]@{ rule = 'Host(`' + $script:hostname + '`)'; priority = 1; entryPoints = @('websecure'); tls = [ordered]@{}; service = 'actual-web' }
    }
    if (-not $IncludeWeb) { $routers.Remove('web') }
    $services = [ordered]@{
        'actual-api' = [ordered]@{ loadBalancer = [ordered]@{ servers = @([ordered]@{ url = "http://127.0.0.1:$($script:apiPort)" }) } }
        'actual-gateway' = [ordered]@{ loadBalancer = [ordered]@{ servers = @([ordered]@{ url = "h2c://127.0.0.1:$($script:apiGatewayPort)" }) } }
        'actual-web' = [ordered]@{ loadBalancer = [ordered]@{ servers = @([ordered]@{ url = "http://127.0.0.1:$($script:webPort)" }) } }
    }
    $dynamic = [ordered]@{
        http = [ordered]@{ routers = $routers; services = $services }
        tls = [ordered]@{ certificates = @([ordered]@{ certFile = $script:serverCertificatePemPath; keyFile = $script:serverPrivateKeyPemPath }) }
    }
    [System.IO.File]::WriteAllText($script:traefikDynamicConfigPath, (ConvertTo-Json -InputObject $dynamic -Depth 12), [System.Text.UTF8Encoding]::new($false))
}

function Set-IntegratedContext {
    $context = [ordered]@{
        schemaVersion = 1
        scope = 'integrated-onboarding'
        sourceSha = $script:sourceSha
        testMergeSha = $script:testMergeSha
        productVersion = $script:productVersion
        runId = $script:runId
        publicOrigin = $script:publicOrigin
        apiBaseUri = "http://127.0.0.1:$($script:apiPort)"
        gatewayBaseUri = "http://127.0.0.1:$($script:apiGatewayPort)"
        webBaseUri = $script:publicOrigin
        tenantId = $script:initializedTenantId
        operatorEmail = $script:operatorEmail
        operatorPasswordFile = $script:passwordFile
        candidateArchive = $script:candidateArchive
        candidateRuntimeId = 'win-x64'
        candidateVersion = $script:productVersion
        candidateSha256 = $script:candidateSha256
        installRoot = $script:serviceInstallRoot
        stateRoot = $script:serviceStateRoot
        identityReceiptPath = $script:identityReceiptPath
        controlRequestPath = $script:controlRequestPath
        controlAckPath = $script:controlAckPath
        safeEvidenceDirectory = $script:safeEvidenceDirectory
    }
    $script:contextPath = Join-Path $script:handoffRoot 'hosted-context.json'
    Write-AtomicJson -Path $script:contextPath -Value $context
    Assert-HandoffPath -Path $script:contextPath -Root $script:handoffRoot -MustExist | Out-Null
}

function Build-HostedAcceptanceAssemblies {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $mainProject = Join-Path $script:repositoryRoot 'src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj'
    $webProject = Join-Path $script:repositoryRoot 'src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj'
    $webAppProject = Join-Path $script:repositoryRoot 'src/NetRatel/NetRatel.Web/NetRatel.Web.csproj'
    $script:webPublishDirectory = Join-Path $script:taskRoot 'web-publish'
    $script:testResultsDirectory = Join-Path $script:taskRoot 'test-results'
    [void](Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @('build', $mainProject, '--configuration', 'Release', '--verbosity', 'quiet') -TimeoutSeconds 1800 -WorkingDirectory $script:repositoryRoot)
    [void](Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @('build', $webProject, '--configuration', 'Release', '--verbosity', 'quiet') -TimeoutSeconds 1800 -WorkingDirectory $script:repositoryRoot)
    [void](Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @('publish', $webAppProject, '--configuration', 'Release', '--no-restore', '--no-build', '--output', $script:webPublishDirectory, '--verbosity', 'quiet') -TimeoutSeconds 1200 -WorkingDirectory $script:repositoryRoot)
    $script:webDll = Join-Path $script:webPublishDirectory 'NetRatel.Web.dll'
    $script:mainTestModule = Join-Path $script:repositoryRoot 'src/NetRatel/NetRatel.Tests/bin/Release/net10.0/NetRatel.Tests.dll'
    $script:webTestModule = Join-Path $script:repositoryRoot 'src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/NetRatel.Web.PlaywrightTests.dll'
    foreach ($path in @($script:apiDll, $script:webDll, $script:mainTestModule, $script:webTestModule)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'api_publish_output_missing' }
    }
    $programDirectories = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $edgePaths = @($programDirectories | ForEach-Object { Join-Path $_ 'Microsoft\Edge\Application\msedge.exe' })
    if (-not ($edgePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) { throw 'hosted_browser_missing' }
}

function Initialize-ProductionLocalRuntime {
    Stop-OwnedApi
    $script:operatorEmail = "hosted-$($script:runId.Substring(0, 12))@example.test"
    $script:operatorDisplayName = "Hosted operator $($script:runId.Substring(0, 8))"
    $script:tenantName = "Hosted tenant $($script:runId.Substring(0, 8))"
    $bootstrapEnvironment = @{} + $script:apiEnvironment
    $bootstrapEnvironment['Bootstrap__Unattended__PasswordFile'] = $script:passwordFile
    $bootstrapEnvironment['Bootstrap__Unattended__DisplayName'] = $script:operatorDisplayName
    $bootstrapEnvironment['Bootstrap__Unattended__Email'] = $script:operatorEmail
    $bootstrapEnvironment['Bootstrap__Unattended__TenantName'] = $script:tenantName
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $script:currentOperation = 'initialize-unattended-production-local-instance'
    $initialization = Invoke-CapturedProcess -FilePath $dotnet -ArgumentList @($script:apiDll, '--initialize-unattended') -TimeoutSeconds 180 -WorkingDirectory $script:apiPublishDirectory -Environment $bootstrapEnvironment
    if ($initialization.ExitCode -ne 0) { throw 'api_not_ready' }

    Start-OwnedApi
    Wait-ForProductionApiReady -TimeoutSeconds 90
    Assert-InitializedLocalIdentity
    Wait-ForProductionApiReady -TimeoutSeconds 90
}

function Start-OwnedWeb {
    $script:webProcess = Start-OwnedProcess -Role 'web' -FilePath (Get-Command dotnet -ErrorAction Stop).Source -ArgumentList @($script:webDll) -WorkingDirectory $script:webPublishDirectory -Environment @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASPNETCORE_URLS' = "http://127.0.0.1:$($script:webPort)"
        'ApiBaseUrl' = "http://127.0.0.1:$($script:apiPort)"
        'DataProtection__KeysDirectory' = $script:dataProtectionDirectory
        'DataProtection__ApplicationName' = 'NetRatel'
    }
}

function Verify-OnlinePresenceAfterMain {
    if ($null -eq $script:publicApiClient -or $null -eq $script:agentId -or $script:initializedTenantId -le 0) { throw 'hosted_main_test_failed' }
    $response = Send-ApiJson -Client $script:publicApiClient -Method 'GET' -Path '/api/v2/client-presence' -Body $null
    try {
        if ($response.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'hosted_main_test_failed' }
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $items = @($body.items)
        $matches = @($items | Where-Object { [Guid]$_.agentId -eq $script:agentId -and [int]$_.tenantId -eq $script:initializedTenantId -and $_.online })
        if ($matches.Count -ne 1) { throw 'hosted_main_test_failed' }
    }
    finally { $response.Dispose() }
}

function Restart-OwnedTraefik {
    if ($null -ne $script:traefikProcess) {
        if (-not (Stop-OwnedProcess -Record $script:traefikProcess)) { throw 'listener_process_exited' }
        Dispose-OwnedProcess -Record $script:traefikProcess
        $script:cleanup.traefik_process_stopped = $true
    }
    Start-OwnedTraefik
    $response = Wait-ForHttpsResponse -Client $script:httpClient -Uri "$($script:publicOrigin)/api/v2/setup/status" -TimeoutSeconds 45
    if ($response.StatusCode -ne 200) { throw 'https_route_timeout' }
}

function Wait-ForControlledTest {
    param([Parameter(Mandatory)][object]$Record, [Parameter(Mandatory)][int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-OwnedProcessRunning -Record $Record)) {
            if (-not (Stop-OwnedProcess -Record $Record)) {
                $script:cleanup.captured_processes_stopped = $false
                $script:pendingCapturedProcesses.Add($Record)
                throw 'process_output_pipe_timeout'
            }
            Dispose-OwnedProcess -Record $Record
            return [int]$Record.ExitCode
        }
        Invoke-PendingControlRequest
        Start-Sleep -Milliseconds 250
    }
    $script:currentOperation = "process:$($Record.Role)"
    if (-not (Stop-OwnedProcess -Record $Record)) {
        $script:cleanup.captured_processes_stopped = $false
        $script:pendingCapturedProcesses.Add($Record)
    }
    else { Dispose-OwnedProcess -Record $Record }
    throw 'process_timeout'
}

function Invoke-PendingControlRequest {
    if (-not (Test-Path -LiteralPath $script:controlRequestPath -PathType Leaf)) { return }
    Assert-HandoffPath -Path $script:controlRequestPath -Root $script:handoffRoot -MustExist | Out-Null
    $item = Get-Item -LiteralPath $script:controlRequestPath -Force -ErrorAction Stop
    if ($item.Length -gt 4096) { throw 'control_request_invalid' }
    try { $request = Read-BoundedHandoffJson -Path $script:controlRequestPath -MaximumBytes 4096 }
    catch { throw 'control_request_invalid' }
    $requiredFields = @('schemaVersion', 'scope', 'runId', 'requestId', 'sequence', 'action')
    if ($request -isnot [System.Management.Automation.PSCustomObject] -or
        @($request.PSObject.Properties).Count -ne $requiredFields.Count -or
        @($requiredFields | Where-Object { $request.PSObject.Properties.Name -notcontains $_ }).Count -ne 0 -or
        $request.schemaVersion -ne 1 -or $request.scope -cne 'integrated-onboarding' -or $request.runId -cne $script:runId -or
        $request.action -isnot [string] -or $request.action -cnotin @('stop_api', 'start_api', 'restart_api')) {
        throw 'control_request_invalid'
    }
    $requestGuid = [Guid]::Empty
    $sequence = 0L
    if ($request.requestId -isnot [string] -or
        -not [Guid]::TryParseExact([string]$request.requestId, 'D', [ref]$requestGuid) -or $requestGuid -eq [Guid]::Empty -or
        [string]$request.sequence -notmatch '^[1-9][0-9]{0,17}$' -or
        -not [long]::TryParse([string]$request.sequence, [ref]$sequence) -or $sequence -le 0) {
        throw 'control_request_invalid'
    }
    if ($sequence -eq $script:lastControlSequence -and
        $requestGuid.ToString('D') -ceq $script:lastControlRequestId -and
        $request.action -ceq $script:lastControlAction) { return }
    if ($sequence -le $script:lastControlSequence -or -not $script:seenControlRequestIds.Add($requestGuid.ToString('D'))) {
        throw 'control_request_invalid'
    }
    $script:lastControlRequestId = $requestGuid.ToString('D')
    $script:lastControlAction = [string]$request.action
    $script:lastControlSequence = $sequence
    $result = 'passed'
    try {
        switch ($request.action) {
            'stop_api' { Stop-OwnedApi }
            'start_api' {
                Start-OwnedApi
                Wait-ForProductionApiReady -TimeoutSeconds 90
            }
            'restart_api' {
                Stop-OwnedApi
                Start-OwnedApi
                Wait-ForProductionApiReady -TimeoutSeconds 90
            }
        }
    }
    catch {
        $result = 'failed'
        $script:controlActionFailures.Add([string]$request.action)
        Add-SafeFailureDetail -Check 'api_phase_control' -Operation "control:$([string]$request.action)" -ExceptionCategory $_.Exception.GetBaseException().GetType().Name -ExitCode $null -SafeCode 'control_action_failed'
    }
    $ack = [ordered]@{
        schemaVersion = 1
        scope = 'integrated-onboarding'
        runId = $script:runId
        requestId = $requestGuid.ToString('D')
        sequence = [long]$request.sequence
        action = [string]$request.action
        result = $result
    }
    try { Write-AtomicJson -Path $script:controlAckPath -Value $ack }
    catch { throw 'control_ack_write_failed' }
}

function Invoke-HostedMtpMethod {
    param([Parameter(Mandatory)][string]$Role, [Parameter(Mandatory)][string]$TestModule, [Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$ResultsDirectory, [Parameter(Mandatory)][string]$TrxName, [Parameter(Mandatory)][int]$TimeoutSeconds)
    [System.IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
    $trx = Join-Path $ResultsDirectory $TrxName
    if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx -Force -ErrorAction Stop }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $environment = @{ 'NETRATEL_HOSTED_ONBOARDING_CONTEXT_FILE' = $script:contextPath }
    $record = Start-OwnedProcess -Role $Role -FilePath $dotnet -ArgumentList @('test', '--test-modules', $TestModule,
        '--filter-method', $Method, '--minimum-expected-tests', '1', '--max-threads', '1',
        '--results-directory', $ResultsDirectory, '--report-trx', '--report-trx-filename', $TrxName) -WorkingDirectory $script:repositoryRoot -Environment $environment
    $exitCode = Wait-ForControlledTest -Record $record -TimeoutSeconds $TimeoutSeconds
    if ($exitCode -ne 0) {
        $script:lastProcessExitCode = $exitCode
        throw $(if ($Role -eq 'main-test') { 'hosted_main_test_failed' } else { 'hosted_web_test_failed' })
    }
    $safeCode = if ($Role -eq 'main-test') { 'hosted_main_test_missing_or_zero' } else { 'hosted_web_test_missing_or_zero' }
    return Assert-TestResults -Path $trx -SafeFailureCode $safeCode
}

function Export-SafeEvidence {
    if ([string]::IsNullOrWhiteSpace($script:evidenceExportDirectory) -or -not (Test-Path -LiteralPath $script:evidenceExportDirectory -PathType Container)) { return }
    foreach ($name in @('clients-operator-desktop-light.png', 'clients-operator-phone-dark.png', 'hosted-directory-api-unavailable.png', 'hosted-directory-api-recovered.png')) {
        $source = Join-Path $script:safeEvidenceDirectory $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Assert-HandoffPath -Path $source -Root $script:handoffRoot -MustExist | Out-Null
            $destination = Join-Path $script:evidenceExportDirectory $name
            Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
        }
    }
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
        if (($null -ne $script:apiProcess -and -not (Test-OwnedProcessRunning -Record $script:apiProcess)) -or
            ($null -ne $script:webProcess -and -not (Test-OwnedProcessRunning -Record $script:webProcess)) -or
            ($null -ne $script:traefikProcess -and -not (Test-OwnedProcessRunning -Record $script:traefikProcess))) {
            throw 'listener_process_exited'
        }
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
    param([Parameter(Mandatory)][string]$StoreLocation, [Parameter(Mandatory)][string]$StoreName, [Parameter(Mandatory)][string]$Thumbprint)
    $storePath = "Cert:\$StoreLocation\$StoreName"
    $matches = @(Get-ChildItem -LiteralPath $storePath -ErrorAction Stop | Where-Object { $_.Thumbprint -eq $Thumbprint })
    foreach ($certificate in $matches) {
        if ($StoreLocation -eq 'CurrentUser' -and $StoreName -eq 'My') {
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
        scope = $script:scope
        integratedAcceptance = $script:integratedAcceptance
        initialAcceptance = $script:initialAcceptance
        mandatoryLaterAcceptance = $script:mandatoryLaterAcceptance
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
        progress = [ordered]@{
            check = $script:currentCheck
            operation = $script:currentOperation
        }
        testResults = [ordered]@{
            main = $script:mainTestCounts
            web = $script:webTestCounts
        }
        postgresIdentityDiagnostic = $script:postgresIdentityDiagnostic
        failedCheck = $script:failureCode
        failureDetails = @($script:failureDetails)
    }
    $parent = Split-Path -Parent $ReceiptPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    $temporary = "$ReceiptPath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        [System.IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $receipt -Depth 8) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        if ([System.IO.File]::Exists($ReceiptPath)) { [System.IO.File]::Replace($temporary, $ReceiptPath, $null, $true) }
        else { [System.IO.File]::Move($temporary, $ReceiptPath) }
    }
    finally { if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) } }
}

function Write-QualificationCheckpoint {
    try { Write-QualificationReceipt }
    catch { throw 'receipt_write_failed' }
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
        $script:repositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
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
        $script:runId = [Guid]::NewGuid().ToString('D')
        $script:taskRoot = Join-Path $env:RUNNER_TEMP "netratel-hosted-onboarding-$($script:runId.Replace('-', ''))"
        [System.IO.Directory]::CreateDirectory($script:taskRoot) | Out-Null
        $script:cleanup.task_files_removed = $false
        $script:currentOperation = 'protect-owned-task-directory'
        Protect-TaskRoot -Path $script:taskRoot
        Assert-ProtectedDirectory -Path $script:taskRoot
        $script:hostname = "netratel-$($script:runId.Replace('-', '')).test"
        $script:publicOrigin = "https://$($script:hostname)"
        $script:apiPort = Get-FreeLoopbackPort
        do { $script:postgresPort = Get-FreeLoopbackPort } while ($script:postgresPort -eq $script:apiPort)
        do { $script:apiGatewayPort = Get-FreeLoopbackPort } while ($script:apiGatewayPort -eq $script:apiPort -or $script:apiGatewayPort -eq $script:postgresPort -or $script:apiGatewayPort -eq $script:traefikPort)
        do { $script:webPort = Get-FreeLoopbackPort } while ($script:webPort -in @($script:apiPort, $script:postgresPort, $script:apiGatewayPort, $script:traefikPort))
        New-PrivateHandoff
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
        Build-HostedAcceptanceAssemblies
    }

    Invoke-QualificationCheck 'actual_api_bootstrap_listener' {
        $script:bootstrapStateDirectory = Join-Path $script:taskRoot 'bootstrap-state'
        $script:dataProtectionDirectory = Join-Path $script:taskRoot 'data-protection-keys'
        $script:storageRoot = Join-Path $script:taskRoot 'storage'
        $script:artifactStorageRoot = Join-Path $script:taskRoot 'client-artifacts'
        foreach ($path in @($script:bootstrapStateDirectory, $script:dataProtectionDirectory, $script:storageRoot, $script:artifactStorageRoot)) {
            [System.IO.Directory]::CreateDirectory($path) | Out-Null
        }
        if (Test-Path -LiteralPath (Join-Path $script:bootstrapStateDirectory 'descriptor.json')) { throw 'bootstrap_state_not_fresh' }
        New-StableAgentSigningKey
        $script:apiEnvironment = New-ApiEnvironment
        $probeHandler = [System.Net.Http.HttpClientHandler]::new()
        $probeHandler.AllowAutoRedirect = $false
        $probeHandler.UseProxy = $false
        $script:apiProbeClient = [System.Net.Http.HttpClient]::new($probeHandler)
        $script:apiProbeClient.Timeout = [TimeSpan]::FromSeconds(4)
        Start-OwnedApi
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseProxy = $false
        $script:httpClient = [System.Net.Http.HttpClient]::new($handler)
        $script:httpClient.Timeout = [TimeSpan]::FromSeconds(4)

        $live = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (-not (Test-OwnedProcessRunning -Record $script:apiProcess)) { throw 'api_process_exited_before_listener' }
            try {
                $response = $script:apiProbeClient.GetAsync("http://127.0.0.1:$($script:apiPort)/health/live").GetAwaiter().GetResult()
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

        $statusResponse = $script:apiProbeClient.GetAsync("http://127.0.0.1:$($script:apiPort)/api/v2/setup/status").GetAwaiter().GetResult()
        try {
            if ($statusResponse.StatusCode -ne [System.Net.HttpStatusCode]::OK) { throw 'api_setup_status_unavailable' }
            $setupStatus = $statusResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        }
        finally { $statusResponse.Dispose() }
        $descriptorPath = Join-Path $script:bootstrapStateDirectory 'descriptor.json'
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
        Set-QualificationOperation 'create-root-certificate'
        $script:rootCertificate = New-SelfSignedCertificate -Subject "CN=NetRatel hosted qualification CA $([Guid]::NewGuid().ToString('N'))" -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddHours(4) -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
        $script:rootCertificateThumbprint = $script:rootCertificate.Thumbprint
        $script:cleanup.trust_certificates_removed = $false
        Set-QualificationOperation 'create-server-certificate'
        $script:serverCertificate = New-SelfSignedCertificate -Subject "CN=$($script:hostname)" -DnsName $script:hostname -Signer $script:rootCertificate -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature, KeyEncipherment -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddHours(2) -TextExtension @('2.5.29.19={critical}{text}ca=0', '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
        $script:serverCertificateThumbprint = $script:serverCertificate.Thumbprint

        $script:serverCertificatePemPath = Join-Path $script:taskRoot 'tls-certificate.pem'
        $script:serverPrivateKeyPemPath = Join-Path $script:taskRoot 'tls-private-key.pem'
        Set-QualificationOperation 'export-server-certificate'
        [System.IO.File]::WriteAllText($script:serverCertificatePemPath, $script:serverCertificate.ExportCertificatePem(), [System.Text.UTF8Encoding]::new($false))
        Set-QualificationOperation 'export-server-private-key'
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($script:serverCertificate)
        try { [System.IO.File]::WriteAllText($script:serverPrivateKeyPemPath, $rsa.ExportPkcs8PrivateKeyPem(), [System.Text.UTF8Encoding]::new($false)) }
        finally { $rsa.Dispose() }

        $rootCertificatePath = Join-Path $script:taskRoot 'qualification-ca.cer'
        Set-QualificationOperation 'export-root-certificate'
        [System.IO.File]::WriteAllBytes($rootCertificatePath, $script:rootCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Set-QualificationOperation 'trust-local-machine-root'
        $certutil = (Get-Command certutil.exe -ErrorAction Stop).Source
        $null = Invoke-CapturedProcess -FilePath $certutil -ArgumentList @('-f', '-addstore', 'Root', $rootCertificatePath) -TimeoutSeconds 30 -WorkingDirectory $script:taskRoot
        Set-QualificationOperation 'verify-local-machine-root'
        $machineRootMatches = @(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\Root' -ErrorAction Stop | Where-Object { $_.Thumbprint -eq $script:rootCertificateThumbprint })
        if ($machineRootMatches.Count -ne 1) { throw 'local_machine_trust_failed' }
        $script:localMachineRootCertificateImported = $true
        Set-QualificationOperation 'write-hosts-entry'
        Add-OwnedHostsEntry -HostName $script:hostname -Marker "netratel-hosted-onboarding-$($script:runId.Replace('-', ''))"

        $script:traefikDynamicConfigPath = Join-Path $script:taskRoot 'traefik-dynamic.json'
        $script:traefikStaticConfigPath = Join-Path $script:taskRoot 'traefik-static.json'
        $staticConfig = [ordered]@{
            entryPoints = [ordered]@{ websecure = [ordered]@{ address = "127.0.0.1:$($script:traefikPort)" } }
            providers = [ordered]@{ file = [ordered]@{ filename = $script:traefikDynamicConfigPath; watch = $false } }
            log = [ordered]@{ level = 'ERROR' }
        }
        Set-QualificationOperation 'write-traefik-configuration'
        [System.IO.File]::WriteAllText($script:traefikStaticConfigPath, (ConvertTo-Json -InputObject $staticConfig -Depth 6), [System.Text.UTF8Encoding]::new($false))
        Set-IntegratedTraefikConfiguration
        Set-QualificationOperation 'start-traefik'
        Start-OwnedTraefik

        Set-QualificationOperation 'verify-strict-https'
        $status = Wait-ForHttpsResponse -Client $script:httpClient -Uri "$($script:publicOrigin)/api/v2/setup/status"
        $setupStatus = $status.Body | ConvertFrom-Json
        if (-not $setupStatus.setupRequired -or $setupStatus.isReady) { throw 'https_api_not_in_unconfigured_bootstrap_state' }
        if ($setupStatus.state -ne 0) { throw 'https_api_not_in_unconfigured_bootstrap_state' }
    }

    Invoke-QualificationCheck 'initialized_production_local_api' { Initialize-ProductionLocalRuntime }

    Invoke-QualificationCheck 'verified_candidate_client_archive' {
        $script:currentOperation = 'prepare-verified-candidate-client-archive'
        $archiveDirectory = $env:NETRATEL_CLIENT_ARTIFACT_DIRECTORY
        if ([string]::IsNullOrWhiteSpace($archiveDirectory)) { throw 'candidate_archive_missing' }
        $archiveName = "netratel-client-$($script:productVersion)-win-x64.zip"
        Assert-ClientArchive -ArchivePath (Join-Path $archiveDirectory $archiveName) -ArtifactDirectory $archiveDirectory
        $script:checks.verified_candidate_client_archive = 'passed'
    }

    Invoke-QualificationCheck 'client_installation_baseline' {
        Assert-CleanClientInstallationBaseline
    }

    Invoke-QualificationCheck 'actual_web_listener' {
        Set-IntegratedContext
        Set-IntegratedTraefikConfiguration -IncludeWeb
        Restart-OwnedTraefik
        Start-OwnedWeb
        $webResponse = Wait-ForHttpsResponse -Client $script:httpClient -Uri "$($script:publicOrigin)/login?ReturnUrl=%2Fclients" -TimeoutSeconds 90
        if ($webResponse.StatusCode -ne 200) { throw 'https_route_timeout' }
    }

    Invoke-QualificationCheck 'hosted_main_onboarding' {
        $script:clientInstallationAttempted = $true
        $script:mainTrxPath = Join-Path $script:testResultsDirectory 'main-hosted-onboarding.trx'
        $script:mainTestCounts = Invoke-HostedMtpMethod -Role 'main-test' -TestModule $script:mainTestModule `
            -Method 'NetRatel.Tests.Infrastructure.WindowsHostedOnboardingTests.ProductionLocalOnboarding_ActualSystemServiceReachesTwoAcknowledgedHeartbeatsAndDirectory' `
            -ResultsDirectory $script:testResultsDirectory -TrxName 'main-hosted-onboarding.trx' -TimeoutSeconds 2400
        Read-HostedIdentityReceipt
        Verify-OnlinePresenceAfterMain
    }

    Invoke-QualificationCheck 'hosted_web_directory' {
        $script:webTrxPath = Join-Path $script:testResultsDirectory 'web-hosted-onboarding.trx'
        $script:webTestCounts = Invoke-HostedMtpMethod -Role 'web-test' -TestModule $script:webTestModule `
            -Method 'NetRatel.Web.PlaywrightTests.WindowsHostedOnboardingBrowserSmokeTests.ProductionLocalOnboarding_DirectoryAndScopedIdentitiesUseTheSameAuthenticatedRuntime' `
            -ResultsDirectory $script:testResultsDirectory -TrxName 'web-hosted-onboarding.trx' -TimeoutSeconds 2400
        Verify-OnlinePresenceAfterMain
        if ($script:controlActionFailures.Count -gt 0) { throw 'control_action_failed' }
    }
    $script:initialAcceptance = 'completed'
    $script:integratedAcceptance = 'initial_completed'
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
    foreach ($client in @($script:publicApiClient, $script:httpClient, $script:apiProbeClient)) {
        if ($null -ne $client) {
            try { $client.Dispose() }
            catch { $cleanupFailed = $true; Add-CleanupFailure -Operation 'dispose-http-client' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name }
        }
    }

    $script:cleanup.captured_processes_stopped = $true
    foreach ($record in @($script:pendingCapturedProcesses)) {
        if (-not (Stop-OwnedProcess -Record $record)) {
            $cleanupFailed = $true
            $script:cleanup.captured_processes_stopped = $false
            Add-CleanupFailure -Operation $record.Role -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { Dispose-OwnedProcess -Record $record }
    }

    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -in @('main-test', 'web-test') -and -not $_.Stopped })) {
        if (-not (Stop-OwnedProcess -Record $record)) {
            $cleanupFailed = $true
            $script:cleanup.captured_processes_stopped = $false
            Add-CleanupFailure -Operation "stop-$($record.Role)" -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { Dispose-OwnedProcess -Record $record }
    }

    if ($script:clientInstallationAttempted) {
        if (-not $script:cleanup.captured_processes_stopped) {
            $script:cleanup.client_service_removed = $false
            $script:cleanup.updater_service_removed = $false
            $script:cleanup.client_default_directories_removed = $false
            $script:checks.owned_client_cleanup = 'failed'
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'retain-owned-client-installation' -ExceptionCategory 'InstallerProcessNotProvenStopped'
        }
        else {
            try {
                Remove-OwnedClientInstallation
                $script:checks.owned_client_cleanup = 'passed'
            }
            catch {
                $script:cleanup.client_service_removed = $false
                $script:cleanup.updater_service_removed = $false
                $script:cleanup.client_default_directories_removed = $false
                $script:checks.owned_client_cleanup = 'failed'
                $cleanupFailed = $true
                Add-CleanupFailure -Operation 'remove-owned-client-installation' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
            }
        }
    }

    $allWebProcessesStopped = $true
    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -eq 'web' })) {
        $stopped = Stop-OwnedProcess -Record $record
        if (-not $stopped) {
            $cleanupFailed = $true
            $allWebProcessesStopped = $false
            Add-CleanupFailure -Operation 'stop-owned-web' -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { Dispose-OwnedProcess -Record $record }
    }
    $script:cleanup.web_process_stopped = $allWebProcessesStopped
    $allTraefikProcessesStopped = $true
    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -eq 'traefik' })) {
        $stopped = Stop-OwnedProcess -Record $record
        if (-not $stopped) {
            $cleanupFailed = $true
            $allTraefikProcessesStopped = $false
            Add-CleanupFailure -Operation 'stop-owned-traefik' -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { Dispose-OwnedProcess -Record $record }
    }
    $script:cleanup.traefik_process_stopped = $allTraefikProcessesStopped
    $allApiProcessesStopped = $true
    foreach ($record in @($script:trackedProcesses | Where-Object { $_.Role -eq 'api' })) {
        $stopped = Stop-OwnedProcess -Record $record
        if (-not $stopped) {
            $cleanupFailed = $true
            $allApiProcessesStopped = $false
            Add-CleanupFailure -Operation 'stop-owned-api' -ExceptionCategory 'OwnedProcessStillRunningOrOutputOpen'
        }
        else { Dispose-OwnedProcess -Record $record }
    }
    $script:cleanup.api_process_stopped = $allApiProcessesStopped

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

    if ($script:cleanup.api_process_stopped -and $script:cleanup.traefik_process_stopped -and $script:cleanup.web_process_stopped) {
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
                Remove-TrustedCertificate -StoreLocation 'LocalMachine' -StoreName 'Root' -Thumbprint $script:rootCertificateThumbprint
                Remove-TrustedCertificate -StoreLocation 'CurrentUser' -StoreName 'My' -Thumbprint $script:rootCertificateThumbprint
            }
            if ($script:serverCertificateThumbprint) {
                $script:currentOperation = 'remove-owned-server-certificate-and-key'
                Remove-TrustedCertificate -StoreLocation 'CurrentUser' -StoreName 'My' -Thumbprint $script:serverCertificateThumbprint
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

    if ($script:cleanup.captured_processes_stopped -and $null -ne $script:safeEvidenceDirectory) {
        try { Export-SafeEvidence }
        catch {
            $cleanupFailed = $true
            Add-CleanupFailure -Operation 'export-allowlisted-browser-evidence' -ExceptionCategory $_.Exception.GetBaseException().GetType().Name
        }
    }

    $clientCleanupComplete = -not $script:clientInstallationAttempted -or
        ($script:cleanup.client_service_removed -and $script:cleanup.updater_service_removed -and $script:cleanup.client_default_directories_removed)
    $canRemoveTaskRoot = $null -ne $script:taskRoot -and $script:cleanup.postgresql_stopped -and $script:cleanup.api_process_stopped -and $script:cleanup.traefik_process_stopped -and $script:cleanup.web_process_stopped -and $script:cleanup.captured_processes_stopped -and $script:cleanup.hosts_entry_removed -and $script:cleanup.trust_certificates_removed -and $clientCleanupComplete -and (-not $script:localUserCreated -or $script:cleanup.local_user_removed)
    if ($canRemoveTaskRoot) {
        try {
            $expectedParent = [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
            $actualParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $script:taskRoot)).TrimEnd('\')
            if ($actualParent -cne $expectedParent -or (Split-Path -Leaf $script:taskRoot) -notmatch '^netratel-hosted-onboarding-[0-9a-f]{32}$') { throw 'task_root_ownership_check_failed' }
            Assert-ProtectedDirectory -Path $script:taskRoot
            Assert-DirectoryTreeHasNoReparsePoints -Path $script:taskRoot
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
    Write-Error "Windows hosted onboarding failed at check '$($script:failureCode)'. Detailed process output and temporary configuration are not uploaded."
    exit 1
}
Write-Host 'Windows hosted initial onboarding acceptance passed. Later mandatory lifecycle and upgrade phases remain unrun.'
