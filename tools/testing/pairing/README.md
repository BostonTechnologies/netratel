# Actual source-pair acceptance

`tools/ci/run-pairing-acceptance.sh` runs the current NetRatel and an explicitly selected RatelDesk source checkout through ordinary Debug builds, PostgreSQL migrations, unattended local-administrator bootstrap, and normal production hosting. It requires .NET SDK 10.0.401, Python 3, Node 20 or newer with npm, OpenSSL, and the Linux Docker socket. The current Debug production Client executes in a disposable .NET 10 runtime container so its real enrollment credentials cannot affect a host installation.

Set absolute source roots and a fresh runtime directory outside either checkout:

```bash
export NETRATEL_PAIRING_SOURCE_ROOT=/path/to/netratel
export RATELDESK_PAIRING_SOURCE_ROOT=/path/to/rateldesk
export NETRATEL_PAIRING_RUNTIME_ROOT=/path/to/disposable/pairing-run
bash "$NETRATEL_PAIRING_SOURCE_ROOT/tools/ci/run-pairing-acceptance.sh"
```

The helper installs pinned Playwright 1.63.0 and its Chromium. Set `PAIRING_BROWSER_WITH_DEPS=1` on an ordinary hosted runner to install Chromium's operating-system dependencies. Existing proxy and CA settings are inherited. `DOTNET_HOST_PATH`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, and `PLAYWRIGHT_BROWSERS_PATH` can select existing tool installations and caches.

The bounded check drives both actual parent account pages at desktop and narrow widths in light and dark themes. It generates and replaces a code, rejects the replaced code inline, pairs from each product, and saves distinct named mappings with the selected capabilities. It checks View and read-only Test, enrolls the actual Client, admits real disk telemetry, creates a controlled threshold and published Flow, loses a success response after the receiver's durable incident/receipt commit, and verifies the sender recovers the original receipt and identical replay remains one incident. It creates a scoped ordinary request task and job, observes real native acknowledgement and a bounded shell execution, and verifies the authenticated result callback and retained history. Finally, it deletes each local connection from its rendered page while the peer is stopped, repeats deletion, restarts, and verifies local authority remains removed while business histories remain. The same captured business bearer must be accepted immediately before deletion and denied while still unexpired afterward. The disposable fixture uses the supported 900-second access-token lifetime to allow bounded native work and restarts to finish before that revocation check.

A passed `acceptance-receipt.json` is written only after every required stage and owned resource cleanup succeed. It records both source SHAs, dirty status, captured and current working-tree hashes, and the actual Client/runtime assembly hashes. Safe source-manifest evidence records dirty file names. Set `PAIRING_REQUIRE_CLEAN_SOURCE=1` for the final commit-bound run; this rejects dirty or changed source trees. The remaining `evidence/` directory contains masked screenshots and safe assertion summaries; `NETRATEL_PAIRING_EVIDENCE_DIRECTORY` can select an artifact directory for those safe files. Codes, passwords, enrollment credentials, bearer tokens, signing keys, database credentials, raw captured requests, and private native logs are not retained. A failing command exits nonzero and does not produce a passed receipt.

This check uses genuine existing-disk telemetry. It does not claim the separate privileged loop-device/ext4 allocation proof ran. The reusable allocation helpers remain independent of the pairing lane.

The opt-in `PairingLifecycleTests` wrapper runs the same entry point when `NETRATEL_PAIRING_ACCEPTANCE=1` and verifies the safe receipt. It requires both source roots explicitly; there is no released-image fallback or mandatory companion fetch in regular unit tests.

For the focused Final Save correction, `tools/ci/run-pairing-save-acceptance.sh` accepts the same explicit corrected source roots and a fresh runtime root, plus `NETRATEL_PAIRING_BASELINE_SOURCE_ROOT` pointing to a clean isolated checkout of the released NetRatel source. It builds only the baseline API and the corrected API/Web/migration graph. Its owned, certificate-validated RatelDesk front door uses default HTTPS port 443 with matching identity/storage API settings. Port 443 must be available.

This optional check uses one desktop viewport. It generates once in NetRatel, consumes in RatelDesk, selects real tenants/customer/name and both capabilities, and records the baseline receiver guard failure. It replaces only NetRatel's API with the corrected Debug runtime while retaining the pair, database, keyrings and RatelDesk form, then retries that draft once. A second disposable fixture proves the corrected fresh journey completes on its first Save. It creates no native execution or incident; business counts must remain zero. Each journey's `save-acceptance-receipt.json` is written only after positive cleanup, with source identities, runtime hashes and separate Save attempt counts. This is controlled source acceptance; deployed configuration and owner acceptance remain separate evidence.
