# Release engineering

All first-party components evaluate from the root product version. The current
prerelease is `0.1.0-rc.2`; the component inventory is
`release/release-manifest.json`.

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
GitHub Actions creates a
Sigstore-backed build-provenance attestation for each uploaded release artifact;
verify it with `gh attestation verify` after it is publicly released. A separate owner decision
is required before creating a public GitHub release, publishing OCI images or
packages, or recording immutable artifact digests in the private consumer lock.
Every final image is built with public OCI source, revision, and product-version
labels, then its saved layers are scanned for generic credential material before
the image smoke tests run. Its fail-closed `Release rehearsal` aggregate
requires source validation, archive verification, all native Client packages,
all final images, and both image smoke suites to succeed.

For the native Client, the generated publish directory includes the executable,
its update manifest, required sidecars, and the Linux PTY helper. Do not
advertise a runtime until its final archive has been built and smoke-tested.
NetRatel `0.1.0-rc.1` archives and tag remain published historical release
artifacts. NetRatel `0.1.0-rc.2` is a source candidate: no rc.2 public images
or archives have been published yet. The release workflow validates the committed
version, builds every final runtime container, and packages CLI, stdio MCP, and
native Client artifacts. Native Client packages are built on their matching
Linux, Windows, and macOS runners. Publication, signing, package visibility,
and a release tag remain explicit owner actions.

Use `release/compose.images.yaml` only with approved immutable release-image
digests. It is intentionally a deployment bundle, not a source-build recipe.

## Owner-operated promotion

PR and release rehearsal workflows never call the promotion command. After
review and explicit publication approval, use a clean checkout of the merged
public commit and an owner-created `v0.1.0-rc.2` tag pointing to that commit.
Download the successful rehearsal's four artifact sets into separate directories
under `review-inputs`, retaining each set's `SHA256SUMS`.

Prepare and verify the flat downloadable layout without publishing:

```sh
python3 tools/ci/promote-release.py stage --inputs review-inputs --output staged-release --version 0.1.0-rc.2
(cd staged-release && sha256sum -c SHA256SUMS)
```

Before choosing a package prefix, an authorized operator must list the
organization's container packages using a GitHub credential with
`read:packages`. Public anonymous lookup cannot establish absence of private
packages. The promotion command repeats this check and rejects names occupied by
non-public packages. It never changes package visibility.

With a separately approved package prefix and registry login, invoke:

```sh
python3 tools/ci/promote-release.py promote \
  --approve "0.1.0-rc.2@$(git rev-parse HEAD)" \
  --package-prefix APPROVED-PUBLIC-PREFIX \
  --inputs review-inputs --output promoted-release --state promotion-state.json
```

This command **pushes images**. Do not run it as a rehearsal. It uses unique
version/commit tags and never writes `latest` or stable aliases. Its journal
allows a partial push to resume without rebuilding completed components.
A pre-existing tag without a corresponding journal is an error requiring
explicit digest recovery, not permission to overwrite. Preserve the journal.
New registry packages may initially be private; a separate owner decision is
required for any visibility change before anonymous verification can succeed.

The command pulls every exact digest using an empty Docker credential directory,
checks the public source/version labels, then runs the OIDC/enrollment and
HTTP MCP smokes against those digest references. It finalizes the extracted
bundle from the returned image digests and records the commit, version, asset
basenames and hashes in `publication.json`. A failed smoke is not publication
approval. After the command succeeds, inspect `publication.json`, verify
`SHA256SUMS` again, and create the prerelease only with explicit owner approval:

```sh
gh release create v0.1.0-rc.2 promoted-release/* --verify-tag --prerelease \
  --title "NetRatel 0.1.0-rc.2" --notes-file approved-release-notes.md
```

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
