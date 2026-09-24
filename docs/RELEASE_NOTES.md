# NetRatel 0.1.0-rc.6 candidate

`0.1.0-rc.6` is prepared for evaluation and integration testing. It is not a
stable release channel and must not be used for unattended fleet updates.
The owner will publish it only after merging the reviewed source and verifying
the matching release artifacts.

## PostgreSQL-only installation and continuity

- PostgreSQL is the only supported first-party database for new and existing
  installations. SQLite deployment and migration support ended after rc.5;
  preserve an rc.5 SQLite installation and its data until a separate migration
  path is available.
- Bundled and external PostgreSQL profiles support local-first setup, Hybrid
  and OIDC sign-in, persistent signing and Data Protection material, and the
  documented public HTTPS deployment.
- A Ready installation remains Ready when its first administrator hands over
  to another viable administrator or its original tenant is removed. The
  durable instance marker still detects a missing or replaced database.
- The API accepts the same singular, plural, and documented compatibility
  OIDC audience settings during bootstrap and bearer validation.

## Account and integration experience

- Local account security displays the persisted authenticator state and guides
  enrollment, recovery-code acknowledgment, and passphrase changes.
- Integration credentials use scoped tenant and server permissions. Creation
  and authenticator enrollment keep their dialogs open during committed
  requests and show successful one-time secrets or recovery codes until the
  user acknowledges them.
- The centered login offers local and identity-provider actions according to
  the configured mode. The identity-provider action now uses a readable filled
  button with a visible keyboard focus ring.
- Deployment branding, theme selection before visible content, native Client
  enrollment/signing, and API versus HTTP-MCP credential purpose boundaries
  remain in place.

## Release verification

The generic CI upgrade gate starts from the published rc.5 PostgreSQL image
digests and verifies Local and OIDC continuity against candidate images. The
release workflow also builds and verifies the Compose bundle, CLI and stdio
MCP archives, native Client archives, checksums, SBOMs, and image labels.
The owner must use the accepted merged SHA, matching tag and artifact receipt,
then verify public downloads after publication. See [release engineering](RELEASES.md),
[self-hosting](SELF_HOSTING.md), and [the rc.5 historical notes](RELEASE_NOTES_0.1.0-rc.5.md).
