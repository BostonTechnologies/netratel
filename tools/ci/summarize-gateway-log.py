#!/usr/bin/env python3
"""Emit bounded, allowlisted gateway events without copying log payloads."""
from collections import deque
import sys


EVENTS = {
    "Presence admitted.": "presence_admitted",
    "Command gateway admitted. authority=akka.": "command_admitted",
    "Command gateway session failed:": "command_transport_failed",
    "Command gateway stream had already closed": "command_stream_closed",
    "Gateway extension 'command' failed without ending presence:": "command_extension_failed",
    "Gateway extension 'command' ended unexpectedly": "command_extension_ended",
    "Gateway extension 'command' stopped with its presence owner.": "command_owner_stopped",
    "Refreshing the gateway session before the agent token expires.": "presence_token_refresh",
    "Gateway session failed:": "presence_transport_failed",
}
ERRORS = (
    "RpcException", "HttpRequestException", "IOException", "OperationCanceledException",
    "TaskCanceledException", "InvalidOperationException", "ObjectDisposedException",
    "ArgumentException", "TimeoutException",
)
STATUSES = (
    "Cancelled", "Unknown", "InvalidArgument", "DeadlineExceeded", "NotFound",
    "AlreadyExists", "PermissionDenied", "ResourceExhausted", "FailedPrecondition",
    "Aborted", "OutOfRange", "Unimplemented", "Internal", "Unavailable", "DataLoss",
    "Unauthenticated",
)


def summarize(lines):
    events = deque(maxlen=20)
    for line in lines:
        for marker, event in EVENTS.items():
            if marker not in line:
                continue
            details = [event]
            if event.endswith("failed"):
                details.extend(error for error in ERRORS if error + ":" in line)
                details.extend(status for status in STATUSES if f'StatusCode="{status}"' in line)
            events.append(" ".join(details))
            break
    return list(events)


if __name__ == "__main__":
    for event in summarize(sys.stdin):
        print("Gateway diagnostic: " + event)
