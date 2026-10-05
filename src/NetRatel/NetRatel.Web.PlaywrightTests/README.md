# NetRatel Web Playwright tests

This suite runs local, authenticated visual fixtures for high-risk NetRatel Web surfaces. It never calls Dev or Production. The fixtures include deliberately long client, tenant, build, disk, and log values so responsive behavior is exercised without requiring live data.

The checks cover:

- gateway telemetry at desktop, tablet, and phone viewports;
- the gateway log explorer at desktop and phone viewports;
- dialog/viewport geometry, usable mobile charts and controls, document horizontal overflow, and visible Blazor error UI;
- the Helpdesk M2M purpose alongside unchanged personal API and HTTP MCP purposes, explicit tenant/resource grants, once-only manual secret reveal, deployment locks, stale-authority status, read-only connection tests and protected callback URL cleanup;
- screenshots written to `TestResults/playwright` for visual review.

Build the project and install its pinned Chromium version once:

```bash
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
pwsh src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Debug/net10.0/playwright.ps1 install chromium
```

Run the local fixture suite:

```bash
dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
```

The fixture host authenticates a local synthetic user and supplies deterministic in-memory telemetry and log data. Do not add live URLs, credentials, storage state, or external control-plane calls to this suite. See [the UX refresh baseline](../../docs/web-ux-refresh.md) for the full responsive viewport matrix and theme-preference contract.

The Helpdesk fixture includes light/dark desktop, 360-pixel phone and 200% zoom cases. Its screenshots contain only empty setup forms. Secret-bearing fixture cases capture no screenshots or traces; their deterministic service responses test the Web interaction, not live token issuance.

The existing `tools/ci/smoke-local-first-compose.sh` journey also exercises the actual issuer in its disposable Compose environment. The bootstrap administrator enrolls a new test resource, explicitly saves the canonical Web/API/issuer settings in the UI, creates a dedicated read-only service client, obtains a token without restart, reads only its approved tenant catalog, rotates the secret and revokes both cached token revisions. This journey keeps generated secrets and access tokens in process memory and emits no secret-bearing screenshots, traces or storage state. Its acceptance-only overlay permits the deliberately configured loopback HTTP addresses; production remains HTTPS by default. Full reciprocal pairing and incident delivery require the separate two-product acceptance suite.
