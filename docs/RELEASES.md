# Release engineering

All first-party components evaluate their product version from
`Directory.Build.props`. Change the version there only. The component inventory
is `release/release-manifest.json`; CI adds the derived version to the bundled
copy.

Review the GitHub release notes for each candidate before publishing its draft.
The [rc.5 notes](RELEASE_NOTES_0.1.0-rc.5.md) and
[rc.6 notes](RELEASE_NOTES_0.1.0-rc.6.md) are historical; neither sets the
product version. Each candidate is source preparation until the owner merges,
tags, publishes the release, and verifies the public downloads.

`tools/ci/verify-product-version.sh` checks evaluated project metadata against
`Directory.Build.props`. Tag-driven CI requires a `v` tag whose normalized
value exactly matches that source version before it builds review artifacts.

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

Edit `Directory.Build.props` to set the product version, merge the reviewed
source, and create the matching `v<version>` tag. The tag-triggered `Release build`
workflow builds and tests the deliverables but does not publish them.
The publication workflow waits for that exact tag-triggered build when a
release is published, so the GitHub release may be published immediately after
the tag is pushed. The tag must be exactly `v<version>`; a different tag is
rejected with the required tag and no registry write is attempted.

Publishing the GitHub release starts `Publish release` on GitHub-hosted Actions.
For a release that was already published before this workflow existed, dispatch
it manually with the existing tag:

```sh
gh workflow run release-publish.yml --ref main \
  -f tag="v$(python3 tools/ci/product-version.py)"
```

The workflow derives the version and commit from the tagged source, finds its
successful release build, downloads the matching artifacts, and authenticates
their checksums and GitHub artifact identities before any registry write. Each
of the five fixed public packages is built, scanned, and pushed by a separate
GitHub runner. The final job verifies anonymous digest pulls, runs the OIDC
Compose and HTTP MCP smokes, creates the digest-pinned Compose bundle, and
uploads the full asset set with checksums and a publication record. Existing
assets are reused only when their digests match; image tags are never
silently overwritten. The Actions run is the progress and failure record.

The CLI tool is distributed as the downloadable NuGet package; this path does
not push to NuGet.org. Do not move an earlier release tag or replace its assets.
Keep the release issue open until the public downloads and registry digests are
verified.

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
