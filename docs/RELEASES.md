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
