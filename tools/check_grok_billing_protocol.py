"""Qualify an explicitly pinned Grok binary against synthetic, isolated ACP auth.

This checks transport, credential-class rejection and a bounded owner switch,
not live quotas or renewal. An unobserved owner switch produces a nonzero exit.
It never asks for a bearer, logs a payload, or sends a model/session request.
"""

import argparse
import hashlib
import http.server
import json
import os
import queue
import subprocess
import tempfile
import threading
import time
from pathlib import Path


def cleanup_probe(process, reader, server, thread):
    failures = []

    def attempt(name, action):
        try:
            action()
        except Exception:
            failures.append(name)

    try:
        if process is not None:
            if process.stdin is not None:
                attempt("stdin.close", process.stdin.close)
            try:
                process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                attempt("process.kill", process.kill)  # Only this isolated fixture process.
                attempt("process.wait_after_kill", lambda: process.wait(timeout=4))
            except Exception:
                failures.append("process.wait")
                attempt("process.kill", process.kill)
                attempt("process.wait_after_kill", lambda: process.wait(timeout=4))
            finally:
                if process.stdout is not None:
                    attempt("stdout.close", process.stdout.close)
                if reader is not None:
                    attempt("reader.join", lambda: reader.join(timeout=1))
    finally:
        attempt("server.shutdown", server.shutdown)
        attempt("server.close", server.server_close)
        attempt("server-thread.join", lambda: thread.join(timeout=1))
    return failures


def check(binary, expected_version, mode):
    requests = []
    admitted_class = mode == "oidc_xai"

    class Backend(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append(self.path)
            billing = self.path.startswith("/billing")
            self.send_response(200 if billing else 404)
            self.end_headers()
            self.wfile.write(b'{"config":{"creditUsagePercent":42.5}}' if billing else b"{}")

        def log_message(self, *_):
            pass

    with tempfile.TemporaryDirectory(prefix="cerith-grok-protocol-") as directory:
        root = Path(directory)
        auth_path = root / "synthetic-auth.json"
        scope = "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828"

        def write_owner(owner):
            auth = {
                "key": "synthetic-not-a-credential-" + owner,
                "auth_mode": "oidc" if admitted_class else mode,
                "create_time": "2026-10-02T00:00:00Z",
                "expires_at": "2099-01-01T00:00:00Z",
                "user_id": "synthetic-" + owner,
                "principal_type": "User",
                "principal_id": "synthetic-" + owner,
                "email": owner + "@example.invalid",
                "oidc_issuer": "https://auth.x.ai" if mode == "api_key" or admitted_class
                else "https://untrusted.example.invalid",
            }
            auth_path.write_text(json.dumps({scope: auth, "xai::api_key": auth}), encoding="utf-8")

        write_owner("a")
        env = {name: os.environ[name] for name in ("SystemRoot", "WINDIR", "PATH") if name in os.environ}
        for name in ("HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TEMP", "TMP"):
            env[name] = directory
        env.update(GROK_HOME=directory, GROK_AUTH_PATH=str(auth_path))
        version = subprocess.run([str(binary), "--version"], cwd=directory, env=env,
                                 capture_output=True, timeout=5, check=True)
        assert version.stdout.decode("utf-8").split()[1] == expected_version, "Unexpected binary version"

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Backend)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        backend = f"http://127.0.0.1:{server.server_port}"
        process = None
        reader = None
        cleanup_failures = []
        try:
            process = subprocess.Popen(
                [str(binary), "agent", "--no-leader", "--cli-chat-proxy-base-url", backend,
                 "--xai-api-base-url", backend, "stdio", "--leader-socket", str(root / "isolated.sock")],
                cwd=directory, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL)
            responses = queue.Queue(maxsize=64)

            def read_messages():
                while True:
                    line = process.stdout.readline(1024 * 1024 + 1)
                    if not line:
                        return
                    if len(line) > 1024 * 1024:
                        responses.put_nowait({"oversized": True})
                        return
                    try:
                        responses.put_nowait(json.loads(line))
                    except (ValueError, queue.Full):
                        return

            reader = threading.Thread(target=read_messages, daemon=True)
            reader.start()
            next_id = 0

            def call(method, parameters, budget=12):
                nonlocal next_id
                next_id += 1
                message = {"jsonrpc": "2.0", "id": next_id, "method": method, "params": parameters}
                process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
                process.stdin.flush()
                deadline = time.monotonic() + budget
                while True:
                    response = responses.get(timeout=max(0.01, deadline - time.monotonic()))
                    assert not response.get("oversized"), "Oversized ACP response"
                    if response.get("id") == next_id:
                        return response
                    assert time.monotonic() < deadline, "ACP response timeout"

            initialized = call("initialize", {"protocolVersion": 1,
                               "clientInfo": {"name": "cerith-isolated-protocol-check", "version": "1.0"},
                               "clientCapabilities": {}})
            assert "result" in initialized, "ACP initialization failed"
            before = call("_x.ai/auth/info", {})
            assert before.get("result", {}).get("principalId") == "synthetic-a", "Synthetic owner was not loaded"
            billing = call("_x.ai/billing", {})
            if admitted_class:
                assert billing.get("result", {}).get("config", {}).get("creditUsagePercent") == 42.5, "Mock quota was not acquired"
                assert any("billing" in path for path in requests), "Admitted class did not reach loopback billing"
            else:
                assert billing.get("error", {}).get("code") == -32000, "Billing did not reject unsupported auth"
                assert not any("billing" in path for path in requests), "Unsupported auth reached billing backend"
            write_owner("b")
            deadline = time.monotonic() + 3
            changed = False
            while time.monotonic() < deadline:
                try:
                    result = call("_x.ai/auth/info", {}, max(0.01, deadline - time.monotonic()))
                except queue.Empty:
                    break
                changed = result.get("result", {}).get("principalId") == "synthetic-b"
                if changed:
                    break
                time.sleep(0.1)
            # Report this bounded observation; failure to reload is not mislabelled as safe owner handling.
            print(json.dumps({"version": expected_version, "auth_case": mode,
                              "credential_class_check_passed": True,
                              "mock_billing_acquired": admitted_class,
                              "synthetic_owner_change_observed": changed}))
        finally:
            cleanup_failures = cleanup_probe(process, reader, server, thread)
        if cleanup_failures:
            raise RuntimeError("Probe cleanup failed: " + ", ".join(cleanup_failures))
        return changed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    if not __debug__:
        parser.error("Run without optimization; assertions are qualification gates")
    binary = args.binary.resolve(strict=True)
    with binary.open("rb") as stream:
        assert hashlib.file_digest(stream, "sha256").hexdigest() == args.sha256.lower(), "Binary hash mismatch"
    results = [check(binary, args.version, mode) for mode in ("api_key", "oidc", "oidc_xai")]
    raise SystemExit(0 if all(results) else 1)
