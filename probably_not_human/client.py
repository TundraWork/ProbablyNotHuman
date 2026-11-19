"""Standard-library client for the main-thread game mailbox."""
from __future__ import annotations

import json
import os
import time
import uuid
from pathlib import Path
from typing import Any


class BridgeError(Exception):
    def __init__(self, code: str, message: str, flow: dict[str, Any] | None = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.flow = flow  # The game state the refusal was judged in, when the bridge sent it.


class OutcomeUnknown(BridgeError):
    """An action might have executed; callers must not blindly retry."""


def discover_bridge_dir() -> Path:
    suffix = Path("AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/probably_not_human")
    if os.name == "nt":
        profile = os.environ.get("USERPROFILE")
        if profile:
            return Path(profile) / suffix
        raise BridgeError("discovery_failed", "USERPROFILE is not set; specify --bridge-dir")
    candidates = sorted(p / suffix for p in Path("/mnt/c/Users").glob("*") if (p / suffix).is_dir())
    if len(candidates) == 1:
        return candidates[0]
    raise BridgeError("discovery_failed", "Expected one installed Windows bridge; specify --bridge-dir")


class Client:
    def __init__(self, bridge_dir: str | Path | None = None, *, timeout: float = 10.0,
                 heartbeat_max_age: float = 10.0, poll_interval: float = 0.05):
        self.bridge_dir = Path(bridge_dir) if bridge_dir is not None else discover_bridge_dir()
        self.timeout = timeout
        self.heartbeat_max_age = heartbeat_max_age
        self.poll_interval = poll_interval

    def _read(self, path: Path, *, replacement_window: float = 0) -> dict[str, Any]:
        deadline = time.monotonic() + replacement_window
        while True:
            try:
                value = json.loads(path.read_bytes())
                break
            except (OSError, ValueError, UnicodeError) as exc:
                # The game writes through ModHelper, which truncates then writes: a read can see a missing, locked,
                # empty or half-written file, and WSL reports a file mid-rewrite as ENODATA. This retries a read only;
                # actions are never resubmitted.
                if time.monotonic() >= deadline:
                    if isinstance(exc, FileNotFoundError):
                        raise BridgeError("bridge_unavailable", f"Missing {path.name}") from None
                    if isinstance(exc, OSError):
                        raise BridgeError("io_error", str(exc)) from exc
                    raise BridgeError("incomplete", f"Incomplete JSON in {path.name}") from exc
                time.sleep(0.005)
        if not isinstance(value, dict):
            raise BridgeError("protocol_error", f"Expected object in {path.name}")
        return value

    def health(self) -> dict[str, Any]:
        heartbeat = self._read(self.bridge_dir / "heartbeat.json", replacement_window=0.25)
        if type(heartbeat.get("protocol")) is not int or heartbeat["protocol"] != 1:
            raise BridgeError("protocol_error", "Unsupported heartbeat protocol")
        if not isinstance(heartbeat.get("session_id"), str) or not heartbeat["session_id"]:
            raise BridgeError("protocol_error", "Missing heartbeat session_id")
        if not isinstance(heartbeat.get("game_version"), str):
            raise BridgeError("protocol_error", "Missing heartbeat game_version")
        updated = heartbeat.get("updated_at")
        if type(updated) is not int:
            raise BridgeError("protocol_error", "Invalid heartbeat timestamp")
        age = time.time() * 1000 - updated
        if age > self.heartbeat_max_age * 1000 or age < -5000:
            raise BridgeError("stale_heartbeat", "Game heartbeat is stale or clock is out of sync")
        return heartbeat

    def request(self, method: str, params: dict[str, Any] | None = None, timeout: float | None = None) -> Any:
        if method not in {"observe", "describe", "act", "wait"}:
            raise ValueError("Unsupported mailbox method")
        if params is not None and not isinstance(params, dict):
            raise TypeError("params must be an object")
        session = self.health()["session_id"]
        request_id = str(uuid.uuid4())
        inbox = self.bridge_dir / "inbox"
        outbox = self.bridge_dir / "outbox"
        request_path = inbox / f"{request_id}.json"
        response_path = outbox / f"{request_id}.json"
        temporary = inbox / f".{request_id}.tmp"
        payload = json.dumps({"id": request_id, "protocol": 1, "session_id": session, "method": method,
                              "params": params or {}, "expires_at": int((time.time() + (timeout or self.timeout)) * 1000)},
                             allow_nan=False).encode("utf-8")
        try:
            inbox.mkdir(parents=True, exist_ok=True)
            outbox.mkdir(parents=True, exist_ok=True)
            with temporary.open("xb") as stream:
                stream.write(payload)
            os.replace(temporary, request_path)
        except OSError as exc:
            temporary.unlink(missing_ok=True)
            raise BridgeError("io_error", str(exc)) from exc
        deadline = time.monotonic() + (timeout or self.timeout)
        # Once published, any interruption leaves files intact: the outcome is unknown.
        while True:
            try:
                heartbeat = self.health()
            except BridgeError as exc:
                raise OutcomeUnknown(exc.code, f"{exc.message}; request {request_id} outcome unknown") from exc
            if heartbeat["session_id"] != session:
                raise OutcomeUnknown("session_changed", f"Game session changed; request {request_id} outcome unknown")
            if response_path.exists():
                try:
                    response = self._read(response_path, replacement_window=0.25)
                except BridgeError as exc:
                    if exc.code != "incomplete":
                        raise
                    response = None  # Still being written; poll again.
                if response is not None:
                    try:
                        return self._result(request_id, response)
                    finally:
                        request_path.unlink(missing_ok=True)
                        response_path.unlink(missing_ok=True)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise OutcomeUnknown("timeout", f"Request {request_id} timed out; outcome unknown, do not automatically retry")
            time.sleep(min(self.poll_interval, remaining))

    @staticmethod
    def _result(request_id: str, response: dict[str, Any]) -> Any:
        if (response.get("id") != request_id or type(response.get("protocol")) is not int
                or response["protocol"] != 1 or type(response.get("ok")) is not bool):
            raise BridgeError("protocol_error", "Invalid response envelope")
        if response["ok"]:
            if not isinstance(response.get("result"), (dict, list)):
                raise BridgeError("protocol_error", "Response result must be an object or array")
            return response["result"]
        error = response.get("error")
        if not isinstance(error, dict) or not all(isinstance(error.get(k), str) for k in ("code", "message")):
            raise BridgeError("protocol_error", "Invalid response error")
        raise BridgeError(error["code"], error["message"], error.get("flow"))

    def observe(self, include: list[str] | None = None, compact: bool = False, inventories: list[str] | None = None) -> dict[str, Any]:
        """include: sections among game, ui, surfaces, trade, input, night, saves; compact drops geometry/grid cells/UI visuals;
        inventories: list items only for these inventory ids or roles (game.inventory_summary still lists all)."""
        params: dict[str, Any] = {}
        if include is not None:
            params["include"] = list(include)
        if compact:
            params["compact"] = True
        if inventories is not None:
            params["inventories"] = list(inventories)
        return self.request("observe", params)

    def wait(self, until: dict[str, Any], timeout: float = 10.0) -> dict[str, Any]:
        """Block until every condition in until matches observation["flow"] (see describe()["wait"]).
        Returns {"satisfied", "frames", "flow", "until"}; satisfied is False when timeout ran out first."""
        if not isinstance(until, dict) or not until:
            raise ValueError("until must be a nonempty object of flow conditions")
        return self.request("wait", {"until": until, "timeout_ms": int(timeout * 1000)}, timeout=timeout + 5)

    def capture(self, path: str | None = None, timeout: float = 10.0) -> Path:
        """Capture the screen (or the UI element at path) and return the PNG's local path."""
        result = self.act("ui.capture", {"path": path} if path else {})
        if not self.wait({"captures_pending": 0}, timeout)["satisfied"]:
            raise BridgeError("timeout", "Capture was not written in time")
        return self.bridge_dir / result["file"]

    def describe(self) -> dict[str, Any] | list[Any]:
        return self.request("describe")

    def act(self, action: str, arguments: dict[str, Any] | None = None, *, expected_observation: int | None = None) -> dict[str, Any]:
        if not isinstance(action, str) or not action:
            raise ValueError("action must be a nonempty string")
        if arguments is not None and not isinstance(arguments, dict):
            raise TypeError("arguments must be an object")
        params = {"action": action, "arguments": arguments or {}}
        if expected_observation is not None:
            if type(expected_observation) is not int or expected_observation < 0:
                raise ValueError("expected_observation must be a nonnegative integer")
            params["expected_observation"] = expected_observation
        return self.request("act", params)
