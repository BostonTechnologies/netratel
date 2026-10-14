# Native renewal findings and installed-host check

## Confirmed source defects

The native token service caches until the earlier of JWT `exp` and its local
response time plus `expires_in`. The gateway verifies the signed JWT and
acknowledges its actual expiry. The former presence client compared that ACK
against the cache deadline, incorrectly constructing `DataLoss` when valid
authority exceeded the conservative hint. A real token-service/gateway-validator
composition with separate fake clocks reproduced this before the change:

| Client clock behind server | Fresh cache expiry (UTC) | Valid gateway expiry (UTC) | Difference |
| --- | --- | --- | --- |
| 500 milliseconds | 2026-10-04 12:01:09.750 | 2026-10-04 12:01:10.000 | 250 milliseconds |
| 30 seconds | 2026-10-04 12:00:40.250 | 2026-10-04 12:01:10.000 | 29.750 seconds |

Cache reuse and renewal scheduling retain their conservative deadline. Presence
authority and ACK validation use the token's actual expiry; opaque tokens keep
the existing response lifetime bound. The gateway still verifies signature,
issuer, audience, lifetime and current enrollment. The client still checks the
tenant, client, connection, epoch, operation, sequence, authority token and
expiry extension before publishing credentials to extensions. An ACK beyond
JWT expiry, missing/invalid timestamp or mismatched envelope remains rejected.

Undifferentiated `DataLoss` previously entered `AuthenticationAttention` and
received the five-minute authentication floor. It now follows bounded backend
recovery without implying rejected credentials. Typed trust failures and genuine
permission/enrollment/protected-store failures retain their attention handling.
The established 0–5 minute, 5–30 minute and continuous randomized 5–10 minute
outage phases remain unchanged.

Renewal failures now identify local validation versus remote RPC, renewal phase,
safe reason, correlation/connection, cache/token/server expiry and expiry delta.
Status details use exact safe product phrases, with other text redacted. Only
validated correlation IDs and bounded retry hints from allowlisted trailers are
reported. Tokens, credential bodies and arbitrary metadata remain omitted. The
existing `X-Correlation-Id` header carries the local attempt ID into backend logs.

Windows and Linux use the same token cache and presence renewal implementations.
Platform-specific protected storage and interactive helper mechanisms remain
separate. This source comparison does not establish the deployed clock offset.

SCM `ERROR_SERVICE_NEVER_STARTED` (1077) means the service has not run since boot;
it does not by itself prove failure. Stopped services now remain Stopped for that
code and ignore service-specific codes when the SCM says they are inapplicable.
Real nonzero failure codes remain Failed and appear in the existing raw-state
details. The screenshot's actual exit codes are unavailable, so this corrected
mapping does not establish the cause of every screenshot label.

## Findings that still require the deployed environment

The reproduced defect can cause renewal failures; it is not a confirmed
explanation of any particular deployed event without backend and machine logs.
Search existing API/admission/disconnection logs around the reported events and
match connection IDs if the old client correlation was not forwarded. Establish each
host's UTC clock offset against the deployment's approved time source. Compare
Windows/Linux at the same installed commit/version. Keep credentials intact.

RunEx warnings and unexpected process replacement require separate investigation.
RunEx diagnostics now retain exception type/HRESULT, requested
session, launch flags, enabled/task state and last task result. COM objects are
released, and a missing-task repair must succeed before launch is retried. A
launch request remains explicitly unconfirmed until the existing authenticated
helper pipe connects in the selected interactive user session. Helper failures
remain isolated from presence.

On Windows inspect the existing task without replacing its principal or session:

```powershell
$helperTask = Get-ScheduledTask -TaskName 'NetRatel.RemoteDesktop.UserHelper'
$helperTask | Select-Object TaskName, State
$helperTask.Principal | Select-Object GroupId, UserId, LogonType, RunLevel
Get-ScheduledTaskInfo -TaskName 'NetRatel.RemoteDesktop.UserHelper' |
  Select-Object LastRunTime, LastTaskResult, NextRunTime
```

Match the requested session with the connected helper PID/session/version in
Remote Support diagnostics. Retain service, update and Task Scheduler logs around
the process replacement; neither RunEx nor presence alone identifies its cause.

## Bounded installed-host acceptance

Retain the validated Traefik `readTimeout=0s` setting on the actual streaming
entrypoint. Source changes do not mutate the deployed proxy.

Observe one designated Windows client and one Linux client through three normal
in-place renewals at the same version (roughly 45 minutes for a 15-minute token
policy). Record stable service PID, connection/epoch, recent heartbeat ACKs and
the logged cache/token/server expiry deltas. Confirm no local `DataLoss`, no
authentication-only delay and continued ordinary operations. End after those
renewals; an overnight observation is not a release gate. Independently request
the helper for one approved interactive Windows session and confirm its pipe
readiness/session/version, rather than treating a launch request as success.

If an outage occurs, verify the service remains live and eventually retries;
ordinary explicit shutdown must still stop it. Report installed-host acceptance
separately from deterministic regression and artifact-integrity results.
