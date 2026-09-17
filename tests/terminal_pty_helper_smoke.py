#!/usr/bin/env python3

import base64
import json
import pathlib
import select
import subprocess
import sys
import time
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
HELPER = ROOT / "NetRatel.Client" / "Service" / "Terminal" / "terminal_pty_helper.py"


class TerminalPtyHelperSmokeTests(unittest.TestCase):
    def test_prompt_input_resize_and_exit(self):
        process = subprocess.Popen(
            [
                sys.executable,
                "-u",
                str(HELPER),
                "--cols",
                "90",
                "--rows",
                "24",
                "--",
                "/bin/bash",
                "--noprofile",
                "--norc",
                "-i",
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.addCleanup(self._cleanup, process)

        ready = self._read_status(process)
        self.assertEqual("ready", ready["type"])
        self.assertEqual(90, ready["cols"])
        self.assertEqual(24, ready["rows"])

        self._send(process, {
            "type": "input",
            "data": base64.b64encode(b"printf 'NetRatel_INPUT_OK\\n'; stty size\n").decode("ascii"),
        })
        initial_output = self._read_output_until(process, b"24 90", timeout=5)
        self.assertIn(b"NetRatel_INPUT_OK", initial_output)

        self._send_many(process, [
            {"type": "resize", "id": 7, "cols": 77, "rows": 17},
            {
                "type": "input",
                "data": base64.b64encode(b"stty size; exit\n").decode("ascii"),
            },
        ])
        resize = self._read_status(process)
        self.assertEqual({
            "type": "resize",
            "id": 7,
            "result": "applied",
            "cols": 77,
            "rows": 17,
        }, resize)

        resized_output = self._read_output_until(process, b"17 77", timeout=5)
        self.assertIn(b"17 77", resized_output)
        exit_code = process.wait(timeout=5)
        remaining_status = process.stderr.read().decode("utf-8", "replace")
        self.assertEqual(0, exit_code, remaining_status)

    @staticmethod
    def _send(process, message):
        process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        process.stdin.flush()

    @staticmethod
    def _send_many(process, messages):
        payload = "".join(json.dumps(message) + "\n" for message in messages)
        process.stdin.write(payload.encode("utf-8"))
        process.stdin.flush()

    @staticmethod
    def _read_status(process):
        line = process.stderr.readline()
        if not line:
            raise AssertionError("PTY helper closed its status stream")
        return json.loads(line)

    @staticmethod
    def _read_output_until(process, marker, timeout):
        deadline = time.monotonic() + timeout
        output = bytearray()
        while time.monotonic() < deadline:
            readable, _, _ = select.select([process.stdout], [], [], 0.2)
            if process.stdout in readable:
                chunk = process.stdout.read1(8192)
                if not chunk:
                    break
                output.extend(chunk)
                if marker in output:
                    return bytes(output)
        raise AssertionError(f"did not receive {marker!r}; output={bytes(output)!r}")

    @staticmethod
    def _cleanup(process):
        try:
            if process.poll() is None:
                TerminalPtyHelperSmokeTests._send(process, {"type": "close"})
                process.wait(timeout=2)
        except Exception:
            process.kill()
        finally:
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
