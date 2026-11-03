"""Bounded process checks against a synthetic loopback API; never a live deployment."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import subprocess
import threading
import time


def require(condition, message):
    if not condition:
        raise ValueError(message)


def command(arguments, environment, directory, stages, expected=0):
    started = time.monotonic()
    options = {"start_new_session": True} if os.name != "nt" else {
        "creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
    process = subprocess.Popen(arguments, env=environment, cwd=directory, text=True, encoding="utf-8", errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options)
    try:
        stdout, stderr = process.communicate(timeout=45)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           capture_output=True, check=False)
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        stdout, stderr = process.communicate()
        stages.append({"command": arguments, "exit": process.returncode,
                       "stdout": stdout, "stderr": stderr, "timedOut": True,
                       "seconds": time.monotonic() - started})
        raise ValueError("CLI tool command exceeded its 45-second process deadline")
    stages.append({"command": arguments, "exit": process.returncode,
                   "stdout": stdout, "stderr": stderr, "seconds": time.monotonic() - started})
    require(process.returncode == expected,
            f"CLI tool command exited {process.returncode}; expected {expected}: {arguments[:4]}")
    return stdout.strip()


def probe(entrypoint, environment, directory, version, revision, evidence):
    credential = "synthetic-cli-artifact-fixture-credential"
    agent = "35ba3a1d-8665-499d-a8a6-42fa07e9a633"
    requests = []
    stages = []
    receipt = {"passed": False, "stages": stages, "observedRequests": requests,
               "scope": "actual installed tool process with disposable loopback API"}

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            authorized = self.headers.get("Authorization") == "Bearer " + credential
            requests.append({"method": "GET", "path": self.path, "expectedFixtureCredential": authorized})
            code, body = 404, {"code": "unexpected_fixture_path"}
            if authorized and self.path == "/api/v1/auth/ai-agent/status":
                code, body = 200, {"authenticated": True, "fixture": "auth-status"}
            elif authorized and self.path == f"/api/v2/mcp/operator/agents/7/{agent}/clients":
                code, body = 200, {"tenantId": 7, "agentId": agent, "fixture": "managed-client"}
            elif authorized and self.path == f"/api/v2/mcp/operator/agents/8/{agent}/clients":
                code, body = 403, {"code": "tenant_access_denied"}
            data = json.dumps(body).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_POST(self):
            requests.append({"method": "POST", "path": self.path})
            self.send_response(500)
            self.end_headers()

        def log_message(self, *values):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    config = Path(directory) / "fixture-config.json"
    config.write_text(json.dumps({"apiBaseUrl": f"http://127.0.0.1:{server.server_port}",
                                 "integrationCredential": credential}), encoding="utf-8")
    config.chmod(0o600)
    original = config.read_bytes()

    def run(stage, arguments, expected=0):
        output = command([*entrypoint, "--config", str(config), *arguments],
                         environment, directory, stages, expected)
        stages[-1]["stage"] = stage
        require(credential not in stages[-1]["stdout"] + stages[-1]["stderr"],
                "The tool disclosed the complete synthetic credential")
        return output

    try:
        run("help", ["--help"])
        actual_version = run("version", ["--version"])
        require(actual_version == version + "+" + revision, "Installed tool version/source identity differs")
        run("config_validate", ["config", "validate"])
        require(json.loads(run("auth_whoami", ["auth", "whoami"]))["authenticationMode"] == "IntegrationCredential",
                "The configured integration authentication mode changed")
        require(json.loads(run("auth_status", ["auth", "status"]))["fixture"] == "auth-status",
                "The authenticated status route changed")
        result = run("managed_client_get", ["clients-v2", "get", "--tenant-id", "7", "--agent-id", agent])
        require(json.loads(result)["tenantId"] == 7, "Managed-client JSON routing changed")
        run("forbidden_tenant", ["clients-v2", "get", "--tenant-id", "8", "--agent-id", agent], expected=2)
        run("invalid_tenant", ["clients-v2", "get", "--tenant-id", "0", "--agent-id", agent], expected=1)
        run("credential_output_refused", ["auth", "token"], expected=1)
        run("setup_help", ["setup", "--help"])
        run("setup_requires_tty", ["setup", "--target", "cli"], expected=2)
        require(len(requests) == 3 and all(request["method"] == "GET" and
                request.get("expectedFixtureCredential") for request in requests),
                "Unexpected requests, credential fallback, or unauthenticated traffic occurred")
        require(config.read_bytes() == original, "Read-only checks or non-TTY setup changed the configuration")
        receipt["passed"] = True
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)
        evidence.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return len(stages)
