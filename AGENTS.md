# NetRatel repository instructions

Read this file at the start of every task in this repository, especially before changing release code, CI, tags, or GitHub releases.

## Product version

- `Directory.Build.props` is the only file to edit to change the product version. Derive release tags, archive names, container tags, and the distributed manifest from its evaluated version.
- Do not add a second committed product version or a version-specific release workflow, CI job, test name, artifact-set name, or container package repository. Keep build and validation identities generic across releases.
- Do not pin the previous release version or image digests in CI. Select the latest completed published release and use its publication record for upgrade tests.
- Historical release notes may identify the release they describe; they must not control the build or publication version.

## Build and release pipeline

- Run release builds and container publication on GitHub-hosted Actions. Do not build release containers or run release builds on the operator's local machine.
- The tag-triggered build must validate all required targets. Publishing a GitHub release must trigger the generic publication workflow; an already-published release must be dispatchable by tag.
- Before any registry write, match the tag, `Directory.Build.props` version, public commit, successful release-build run, and authenticated artifact receipt. Never overwrite an existing release image tag or a release asset with different bytes.
- Publish all required downloadable archives, checksums, SBOMs, the digest-pinned Compose bundle, and the publication record. Publish the five images in the fixed public package repositories: `netratel-api`, `netratel-web`, `netratel-migrations`, `netratel-mcp-http`, and `netratel-client`.
- Distribution integrity is complete only after public asset checksums, immutable image digests, scans and anonymous image pulls pass. Functional acceptance may remain pending owner testing; publication metadata must distinguish these states and must not claim unexecuted smoke passed. Report the GitHub Actions run URL so the owner can watch progress.

## Fast CI and owner functional acceptance

- Required PR and main validation must finish successfully in under 20 minutes on standard GitHub-hosted runners; target 10–15 minutes. A timeout, cancellation, skipped required execution, or zero-test result is not a pass.
- Keep restore/build, fast regression tests, and functional smoke visibly separate. Report meaningful stage durations and the selected test groups.
- Default CI covers compilation, source/artifact integrity, fast component/unit tests, and focused API/PostgreSQL regressions. Prefer the smallest deterministic test at the layer owning a behavior.
- Full product-pair startup, native client install/update/execution, real-time rotation/recovery/soak, browser acceptance, and deployment/upgrade matrices are opt-in manual checks. Give expensive tests explicit categories so they cannot silently enter generic test commands.
- Do not add mandatory deep acceptance for every new feature or duplicate an existing scenario across multiple layers/platforms without a concrete need and an explicit owner request for the additional automatic cost.
- Existing targeted tests and relevant execution evidence are sufficient for a focused change. Do not add implementation-mirroring tests, exhaustive speculative cases, unnecessary wait loops, broad rerun requirements, or new evidence frameworks just to expand a pass count.
- Keep real assertions and failure propagation. Do not use continue-on-error, fake receipts, zero-test selectors, retries until green, suppressed assertions, or shorter timeouts to manufacture a successful duration.
- Owner deployment testing with OTEL/log inspection is the intended functional acceptance path. A distributed artifact may be build/integrity verified while manual functional acceptance remains pending; label those states accurately.
- Preserve production auth/tenant/credential/rate-limit behavior and release version/source/checksum/digest/immutability checks. Reduce test cadence and duplicate work rather than weakening the product.
- For a change that adds CI work, inspect its measured incremental cost and retain the workflow budget. If an expensive diagnostic is useful, place it in the optional workflow by default.

## Review discipline

- Do not rename or clone generic CI tests and jobs for a release candidate. Extend the existing generic checks when coverage changes.
- For a version bump, verify that changing only `Directory.Build.props` updates every generated version output. Keep release notes and historical evidence separate from configuration.
- Preserve partial-publication evidence and journals for recovery. Do not claim release completion from a passed rehearsal, a tag, or a published release page alone.

## Documentation placement and integrity

- Before creating documentation, inspect `docs/README.md` and the existing category tree. Update the existing canonical guide first; otherwise put the new document inside the closest existing category.
- Do not add ad hoc reports, plans, release notes, troubleshooting notes or setup guides directly to `docs/` or the repository root. A new category needs a clear durable purpose and an entry in `docs/README.md`.
- HTTP MCP documentation belongs under `docs/mcp-http/`; deployment instructions belong under `docs/deployment/`. CLI/stdio and shared tooling configuration belong under `docs/integrations/`.
- The only docs-root files are `README.md` and the distribution-required legal notice `THIRD-PARTY-NOTICES.txt`. Do not create redirect stubs for moved documents. Record old-to-new destinations in `docs/implementation/documentation-move-map.md`.
- Move documents with all inbound/outbound links, generated/distribution references and assets updated. Preserve legal notices, published assets/tags, useful content and historical evidence; check path case on Linux.
- Keep current instructions distinct from historical evidence and implementation plans. Never include real credentials, private deployment values or unredacted logs in examples.
- Run `tools/ci/validate-layout.sh` for documentation placement/local-link validation. Keep this check lightweight and part of existing fast CI; do not add an external-link crawler or mandatory deployment acceptance.
