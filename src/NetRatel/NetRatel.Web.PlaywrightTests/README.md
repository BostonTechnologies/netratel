# NetRatel Web Playwright tests

This suite runs local, authenticated visual fixtures for high-risk NetRatel Web surfaces. It never calls Dev or Production. The fixtures include deliberately long client, tenant, build, disk, and log values so responsive behavior is exercised without requiring live data.

The checks cover:

- gateway telemetry at desktop, tablet, and phone viewports;
- the gateway log explorer at desktop and phone viewports;
- read-only Services launched from the real client cards/table, offline cache, drawer-open 1280px geometry, light/dark/system themes, keyboard focus and cancellation;
- Services reflow at 200% zoom equivalent (CSS 640×400 with device scale 2 for physical 1280×800; CSS 195×422 with device scale 2 for physical 390×844), plus a separate 200% text-size case, with every control and last-row field reachable;
- Flows with the actual published Velox8 surface, node drag, slot layout/connection, ViewPool and minimap; real pointer-created edges, property edits, pan/zoom and canonical geometry/viewport replay after a full browser reload;
- Flows with the desktop navigation drawer open, narrow layouts, light/dark/system themes and the same paired viewport/device-scale 200% zoom equivalents; every editor control is reachable and Fit graph places all native nodes inside the canvas with their headers and ports unobscured by the collapsible native overview;
- preserved dirty edits on close, revision conflicts and tenant changes, inert validation/sample preview, independent clone, immutable published history and keyboard focus restoration;
- tenant-scoped Monitoring links to immutable versions and actual run receipts without history fanout, plus wrong-tenant lookup rejection;
- tenant-authorized RatelDesk connector setup through real Routes/MainLayout, explicit mappings, read-only validation, inert preview, password rotation, dirty/conflict retention, pending-write guards, tenant changes and the same 200% zoom equivalents;
- dialog/viewport geometry, usable mobile charts and controls, document horizontal overflow, and visible Blazor error UI;
- Monitoring tenant-scoped counts, exact rule/group/bypass editors, manual clear/refire history, honest Unknown evidence, cached Services launch, failed draft retention, guarded duplicate saves and late-tenant response cancellation;
- Monitoring at desktop with drawer open, light/dark/system themes, narrow viewports and the same true 200% zoom-equivalent geometry;
- the combined Monitoring/Flows route and service registrations, ordered Signals navigation, and an actual occurrence-link journey through its paired run receipt and immutable published graph;
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
