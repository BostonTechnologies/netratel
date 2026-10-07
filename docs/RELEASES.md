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
Compose template, and native Client archives for each supported runtime.
The Linux review bundle and every
native Client archive set have generated SPDX SBOMs. Every uploaded artifact set carries `SHA256SUMS`; the
Linux review bundle is checked for expected files, SBOM shape, checksum
integrity, unsafe archive paths, generic key/token patterns, CLI help and exact-version execution,
and a stdio MCP initialize, tool-list, and harmless capabilities-read sequence
before upload; it also verifies an actionable malformed API-URL diagnostic.
Native Client archives are
also checked for the expected executable, manifest version/runtime, updater and
terminal support files, source-identical updater scripts, executable presence,
SBOM coverage and checksums. Native archive producers use
`verify-client-release-artifact.sh --integrity-only`; client installation,
updater execution and the Linux native PTY self-test are opt-in diagnostics.
GitHub Actions creates a Sigstore-backed build-provenance attestation for each
uploaded release artifact; verify it with `gh attestation verify` after it is
publicly released. A public GitHub prerelease, OCI publication, or recording
immutable artifact digests requires an explicit authorized promotion decision;
it is never performed by a PR or non-publishing build workflow.
Every final image is built with public OCI source, revision, and product-version
labels, then its saved layers are scanned for generic credential material before
publication. The fail-closed `Release rehearsal` aggregate keeps its generic
identity and requires fast source/build/regression validation, CLI/stdio archive
verification and all supported native Client archive producers. Actual release
images are built and scanned once by the publication path; no throwaway image
matrix or functional deployment/upgrade rehearsal blocks the tag build.

For the native Client, the generated publish directory includes the executable,
its update manifest, required sidecars, and the Linux PTY helper. Do not
advertise a runtime until its final archive has been built and integrity-verified.
Report functional acceptance as pending unless a matching manual run exists.
NetRatel `0.1.0-rc.1` archives and tag remain published historical release
artifacts. Release availability for `0.1.0-rc.5` is determined by its matching
immutable prerelease tag and release record; never infer it from a source
checkout. The release workflow validates the committed version and
packages CLI, stdio MCP, and native Client
artifacts. Native Client packages are built on their matching Linux, Windows,
and macOS runners. Publication, signing, package visibility, and a release tag
remain explicit controlled actions.

Use `release/compose.images.yaml` only with approved immutable release-image
digests. It is intentionally a deployment bundle, not a source-build recipe.

## Owner-operated promotion

Edit `Directory.Build.props` to set the product version, merge the reviewed
source, and create the matching `v<version>` tag. The tag-triggered `Release build`
workflow builds and tests the deliverables but does not publish them.
`Directory.Build.targets` adds build metadata; it does not need a version edit.
The tag freezes the source commit when it is created. Publishing a draft that
already has a tag does not retarget it to the latest `main`.

After the version PR is merged, use an up-to-date, clean checkout of `main`:

```sh
git switch main
git pull --ff-only origin main
version="$(python3 tools/ci/product-version.py)"
tag="v$version"
git tag "$tag"
git push origin "refs/tags/$tag"
gh release create "$tag" --verify-tag --draft --prerelease --generate-notes
```

Review the draft and wait for that tag's `Release build` to pass, then publish
the draft in GitHub's Releases page. For a stable version (empty
`VersionSuffix`), omit `--prerelease`. If a published tag points to the wrong
source version or predates the intended fix, increment `VersionSuffix` through
a new PR and cut a new tag; retain the earlier tag and release as history.

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
GitHub runner. The final job verifies anonymous digest pulls, creates the digest-pinned Compose bundle, and
uploads the full asset set with checksums and a publication record. Existing
assets are reused only when their digests match; image tags are never
silently overwritten. The Actions run is the progress and failure record.
New publication records retain legacy `verification.state=complete` with
`scope=build-distribution-integrity` and `requiredSmokes=[]`; their separate
`functionalAcceptance` remains `pending-owner-testing` with no executed smokes.
Older publication records remain readable. A complete distribution record does
not claim product startup, browser, native-client or reciprocal-link acceptance.

The CLI tool is distributed as the downloadable NuGet package; this path does
not push to NuGet.org. Do not move an earlier release tag or replace its assets.
Keep the release issue open until the public downloads and registry digests are
verified.

## Fast validation and optional functional acceptance

The 7 October 2026 owner policy requires PR/main validation to succeed in
strictly less than 20 minutes, targeting 10–15 minutes. It keeps disclosure,
version, PostgreSQL-only and release-script checks, one solution Release
restore/build, and three prebuilt fast assemblies with separate nonzero
all-passing TRX receipts. `tools/ci/run-fast-regressions.sh` excludes
`category=compose`, `category=hosted` and `category=manual-integration`.
Restore, build and test outcomes remain independently visible. The same fast
selection applies to tag-build validation, with real CLI/stdio/native packaging
and integrity checks retained. Packaging/publication costs are measured
separately; the under-20-minute acceptance applies to required PR/main runs.

Use [integration-validation.yml](../.github/workflows/integration-validation.yml)
only through explicit `workflow_dispatch` to select one functional suite at an
actual source ref, compatible peer and artifact identity. There is no implicit
PR/main/tag/publication dependency or nightly schedule. Browser/Chromium,
full-pair links, physical incidents, real-time rotation/recovery, native
install/update/PTY and deployment/upgrade tests retain their real assertions,
timeouts, cleanup and useful logs. OIDC and MCP release-image smokes use the
already published digest-pinned images; they do not perform registry writes.

For example, request browser fixtures at an exact source ref, or select one
upgrade against a completed public release:

```sh
gh workflow run integration-validation.yml --ref main -f suite=browser -f source_ref='<commit-or-tag>'
gh workflow run integration-validation.yml --ref main -f suite=upgrade-local -f release_tag='<completed-release-tag>'
```

Other explicit choices are `service-link`, `gateway`, `deployment`,
`release-images`, `native-client`, `physical-incident` and `upgrade-oidc`.
Published-image and upgrade suites require `release_tag`; other suites use
`source_ref`. The physical suite requires source containing connector PR #152.
An upgrade selects and records the latest other completed published release
through the existing prior-release selector, reusing verified public archives
and immutable image digests.

When explicitly requested, each Compose smoke
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
