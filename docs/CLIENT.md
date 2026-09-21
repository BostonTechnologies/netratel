# Native Client

Install the Native Client only from an owner-approved release artifact and its
matching `netratel-client-manifest.json`. The release rehearsal verifies the
manifest, required sidecars, and archive integrity; Linux packages additionally
exercise the native PTY helper.

For a first enrollment, obtain a short-lived enrollment code from an authorized
operator and run the packaged executable with the deployment API URL:

```sh
NetRatel.Client --enroll replace-with-short-lived-code --api https://netratel.example.invalid
```

Treat enrollment codes and persisted agent credentials as secrets. Do not copy
them between machines or use an operator OIDC credential in their place. A
Client that has not enrolled exits rather than starting an unauthenticated
service.

The package includes runtime-specific update helpers and a manifest. Preserve
the existing installation identity and rollback material during an update; do
not bypass ordinary downgrade protection or point a Client at an unapproved
feed. Rc.1 is historical. Install an rc.3 Client only from the matching
prerelease asset after verifying its checksum, manifest and supported runtime;
neither prerelease establishes an unattended update channel.
