import subprocess
import unittest

from tools.check_grok_billing_protocol import cleanup_probe


class CleanupProbeTests(unittest.TestCase):
    def test_stdin_close_failure_does_not_skip_process_or_server_cleanup(self):
        calls = []

        class BrokenInput:
            def close(self):
                calls.append("stdin.close")
                raise OSError("synthetic broken pipe")

        class Output:
            def close(self):
                calls.append("stdout.close")

        class Process:
            stdin = BrokenInput()
            stdout = Output()

            def __init__(self):
                self.wait_count = 0

            def wait(self, timeout):
                calls.append("process.wait")
                self.wait_count += 1
                if self.wait_count == 1:
                    raise subprocess.TimeoutExpired("isolated-fixture", timeout)

            def kill(self):
                calls.append("process.kill")

        class Reader:
            def join(self, timeout):
                calls.append("reader.join")

        class Server:
            def shutdown(self):
                calls.append("server.shutdown")

            def server_close(self):
                calls.append("server.close")

        class Thread:
            def join(self, timeout):
                calls.append("server-thread.join")

        failures = cleanup_probe(Process(), Reader(), Server(), Thread())

        self.assertEqual(
            [
                "stdin.close",
                "process.wait",
                "process.kill",
                "process.wait",
                "stdout.close",
                "reader.join",
                "server.shutdown",
                "server.close",
                "server-thread.join",
            ],
            calls,
        )
        self.assertEqual(["stdin.close"], failures)


if __name__ == "__main__":
    unittest.main()
