import io
import json
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor

from probably_not_human import BridgeError, Client, OutcomeUnknown
from probably_not_human.mcp import serve


class MailboxTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "inbox").mkdir()
        (self.root / "outbox").mkdir()
        self.client = Client(self.root, timeout=0.15, poll_interval=0.001)
        self.heartbeat()
        self.stop = threading.Event()
        self.threads = []
        self.addCleanup(self.shutdown)

    def shutdown(self):
        self.stop.set()
        for thread in self.threads:
            thread.join(2)
        self.assertTrue(all(not thread.is_alive() for thread in self.threads))

    def heartbeat(self, session="test-session", age=0):
        self.atomic(self.root / "heartbeat.json", {"protocol": 1, "session_id": session,
                    "game_version": "test", "updated_at": int((time.time() - age) * 1000)})

    def atomic(self, path, value):
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(value), encoding="utf-8")
        os.replace(temporary, path)

    def game(self, handler):
        def worker():
            handled = set()
            while not self.stop.is_set():
                for path in (self.root / "inbox").glob("*.json"):
                    if path.name in handled:
                        continue
                    handled.add(path.name)
                    request = json.loads(path.read_text())
                    result = handler(request)
                    if result is not None:
                        self.atomic(self.root / "outbox" / path.name, result)
                self.stop.wait(0.001)
        thread = threading.Thread(target=worker, daemon=True)
        self.threads.append(thread)
        thread.start()

    def response(self, request, result):
        return {"id": request["id"], "protocol": 1, "ok": True, "result": result}


    def test_concurrent_unique_requests(self):
        count = 0
        def handler(request):
            nonlocal count
            count += 1
            return self.response(request, {"arrival": count})
        self.game(handler)
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda _: self.client.observe(), range(4)))
        self.assertEqual(sorted(r["arrival"] for r in results), [1, 2, 3, 4])

    def test_heartbeat_reappears_during_filesystem_replacement(self):
        (self.root / "heartbeat.json").unlink()
        def restore():
            self.stop.wait(0.02)
            self.heartbeat(session="replacement-session")
        thread = threading.Thread(target=restore)
        self.threads.append(thread)
        thread.start()
        self.assertEqual(self.client.health()["session_id"], "replacement-session")
        self.assertEqual(list((self.root / "inbox").iterdir()), [])

    def test_expiry_leaves_inflight_and_never_retries(self):
        with self.assertRaises(OutcomeUnknown) as caught:
            self.client.act("advance_day")
        self.assertEqual(caught.exception.code, "timeout")
        requests = list((self.root / "inbox").glob("*.json"))
        self.assertEqual(len(requests), 1)
        self.assertLessEqual(json.loads(requests[0].read_text())["expires_at"], time.time() * 1000 + 2)

    def test_stale_heartbeat_rejects_before_publish(self):
        self.heartbeat(age=60)
        with self.assertRaises(BridgeError) as caught:
            self.client.observe()
        self.assertEqual(caught.exception.code, "stale_heartbeat")
        self.assertEqual(list((self.root / "inbox").iterdir()), [])

    def test_session_change_preserves_request(self):
        def handler(request):
            self.heartbeat(session="new-session")
        self.game(handler)
        with self.assertRaises(OutcomeUnknown) as caught:
            self.client.observe()
        self.assertEqual(caught.exception.code, "session_changed")
        self.assertEqual(len(list((self.root / "inbox").glob("*.json"))), 1)

    def test_remote_error(self):
        self.game(lambda r: {"id": r["id"], "protocol": 1, "ok": False,
                             "error": {"code": "precondition_failed", "message": "No customer"}})
        with self.assertRaises(BridgeError) as caught:
            self.client.act("sell")
        self.assertEqual(caught.exception.code, "precondition_failed")
        self.assertEqual(str(caught.exception), "No customer")

    def test_partially_written_response_is_reread(self):
        # ModHelper writes are not atomic; a half-written response must not be an error.
        def handler(request):
            path = self.root / "outbox" / f"{request['id']}.json"
            path.write_text('{"id": "' + request["id"][:5], encoding="utf-8")
            def finish():
                self.stop.wait(0.05)
                path.write_text(json.dumps(self.response(request, {"done": True})), encoding="utf-8")
            thread = threading.Thread(target=finish)
            self.threads.append(thread)
            thread.start()
        self.game(handler)
        self.assertEqual(self.client.observe(), {"done": True})

    def test_wait_uses_its_own_deadline(self):
        seen = {}
        def handler(request):
            seen.update(request)
            return self.response(request, {"satisfied": True, "frames": 3, "flow": {}, "until": request["params"]["until"]})
        self.game(handler)
        result = self.client.wait({"customer_arrived": True}, timeout=2.0)
        self.assertTrue(result["satisfied"])
        self.assertEqual(seen["method"], "wait")
        self.assertEqual(seen["params"]["timeout_ms"], 2000)
        self.assertGreater(seen["expires_at"], time.time() * 1000 + 5000)

    def test_malformed_protocol(self):
        self.game(lambda r: {"id": r["id"], "protocol": 2, "ok": True, "result": {}})
        with self.assertRaises(BridgeError) as caught:
            self.client.observe()
        self.assertEqual(caught.exception.code, "protocol_error")

    def test_mcp_handshake_and_live_call(self):
        self.game(lambda r: self.response(r, {"visible": {"day": 7}}))
        messages = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "observe"}},
            {"jsonrpc": "2.0", "id": 4, "method": "ping"},
            {"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "action", "arguments": {}}},
            {"jsonrpc": "2.0", "id": 6, "method": "missing"},
        ]
        sink = io.StringIO()
        serve(self.client, io.StringIO("".join(json.dumps(m) + "\n" for m in messages)), sink)
        responses = [json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertEqual([r["id"] for r in responses], [1, 2, 3, 4, 5, 6])
        self.assertEqual(responses[0]["result"]["protocolVersion"], "2025-03-26")
        self.assertEqual({t["name"] for t in responses[1]["result"]["tools"]}, {"observe", "describe", "action", "health", "wait"})
        self.assertEqual(json.loads(responses[2]["result"]["content"][0]["text"]), {"visible": {"day": 7}})
        self.assertEqual(responses[3]["result"], {})
        self.assertEqual(responses[4]["error"]["code"], -32602)
        self.assertEqual(responses[5]["error"]["code"], -32601)

    def test_mcp_observe_passes_filters(self):
        self.game(lambda r: self.response(r, {"params": r["params"]}))
        messages = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "observe", "arguments": {"include": ["game"], "compact": True}}},
        ]
        sink = io.StringIO()
        serve(self.client, io.StringIO("".join(json.dumps(m) + "\n" for m in messages)), sink)
        responses = [json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertEqual(json.loads(responses[1]["result"]["content"][0]["text"]), {"params": {"include": ["game"], "compact": True}})

    def test_mcp_parse_and_tool_error(self):
        self.heartbeat(age=60)
        source = io.StringIO('not-json\n' + json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}}) + '\n' + json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "health"}}) + '\n')
        sink = io.StringIO()
        serve(self.client, source, sink)
        responses = [json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertEqual(responses[0]["error"]["code"], -32700)
        self.assertTrue(responses[2]["result"]["isError"])


if __name__ == "__main__":
    unittest.main()
