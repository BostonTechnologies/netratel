# NetRatel 0.1.1-beta.48

This release fixes job-definition deletion that removed historical execution
records for jobs without an MCP ownership record
([#410](https://github.com/BostonTechnologies/netratel/issues/410)). Deleting a job
now retires its definition and preserves its existing history for ordinary,
paired and MCP-owned jobs. Publication verifies distribution integrity.

## Changes

- **Retained execution history:** deleting a definition keeps its saved
  parameters and steps, runs, step outcomes, activities and logs. The definition
  disappears from active get/list and cannot start another run. Explicit
  deletion of an execution record remains a separate operation.
- **Active-run protection:** `DELETE /api/v1/jobs/{jobId}` returns
  `409 job_has_active_runs` while any run is pending or running. Retirement and
  new run admission share the database fence, so a previously captured
  definition cannot authorize execution after retirement. Development marker
  jobs retain their existing `marker_job_has_active_run` conflict response.
- **Readable history within its tenant:** historical list, detail and stream
  responses retain the job name only when the definition belongs to the run's
  tenant. The operations dashboard follows the same rule, and existing run
  links remain usable after definition retirement. Deletion dialogs explain
  that history remains available.
- **Existing authority and audit:** tenant `JobManagement` checks, MCP owner
  binding, confirmation and version checks remain in force. MCP retirement
  retains its ownership records and accepted audits. Definition, step and
  parameter writes share the retirement fence, including paired input sync,
  so an earlier active-state check cannot permit editing a retired job.

## Upgrade and recovery

Run the shipped migrations before starting the matching components, following
[release installation](../../release/INSTALL.md). Migration
`20261025055701_RetainJobDefinitionHistory` adds nullable `Jobs.DeletedAtUtc`
and backfills timestamps for existing retired MCP-owned definitions. Active
definitions retain their current behavior.

The migration cannot reconstruct history already removed by an older release.
Preserve any earlier loss evidence and use a fresh owned terminal run to verify
the corrected lifecycle. The migration refuses to drop retirement state while
retired definitions exist. Keep API and worker binaries on a version that
enforces retirement; use the deployment's normal backup recovery procedure for
rollback.

## Validation

PostgreSQL regressions cover migration/backfill, active-run conflicts, both
retirement/admission and retirement/edit lock orders, concurrent step reordering,
and tenant isolation.
