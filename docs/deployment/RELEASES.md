# Release engineering

All first-party components evaluate their product version from
`Directory.Build.props`. Change the version there only. The component inventory
is `release/release-manifest.json`; CI adds the derived version to the bundled
copy.

Review the GitHub release notes for each candidate before publishing its draft.
[Historical release notes](../release-notes/README.md) describe their released
versions; they do not set the current product version. Each candidate is source preparation until the owner merges,
tags, publishes the release, and verifies the public downloads.

`tools/ci/verify-product-version.sh` checks evaluated project metadata against
`Directory.Build.props`. Tag-driven CI requires a `v` tag whose normalized
value exactly matches that source version before it builds review artifacts.

The release-build workflow is deliberately non-publishing. It creates a Linux
CLI archive, a local .NET tool package, Linux and Windows stdio MCP archives, an image-only
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
publicly released. A public GitHub release, OCI publication, or recording
immutable artifact digests requires an explicit authorized promotion decision;
it is never performed by a PR or non-publishing build workflow.
Every final image is built with public OCI source, revision, and product-version
labels, then its saved layers are scanned for generic credential material before
publication. The scanner resolves the saved manifest's legacy `layer.tar` or OCI
blob references, verifies OCI content digests, and scans each unique physical
layer, including content overwritten or removed by later layers. Repeated and
empty layers are valid, but a scan without nonempty regular text content fails.
Its JSON coverage receipt records references, unique layers, files, bytes, and
raw matching-file counts. Exact upstream curl/Python help-text matches reviewed
in [#281](https://github.com/BostonTechnologies/netratel/issues/281) retain their
raw match status and exact file hashes in that receipt; new or changed matches
fail pending review. Historical release bytes stay immutable.

The fail-closed `Release rehearsal` aggregate keeps its generic
identity and requires fast source/build/regression validation, CLI/stdio archive
verification and all supported native Client archive producers. Actual release
images are built and scanned once by the publication path; no throwaway image
matrix or functional deployment/upgrade rehearsal blocks the tag build.

For the native Client, the generated publish directory includes the executable,
its update manifest, required sidecars, and the Linux PTY helper. Do not
advertise a runtime until its final archive has been built and integrity-verified.
Report functional acceptance as pending unless a matching manual run exists.
Release availability is determined by the matching immutable tag, completed
publication record and verified downloadable artifacts; never infer it from a
source checkout. The release workflow validates the committed version and
packages CLI, stdio MCP, and native Client
artifacts. Native Client packages are built on their matching Linux, Windows,
and macOS runners. Publication, signing, package visibility, and a release tag
remain explicit controlled actions.

Use `release/compose.images.yaml` only with approved immutable release-image
digests. It is intentionally a deployment bundle, not a source-build recipe.

The Compose archive retains the documentation index, third-party notices, and
the deployment, client, HTTP MCP, integration, operations and contract guides,
plus architecture decisions for offline use.
`tools/ci/prepare-bundle-documentation.py` maintains this selection and includes
the README hero image. Implementation history, evidence, release notes and other
branding collateral stay in the repository. Links to omitted content identify the exact build source commit;
links to shipped guides remain local. Packaging validates Markdown destinations
and anchors, HTML image references and filename case in the staged archive.
Deployment/configuration files, setup launcher, manifest and legal notices follow
the existing release packaging and digest-insertion path. A packaging pass does
not establish deployment acceptance.

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
all-passing TRX receipts. Components run first; Core's Infrastructure namespace
and its exact complement then overlap with the separate API suite. Each Core
process owns its PostgreSQL collection server and isolated databases. Each
uses half the default collection-thread allowance, preserving Core's aggregate
concurrency budget across the two processes. A
same-binary discovery manifest verifies that both completed receipts cover the
full selected Core population without overlap, including theory rows. Each
group reports its duration. Source-changing PR events retain required checks;
draft/readiness changes alone do not restart them.
`tools/ci/run-fast-regressions.sh` excludes
`category=compose`, `category=hosted` and `category=manual-integration`.
Restore, build and test outcomes remain independently visible. The same fast
selection applies to tag-build validation, with real CLI/stdio/native packaging
and integrity checks retained. Packaging/publication costs are measured
separately; the under-20-minute acceptance applies to required PR/main runs.

Before the fast assemblies start, `tools/ci/prepare-fast-test-containers.sh`
prepares their PostgreSQL and Testcontainers resource-reaper images sequentially.
One 120-second deadline covers the daemon probe, cache checks, single missing-image
pulls and final image inspections, with a five-second termination grace. Preparation
failures stop the required job. Its separate duration and image identities make
cold dependency preparation visible without changing test selection or the
20-minute job cap.

Provider regressions retain isolated databases and real migrations for every case. The script-source revision, client release import and client update suites reuse one PostgreSQL server per test class, avoiding repeated container startup while preserving their concurrent transaction, crash/recovery, notification and fencing assertions. The owned-job boundary and monitoring-flow bridge suites use separate class servers to move their measured 187.2 seconds of baseline case work outside the common serial PostgreSQL collection. This adds two servers without changing test selection, timeout budgets or assertions; it does not guarantee the same wall-time saving. Compare hosted TRX case counts and stage durations when changing this setup; local focused timings do not establish the hosted budget. Further concurrency or selection changes require hosted evidence.

Use [integration-validation.yml](../../.github/workflows/integration-validation.yml)
only through explicit `workflow_dispatch` to select one functional suite at an
actual source ref, compatible peer and artifact identity. There is no implicit
PR/main/tag/publication dependency or nightly schedule. Browser/Chromium,
source-pair acceptance, native
install/update/PTY and deployment/upgrade tests retain their real assertions,
timeouts, cleanup and useful logs. OIDC and MCP release-image smokes use the
already published digest-pinned images; they do not perform registry writes.

For example, request browser fixtures at an exact source ref, or select one
upgrade against a completed public release:

```sh
gh workflow run integration-validation.yml --ref main -f suite=browser -f source_ref='<commit-or-tag>'
gh workflow run integration-validation.yml --ref main -f suite=upgrade-local -f release_tag='<completed-release-tag>'
gh workflow run integration-validation.yml --ref main -f suite=deployment -f deployment_profile=local -f source_ref='<exact-public-commit>'
gh workflow run integration-validation.yml --ref main -f suite=deployment -f deployment_profile=hybrid -f source_ref='<exact-public-commit>'
gh workflow run integration-validation.yml --ref main -f suite=deployment -f deployment_profile=http-host-setup -f source_ref='<commit-or-tag>'
gh workflow run integration-validation.yml --ref main -f suite=migrations-image -f source_ref='<commit-or-tag>'
gh workflow run integration-validation.yml --ref main -f suite=migrations-image -f migrations_comparison=html-ownership -f source_ref='<exact-public-commit>'
gh workflow run integration-validation.yml --ref main -f suite=migrations-image -f migrations_comparison=schema-ownership -f source_ref='<exact-public-commit>'
gh workflow run integration-validation.yml --ref main -f suite=api-image -f source_ref='<exact-public-commit>'
gh workflow run integration-validation.yml --ref main -f suite=stdio-assessment -f source_ref='<full-commit-sha>'
```

The ordinary source `deployment` suite with `deployment_profile=oidc` builds all
five default production images once from an immutable selected commit, using
the protected public revision/version image helper. Its source Compose run uses
those exact prepared API/Web/migrations images; the existing client acceptance
uses the prepared client. HTTP MCP is built and measured without asserting its
runtime acceptance. Other deployment profiles retain their existing build paths.

The `local` and `hybrid` deployment profiles reuse the fresh local-first Compose
journey against the selected source. They exercise proof-gated setup, Local
sign-in, the original protected cookie after API/Web process restarts, tenant
authorization, branding edits and a real PNG upload, account security and the
credential wizard. Hybrid adds the existing pinned disposable OIDC issuer and
requires SSO in a separate browser context without a Local cookie. These two
profiles use loopback HTTP; Hybrid explicitly uses Development metadata settings.
The `oidc` profile retains its separate Production TLS acceptance. Local/Hybrid
do not build the unrelated Client, CLI or MCP deployment inputs or collect a new
five-image comparison. The browser TRX and screenshots remain under `TestResults`.

After successful OIDC acceptance, before cleanup removes local images, the run
retains pristine image `/app` file hashes/bytes, verified SPDX and dependency
graphs, OCI source/version/platform/config identities and resolved source build
bases under `TestResults/oidc-compose/stack-images`. The latest completed release
is selected generically and its publication/source/image digests authenticated,
including the original input receipt and successful exact-source release-build
run/attempt. The runner SDK version is labeled separately from resolved image bases.
Its original build-base provenance is explicitly unknown when not attested by
the publication record; current floating base tags are never substituted.
Normalized standalone gzip layers and each complete five-image unique-layer
union compare the current production images with that published release. This
separate-run comparison does not isolate an individual change, and registry
transfer bytes remain unknown. Earlier fixed-companion results must not be summed.

The same run retains five raw PID 1 `/proc/1/status` samples over at least ten
seconds after the client workload for API/Web/client, with process name/PID,
timestamps and RSS min/median/max. These describe process RSS only. MCP is
unstarted and migrations are one-shot; their runtime memory remains unmeasured.
Client enrollment-start through the existing authenticated API and authoritative
gateway-presence assertions is timed with a monotonic clock. It includes the
intervening ordered smoke stages and is an upper bound on readiness, with no
baseline/performance-improvement claim. API/Web initialized-service durations
remain unknown. The optional capture adds no required job, matrix or publication.

The optional Linux-only `stdio-assessment` runs an ordinary framework-dependent
stdio control and a self-contained trimmed JIT diagnostic at the same source and
SDK. It preserves full publish diagnostics without counting MSBuild summary
echoes, packages both modes with notices, SPDX and checksums, and probes their
original archive bytes for identity, catalogs/schemas, handlers and bounded
synthetic HTTPS read/denial/preview behavior. Failures retain stdout/stderr and
stage receipts. FDD download/payload bytes and the separately observed existing
shared runtime are reported separately from the self-contained runtime payload;
these different populations do not establish cross-mode savings. This assessment
changes no shipping mode and performs no Native AOT attempt. Live-backend policy,
Windows protected launch and unresolved diagnostic acceptance remain separate.

The optional `migrations-image` suite compares the actual same-source migrations
payload on matching ASP.NET and CoreCLR images without publishing either image.
It checks the final runtime configuration, SBOM/file hashes, nonroot identity and
writable state, native GSSAPI/OpenSSL loading, culture, timezone and trust roots.
Disposable PostgreSQL checks cover both migration histories, a state-preserving
rerun, retained role customizations and a real failure exit. The upgrade helper
keeps API/Web/CLI at the latest completed published release and substitutes only
the candidate migrations image. Its deterministic gzip layer exports report
standalone and five-image shared-layer totals; these measurements are distinct
from registry transfer bytes. Remaining migrations dependency/registration work
and other application acceptance stay separate.

The default `migrations_comparison=base` retains that base comparison. Selecting
`html-ownership` instead compares two CoreCLR images from the same source, with
identical resolved SDK/runtime digests and diagnostic assembly build timestamp.
The control reconstructs the earlier HTML ownership by moving the unchanged
adapter and its two package references back into Infrastructure; it is explicitly
a diagnostic control, not original published bytes. Only the four HTML libraries
and their package records may disappear. Retained package metadata/relationships
and other payload bytes must match, except the affected first-party assemblies,
dependency graph, legal notices and verified SPDX metadata. SPDX creation times
remain their real generation times and namespaces bind each actual file inventory;
both differences are retained in the receipt. This mode reuses the full native,
PostgreSQL, role-preserving rerun and latest-release upgrade checks above.

`schema-ownership` uses the same controlled comparison for the unchanged schema
utility and its NJsonSchema reference. Its fixed removal allowlist is NJsonSchema,
NJsonSchema.Annotations, Namotion.Reflection and Newtonsoft.Json in the migrations
payload; API and MCP retain their active dependencies. One additional API publish
stage supplies the actual API dependency set for before/after utility calls in
isolated load contexts. The original control Infrastructure assembly and new API
assembly must accept the valid vector and reject the missing-required vector with
identical errors and dependency bytes. Package identities, notice hash and the
incremental API build/probe durations are retained. The defining utility assembly
changes from Infrastructure to API; namespace/signature preservation is not a
claim of compatibility for an external binary bound to its old assembly identity.

The optional `deployment_profile=http-host-setup` first compares the Linux x64
HTTP MCP image with a same-source RID-neutral control. It checks final host,
CLI and stdio inventories, runtime graphs, SPDX digests, retained launchers and
symbols, nonroot writable state, and actual entrypoint/catalog identities.
Content-hash duplicate measurements are evidence only; the image retains its
separate supported tooling layouts. Normalized layer exports hold four
companion images at one completed publication and report standalone and
five-image shared-layer totals, separately from registry transfer sizes.
These receipts are under `TestResults/mcp-http-image`.

The profile then reuses source Compose and public HTTPS fixtures for a fresh local-account stack. It generates into the supplied persistent setup volume, approves that exact generation through the administrator protocol, executes the generated apply action, obtains a separate temporary personal MCP credential, and requires authenticated catalog plus a delegated read-only API status result. It also runs bare `dotnet NetRatel.Mcp.Http.dll setup` inside the serving container, saves the personal default to its separate client-data volume, then reloads and verifies that profile after recreation. It recreates tooling/runtime containers and repeats host verification with the equivalent `/mcp` input and unchanged generation fingerprint. The redacted receipts under `TestResults/http-host-setup` distinguish this headless generation/protocol/deployment smoke from the focused interactive terminal fixtures and later owner testing. The default deployment profile remains the existing `oidc` suite; neither profile enters required PR/main CI.

The optional `api-image` diagnostic compares the unchanged default noble API image with the `chiseled-candidate` target on one linux/amd64 hosted runner. The candidate uses Microsoft's [noble-chiseled-extra base](https://github.com/dotnet/dotnet-docker/tree/main/src/aspnet/10.0/noble-chiseled-extra) and Canonical's [GSSAPI library slice](https://github.com/canonical/chisel-releases/blob/ubuntu-24.04/slices/libgssapi-krb5-2.yaml), including its dependency and copyright closure. Both arms must retain identical application/SBOM bytes and runtime patches. The selected probes cover actual GSSAPI application loading, nonroot ownership/writes, trust/globalization/timezone support, and disposable PostgreSQL/local upgrade with owner-only keys and graceful API shutdown. Published latest-completed Web/migrations/CLI inputs stay fixed; candidate images stay on the runner.

The diagnostic records standalone and five-image unique-layer exports normalized to gzip level 6 with zero mtime and no filename. These exclude config/manifests and are not registry transfer bytes. A smaller standalone image can increase the shared-stack union when companion images retain the original base. The default production target, framework-dependent untrimmed mode and release publication remain unchanged; OIDC/Hybrid, gateway/client recovery, OTEL and broader functional acceptance remain separate gates before adoption.

Other explicit choices are `pairing`, `gateway`, `deployment`,
`release-images`, `native-client` and `upgrade-oidc`.
Published-image and upgrade suites require `release_tag`; other suites use
`source_ref`. Pairing also requires an explicit RatelDesk `companion_ref`;
it builds both current source checkouts and uses the actual Client. Generic
owned-volume telemetry helpers remain available independently; the old
service-link rotation proof and its pinned released-peer selector are retired.
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
