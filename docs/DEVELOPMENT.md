# Development and testing

Use the SDK pinned in `global.json`. From a clean source checkout:

```sh
dotnet restore NetRatel.sln
dotnet build NetRatel.sln --configuration Release --no-restore
dotnet test NetRatel.sln --configuration Release --no-build
```

`tools/ci/verify-product-version.sh` checks that all first-party projects
evaluate to the version in `release/release-manifest.json`. Run the public
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
