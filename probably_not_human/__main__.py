import argparse
import json
import sys
from .client import BridgeError, Client
from .mcp import serve


def main() -> int:
    parser = argparse.ArgumentParser(description="Probably Stolen structured gameplay bridge")
    parser.add_argument("--bridge-dir", help="Windows or WSL mailbox directory")
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--heartbeat-max-age", type=float, default=10.0)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("health", "describe", "mcp"):
        commands.add_parser(command)
    observe = commands.add_parser("observe")
    observe.add_argument("--include", help="Comma-separated sections, e.g. game,trade")
    observe.add_argument("--compact", action="store_true", help="Drop geometry, grid cells, UI paths and UI visuals")
    wait = commands.add_parser("wait")
    wait.add_argument("until", help='JSON object of flow conditions, e.g. {"customer_arrived": true}')
    wait.add_argument("--wait-timeout", type=float, default=10.0)
    action = commands.add_parser("act")
    action.add_argument("action")
    action.add_argument("arguments", nargs="?", default="{}", help="JSON object")
    action.add_argument("--expected-observation", type=int)
    args = parser.parse_args()
    try:
        client = Client(args.bridge_dir, timeout=args.timeout, heartbeat_max_age=args.heartbeat_max_age)
        if args.command == "mcp":
            serve(client)
        else:
            if args.command == "act":
                result = client.act(args.action, json.loads(args.arguments), expected_observation=args.expected_observation)
            elif args.command == "wait":
                result = client.wait(json.loads(args.until), args.wait_timeout)
            elif args.command == "observe":
                result = client.observe(args.include.split(",") if args.include else None, args.compact)
            else:
                result = getattr(client, args.command)()
            print(json.dumps(result, indent=2, allow_nan=False))
        return 0
    except (BridgeError, OSError, ValueError, TypeError) as exc:
        print(json.dumps({"error": {"code": exc.code if isinstance(exc, BridgeError) else "client_error", "message": str(exc)}}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
