# Release engineering

All first-party components evaluate from the root product version. The current
candidate prerelease is `0.1.0-rc.6`; the component inventory is
`release/release-manifest.json`.

The reviewed release notes for this candidate are
[`RELEASE_NOTES.md`](RELEASE_NOTES.md). They are used as the GitHub prerelease
notes only after the matching tagged workflow, provenance checks and promotion
record complete.
The [rc.5 notes](RELEASE_NOTES_0.1.0-rc.5.md) remain historical. The rc.6
candidate is source preparation until the owner merges, tags, promotes, and
verifies the public downloads.

`tools/ci/verify-product-version.sh` checks evaluated project metadata against
that manifest. Tag-driven CI requires a `v` tag whose normalized value exactly
matches the committed manifest before it builds review artifacts.

The release-build workflow is deliberately non-publishing. It creates a Linux
CLI archive, a local .NET tool package, a Linux stdio MCP archive, an image-only
Compose release bundle, and native Client archives for each supported runtime.
The Linux review bundle and every
native Client archive set have generated SPDX SBOMs. Every uploaded artifact set carries `SHA256SUMS`; the
Linux review bundle is checked for expected files, SBOM shape, checksum
integrity, unsafe archive paths, generic key/token patterns, CLI help and exact-version execution,
and a stdio MCP initialize, tool-list, and harmless capabilities-read sequence
before upload; it also verifies an actionable malformed API-URL diagnostic.
Native Client archives are
also checked for the expected executable, manifest version/runtime, updater and
terminal support files. Each matching Linux, Windows, and macOS runner executes
the freshly published Client; the extracted archive is executed again before
upload (with the Linux archive additionally running its native PTY self-test).
GitHub Actions creates a Sigstore-backed build-provenance attestation for each
uploaded release artifact; verify it with `gh attestation verify` after it is
publicly released. A public GitHub prerelease, OCI publication, or recording
immutable artifact digests requires an explicit authorized promotion decision;
it is never performed by a PR or release-rehearsal workflow.
Every final image is built with public OCI source, revision, and product-version
labels, then its saved layers are scanned for generic credential material before
the image smoke tests run. Its fail-closed `Release rehearsal` aggregate
requires source validation, archive verification, all native Client packages,
all final images, both image smoke suites, and the PostgreSQL previous-release
upgrade gate to succeed. The generic upgrade gate uses a controlled prior
version and published image digests, then verifies Local and OIDC continuity
against the candidate images. Its CI job and package repository names do not
contain an RC version.

For the native Client, the generated publish directory includes the executable,
its update manifest, required sidecars, and the Linux PTY helper. Do not
advertise a runtime until its final archive has been built and smoke-tested.
NetRatel `0.1.0-rc.1` archives and tag remain published historical release
artifacts. Release availability for `0.1.0-rc.5` is determined by its matching
immutable prerelease tag and release record; never infer it from a source
checkout. The release workflow validates the committed version, builds every
final runtime container, and packages CLI, stdio MCP, and native Client
artifacts. Native Client packages are built on their matching Linux, Windows,
and macOS runners. Publication, signing, package visibility, and a release tag
remain explicit controlled actions.

Use `release/compose.images.yaml` only with approved immutable release-image
digests. It is intentionally a deployment bundle, not a source-build recipe.

## Owner-operated promotion

PR and release rehearsal workflows never call the promotion command. After
review and explicit publication approval, use a clean checkout of the merged
public commit and an owner-created `v0.1.0-rc.6` tag pointing to that commit.
Download the successful release-workflow artifact sets into a sibling release
workspace (not the clean source checkout), retaining each set's `SHA256SUMS`.
Create `release-receipt.json` beside them with the repository, workflow path,
successful run ID and attempt, approved commit/version, and—for every required
file—the GitHub artifact name, immutable artifact ID, GitHub ZIP digest,
explicit artifact-relative path, and file SHA-256. Promotion re-reads the run
metadata and downloads each identified artifact through authenticated GitHub CLI access before
any image build or push; a locally recomputed checksum alone is not provenance.

Prepare and verify the flat downloadable layout without publishing:

```sh
mkdir -p ../netratel-release-work/review-inputs
python3 tools/ci/promote-release.py stage \
  --inputs ../netratel-release-work/review-inputs \
  --output ../netratel-release-work/staged-release --version 0.1.0-rc.6
(cd ../netratel-release-work/staged-release && sha256sum -c SHA256SUMS)
```

Before owner-approved image publication, run the authenticated, non-publishing
receipt/staging/resume rehearsal against that exact run's downloads and receipt:

```sh
python3 tools/ci/promote-release.py preflight \
  --inputs ../netratel-release-work/review-inputs \
  --receipt ../netratel-release-work/release-receipt.json \
  --output ../netratel-release-work/preflight-staged \
  --state ../netratel-release-work/preflight-state.json \
  --version 0.1.0-rc.6
```

The only allowed container package repositories are `netratel-api`,
`netratel-web`, `netratel-migrations`, `netratel-mcp-http`, and
`netratel-client`. A release candidate is identified only by its immutable
image tag (for example `0.1.0-rc.6`) and digest; never create an RC-specific
package name or CI/test name. Before promotion, an authorized operator must
list the organization's container packages using a GitHub credential with
`read:packages`. The promotion command repeats this check and fails closed
unless each approved package is public and linked to this repository. It never
changes package visibility.

With registry login, invoke:

```sh
python3 tools/ci/promote-release.py promote \
  --approve "0.1.0-rc.6@$(git rev-parse HEAD)" \
  --inputs ../netratel-release-work/review-inputs \
  --receipt ../netratel-release-work/release-receipt.json \
  --output ../netratel-release-work/promoted-release \
  --state ../netratel-release-work/promotion-state.json
```

This command **pushes images**. Do not run it as a rehearsal. It uses the exact
approved product-version tag (for example `0.1.0-rc.6`) in each fixed package
repository and never writes `latest` or stable aliases. Its journal
allows a partial push to resume without rebuilding completed components. Its
journal records the verified source receipt and exact input file digests, so a
retry rejects substitutions even if a new `SHA256SUMS` was generated. The
modified Compose archive is a derived bundle: its source build digest and final
digest are both recorded separately in the candidate and final publication
records. A candidate record is not a completed publication; it becomes
`publication.json` only after both required image smokes pass.
A pre-existing tag without a corresponding journal is an error requiring
explicit digest recovery, not permission to overwrite. Preserve the journal.
The approved packages are already public; a visibility mismatch is a genuine
external gate, not permission to create a substitute package.

The command pulls every exact digest using an empty Docker credential directory,
checks the public source/version labels, then runs the OIDC/enrollment and
HTTP MCP smokes against those digest references. It finalizes the extracted
bundle from the returned image digests and records the commit, version, asset
basenames and hashes in `publication.json`. A failed smoke is not publication
approval. After the command succeeds, inspect `publication.json`, verify
`SHA256SUMS` again, and create the prerelease only with explicit owner approval:

```sh
gh release create v0.1.0-rc.6 promoted-release/* --verify-tag --prerelease \
  --title "NetRatel 0.1.0-rc.6" --notes-file approved-release-notes.md
```

After publication, download the public release assets into a new empty
directory, verify its downloaded `SHA256SUMS` against every downloaded asset,
verify the published attestations and image digests, and record that evidence
against the accepted merged SHA. Do not mark release issues complete from the
candidate source version or a successful private staging directory alone.

The CLI tool is distributed as the downloadable NuGet package; this path does
not push to NuGet.org. Do not replace rc.1 assets or change its tag. Public
registry access and owner deployment acceptance are separate from successful
source checks and non-publishing rehearsal.

## Release rehearsal validation

The non-publishing release workflow builds the CLI, stdio MCP, all supported
native Client packages, and final container images before it produces review
artifacts. It then runs the image-only Compose bundle against the actual local
release-equivalent API, Web, and migration images. PR validation runs both that
smoke and the source Compose variant against a generic OIDC provider. It also
builds the actual Linux CLI/stdio-MCP archives and every native Client archive,
generates their SBOMs and checksums, and runs the same archive-content and
protocol verifiers before retaining the review artifacts. Each Compose smoke
verifies an anonymous protected API request is rejected, performs an
authorization-code browser session, verifies the authenticated API request,
creates a synthetic short-lived enrollment code, enrolls a disposable native
Client, verifies its authenticated agent ping plus HTTPS gRPC presence and an
authoritative telemetry snapshot,
loads Blazor static assets through Chromium and completes the generic OIDC
browser login/logout flow against that same stack,
executes and cancels disposable commands through the command gateway, verifies
re-admission after a temporary Client restart, and confirms logout clears the
Web cookie. The same disposable stack also extracts the built CLI archive,
mints its scoped client-credentials token from the test OIDC issuer, and checks
that the archive can read the synthetic tenant through the protected API. It
also runs the built stdio MCP archive through its real protocol handshake and
uses its configured client-credentials path for the same protected tenant read.

The HTTP MCP image has its own release-equivalent smoke: it starts with a
disposable OIDC provider, serves protected-resource metadata, rejects an
unauthenticated or invalid bearer token while advertising that metadata,
rejects a signed token missing the required scope, then obtains a signed scoped
test token and performs an authorized capabilities read.
