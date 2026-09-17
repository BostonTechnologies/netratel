# NetRatel Web Playwright tests

This suite runs local, authenticated visual fixtures for high-risk NetRatel Web surfaces. It never calls Dev or Production. The fixtures include deliberately long client, tenant, build, disk, and log values so responsive behavior is exercised without requiring live data.

The checks cover:

- gateway telemetry at desktop, tablet, and phone viewports;
- the gateway log explorer at desktop and phone viewports;
- dialog/viewport geometry, usable mobile charts and controls, document horizontal overflow, and visible Blazor error UI;
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
