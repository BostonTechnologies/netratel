"""Actual archived stdio processes against a disposable synthetic HTTPS API."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import queue
import ssl
import subprocess
import threading
import time


def require(condition, message):
    if not condition:
        raise ValueError(message)


class Fixture:
    credential = "synthetic-stdio-assessment-credential"
    agent = "35ba3a1d-8665-499d-a8a6-42fa07e9a633"

    def __init__(self, directory):
        directory.mkdir()
        self.requests = []
        self.certificate = directory / "localhost.crt"
        key = directory / "localhost.key"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
                        "-keyout", str(key), "-out", str(self.certificate)], check=True, capture_output=True)
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def respond(self):
                authorized = self.headers.get("Authorization") == "Bearer " + fixture.credential
                fixture.requests.append({"method": self.command, "path": self.path, "authenticated": authorized})
                base = f"/api/v2/mcp/operator/agents/7/{fixture.agent}/clients"
                code, body = 500, {"code": "unexpected_fixture_request"}
                if authorized and self.command == "GET" and self.path == base:
                    code, body = 200, {"tenantId": 7, "agentId": fixture.agent, "fixture": "authenticated-read"}
                elif authorized and self.command == "GET" and self.path == base.replace("/7/", "/8/"):
                    code, body = 403, {"code": "tenant_access_denied"}
                elif authorized and self.command == "POST" and self.path == base + "/ping/preview":
                    code, body = 200, {"fixture": "mutation-preview", "planToken": "A" * 32, "idempotencyKey": "B" * 32}
                data = json.dumps(body).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = respond
            do_POST = respond

            def log_message(self, *values):
                pass  # Keep the fixture's structured method/path/authentication inventory.

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.certificate, key)
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.worker.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join()


def probe(entrypoint, directory, state, version, revision, fixture):
    directory.mkdir()
    state.mkdir()
    config = state / "config.json"
    config.write_text(json.dumps({"apiBaseUrl": f"https://localhost:{fixture.server.server_port}",
                                  "instance": "prod", "integrationCredential": fixture.credential}))
    config.chmod(0o600)
    original = config.read_bytes()
    environment = {key: value for key, value in os.environ.items() if not key.startswith("NETRATEL_")}
    environment.update(NETRATEL_MCP_CONFIG=str(config), SSL_CERT_FILE=str(fixture.certificate),
                       XDG_CONFIG_HOME=str(state / "config-home"))
    planned = ["setup-help", "bridge-help", "initialize", "tools", "resources", "prompts", "status", "capabilities", "config",
               "schema-catalog", "schema-response", "prompt", "authenticated-read", "denied-tenant", "invalid-target",
               "mutation-preview", "confirmation-required", "eof-shutdown"]
    record = {"passed": False, "stages": [], "scope": "archived process and synthetic HTTPS backend; live policy acceptance pending"}
    initial_requests = len(fixture.requests)
    process = None
    readers = []
    try:
        for name, arguments in (("setup-help", ["setup", "--help"]), ("bridge-help", ["http-bridge", "--help"])):
            stage = {"stage": name, "exitCode": None}
            record["stages"].append(stage)
            try:
                result = subprocess.run([str(entrypoint), *arguments], env=environment, text=True,
                                        capture_output=True, timeout=20)
            except subprocess.TimeoutExpired as error:
                stage["timedOut"] = True
                for stream, output in (("stdout", error.stdout), ("stderr", error.stderr)):
                    text = output.decode("utf-8", errors="replace") if isinstance(output, bytes) else (output or "")
                    (directory / (name + "." + stream + ".log")).write_text(text)
                raise
            stage["exitCode"] = result.returncode
            (directory / (name + ".stdout.log")).write_text(result.stdout)
            (directory / (name + ".stderr.log")).write_text(result.stderr)
            require(fixture.credential not in result.stdout + result.stderr, "Credential disclosed by an entrypoint")
            require(result.returncode == 0 and result.stdout.strip(), name + " failed")

        process = subprocess.Popen([str(entrypoint)], env=environment, text=True, encoding="utf-8",
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        frames = queue.Queue()

        def read_output():
            with (directory / "protocol.stdout.jsonl").open("w") as output:
                for line in process.stdout:
                    output.write(line)
                    output.flush()
                    frames.put(line)
            frames.put(None)

        def read_errors():
            with (directory / "protocol.stderr.log").open("w") as output:
                for line in process.stderr:
                    output.write(line)

        readers = [threading.Thread(target=read_output), threading.Thread(target=read_errors)]
        for reader in readers:
            reader.start()

        def request(name, method, params):
            identifier = len(record["stages"]) + 1
            record["stages"].append({"stage": name, "method": method, "responseReceived": False})
            process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": identifier, "method": method, "params": params}) + "\n")
            process.stdin.flush()
            deadline = time.monotonic() + 20
            while True:
                require(time.monotonic() < deadline, name + ": response deadline exceeded")
                line = frames.get(timeout=max(0.01, deadline - time.monotonic()))
                require(line is not None, name + ": process ended before its response")
                frame = json.loads(line)
                require(frame.get("jsonrpc") == "2.0", "Non-protocol stdout")
                if "id" not in frame:
                    require("method" in frame, "Invalid server notification")
                    continue
                require(frame["id"] == identifier and "error" not in frame, name + ": JSON-RPC error or unexpected response")
                record["stages"][-1]["responseReceived"] = True
                return frame["result"]

        initialized = request("initialize", "initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                              "clientInfo": {"name": "stdio-trim-assessment", "version": "1"}})
        require(initialized["serverInfo"]["name"] == "NetRatel.Mcp" and
                initialized["serverInfo"]["version"] in (version, version + "+" + revision), "Server identity changed")
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        catalog = {name: request(name, method, {}) for name, method in
                   (("tools", "tools/list"), ("resources", "resources/list"), ("prompts", "prompts/list"))}
        require(catalog["tools"]["tools"] and catalog["resources"]["resources"] and catalog["prompts"]["prompts"], "Empty catalog")
        for tool in catalog["tools"]["tools"]:
            require(isinstance(tool.get("inputSchema"), dict) and isinstance(tool.get("outputSchema"), dict), "Tool schema absent")

        def call(stage, name, arguments):
            return request(stage, "tools/call", {"name": name, "arguments": arguments})["structuredContent"]

        status = call("status", "netratel_capabilities", {"operation": "status"})
        require(status["success"] and status["data"]["hostRelease"]["sourceRevision"] == revision and
                status["data"]["hostRelease"]["productVersion"] == version, "Host source/version identity changed")
        catalog["digest"] = status["data"]["catalog"]["digest"]
        require(call("capabilities", "netratel_capabilities", {"operation": "get"})["success"], "Capabilities handler failed")
        require(call("config", "netratel_config", {"operation": "show"})["success"], "Compatibility handler failed")
        for schema in ("catalog", "response"):
            result = request("schema-" + schema, "resources/read", {"uri": "netratel://schemas/" + schema})
            catalog["schema-" + schema] = [json.loads(item["text"]) for item in result["contents"]]
        request("prompt", "prompts/get", {"name": "inspect_client", "arguments": {"client": "synthetic-assessment"}})
        target = {"tenantId": 7, "agentId": fixture.agent}
        read = call("authenticated-read", "netratel_clients", {"operation": "get", "request": target})
        require(read["success"] and read["data"]["fixture"] == "authenticated-read", "Authenticated handler routing changed")
        denied = call("denied-tenant", "netratel_clients", {"operation": "get", "request": {**target, "tenantId": 8}})
        require(not denied["success"] and denied["error"]["upstreamStatus"] == 403, "Upstream tenant denial changed")
        invalid = call("invalid-target", "netratel_clients", {"operation": "get", "request": {**target, "tenantId": 0}})
        require(not invalid["success"], "Invalid target was accepted")
        preview = call("mutation-preview", "netratel_clients", {"operation": "preview_ping", "request": target})
        require(preview["success"] and preview["data"]["fixture"] == "mutation-preview", "Mutation preview handler changed")
        unconfirmed = call("confirmation-required", "netratel_clients", {"operation": "ping", "request":
                           {**target, "planToken": "A" * 32, "idempotencyKey": "B" * 32}})
        require(not unconfirmed["success"] and unconfirmed["requiresConfirmation"], "Unconfirmed mutation was accepted")
        observed = fixture.requests[initial_requests:]
        require(len(observed) == 3 and all(item["authenticated"] for item in observed) and
                [item["method"] for item in observed] == ["GET", "GET", "POST"], "Unexpected outbound requests")
        (directory / "catalog.json").write_text(json.dumps(catalog, indent=2) + "\n")
        record["stages"].append({"stage": "eof-shutdown", "exitCode": None})
        process.stdin.close()
        record["stages"][-1]["exitCode"] = process.wait(timeout=10)
        require(record["stages"][-1]["exitCode"] == 0, "EOF/shutdown failed")
        record["passed"] = True
    except (ValueError, KeyError, OSError, queue.Empty, subprocess.TimeoutExpired) as error:
        record["error"] = type(error).__name__ + ": " + str(error)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait()
        for reader in readers:
            reader.join(timeout=5)
        if process is not None:
            for stream in (process.stdin, process.stdout, process.stderr):
                if not stream.closed:
                    try:
                        stream.close()
                    except BrokenPipeError:
                        record["cleanupBrokenPipe"] = True
        record["exitCode"] = process.returncode if process is not None else None
        record["observedRequests"] = fixture.requests[initial_requests:]
        record["notReachedStages"] = [stage for stage in planned if stage not in {item["stage"] for item in record["stages"]}]
        require(config.read_bytes() == original, "Probe changed its profile")
        for path in directory.glob("*.log"):
            require(fixture.credential not in path.read_text(), "Credential disclosed in diagnostic output")
        if (directory / "protocol.stdout.jsonl").exists():
            require(fixture.credential not in (directory / "protocol.stdout.jsonl").read_text(), "Credential disclosed on stdout")
        (directory / "probes.json").write_text(json.dumps(record, indent=2) + "\n")
    return record
