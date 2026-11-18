#!/usr/bin/env python3
"""Deterministic model responses for the disposable NetClaw browser check."""

import argparse
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import re
import time
from uuid import uuid4


MODEL = "scheduled-report-fixture"
PROBE = re.compile(r"NRPROBE-(BUFFERED|POSTPARENT)-([A-F0-9]{12})")
FINAL = re.compile(r"NR-SCHEDULED-FINAL-(BUFFERED|POSTPARENT)-([A-F0-9]{12})")


def record(stage, mode, identity):
    print(json.dumps({"utc": datetime.now(timezone.utc).isoformat(), "stage": stage,
                      "mode": mode, "id": identity}), flush=True)


def arguments(schema, values):
    canonical = {name.replace("_", "").lower(): value for name, value in values.items()}
    return {name: canonical[key] for name in schema.get("properties", {})
            if (key := name.replace("_", "").lower()) in canonical}


def model_response(request):
    messages = request.get("messages", [])
    tools = {tool["function"]["name"]: tool["function"].get("parameters", {})
             for tool in request.get("tools", [])}
    user_index = next((index for index in reversed(range(len(messages)))
                       if messages[index]["role"] == "user"), None)
    content = str(messages[user_index].get("content", "")) if user_index is not None else ""
    probe, final = PROBE.search(content), FINAL.search(content)
    if final and not probe:
        record("report_model_response", *final.groups())
        return {"content": final.group()}, "stop"
    if not probe or not tools:
        return {"content": "Scheduled report fixture"}, "stop"

    mode, identity = probe.groups()
    results = [str(message.get("content")) for message in messages[user_index + 1:] if message["role"] == "tool"]
    if any(" scheduled. " in result and "Delivery: reply to this session." in result for result in results):
        if mode == "BUFFERED":
            record("hold_parent_for_35_seconds", mode, identity)
            time.sleep(35)
        record("parent_model_response", mode, identity)
        return {"content": f"NR-SCHEDULED-REGISTERED-{mode}-{identity}"}, "stop"

    if "set_reminder" in tools:
        name = "set_reminder"
        values = {
            "Id": f"nr-{mode.lower()}-{identity.lower()}",
            "Name": "Disposable scheduled report",
            "Prompt": f"Reply exactly NR-SCHEDULED-FINAL-{mode}-{identity} with no tools.",
            "ScheduleType": "once",
            "Schedule": "30s",
            "DeliveryKind": "current_session",
            "_rationale": "Register one harmless 30-second report in this disposable session.",
        }
    elif "load_tool" in tools and not results:
        name = "load_tool"
        values = {"Name": "set_reminder", "_rationale": "Load the scheduler for a disposable reminder."}
    else:
        return {"content": "FIXTURE-SCHEDULER-UNAVAILABLE"}, "stop"

    record(name, mode, identity)
    return {"tool_calls": [{"index": 0, "id": f"call_{uuid4().hex}", "type": "function",
                            "function": {"name": name, "arguments": json.dumps(arguments(tools[name], values))}}]}, "tool_calls"


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, content_type, body):
        encoded = body.encode()
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self):
        if self.path != "/v1/models":
            self.send_error(404)
            return
        self.reply("application/json", json.dumps({"object": "list", "data": [
            {"id": MODEL, "object": "model", "owned_by": "fixture"}
        ]}))

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        length = int(self.headers.get("Content-Length", 0))
        if not 0 < length <= 2_000_000:
            self.send_error(400)
            return
        delta, finish = model_response(json.loads(self.rfile.read(length)))
        metadata = {"id": "fixture", "model": MODEL, "object": "chat.completion.chunk"}
        chunks = [dict(metadata, choices=[{"index": 0, "delta": {"role": "assistant", **delta}}]),
                  dict(metadata, choices=[{"index": 0, "delta": {}, "finish_reason": finish}])]
        self.reply("text/event-stream", "".join(f"data: {json.dumps(chunk)}\n\n" for chunk in chunks) + "data: [DONE]\n\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=15205)
    options = parser.parse_args()
    ThreadingHTTPServer((options.bind, options.port), Provider).serve_forever()
