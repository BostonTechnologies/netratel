#!/usr/bin/env python3
"""Small Unix PTY sidecar for NetRatel.Client.

Control messages are newline-delimited JSON on stdin. PTY output is written raw
to stdout. Lifecycle and resize acknowledgements are newline-delimited JSON on
stderr so terminal bytes and control state never share a stream.
"""

import base64
import errno
import fcntl
import json
import os
import select
import signal
import struct
import sys
import termios


def emit(message):
    sys.stderr.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stderr.flush()


def set_window_size(fd, cols, rows):
    cols = max(2, min(300, int(cols)))
    rows = max(1, min(120, int(rows)))
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
    return cols, rows


def write_all(fd, data):
    view = memoryview(data)
    while view:
        written = os.write(fd, view)
        view = view[written:]


def terminate_child(pid, sig=signal.SIGTERM):
    try:
        os.killpg(pid, sig)
    except ProcessLookupError:
        pass


def parse_args(argv):
    values = list(argv)
    try:
        separator = values.index("--")
    except ValueError as exc:
        raise ValueError("missing command separator") from exc

    options = values[:separator]
    command = values[separator + 1:]
    if not command:
        raise ValueError("missing shell command")

    cols = 120
    rows = 32
    cwd = None
    index = 0
    while index < len(options):
        option = options[index]
        if option == "--cols" and index + 1 < len(options):
            cols = int(options[index + 1])
            index += 2
        elif option == "--rows" and index + 1 < len(options):
            rows = int(options[index + 1])
            index += 2
        elif option == "--cwd" and index + 1 < len(options):
            cwd = options[index + 1]
            index += 2
        else:
            raise ValueError(f"unsupported option: {option}")

    return cols, rows, cwd, command


def run(argv):
    cols, rows, cwd, command = parse_args(argv)
    master_fd, slave_fd = os.openpty()
    cols, rows = set_window_size(slave_fd, cols, rows)
    exec_error_read, exec_error_write = os.pipe()
    pid = os.fork()

    if pid == 0:
        try:
            os.close(exec_error_read)
            os.close(master_fd)
            os.setsid()
            fcntl.ioctl(slave_fd, termios.TIOCSCTTY, 0)
            os.dup2(slave_fd, 0)
            os.dup2(slave_fd, 1)
            os.dup2(slave_fd, 2)
            if slave_fd > 2:
                os.close(slave_fd)
            if cwd:
                os.chdir(cwd)
            os.execvpe(command[0], command, os.environ)
        except BaseException as exc:  # Child can only report through its PTY.
            error = f"NetRatel PTY exec failed: {exc}"
            os.write(exec_error_write, error.encode("utf-8", "replace"))
            os.write(2, (error + "\r\n").encode("utf-8", "replace"))
            os._exit(127)

    os.close(exec_error_write)
    os.close(slave_fd)
    exec_ready, _, _ = select.select([exec_error_read], [], [], 3.0)
    if not exec_ready:
        os.close(exec_error_read)
        terminate_child(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
        raise RuntimeError("shell exec readiness timed out")
    exec_error = os.read(exec_error_read, 4096)
    os.close(exec_error_read)
    if exec_error:
        os.waitpid(pid, 0)
        raise RuntimeError(exec_error.decode("utf-8", "replace"))

    emit({"type": "ready", "pid": pid, "cols": cols, "rows": rows})
    exit_code = None
    closing = False
    stdin_open = True
    control_buffer = bytearray()

    try:
        while True:
            if exit_code is None:
                waited_pid, status = os.waitpid(pid, os.WNOHANG)
                if waited_pid == pid:
                    exit_code = os.waitstatus_to_exitcode(status)

            inputs = [master_fd]
            if stdin_open and exit_code is None:
                inputs.append(sys.stdin.fileno())
            readable, _, _ = select.select(inputs, [], [], 0.25)
            if exit_code is not None and master_fd not in readable:
                break
            if master_fd in readable:
                try:
                    data = os.read(master_fd, 8192)
                except OSError as exc:
                    if exc.errno == errno.EIO:
                        data = b""
                    else:
                        raise
                if not data:
                    break
                write_all(sys.stdout.fileno(), data)

            if stdin_open and sys.stdin.fileno() in readable:
                chunk = os.read(sys.stdin.fileno(), 8192)
                if not chunk:
                    stdin_open = False
                    closing = True
                    terminate_child(pid)
                    continue

                control_buffer.extend(chunk)
                while b"\n" in control_buffer:
                    line, _, remainder = control_buffer.partition(b"\n")
                    control_buffer = bytearray(remainder)
                    if not line:
                        continue
                    message = json.loads(line.decode("utf-8"))
                    message_type = message.get("type")
                    if message_type == "input":
                        write_all(master_fd, base64.b64decode(message.get("data", "")))
                    elif message_type == "resize":
                        request_id = message.get("id")
                        try:
                            applied_cols, applied_rows = set_window_size(
                                master_fd,
                                message.get("cols", cols),
                                message.get("rows", rows))
                            emit({
                                "type": "resize",
                                "id": request_id,
                                "result": "applied",
                                "cols": applied_cols,
                                "rows": applied_rows
                            })
                        except OSError as exc:
                            emit({
                                "type": "resize",
                                "id": request_id,
                                "result": "failed",
                                "error": f"ioctl(TIOCSWINSZ) failed errno={exc.errno}"
                            })
                    elif message_type == "close":
                        closing = True
                        terminate_child(pid)
    finally:
        try:
            os.close(master_fd)
        except OSError:
            pass
        if exit_code is None:
            terminate_child(pid, signal.SIGKILL if closing else signal.SIGTERM)
            try:
                _, status = os.waitpid(pid, 0)
                exit_code = os.waitstatus_to_exitcode(status)
            except ChildProcessError:
                exit_code = -1

    emit({"type": "exit", "code": exit_code})
    return exit_code


if __name__ == "__main__":
    try:
        sys.exit(run(sys.argv[1:]))
    except Exception as error:
        emit({"type": "error", "error": str(error)})
        sys.exit(125)
