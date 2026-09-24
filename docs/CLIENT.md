# Native Client

Install the Native Client only from an owner-approved release artifact and its
matching `netratel-client-manifest.json`. The release rehearsal verifies the
manifest, required sidecars, and archive integrity; Linux packages additionally
exercise the native PTY helper.

For a first enrollment, obtain a short-lived enrollment code from an authorized
operator and run the packaged executable with the deployment API URL:

```sh
NetRatel.Client --enroll replace-with-short-lived-code --api https://netratel.example.invalid
```

Treat enrollment codes and persisted agent credentials as secrets. Do not copy
them between machines or use an operator OIDC credential in their place. A
Client that has not enrolled exits rather than starting an unauthenticated
service.

The package includes runtime-specific update helpers and a manifest. Preserve
the existing installation identity and rollback material during an update; do
not bypass ordinary downgrade protection or point a Client at an unapproved
feed. Rc.1 is historical. Install an rc.3 Client only from the matching
prerelease asset after verifying its checksum, manifest and supported runtime;
neither prerelease establishes an unattended update channel.

## Release import and instance automation

An instance administrator can review GitHub client packs under **Agent Installers
& Updates → GitHub releases**. The catalogue is a read-only view of official
releases. Import downloads every runtime in a pack, verifies the source
publication record and SHA256 checksums, adapts the archive to the local
manifest format, and activates the complete local pack together. The source
archive SHA256 and local normalized artifact SHA256 can differ; each is checked
at its own boundary. Importing leaves the currently offered update releases
unchanged. The separate **Approve and publish for deployment** action records
the operator and offers the pack only through existing enabled tenant update
policies. Legacy offline upload remains available.

Automation is instance-wide and starts **off**. Choose 12-hour or 24-hour UTC
checks, then opt in to stable downloads. Prerelease downloads, automatic
publication, and automatic prerelease deployment each require separate choices.
The last attempt, last success, last error and next check are shown in UTC;
**Check now** schedules one check. After a restart, the durable schedule
coalesces missed checks. Only the newest eligible pack per selected channel is
considered. A database lease prevents two API replicas from checking the same
due schedule. Publication rechecks the policy before offering a release, so
turning it off during a download prevents that automatic rollout. Existing
tenant channel settings, disabled releases and suspended agents still apply.
Use the update attempts view to investigate rollout failures. Failed imports
retain their operation status and can be retried from the management page.

The GitHub source is fixed to this repository's official releases. An optional
read-only GitHub token increases API limits; the API instance needs outbound
access to GitHub API and release assets, including through any configured
outbound proxy. Catalogue responses are cached, and GitHub rate limits and
partial publication evidence are shown instead of treating an incomplete pack
as installable. A client receives updates from its NetRatel instance; it does
not need direct GitHub access.

### Previously installed prerelease updater

The published `v0.1.0-rc.7` Linux updater rejects a newer prerelease with
the same `0.1.0` core. The corrected updater in this candidate cannot replace
the script already running on an installed host. Before an rc.7 Linux client
can receive rc.8 through the established update flow, an operator must first
replace that host's installed updater script with the script from a verified,
owner-approved rc.8 client package. Keep the existing credential, agent state,
and tenant binding; do not reinstall or re-enroll the client. Do not approve
this rollout until the owner has accepted and rehearsed that one-time repair.

## Public install links

An authorized operator can generate a time-limited install link for one tenant,
runtime, immutable version and set of install options. The result shows the
script, command and URL; copying, previewing or downloading that same result
does not issue another enrollment code. For Linux or macOS, use the command
shown for that runtime, for example:

```sh
bash -o pipefail -c "curl -fsSL 'https://netratel.example/clients/install/replace-with-issued-token.sh' | bash"
```

For Windows, use the generated PowerShell command:

```powershell
Invoke-WebRequest -UseBasicParsing -ErrorAction Stop 'https://netratel.example/clients/install/replace-with-issued-token.ps1' | Invoke-Expression
```

Inspect or download the script before execution if required by your operating
procedure. Anyone holding an active URL can use its remaining enrollment
allowance. The maximum uses count is **successful enrollments**, not HTTP
fetches: preview, HEAD, GET and retries do not consume a use. The public script
stops being served when the grant expires, is revoked or is exhausted. Revoking
also blocks enrollment from previously downloaded copies while leaving already
enrolled machines intact. Configure a trusted HTTPS public Web origin and the
public API/download origins before generating a link; a missing or unsafe
public URL is reported as a configuration error. Keep capability URLs and
generated script contents out of external reverse-proxy access logs.
