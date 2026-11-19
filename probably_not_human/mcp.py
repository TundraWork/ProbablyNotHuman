"""Newline-delimited MCP JSON-RPC server; stdout is protocol-only."""
from __future__ import annotations
import base64
import json
import os
import sys
import time
from typing import Any, TextIO
from .client import BridgeError, Client

MAX_WAIT_SECONDS = 120.0

TOOLS = [
    {"name": name, "description": description, "inputSchema": schema}
    for name, description, schema in [
        ("observe", "Read structured game state. Default sections: game, trade, surfaces, night in a run; saves, ui at the menu. "
         "While flow.modal names a full-screen panel, only that panel is returned (modal) and only its actions work.", {"type": "object", "properties": {
            "include": {"type": "array", "items": {"type": "string"},
                        "description": "Sections: game, trade, surfaces, night, saves, ui (generic texts/controls for screens without a section), input (raw input bindings), flow; or keys of one, e.g. game.store, game.inventory_summary, surfaces.calendar"},
            "compact": {"type": "boolean", "description": "Default true: drop geometry, grid cells, UI paths, UI visuals and unreachable controls"},
            "inventories": {"type": "array", "items": {"type": "string"}, "description": "List items only for these inventory ids or roles (game.inventory_summary lists every inventory)"}}, "additionalProperties": False}),
        ("describe", "List available semantic actions and argument schemas", {"type": "object", "properties": {}, "additionalProperties": False}),
        ("wait", "Block until flow matches every condition in until (e.g. {\"customer_arrived\": true, \"dialogue_idle\": true}), or the timeout (default 10 s, at most 120 s) passes; satisfied=false then.", {
            "type": "object", "properties": {"until": {"type": "object"},
                "timeout": {"type": "number", "exclusiveMinimum": 0, "description": "Seconds"},
                "timeout_ms": {"type": "integer", "exclusiveMinimum": 0, "description": "Milliseconds (alternative to timeout)"}},
            "required": ["until"], "additionalProperties": False}),
        ("health", "Read and validate the live game heartbeat", {"type": "object", "properties": {}, "additionalProperties": False}),
        ("action", "Execute one semantic game action; never automatically retry", {
            "type": "object", "properties": {"action": {"type": "string", "minLength": 1}, "arguments": {"type": "object"}, "args": {"type": "object", "description": "Alias of arguments"}, "expected_observation": {"type": "integer", "minimum": 0}},
            "required": ["action"], "additionalProperties": False}),
    ]
]


def compact_json(value: Any) -> str:
    # Raw UTF-8 and no padding: escaped CJK text costs six characters per character.
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def serve(client: Client, source: TextIO = sys.stdin, sink: TextIO = sys.stdout) -> None:
    initialized = False
    # PROBABLY_NOT_HUMAN_MCP_LOG=<path> appends one JSON line per tool call: tool, arguments, result size, error code, seconds.
    log_path = os.environ.get("PROBABLY_NOT_HUMAN_MCP_LOG")
    def record(name: str, arguments: dict, text: str, error: str | None, started: float) -> None:
        if log_path:
            with open(log_path, "a", encoding="utf-8") as log:
                log.write(compact_json({"at": time.time(), "tool": name, "arguments": arguments, "chars": len(text),
                                        "error": error, "seconds": round(time.monotonic() - started, 3)}) + "\n")
    def reply(identifier: Any, *, result: Any = None, error: dict | None = None) -> None:
        envelope = {"jsonrpc": "2.0", "id": identifier}
        envelope["error" if error is not None else "result"] = error if error is not None else result
        sink.write(json.dumps(envelope, allow_nan=False) + "\n")
        sink.flush()

    while True:
        line = source.readline()
        if not line:
            return
        try:
            message = json.loads(line)
        except ValueError:
            reply(None, error={"code": -32700, "message": "Parse error"})
            continue
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
            reply(None, error={"code": -32600, "message": "Invalid request"})
            continue
        if "id" not in message:
            # MCP notifications, including initialized/cancelled, never receive replies.
            continue
        identifier = message["id"]
        if identifier is not None and type(identifier) not in (str, int):
            reply(None, error={"code": -32600, "message": "Invalid request id"})
            continue
        method = message["method"]
        params = message.get("params", {})
        if not isinstance(params, dict):
            reply(identifier, error={"code": -32602, "message": "params must be an object"})
            continue
        if method == "initialize":
            if not isinstance(params.get("protocolVersion"), str):
                reply(identifier, error={"code": -32602, "message": "protocolVersion is required"})
                continue
            initialized = True
            version = params["protocolVersion"]
            if version not in {"2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"}:
                version = "2025-03-26"
            reply(identifier, result={"protocolVersion": version, "capabilities": {"tools": {}},
                                      "serverInfo": {"name": "probably-not-human", "version": "1.2.0"}})
        elif method == "ping":
            reply(identifier, result={})
        elif method not in {"tools/list", "tools/call"}:
            reply(identifier, error={"code": -32601, "message": "Method not found"})
        elif not initialized:
            reply(identifier, error={"code": -32000, "message": "Initialize first"})
        elif method == "tools/list":
            reply(identifier, result={"tools": TOOLS})
        else:
            name = params.get("name")
            arguments = params.get("arguments", {})
            if not isinstance(name, str) or name not in {"observe", "describe", "action", "health", "wait"}:
                reply(identifier, error={"code": -32602, "message": "Unknown tool"})
                continue
            if name == "action" and isinstance(arguments, dict) and "args" in arguments and "arguments" not in arguments:
                arguments = {**arguments, "arguments": arguments["args"]}  # A common slip; the action's own arguments either way.
                del arguments["args"]
            if not isinstance(arguments, dict) or (name not in ("action", "observe", "wait") and arguments) or (name == "wait" and not isinstance(arguments.get("until"), dict)) or (name == "action" and (
                set(arguments) - {"action", "arguments", "expected_observation"} or not isinstance(arguments.get("action"), str)
                or not arguments["action"] or not isinstance(arguments.get("arguments", {}), dict)
                or ("expected_observation" in arguments and (type(arguments["expected_observation"]) is not int or arguments["expected_observation"] < 0)))):
                reply(identifier, error={"code": -32602, "message": "Invalid tool arguments"})
                continue
            started = time.monotonic()
            try:
                if name == "action":
                    value = client.act(arguments["action"], arguments.get("arguments"), expected_observation=arguments.get("expected_observation"))
                elif name == "wait":
                    # One request at a time: a long wait would hold every later call, so it is capped.
                    seconds = arguments["timeout_ms"] / 1000 if "timeout_ms" in arguments else float(arguments.get("timeout", 10.0))
                    value = client.wait(arguments["until"], min(max(seconds, 0.05), MAX_WAIT_SECONDS))
                elif name == "observe":
                    value = client.observe(arguments.get("include"), bool(arguments.get("compact", True)), arguments.get("inventories"))
                else:
                    value = getattr(client, name)()
                text = compact_json(value)
                content = [{"type": "text", "text": text}]
                # A screen capture comes back as an image the model can look at, not only as a file on the game's side.
                if name == "action" and arguments["action"] == "ui.capture" and isinstance(value, dict) and value.get("file"):
                    image = client.bridge_dir / value["file"]
                    if image.is_file():
                        content.append({"type": "image", "data": base64.b64encode(image.read_bytes()).decode("ascii"), "mimeType": "image/png"})
                reply(identifier, result={"content": content, "isError": False})
                record(name, arguments, text, None, started)
            except (BridgeError, OSError, ValueError) as exc:
                error = {"code": exc.code if isinstance(exc, BridgeError) else "client_error", "message": str(exc)}
                if getattr(exc, "flow", None) is not None:
                    error["flow"] = exc.flow
                text = compact_json(error)
                reply(identifier, result={"content": [{"type": "text", "text": text}], "isError": True})
                record(name, arguments, text, error["code"], started)
