# Supported HTTP MCP client transport

Use the consumer's maintained native Streamable HTTP transport when available. Configure the canonical HTTPS `/mcp` URL and its protected HttpMcp-purpose credential; [Netclaw](netclaw-http-mcp.md) documents one supported native connection. For a consumer that only launches stdio processes, the existing NetRatel stdio distribution also provides `NetRatel.Mcp http-bridge`. Its version is the product version; it is maintained and distributed with the repository, rather than downloaded as an unrelated wrapper.

## Obtain and configure the bridge

Run these commands on the **consumer's machine**, as the operating-system user running that consumer. The supported stdio archives target Linux x64 and Windows x64, with matching release-runner smoke tests. The bridge uses integration-credential or configured app-password client profiles supported by shared setup; external interactive OAuth clients should use their maintained native HTTP transport. No SDK, source build or local release image is needed.

### Linux x64

Install the [.NET 10 runtime](https://learn.microsoft.com/dotnet/core/install/linux), and obtain Bash, `curl`, Python 3, `tar` and GNU coreutils through the operating system's package manager.

Select the completed published release from [GitHub Releases](https://github.com/BostonTechnologies/netratel/releases). Replace the placeholder below with that release tag. Download its matching stdio archive and checksums into a new directory:

```sh
set -eu
release_tag='REPLACE_WITH_PUBLISHED_RELEASE_TAG'
release_version=${release_tag#v}
archive=netratel-mcp-stdio-${release_version}-linux-x64.tar.gz
mkdir -p netratel-http-client
cd netratel-http-client
curl --fail --silent --show-error --location \
  "https://github.com/BostonTechnologies/netratel/releases/download/$release_tag/${archive}" \
  --output "$archive"
curl --fail --silent --show-error --location \
  "https://github.com/BostonTechnologies/netratel/releases/download/$release_tag/SHA256SUMS" \
  --output SHA256SUMS
python3 - "$archive" <<'PY'
import hashlib, pathlib, sys
name = sys.argv[1]
rows = [line.split() for line in pathlib.Path('SHA256SUMS').read_text().splitlines()]
matches = [digest for digest, filename in rows if filename.lstrip('*') == name]
if len(matches) != 1 or hashlib.sha256(pathlib.Path(name).read_bytes()).hexdigest() != matches[0]:
    raise SystemExit('Archive checksum missing, ambiguous or mismatched')
PY
tar -xzf "$archive"
cd netratel-mcp-linux-x64
dotnet ./NetRatel.Mcp.dll http-bridge --version
dotnet ./NetRatel.Mcp.dll setup --transport http --profile http-operator
```

The archive contains the `netratel-mcp-linux-x64` directory. If a selected older release does not support `http-bridge`, obtain the correction release before migration. Retain the verified archive/checksum and release publication record privately. Review the Web URL, canonical MCP resource, credential purpose, least-privilege grants and exact UTC expiry during setup. Complete the separate reviewed operation-admission workflow before tenant calls. The bridge consumes the same isolated `HttpMcpClient` profile and protected credential-file references as setup; no raw token belongs in command arguments, launch configuration, output or receipts.

Configure the stdio consumer using absolute paths after extraction:

```json
{
  "command": "dotnet",
  "args": ["/absolute/path/netratel-http-client/netratel-mcp-linux-x64/NetRatel.Mcp.dll", "http-bridge", "--profile", "http-operator"]
}
```

For an already reviewed externally managed profile, use `"--config", "/absolute/protected/http-client.json"` in place of `"--profile", "http-operator"`. Do not point the bridge at a stdio/API-purpose profile or a server outbound profile. Setup, configuration validation and doctor remain explicit commands; ordinary bridge startup writes only JSON-RPC to stdout and fixed, redacted diagnostics to stderr. Profile precedence and origin/purpose bindings remain those of [shared configuration](configuration.md).

### Windows x64

Install the [.NET 10 runtime for Windows](https://learn.microsoft.com/dotnet/core/install/windows) and [PowerShell 7.4 or later](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows). Open PowerShell as the same Windows account that runs the consumer. Select the completed published release and an existing local parent whose complete ancestry is owned by that account or trusted Windows system identities and cannot be replaced by another identity. A directory name such as AppData or Documents does not establish that safety. Create a new private per-version child, then download there:

```powershell
$ErrorActionPreference = 'Stop'
$releaseTag = 'REPLACE_WITH_PUBLISHED_RELEASE_TAG'
$releaseVersion = $releaseTag.TrimStart('v')
$archive = "netratel-mcp-stdio-$releaseVersion-win-x64.zip"
$layoutParent = 'C:\absolute\owner-controlled-parent' # Replace with the selected existing parent.
if (-not (Test-Path -LiteralPath $layoutParent -PathType Container)) { throw 'Select an existing local parent.' }
$clientRoot = Join-Path $layoutParent "mcp-client-$releaseVersion"
New-Item -ItemType Directory -Path $clientRoot | Out-Null
$account = [Security.Principal.WindowsIdentity]::GetCurrent().User
$privateAcl = [Security.AccessControl.DirectorySecurity]::new()
$privateAcl.SetOwner($account)
$privateAcl.SetAccessRuleProtection($true, $false)
$privateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $account, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $clientRoot -AclObject $privateAcl
foreach ($name in @('references', 'state', 'temp')) {
    New-Item -ItemType Directory -Path (Join-Path $clientRoot $name) | Out-Null
}
Set-Location $clientRoot
$download = "https://github.com/BostonTechnologies/netratel/releases/download/$releaseTag"
Invoke-WebRequest "$download/$archive" -OutFile $archive
Invoke-WebRequest "$download/SHA256SUMS" -OutFile SHA256SUMS
$archiveDigests = @(Get-Content SHA256SUMS | ForEach-Object {
    $parts = $_ -split '\s+', 2
    if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $archive) { $parts[0] }
})
if ($archiveDigests.Count -ne 1 -or (Get-FileHash $archive -Algorithm SHA256).Hash -ine $archiveDigests[0]) {
    throw 'Archive checksum missing, ambiguous or mismatched.'
}
Expand-Archive -LiteralPath $archive -DestinationPath .
Set-Location netratel-mcp-win-x64
.\NetRatel.Mcp.exe http-bridge --version
```

The archive contains `netratel-mcp-win-x64`, the Windows executable, the protected launcher and its small managed byte relay, notices, and this guide. Keep the archive, checksum and publication record. For an ordinary shared HTTP client profile, run `.\NetRatel.Mcp.exe setup --transport http --profile http-operator`, then use its absolute executable path with `http-bridge --profile http-operator` in the consumer launch entry. Windows PowerShell 5.1 is not the supported launcher runtime.

### Retain an existing Windows DPAPI credential

The packaged `http-mcp-windows.ps1` supports an existing Windows `Export-Clixml` file whose root value is a `SecureString` or `PSCredential`. Run it under the account and machine that own that DPAPI value. It rejects plaintext strings, custom object formats, network paths, reparse points and paths replaceable by unrelated identities. An unsupported legacy storage format needs an explicit protected-store migration; do not paste its decrypted token into a profile or use a broad API credential.

Prepare a new secret-free profile without changing the source credential, the current consumer configuration or its processes. Continue in the same PowerShell terminal and extracted Windows directory. Select the encrypted source explicitly; if its existing ancestry is unsuitable, make a byte-identical encrypted copy into the new private layout and verify its hash. Leave the original bytes and ACLs intact:

```powershell
$originalCredential = 'C:\absolute\existing-credential.clixml' # Replace with the retained encrypted input.
$credentialCopy = Join-Path $clientRoot 'credential.clixml'
Copy-Item -LiteralPath $originalCredential -Destination $credentialCopy
if ((Get-FileHash -LiteralPath $originalCredential).Hash -cne (Get-FileHash -LiteralPath $credentialCopy).Hash) {
    throw 'Encrypted credential copy differs.'
}
$preparedJson = .\http-mcp-windows.ps1 -Prepare `
    -Profile (Join-Path $clientRoot 'references\http-client-profile.json') `
    -Resource 'https://netratel.example.invalid/mcp' `
    -CredentialFile $credentialCopy `
    -StateDirectory (Join-Path $clientRoot 'state') `
    -TemporaryDirectory (Join-Path $clientRoot 'temp')
if ($LASTEXITCODE -ne 0) { throw 'Protected layout preparation failed; inspect its bounded stage and path role.' }
$preparation = $preparedJson | ConvertFrom-Json
$preparation | ConvertTo-Json -Depth 5
```

Preparation validates the installation and encrypted source, reference parent, private pending-state directory and effective temporary directory, including their ancestry, reparse points and network-drive checks. These directories must already exist. Only new private directories receive ACLs above; do not repair existing ancestry, original credential ACLs, global `TEMP`/`TMP` or machine execution policy. A failed role requires selecting another suitable layout and rerunning preparation with a new reference path. Preparation defaults `-StateDirectory` to the reference parent and the temporary directory to the process's effective selection when omitted; it validates those defaults too.

Preparation refuses to overwrite a profile. Its inspectable receipt binds the canonical resource, `HttpMcp` purpose, installed bridge version and encrypted file checksum, reports the validated layout, and includes the exact absolute consumer launch entry with `-TemporaryDirectory`. That argument reproduces the selected temporary directory even when the consumer inherits different `TEMP`/`TMP`; the bridge child receives the validated values locally. The profile format remains compatible with existing references. The source file stays encrypted and unchanged; the launcher decrypts it only in memory and supplies it to the one spawned bridge through that child's environment. Runtime checks the selected paths again. A changed encrypted file or bridge version requires a newly reviewed prepared profile.

Use the receipt's `command` and `args` in the consumer's supported configuration editor. The launch form is:

```json
{
  "command": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
  "args": ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", "C:\\absolute\\netratel-mcp-win-x64\\http-mcp-windows.ps1", "-Profile", "C:\\absolute\\references\\http-client-profile.json", "-TemporaryDirectory", "C:\\absolute\\temp"]
}
```

Use the actual command and complete argument list returned by preparation, retaining `-TemporaryDirectory` for ordinary launch and reviewed auxiliary preparation. Existing references can use that explicit argument without changing their schema; retain the original launch entry and reference for rollback. Apply the organization's normal downloaded-script review and execution policy; the launcher does not bypass it. The managed relay flushes early stdout notifications and observes broken pipes while stdin remains open. A relay failure first closes its own child's input, then applies a bounded shutdown to that spawned child if required. It does not restart the consumer or replay an uncertain request.

Preparation, consumer configuration change, reconnect, loaded metadata and admitted business reads are separate receipt fields. Stop only the selected consumer connection using its supported controls, retain the original configuration as rollback, select the prepared launch entry and reconnect once. Rollback restores that saved entry and reconnects it; keep both encrypted storage and the correction archive. The launcher performs no consumer switch automatically.

## Verify the selected host and exact operation contract

### Prove which bridge the consumer is running

An updated launch entry takes effect when the consumer starts that connection
again. A prepared profile, a configuration readback and a successful separately
launched diagnostic bridge do not identify an already running connection. Track
these states independently under [#288](https://github.com/BostonTechnologies/netratel/issues/288).

Before reconnecting, record the selected consumer connection and its process
tree, the old bridge PID/start time, and the verified replacement archive and
release tag. Keep each extracted release directory unchanged. Stop that one
connection through the consumer's supported controls, select the prepared launch
entry, then reconnect. Inspect the new process tree from that consumer: the
protected Windows launcher creates a PowerShell process with one bridge child;
a direct bridge launch has no PowerShell wrapper. A bridge found by its filename
alone may belong to a separate diagnostic session. If the selected consumer
cannot reconnect in the current session, retain `consumerReconnect: "pending"`
until it is reopened through its supported controls.

On Windows, use the same PowerShell 7 terminal and extracted directory from
installation. Obtain the **new bridge child's PID from that selected connection's
process tree**, then replace the placeholder below. Windows supplies
`Get-CimInstance`; PowerShell supplies `Get-FileHash`. This readback omits command
lines and environment variables, which can contain protected inputs:

```powershell
$selectedBridgeProcessId = 0 # Replace with the selected connection's bridge child PID.
if ($selectedBridgeProcessId -le 0) { throw 'Select the actual consumer bridge process first.' }
$expectedExecutable = (Resolve-Path -LiteralPath '.\NetRatel.Mcp.exe').Path
$bridgeProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$selectedBridgeProcessId"
if (-not $bridgeProcess -or $bridgeProcess.ExecutablePath -ine $expectedExecutable) {
    throw 'The selected process is absent or is running a different bridge directory.'
}
$assemblyPath = Join-Path (Split-Path -Parent $expectedExecutable) 'NetRatel.Mcp.dll'
$bridgeVersion = (& $expectedExecutable http-bridge --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'The verified bridge directory failed its local version probe.' }
$parentProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($bridgeProcess.ParentProcessId)"
[ordered]@{
    processId = $bridgeProcess.ProcessId
    parentProcessId = $bridgeProcess.ParentProcessId
    parentName = $parentProcess.Name
    startedAtUtc = $bridgeProcess.CreationDate.ToUniversalTime().ToString('o')
    executable = $bridgeProcess.ExecutablePath
    bridgeVersion = $bridgeVersion
    executableSha256 = (Get-FileHash -LiteralPath $expectedExecutable -Algorithm SHA256).Hash
    assemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
} | ConvertTo-Json
```

Match both hashes to the files extracted from the verified archive, and match
the reported product version and available source suffix to its publication
record. Windows MCP is framework-dependent: the apphost EXE alone does not
identify the managed bridge implementation. Retain the process ancestry linking
this child to the selected consumer and its new start time after the reconnect.
The `--version` probe identifies the unchanged on-disk package; that process
linkage supplies the evidence that the selected connection launched it. If the
process exits or restarts while recording, repeat the readback for the current
connection. Keep local paths and process receipts private.

For Linux, apply the same process-tree check to the selected `dotnet` process
and its absolute `NetRatel.Mcp.dll` argument from the saved launch entry. Hash
that DLL in the verified release directory and compare its `http-bridge
--version` probe; hashing the shared `dotnet` executable identifies the runtime,
not the bridge. Use the consumer's process diagnostics to distinguish its child
from any diagnostic launch. Consumers using native Streamable HTTP have no
NetRatel bridge process: record that transport and the consumer version instead.

### Verify server identity and loaded definitions

After the actual consumer reconnects, call `netratel_capabilities` with `{"operation":"status"}` through that selected native connection. Record the MCP host's own product and informational versions, available public source revision, catalog revision and SHA-256 digest. `netratel_system/version` describes the downstream API; compare both identities with the selected release. Neither response reports the local bridge version. Missing MCP source metadata is reported as unavailable, never inferred from the API version.

For a complex argument shape, call the bounded discovery operation before constructing the business call:

```json
{
  "operation": "schema",
  "request": {"tool": "netratel_policy", "operation": "confirm_create"}
}
```

This returns the complete selected input envelope, confirmation/idempotency requirements and schema digest without dispatching the inspected operation or checking its business authorization. The structured response is limited to 16 KiB; a future oversized contract fails explicitly and points to protocol `tools/list`, with no partial JSON schema. Only exact published tool and operation names are accepted. Compare the current server contract with the consumer's newly loaded definitions; `nativeLoadedMetadata: "not_observed"` means the server cannot certify or refresh that external cache. A successful server-side discovery call alone is not proof that the consumer has reloaded its native tools. Retain the consumer's observed loaded definitions or its supported discovery receipt separately, then make the permitted read through that connection. Keep unobservable loaded metadata marked unobserved even when the read succeeds.

## Streaming, limits and recovery

The relay reads HTTP response headers before consuming the body. It flushes complete JSON-RPC SSE events as they arrive, including UTF-8 characters split across network reads and multiline SSE data. It forwards cancellation notifications over a separate bounded control path while the original request remains pending, then cancels that local response read after the endpoint accepts the notification. String and numeric request IDs retain their distinct association. Ordinary notifications receive no manufactured response.

At most eight ordinary sends and eight control sends are active, with one optional session GET event stream. Each input or response frame is bounded to 1,048,576 characters, and each SSE response/event stream to 16,777,216 characters. Stdout backpressure blocks consumption; no unbounded output queue is maintained. Requests have a five-minute total budget, including token acquisition and streaming. An over-capacity request receives `client_busy`; malformed or overlong stdin receives JSON-RPC `-32700` with `id:null`, then the next frame is processed. A duplicate outstanding ID is rejected. The bridge uses the maintained .NET HTTP stack with automatic redirects disabled; its bounded raw JSON-RPC relay does not translate the server catalog.

A successful initialize retains the session ID/protocol version; the accepted initialized notification then opens the optional GET event stream; HTTP 405 means that the server does not support that stream. An interrupted or exhausted event stream emits a redacted reconnect diagnostic. The relay never automatically reconnects or replays a POST. A session/endpoint HTTP 404 requires explicit diagnosis and reconnection.

HTTP 429 yields safe typed `retryAfterSeconds` or `retryAfterUtc` when the header parses correctly (delta hints must be between zero and seven days), without echoing raw headers or response bodies. HTTP 401/403 admission bodies are bounded to 16 KiB; only known credential codes and a bounded safe correlation identifier are preserved. Known admission rejections report `not_admitted`; timeouts and interrupted remote outcomes remain `uncertain`. Expired/revoked credential remediation is rebuilt from the shared local vocabulary, without copying remote text, credentials or arbitrary headers. Unknown codes retain a generic HTTP error. Timeouts return `transport_timeout`, separately from `request_cancelled` and other transport failures. Interrupted mutations may have completed remotely: reconcile through an admitted read/status operation before deciding whether another mutation is appropriate. `automaticReplay:false` applies to every bridge error. A retry delay alone does not authorize mutation replay.

## Private migration and acceptance receipt

Before switching the consumer, privately record the old adapter's executable/package, source repository or vendor, version, owner and binary checksum where available. If these are unavailable, retain `unknown`; do not attribute that adapter's measured failures to NetRatel's deployed server.

Stop the consumer's old connection, preserve its protected credential storage, install the verified replacement, and replace its launch entry with the explicit bridge command above (or the consumer's maintained native HTTP connection). Reuse a credential only through its reviewed protected storage reference and matching canonical resource; otherwise use client setup to obtain a fresh reviewed credential. Restart/reconnect the consumer and confirm its initialize, catalog and permitted read against the intended deployment. Never run both connections as an accidental retry path.

Keep a private receipt containing the release tag/source SHA, archive checksum, bridge `--version`, transport choice, credential reference identifier (no contents), canonical resource, exact UTC expiry, old adapter provenance or unknowns, reconnect result, and owner acceptance results. Record streaming-before-final and in-flight cancellation separately from repository loopback tests. Include denied/blocked and unexecuted cases accurately. Verify the installed connection and its live behavior before completing the migration.
