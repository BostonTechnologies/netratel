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
- A release is complete only after public asset checksums, immutable image digests, anonymous image pulls, and release-image smoke tests pass. Report the GitHub Actions run URL so the owner can watch progress.

## Review discipline

- Do not rename or clone generic CI tests and jobs for a release candidate. Extend the existing generic checks when coverage changes.
- For a version bump, verify that changing only `Directory.Build.props` updates every generated version output. Keep release notes and historical evidence separate from configuration.
- Preserve partial-publication evidence and journals for recovery. Do not claim release completion from a passed rehearsal, a tag, or a published release page alone.
