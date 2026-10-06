# Development and testing

Use the SDK pinned in `global.json`. From a clean source checkout:

```sh
dotnet restore NetRatel.sln
dotnet build NetRatel.sln --configuration Release --no-restore
dotnet test NetRatel.sln --configuration Release --no-build
```

Test assertions use [AwesomeAssertions 9.6.0](https://www.nuget.org/packages/AwesomeAssertions/9.6.0),
whose package metadata and [source license](https://github.com/AwesomeAssertions/AwesomeAssertions/blob/e3679af2c80726b2d046faba91cbebee0b6175aa/LICENSE)
declare Apache-2.0. Keep assertion dependencies permissively licensed for both
commercial and non-commercial use; verify package and source licensing when
updating them. Test projects mark this dependency private with `PrivateAssets="all"`.

`tools/ci/verify-product-version.sh` checks that all first-party projects
evaluate to the version in `Directory.Build.props`. Run the public
disclosure checks before preparing an export:

```sh
tools/ci/check-public-disclosure.sh
tools/ci/test-public-disclosure.sh
```

The disposable generic-OIDC Compose rehearsal is described in
[self-hosting](SELF_HOSTING.md). It uses synthetic data and is distinct from a
deployment smoke test. Do not point it at a production database, OIDC tenant,
or managed Client.

CI builds final Client packages on matching Linux, Windows, and macOS runners,
and runs source- and release-image Compose rehearsals. See
[release engineering](RELEASES.md) for its artifact and provenance checks.

Windows/WSL/public-HTTPS Client enrollment and lifecycle journeys through OS
services require explicit owner approval before adding or reinstating CI. Keep
ordinary build, packaging, checksum, release-source/version, focused offline
unit/security/contract tests and established server checks. Owner-run manual
acceptance covers installation, repair, enrollment and online service behavior.
