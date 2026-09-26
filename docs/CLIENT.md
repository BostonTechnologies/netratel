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
policies. Legacy offline upload remains available, but it publishes immediately
after manifest validation and bypasses GitHub publication verification and the
saved automation policy. Use GitHub import when those checks and the
instance-wide automation controls are required.

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
the same `0.1.0` core before it can read the candidate package. The generic
Linux client archive now includes `updater/repair-linux-updater.py` for this
one-time installed-script repair. It works with any verified candidate Linux
archive and has no release-version pin. It checks the archive checksum and
manifest, takes the updater's activation lock, saves the installed script,
and atomically replaces only that script while preserving ownership and mode.
It does not install or enroll a Client. The existing agent ID, credential,
tenant and active Client symlink stay intact.

On an affected host, download the approved Linux archive and its release
`SHA256SUMS` from the same completed publication. Verify the checksum before
extracting and running the utility. Confirm the installed updater's path with
`systemctl cat netratel-update.service`; its default is
`/opt/netratel/client/updater/netratel-update.sh`. Then run:

```sh
archive='netratel-client-<approved-version>-linux-x64.tar.gz'
sha256sum --ignore-missing --check SHA256SUMS
sha="$(awk -v name="$archive" '$2 == name { print $1 }' SHA256SUMS)"
test "${#sha}" -eq 64
tar -xOzf "$archive" netratel-client-linux-x64/updater/repair-linux-updater.py > repair-linux-updater.py
sudo python3 repair-linux-updater.py --archive "$archive" --sha256 "$sha"
```

For a nondefault installed updater or state directory, pass `--updater` and
`--lock` with the paths from that host's service configuration. If an update
attempt holds the lock, the utility stops without changing the script. A
repeat against the same candidate reports that the repair is already done.
After repair, approve the candidate through the ordinary instance policy and
verify the update attempt, readmission, agent ID and tenant binding. Keep the
reported backup until that check succeeds; restore it if the replacement
fails before activation. No Client reinstall or re-enrollment is needed.

Hosted acceptance extracts the actual published updater and records its version
gate result. For rc.7, that result is rejection. It runs the packaged repair
utility with the built candidate archive, and then activates that verified
candidate. The OIDC
previous-release upgrade also runs the enrolled published Client under its
original credential owner, repairs its updater, and checks the server-offered
update, readmission, confirmation, and original identity.

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
public URL is reported as a configuration error. Issued scripts are protected
snapshots: a later template or package change does not rewrite an old link or
archive. For recovery, revoke the old link from the management page (or the
management revoke endpoint), confirm the tenant/runtime/origin/options, and
generate a new link. Old packages remain supported until their link expires,
is revoked, or is exhausted; do not edit a capability URL or downloaded script.
Keep capability URLs and generated script contents out of external reverse-proxy
access logs.
