"""Actual Linux terminal/noninteractive setup with protected files and synthetic HTTPS."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import ssl
import subprocess
import threading
import time
import unicodedata

spec = importlib.util.spec_from_file_location("cli_process", Path(__file__).with_name("cli-tool-fixture.py"))
process_fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(process_fixture)
require = process_fixture.require

def screen(raw, rows=40, columns=140):
    """Reconstruct cursor-diff text so stale earlier prompts cannot advance the journey."""
    text = raw.decode("utf-8", "replace")
    cells = [[" "] * columns for _ in range(rows)]
    row = column = index = 0
    saved = (0, 0)
    while index < len(text):
        character = text[index]
        if character == "\x1b":
            match = re.match(r"\x1b\[([0-?]*)([ -/]*)([@-~])", text[index:])
            if match:
                command = match[3]
                numbers = [int(value) if value.isdigit() else 0 for value in match[1].split(";")]
                first = numbers[0]
                if command in ("H", "f"):
                    row = max(0, (first or 1) - 1)
                    column = max(0, ((numbers[1] if len(numbers) > 1 else 1) or 1) - 1)
                elif command == "A": row -= first or 1
                elif command in ("B", "e"): row += first or 1
                elif command in ("C", "a"): column += first or 1
                elif command == "D": column -= first or 1
                elif command == "G": column = (first or 1) - 1
                elif command == "d": row = (first or 1) - 1
                elif command == "J" and first in (2, 3): cells = [[" "] * columns for _ in range(rows)]
                elif command == "K" and 0 <= row < rows:
                    start = 0 if first in (1, 2) else column
                    end = columns if first in (0, 2) else column + 1
                    for position in range(max(0, start), min(columns, end)): cells[row][position] = " "
                elif command == "s": saved = (row, column)
                elif command == "u": row, column = saved
                index += match.end()
                continue
            if index + 1 >= len(text): break
            if text[index + 1] == "]":
                end = re.search(r"\x07|\x1b\\", text[index + 2:])
                index = len(text) if not end else index + 2 + end.end()
                continue
            if text[index + 1] == "7": saved = (row, column)
            elif text[index + 1] == "8": row, column = saved
            index += 2
            continue
        if character == "\r": column = 0
        elif character == "\n": row += 1
        elif character == "\b": column = max(0, column - 1)
        elif character == "\t": column = (column // 8 + 1) * 8
        elif ord(character) >= 32:
            if 0 <= row < rows and 0 <= column < columns: cells[row][column] = character
            column += 0 if unicodedata.combining(character) else 2 if unicodedata.east_asian_width(character) in ("W", "F") else 1
        index += 1
    return "\n".join("".join(line).rstrip() for line in cells)


class Fixture:
    origin = "https://setup.example.invalid"
    credential = "synthetic-cli-setup-fixture-credential"

    def __init__(self, directory):
        directory.mkdir(mode=0o700)
        self.requests = []
        self.certificate = directory / "fixture.crt"
        key = directory / "fixture.key"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=setup.example.invalid", "-addext", "subjectAltName=DNS:setup.example.invalid",
                        "-keyout", str(key), "-out", str(self.certificate)], check=True, capture_output=True)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.certificate, key)
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def do_CONNECT(self):
                # The public-form origin satisfies the real setup metadata contract.
                # This allowlisted loopback tunnel needs no DNS, hosts edit or privileged port.
                if self.path != "setup.example.invalid:443":
                    self.send_error(403)
                    return
                self.send_response(200, "Connection established")
                self.end_headers()
                self.wfile.flush()
                self.connection = context.wrap_socket(self.connection, server_side=True)
                self.rfile = self.connection.makefile("rb")
                self.wfile = self.connection.makefile("wb")
                self.close_connection = True
                self.handle_one_request()

            def respond(self):
                authenticated = self.headers.get("Authorization") == "Bearer " + fixture.credential
                tls = isinstance(self.connection, ssl.SSLSocket)
                fixture.requests.append({"method": self.command, "path": self.path, "authenticated": authenticated, "tls": tls})
                code, body = 500, {"code": "unexpected_fixture_request"}
                if tls and self.command == "GET" and self.path == "/api/integration-pairing/v1/capabilities" and not authenticated:
                    code, body = 200, {"protocolVersion": 1, "publicOrigin": fixture.origin,
                        "pairingSupported": False, "verificationUri": fixture.origin + "/account/integration-credentials",
                        "httpMcpResource": fixture.origin + "/mcp",
                        **{name + "Endpoint": fixture.origin + "/api/integration-pairing/v1/" + route
                           for name, route in (("start", "start"), ("poll", "poll"), ("cancel", "cancel"), ("acknowledge", "acknowledge"))}}
                elif tls and self.command == "GET" and self.path == "/api/v1/auth/ai-agent/status" and authenticated:
                    code, body = 200, {"authenticated": True, "authMode": "integration_credential", "groups": [], "roles": []}
                data = json.dumps(body).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = respond
            do_POST = respond

            def log_message(self, *values):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.worker.start()

    def environment(self, original):
        environment = {key: value for key, value in original.items() if not key.upper().startswith(("NETRATEL", "BT_"))}
        proxy = "http://127.0.0.1:" + str(self.server.server_port)
        environment.update(SSL_CERT_FILE=str(self.certificate), HTTPS_PROXY=proxy, https_proxy=proxy,
                           HTTP_PROXY=proxy, http_proxy=proxy, NO_PROXY="", no_proxy="", ALL_PROXY="", all_proxy="",
                           TERM="xterm-256color", COLORTERM="truecolor")
        return environment

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join(timeout=5)
        require(not self.worker.is_alive(), "Setup fixture did not stop")


def protected_inventory(directory):
    return {path.name: {"sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "bytes": path.stat().st_size, "mode": path.stat().st_mode & 0o777}
            for path in directory.iterdir() if path.is_file()}


def validate_preservation(before, after, requests, output, credential):
    require(before == after and len(after) == 2 and all(item["mode"] == 0o600 for item in after.values()),
            "Successful setup changed the protected profile/secret bytes, files or owner-only modes")
    require(credential.encode() not in output, "Setup disclosed the complete synthetic credential")
    require(requests == [
        {"method": "GET", "path": "/api/integration-pairing/v1/capabilities", "authenticated": False, "tls": True},
        {"method": "GET", "path": "/api/v1/auth/ai-agent/status", "authenticated": True, "tls": True}],
        "Setup used unexpected requests, authentication fallback or a mutation")


def checked_terminal_screen(raw, credential):
    require(credential.encode() not in raw, "Setup disclosed the complete synthetic credential")
    current = screen(raw)
    require(credential not in current, "Setup rendered the complete synthetic credential")
    return current


def terminal_journey(arguments, environment, directory, evidence, record, credential, deadline=45):
    import errno
    import fcntl
    import pty
    import select
    import struct
    import termios
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    raw = bytearray()
    started = time.monotonic()
    process = None
    record.update(attempted=True, stages=[], timedOut=False, exitCode=None, reaped=False)
    planned = [("Instance", "Instance Web URL"), ("Authentication", "Use existing authentication"),
               ("Review", "Next verifies the selected existing configuration"), ("Finish", "authentication: completed")]
    phase = dsrs = 0
    try:
        process = subprocess.Popen(arguments, env=environment, cwd=directory, stdin=slave, stdout=slave,
                                   stderr=slave, start_new_session=True)
        os.close(slave)
        slave = None
        while time.monotonic() - started < deadline:
            if select.select([master], [], [], .1)[0]:
                try:
                    data = os.read(master, 65536)
                except OSError as error:
                    if error.errno == errno.EIO:
                        break
                    raise
                if not data:
                    break
                raw.extend(data)
                require(len(raw) <= 1024 * 1024, "Setup terminal exceeded its output budget")
                while raw.count(b"\x1b[6n") > dsrs:
                    os.write(master, b"\x1b[1;1R")
                    dsrs += 1
                current = checked_terminal_screen(raw, credential)
                if phase < len(planned) and planned[phase][1] in current:
                    snapshot = "setup-tty-" + planned[phase][0].lower() + ".txt"
                    (evidence / snapshot).write_text(current, encoding="utf-8")
                    record["stages"].append({"screen": planned[phase][0], "seconds": time.monotonic() - started,
                                             "snapshot": snapshot, "input": "F2"})
                    os.write(master, b"\x1bOQ")
                    phase += 1
            if process.poll() is not None:
                break
        record["timedOut"] = process.poll() is None and time.monotonic() - started >= deadline
    finally:
        if process is not None:
            if process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            record.update(exitCode=process.wait(timeout=5), reaped=True)
        if slave is not None:
            os.close(slave)
        os.close(master)
        record.update(seconds=time.monotonic() - started, terminalRows=40, terminalColumns=140, dsrResponses=dsrs,
                      stdoutStderrBytes=len(raw), stdoutStderrSha256=hashlib.sha256(raw).hexdigest())
        (evidence / "setup-tty-output.bin").write_bytes(raw)
    require(not record["timedOut"] and record["exitCode"] == 0 and phase == 4,
            "Interactive setup did not complete all four real screens with exit0")
    return bytes(raw)


def probe(entrypoint, environment, directory, evidence):
    directory.mkdir(mode=0o700)
    fixture = Fixture(directory / "https")
    record = {"passed": False, "scope": "installed portable package; synthetic HTTPS; reuse existing protected authentication",
              "flows": {}, "observedRequests": fixture.requests}
    try:
        for mode in ("noninteractive", "tty"):
            case = directory / mode
            case.mkdir(mode=0o700)
            secret = case / "credential.secret"
            secret.write_text(fixture.credential + "\n", encoding="utf-8")
            secret.chmod(0o600)
            profile = case / "profile.json"
            profile.write_text(json.dumps({"apiBaseUrl": fixture.origin, "integrationCredentialFile": str(secret),
                                          "authenticationMode": "IntegrationCredential", "credentialPurpose": "Api",
                                          "profileName": "fixture"}), encoding="utf-8")
            profile.chmod(0o600)
            before = protected_inventory(case)
            initial = len(fixture.requests)
            flow = {"passed": False, "attempted": True, "protectedBefore": before}
            record["flows"][mode] = flow
            arguments = [*entrypoint, "--config", str(profile), "setup", "--target", "cli", "--profile", "fixture",
                         "--instance", fixture.origin]
            if mode == "noninteractive":
                stages = []
                flow["stages"] = stages
                output = process_fixture.command(arguments + ["--non-interactive"], fixture.environment(environment), case, stages)
                data = json.loads(output)
                require(data.get("saved") is True and data.get("transport") == "Cli" and data.get("path") == str(profile)
                        and len(data.get("checks", [])) >= 2 and all(check.get("status") == "completed" for check in data["checks"])
                        and {check["name"] for check in data["checks"]} >= {"configuration", "authentication"},
                        "Noninteractive setup did not save and complete actual configuration/authentication checks")
                combined = (stages[0]["stdout"] + stages[0]["stderr"]).encode()
                flow["checks"] = data["checks"]
            else:
                combined = terminal_journey(arguments, fixture.environment(environment), case, evidence, flow, fixture.credential)
            after = protected_inventory(case)
            requests = fixture.requests[initial:]
            flow.update(protectedAfter=after, requests=requests)
            validate_preservation(before, after, requests, combined, fixture.credential)
            flow["passed"] = True
        record["passed"] = True
    finally:
        for mode, flow in record["flows"].items():
            flow.setdefault("protectedAfter", protected_inventory(directory / mode))
        (evidence / "setup-acceptance.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
        fixture.close()
    return record
