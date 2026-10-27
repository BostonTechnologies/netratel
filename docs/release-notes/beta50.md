# NetRatel 0.1.1-beta.50

This release fixes two Web defects observed in beta.49: token renewal
terminating an active Blazor circuit and policy-dialog
dismissal losing keyboard focus. Build and distribution verification remain
separate from the owner's deployed acceptance.

## Changes

- **Active OIDC session renewal** ([#414](https://github.com/BostonTechnologies/netratel/issues/414)):
  an active circuit requests a fresh same-origin HTTP renewal before sending the
  pending API operation. Cookie writes and refresh-token rotation happen on that
  HTTP response. An opaque one-use correlation transfers only the access token
  back to the originating server-side circuit; browser JavaScript receives no
  tokens. Principal and credential-generation checks reject wrong-user or late
  sign-out results. Concurrent requests retain the existing refresh serialization.
  The circuit and draft stay open, with no page reload or automatic API replay.
  A rejected renewal follows the existing bounded reauthentication path.
- **Policy dialog focus** ([#415](https://github.com/BostonTechnologies/netratel/issues/415)):
  Inspect and Create remember their exact invoking control before loading can
  disable it. Idle Escape or Close restores keyboard focus after the dialog is
  removed. If an accepted lifecycle change removes the row from the current
  filter, focus returns to the search field. An obsolete dismissal cannot steal
  focus from a newly opened dialog. Pagination, filters and accepted receipts
  retain their existing behavior.

## Deployment

Use the completed publication's matching API, Web, HTTP MCP and migrations
images and checksum-verified archives. Follow [release installation](../../release/INSTALL.md)
and the normal migration sequence. No database migration is introduced by this
Web repair. Use the matching fingerprinted Web assets and retain the existing
Blazor connection affinity when running more than one Web replica; the renewal
HTTP request and circuit must reach the same Web process.

The existing controlled rollout hold is preserved by the owner. Publication
and owner deployment are separate steps.

## Recorded validation

Beta.49 recorded **240/240 successful calls over 3,600 seconds** at its declared
observable bridge-consumer boundary. That result belongs to beta.49; it does
not claim an equivalent beta.50 execution.
