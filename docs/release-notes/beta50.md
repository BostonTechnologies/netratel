# NetRatel 0.1.1-beta.50

This release fixes the two Web defects observed in the completed beta.49 owner
campaign: token renewal terminating an active Blazor circuit and policy-dialog
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
- **Acceptance accounting**: the final beta.49 packet supplies the remaining
  bounded evidence for #399, #394, #390, #382, #361, #312 and #141. Their recorded
  source, companion versions and historical limits are retained. The policy Web
  parent #398 and original evidence register #294 remain open for their own
  acceptance requirements.

## Deployment

Use the completed publication's matching API, Web, HTTP MCP and migrations
images and checksum-verified archives. Follow [release installation](../../release/INSTALL.md)
and the normal migration sequence. No database migration is introduced by this
Web repair. Use the matching fingerprinted Web assets and retain the existing
Blazor connection affinity when running more than one Web replica; the renewal
HTTP request and circuit must reach the same Web process.

The existing controlled rollout hold is preserved by the owner. This change
does not require another native-client automatic-update campaign. Publication
and owner deployment are separate steps.

## Focused owner acceptance

Keep a genuine OIDC policy circuit open into its normal refresh window. Verify
renewal, preview without mutation, one reviewed change with independent version
and audit readback, current authority enforcement, continued reads beyond the
old access-token expiry and after a full reload. Confirm that browser renewal
responses contain no tokens and the pending API operation is sent only once.

At short and normal viewports, inspect a policy on a later filtered page and
check exact opener focus after both Escape and Close. Check the search fallback
when a lifecycle change removes that row. Retain existing stale-review,
nonadministrator and accepted-write/readback recovery evidence.

The beta.49 measured hour remains **240/240 successful calls over 3,600 seconds**
at its actual source and declared observable bridge-consumer boundary. It is
not relabelled as a beta.50 hour. This Web-only delta does not require another
mandatory hour. The original ledger remains **21 PASS / 32 OPEN of 53** until
exact new evidence advances its specific predicates. A targeted Web pass is
reported separately from that historical acceptance state. See the
[campaign guide](https://github.com/BostonTechnologies/netratel/blob/8c5fb3b8169335af233c281a36a9e5afe9113d2c/docs/mcp-http/campaign-retest.md).
