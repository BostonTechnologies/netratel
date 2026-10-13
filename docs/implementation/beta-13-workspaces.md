# Compact operator workspaces — rendered review

The bounded optional workspace matrix ran on 13 October 2026 against
[2f77626](https://github.com/BostonTechnologies/netratel/commit/2f77626), with the
canonical product version from `Directory.Build.props`. Later evidence-only
documentation does not change the reviewed workspace code. These are disposable
authenticated fixtures, not screenshots or acceptance of the owner's deployment.

The workspace matrix executed four cases: **4 passed, 0 failed, 0 skipped**.
The separate Activity case executed once against
[311c106](https://github.com/BostonTechnologies/netratel/commit/311c106):
**1 passed, 0 failed, 0 skipped**. Its production Activity code did not change
during the subsequent script CSS and fixture corrections.
Both Completed MTP receipts were accepted by `tools/ci/verify-mtp-trx.py`.
Browser acceptance remains outside required generic PR/main regressions.

| State | Viewport | Review |
| --- | --- | --- |
| Desktop light | 1366×768 CSS pixels, drawer open | Primary controls/actions fit; prompt list/editor align; short tree begins below filters. |
| Desktop dark | 1366×768 CSS pixels, drawer open | Readable fields, working controls, neutral tabs and visible row actions. |
| Narrow dark | 390×844 CSS pixels | Stacked workspaces; dialogs and actions remain reachable without horizontal overflow. |
| Zoom equivalent light | 640×400 CSS pixels at DPR 2 | Equivalent layout space to 1280×800 physical pixels at 200% zoom; reachable dialog footers and controls. |

Full-page captures may include a fixed app bar at the current scroll position.
Reachability checks independently require each primary control to be inside the
viewport and uncovered by the app bar/drawer; horizontal overflow is rejected.

## Actual layout and focus findings

The full-width MudBlazor divider had `flex-grow: 1`. Computed browser evidence
showed a 236.5 px divider box and a 252.5 px gap between the filter stack and the
first tree row. The scoped direct-child divider rule now uses `flex: 0 0 auto`.
The final matrix requires the divider to stay at most 2 px high and the first
row to start within 28 px below the filters. Unused panel space stays below rows.

Modern Chromium/Monaco exposes its editable surface as the accessible
`Editor content` textbox instead of the older internal `.inputarea` textarea.
The final check requires that actual textbox to receive focus after creation.
Prompt creation separately requires its blank Name field to remain focused.

## Captures

Each linked desktop capture also has dark, narrow and zoom-equivalent siblings
in [the evidence directory](assets/beta-13-workspaces).

| Workspace | Executed review capture |
| --- | --- |
| New Prompt | [Focused blank Name, editor, visible Save/Cancel](assets/beta-13-workspaces/prompts-new-1366x768-light-dpr1.png) |
| Script Library | [Short tree under filters](assets/beta-13-workspaces/scripts-short-tree-1366x768-light-dpr1.png) |
| New Script | [Selected/nested-folder placement and Create](assets/beta-13-workspaces/scripts-new-1366x768-light-dpr1.png) |
| New Task | [Selected target, preserved multiline input, parameters and Launch](assets/beta-13-workspaces/task-parameters-1366x768-light-dpr1.png) |
| Task status | [Completed response from the disposable API fixture](assets/beta-13-workspaces/task-completed-1366x768-light-dpr1.png) |
| Jobs Definitions | [Visible Run/Edit/Clone/Delete actions](assets/beta-13-workspaces/jobs-definitions-1366x768-light-dpr1.png) |
| New Job | [Create-and-add-steps footer](assets/beta-13-workspaces/jobs-new-1366x768-light-dpr1.png) |
| Edit Job | [Existing fields and Save footer](assets/beta-13-workspaces/jobs-edit-1366x768-light-dpr1.png) |
| Activity | [Committed transition without Refresh](assets/beta-13-workspaces/activity-live-transition.png) |
| Activity reconnect | [Fresh snapshot, Live, retained focus and expansion](assets/beta-13-workspaces/activity-reconnect-reconciled.png) |

The open compact drawer appears in desktop captures. Eighteen focused layout
tests additionally verify actual framework anchors and `aria-current`, including
inventory/details, management and settings leaf selection.

## Focused verification

All 82 selected owning component cases passed with zero failures/skips:
Prompts 8; Script Library 9; New Task 10; Jobs 10; Boolean run parameters 4;
MainLayout/navigation 18; release deletion UI 8; client management/Activity 15.
The nine-case final PostgreSQL/file selection passed: eight deletion/recovery
cases plus the corrected script-reference case. Seven owning SSE backend checks
passed. The 86 release contracts and all 21 generated project version outputs
were verified separately.

Local compilation was focused Debug compilation with analyzers deferred to the
unchanged required hosted Release build. Initial failures and stopped diagnostic
runs were preserved and do not count as passes. The final Script Library fixture
interacts with Create before awaiting the dialog-opening handler; pending reads
are released in `finally`, preserving every original assertion.

The required PR head/main-SHA CI, hosted build/publication and public distribution
evidence are recorded on [#229](https://github.com/BostonTechnologies/netratel/issues/229).
Owner deployed-beta functional acceptance remains **pending retest**.
