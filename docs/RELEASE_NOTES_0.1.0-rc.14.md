# NetRatel 0.1.0-rc.14

This candidate includes the Windows generated-installer protected-directory
preflight fix from PR #132 and restores a release tag whose source version
matches its downloadable artifacts and container images.

- The Windows generated installer checks the protected installation directory
  before changing installed files. Upgrade regression coverage verifies that
  the existing Client identity is preserved.
- Release validation reports an actionable tag/version mismatch before SDK
  setup or build work begins.
- `Directory.Build.props` remains the sole product-version authority for all
  applications, packages, archives, manifests, and images.

RC.13 was tagged at an earlier commit that declared RC.12, before the Windows
installer fix was merged. Its publication gate rejected that mismatch and
published no release assets or images. RC.14 supersedes that candidate without
moving its published tag.
