# Beta.20 setup, integrations and Jobs — synthetic acceptance

Rendered acceptance ran on 16 October 2026 with product version derived from
`Directory.Build.props`, using [source 1abcb53](https://github.com/BostonTechnologies/netratel/commit/1abcb53d3ca14028349c304d3a2e5cc1a989d7d2).
These are disposable authenticated fixtures with synthetic identities and data.
They do not establish acceptance of the owner's deployment.

The selected browser suite completed **20 cases: 20 passed, 0 failed, 0 skipped**
in 73.849 seconds: Jobs 11, Integrations 5, and system connections 4. The MTP
receipt passed `tools/ci/verify-mtp-trx.py`. Full captures and hosted receipts are
linked from [delivery tracker #261](https://github.com/BostonTechnologies/netratel/issues/261).

The rendered matrix includes 1366×768 light and dark with the desktop drawer open,
1600×900 light, 390×844 dark, and 200% zoom equivalents. Jobs uses 640×400 CSS
pixels at DPR 2 (1280×800 physical); Integrations uses 683×384 at DPR 2
(1366×768 physical). Captures retain their original bytes. Some Jobs full-page
captures include a short underlay tail; separate assertions require dialog
header/footer controls and all eight ordinary inputs inside the actual viewport.

| Executed state | Sanitized evidence |
| --- | --- |
| Eight short inputs and launch actions fit at 1366×768. | [Capture](assets/beta-20-setup-integrations-jobs/jobs-run-eight-desktop-light.png) |
| The wide desktop keeps a bounded 1160px input dialog. | [Capture](assets/beta-20-setup-integrations-jobs/jobs-run-eight-wide-light.png) |
| Parameter editing preserves the unsaved parent job in dark theme. | [Capture](assets/beta-20-setup-integrations-jobs/jobs-parameter-editor-dark.png) |
| Step 1 collapsed and live Step 2 output opened automatically. | [Capture](assets/beta-20-setup-integrations-jobs/jobs-follow-active-step.png) |
| Final Step 3 output remains open after the synthetic run completes. | [Capture](assets/beta-20-setup-integrations-jobs/jobs-follow-final-output.png) |
| Compact approval entries, credential filters and rows with the drawer open. | [Capture](assets/beta-20-setup-integrations-jobs/integrations-credentials-desktop-light.png) |
| HTTP MCP host approval shows code, fingerprint and explicit consent. | [Capture](assets/beta-20-setup-integrations-jobs/integrations-host-review-dark.png) |
| The real 25-permission tenant catalog uses explicit groups and a bounded body. | [Capture](assets/beta-20-setup-integrations-jobs/integrations-permissions-desktop-dark.png) |
| At 200% zoom, scrolled permissions retain the scope summary and decision actions. | [Capture](assets/beta-20-setup-integrations-jobs/integrations-permissions-zoom.png) |
| System connection tenant/organization mapping stacks on a narrow screen. | [Capture](assets/beta-20-setup-integrations-jobs/system-connection-narrow.png) |

Jobs acceptance exercises eight immediate parameter saves, edit/cancel with a
retained parent draft, zero/one/24-input forms, mixed input validation, false/zero,
secret masking, successful run navigation, and a delayed three-step SSE scenario.
It checks follow, manual pause, Resume and retained final output without forcing
focus away from manual inspection.

Integrations acceptance uses the real catalog of 25 tenant and 5 server
permissions, including the last permission after scrolling. It checks bounded
host/client reviews, scope summaries and decision actions, shared credential
creation, selected inactive deletion, compact connection lists and mapping.
Focused component/API/PostgreSQL tests cover consent changes, query aliases,
stale responses, tenant authority and physical credential deletion separately.

Setup evidence is separate: deterministic full-source proposal/runtime tests and
the virtual terminal journey cover identity continuity, source changes, runtime
proof, exact temporary consent, API-backed operation outcome and cleanup. The
existing optional hosted deployment lane supplies actual restart/fresh-resume
receipts; see the tracker for its result. Required PR/main validation and hosted
release/publication retain their existing gates. Distribution integrity and
owner functional acceptance are separate; owner acceptance remains pending retest.
