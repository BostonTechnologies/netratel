#!/usr/bin/env python3
"""Emit bounded, allowlisted gateway events without copying log payloads."""
import argparse
from collections import Counter, deque
from datetime import datetime, timezone
import json
import re
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
    "Presence authentication renewed.": "presence_authentication_renewed",
    "Gateway session failed:": "presence_transport_failed",
}
for capability, marker in (
    ("file", "File"), ("terminal", "Terminal"), ("control", "Control"),
    ("job", "Job"), ("remote_support", "Remote-support signalling"),
):
    EVENTS[f"{marker} gateway session failed:"] = f"{capability}_transport_failed"
    EVENTS[f"{marker} gateway admitted."] = f"{capability}_admitted"
EVENTS["Remote-support V2 preparation gateway failed:"] = "preparation_transport_failed"
EVENTS["Telemetry V2 stream failed:"] = "telemetry_transport_failed"
EVENTS["Terminal gateway session stopped with its presence owner."] = "terminal_owner_stopped"
EVENTS["Gateway presence extension owner was cancelled."] = "presence_owner_stopped"
EVENTS["Gateway extensions did not stop within "] = "extension_shutdown_failed"
for capability in ("telemetry", "control", "file", "log", "remote-support", "terminal", "command", "job"):
    for marker, event in (
        ("failed without ending presence:", "extension_failed"),
        ("transport failed without ending presence:", "extension_transport_failed"),
        ("ended unexpectedly", "extension_ended"),
        ("stopped with its presence owner.", "owner_stopped"),
    ):
        EVENTS[f"Gateway extension '{capability}' {marker}"] = f"{capability.replace('-', '_')}_{event}"
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
REASONS = (
    "unknown", "unsupported presence authority token", "unsupported heartbeat authority token",
    "invalid connect acknowledgement", "invalid heartbeat acknowledgement",
    "invalid heartbeat policy", "presence_io_timeout", "renewal_io_blocked",
)
IDENTITY = re.compile(r"\b(correlation|serverConnection)=([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})(?=[,\s.]|$)")
TIMESTAMP = re.compile(r"(?:\butc=|^\s*\[?)(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:?\d{2}))(?=[\]\s,]|$)")


def event_for(line):
    return next((event for marker, event in EVENTS.items() if marker in line), None)


def errors_for(line):
    return [error for error in ERRORS if error + ":" in line or error + ";" in line]


def timestamp_for(line):
    match = TIMESTAMP.search(line)
    if match:
        try:
            return datetime.fromisoformat(match[1].replace("Z", "+00:00")).astimezone(timezone.utc)
        except ValueError:
            pass
    return None


def summarize(lines):
    events = deque(maxlen=20)
    for line in lines:
        for marker, event in EVENTS.items():
            if marker not in line:
                continue
            details = [event]
            if event.endswith("failed"):
                details.extend(errors_for(line))
                details.extend(status for status in STATUSES if f'StatusCode="{status}"' in line)
            events.append(" ".join(details))
            break
    return list(events)


def summarize_metrics(lines, identity_limit=16384):
    """Count allowlisted events; never infer causal origin from nearby timestamps.

    Input must be one host's log. UUIDs are used internally for exact correlation
    but never emitted. Timestamped log extent is not uptime or availability.
    """
    counts, primary_reasons, secondary_statuses, secondary_reasons = Counter(), Counter(), Counter(), Counter()
    failures, refreshes, seen_failures, secondary = set(), set(), set(), Counter()
    tracked_identities = set()
    first = last = None
    unidentified_primary = uncorrelated_secondary = suppressed_primary = 0
    correlation_complete = True
    for line in lines:
        timestamp = timestamp_for(line)
        if timestamp is not None:
            first = timestamp if first is None else min(first, timestamp)
            last = timestamp if last is None else max(last, timestamp)
        event = event_for(line)
        if event is None:
            continue
        identities = [(kind, value.lower()) for kind, value in IDENTITY.findall(line)]
        known = []
        for identity in identities:
            if identity in tracked_identities or len(tracked_identities) < identity_limit:
                tracked_identities.add(identity)
                known.append(identity)
            else:
                correlation_complete = False
        if event == "presence_transport_failed":
            # A session can log its failure again; count its initiating event once.
            if any(identity in seen_failures for identity in known):
                continue
            seen_failures.update(known)
            failures.update(known)
            if not identities:
                unidentified_primary += 1
            suppressed = re.search(r"\bsuppressedRepeatedFailures=(\d{1,9})\.", line)
            if suppressed:
                suppressed_primary += int(suppressed[1])
            reason = next((value for value in REASONS if f"reason={value}." in line), "unknown")
            primary_reasons[reason] += 1
        elif event == "presence_token_refresh":
            refreshes.update(known)
        elif event.endswith(("failed", "closed", "ended", "stopped")):
            if known:
                secondary[known[0]] += 1
            else:
                uncorrelated_secondary += 1
            status = next((value for value in STATUSES if f'StatusCode="{value}"' in line or f"grpcStatus={value}," in line), "unknown")
            secondary_statuses[status] += 1
            reason = "presence-owner-stopped" if event.endswith("owner_stopped") else (
                "shutdown-timeout" if event == "extension_shutdown_failed" else
                "stream-closed" if event.endswith("closed") else
                "extension-ended" if event.endswith("ended") else
                next(iter(errors_for(line)), "unknown"))
            secondary_reasons[reason] += 1
        counts[event] += 1
    correlated_failure = correlated_refresh = 0
    for identity, count in secondary.items():
        if identity in failures:
            correlated_failure += count
        elif identity in refreshes:
            correlated_refresh += count
        else:
            uncorrelated_secondary += count
    duration = (last - first).total_seconds() if first is not None else None
    primary_count = counts["presence_transport_failed"]
    return {
        "observed_first_utc": first.isoformat() if first else None,
        "observed_last_utc": last.isoformat() if last else None,
        "observed_seconds": duration,
        "observed_time_basis": "timestamped-log-extent",
        "primary_presence_failures": primary_count,
        "primary_failures_per_observed_hour": round(primary_count * 3600 / duration, 6) if duration else None,
        "primary_failures_without_identity": unidentified_primary,
        "suppressed_primary_failures_reported": suppressed_primary,
        "primary_count_basis": "distinct-logged-owner-when-present",
        "primary_reasons": dict(sorted(primary_reasons.items())),
        "refresh_reasons": {
            "token-expiry-reconnect": counts["presence_token_refresh"],
            "authenticated-renewal": counts["presence_authentication_renewed"],
        },
        "secondary_events": {
            "correlated_presence_failure": correlated_failure,
            "correlated_planned_refresh": correlated_refresh,
            "uncorrelated": uncorrelated_secondary,
        },
        "secondary_statuses": dict(sorted(secondary_statuses.items())),
        "secondary_reasons": dict(sorted(secondary_reasons.items())),
        "events": dict(sorted(counts.items())),
        "correlation_complete": correlation_complete,
        "reset_origin": "unknown",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metrics", action="store_true", help="Emit aggregate counts for one host; no raw payloads or identities.")
    arguments = parser.parse_args()
    if arguments.metrics:
        print(json.dumps(summarize_metrics(sys.stdin), sort_keys=True))
    else:
        for event in summarize(sys.stdin):
            print("Gateway diagnostic: " + event)
