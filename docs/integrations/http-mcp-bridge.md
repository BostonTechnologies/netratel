# Supported HTTP MCP client transport

Use the consumer's maintained native Streamable HTTP transport when available. Configure the canonical HTTPS `/mcp` URL and its protected HttpMcp-purpose credential; [Netclaw](netclaw-http-mcp.md) documents one supported native connection. For a consumer that only launches stdio processes, the existing NetRatel stdio distribution also provides `NetRatel.Mcp http-bridge`. Its version is the product version; it is maintained and distributed with the repository, rather than downloaded as an unrelated wrapper.

## Obtain and configure the bridge

Run these commands on the **consumer's machine**, as the operating-system user running that consumer. The supported archive currently targets Linux x64. Install the [.NET 10 runtime](https://learn.microsoft.com/dotnet/core/install/linux), and obtain Bash, `curl`, Python 3, `tar` and GNU coreutils through the operating system's package manager. The bridge uses integration-credential or configured app-password client profiles supported by shared setup; external interactive OAuth clients should use their maintained native HTTP transport. No SDK, source build or local release image is needed.

Select the completed published correction release from [GitHub Releases](https://github.com/BostonTechnologies/netratel/releases). Replace the placeholder below with that release tag. Download its matching stdio archive and checksums into a new directory:

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
dotnet ./NetRatel.Mcp.dll setup --transport http --profile campaign
```

The archive contains the `netratel-mcp-linux-x64` directory. If a selected older release does not support `http-bridge`, obtain the correction release before migration. Retain the verified archive/checksum and release publication record privately. Review the Web URL, canonical MCP resource, credential purpose, least-privilege grants and exact UTC expiry during setup. Complete the separate reviewed operation-admission workflow before tenant calls. The bridge consumes the same isolated `HttpMcpClient` profile and protected credential-file references as setup; no raw token belongs in command arguments, launch configuration, output or receipts.

Configure the stdio consumer using absolute paths after extraction:

```json
{
  "command": "dotnet",
  "args": ["/absolute/path/netratel-http-client/netratel-mcp-linux-x64/NetRatel.Mcp.dll", "http-bridge", "--profile", "campaign"]
}
```

For an already reviewed externally managed profile, use `"--config", "/absolute/protected/http-client.json"` in place of `"--profile", "campaign"`. Do not point the bridge at a stdio/API-purpose profile or a server outbound profile. Setup, configuration validation and doctor remain explicit commands; ordinary bridge startup writes only JSON-RPC to stdout and fixed, redacted diagnostics to stderr. Profile precedence and origin/purpose bindings remain those of [shared configuration](configuration.md).

## Streaming, limits and recovery

The relay reads HTTP response headers before consuming the body. It flushes complete JSON-RPC SSE events as they arrive, including UTF-8 characters split across network reads and multiline SSE data. It forwards cancellation notifications over a separate bounded control path while the original request remains pending, then cancels that local response read after the endpoint accepts the notification. String and numeric request IDs retain their distinct association. Ordinary notifications receive no manufactured response.

At most eight ordinary sends and eight control sends are active, with one optional session GET event stream. Each input or response frame is bounded to 1,048,576 characters, and each SSE response/event stream to 16,777,216 characters. Stdout backpressure blocks consumption; no unbounded output queue is maintained. Requests have a five-minute total budget, including token acquisition and streaming. An over-capacity request receives `client_busy`; malformed or overlong stdin receives JSON-RPC `-32700` with `id:null`, then the next frame is processed. A duplicate outstanding ID is rejected. The bridge uses the maintained .NET HTTP stack with automatic redirects disabled; its bounded raw JSON-RPC relay does not translate the server catalog.

A successful initialize retains the session ID/protocol version; the accepted initialized notification then opens the optional GET event stream; HTTP 405 means that the server does not support that stream. An interrupted or exhausted event stream emits a redacted reconnect diagnostic. The relay never automatically reconnects or replays a POST. A session/endpoint HTTP 404 requires explicit diagnosis and reconnection.

HTTP 429 yields safe typed `retryAfterSeconds` or `retryAfterUtc` when the header parses correctly (delta hints must be between zero and seven days), without echoing raw headers or response bodies. HTTP 401/403 admission bodies are bounded to 16 KiB; only known credential codes and a bounded safe correlation identifier are preserved. Known admission rejections report `not_admitted`; timeouts and interrupted remote outcomes remain `uncertain`. Expired/revoked credential remediation is rebuilt from the shared local vocabulary, without copying remote text, credentials or arbitrary headers. Unknown codes retain a generic HTTP error. Timeouts return `transport_timeout`, separately from `request_cancelled` and other transport failures. Interrupted mutations may have completed remotely: reconcile through an admitted read/status operation before deciding whether another mutation is appropriate. `automaticReplay:false` applies to every bridge error. A retry delay alone does not authorize mutation replay.

## Private migration and acceptance receipt

Before switching the consumer, privately record the old adapter's executable/package, source repository or vendor, version, owner and binary checksum where available. If these are unavailable, retain `unknown`; do not attribute that adapter's measured failures to NetRatel's deployed server. The campaign's original adapter source and runnable historical harness are not part of this repository.

Stop the consumer's old connection, preserve its protected credential storage, install the verified replacement, and replace its launch entry with the explicit bridge command above (or the consumer's maintained native HTTP connection). Reuse a credential only through its reviewed protected storage reference and matching canonical resource; otherwise use client setup to obtain a fresh reviewed credential. Restart/reconnect the consumer and confirm its initialize, catalog and permitted read against the intended deployment. Never run both connections as an accidental retry path.

Keep a private receipt containing the release tag/source SHA, archive checksum, bridge `--version`, transport choice, credential reference identifier (no contents), canonical resource, exact UTC expiry, old adapter provenance or unknowns, reconnect result, and owner acceptance results. Record streaming-before-final and in-flight cancellation separately from repository loopback tests. Include denied/blocked and unexecuted cases accurately. Installed migration and live campaign claims remain pending until this receipt exists; repository delivery and synthetic regression success establish neither of those outcomes.
