"""Quit the running game through the bridge (if any), install the current build, relaunch through Steam, wait for the bridge.

C# changes need this because the game loads the mod DLL at start and locks it while running.
Changes on the Python side never need it.
"""
from __future__ import annotations
import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from probably_not_human import BridgeError, Client  # noqa: E402

STEAM_APP_ID = 4348910


def alive(client: Client) -> bool:
    try:
        client.health()
        return True
    except BridgeError:
        return False


def wait_for(predicate, seconds: float, step: float = 0.5) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(step)
    return False


def launch() -> None:
    url = f"steam://rungameid/{STEAM_APP_ID}"
    if os.name == "nt":
        os.startfile(url)  # type: ignore[attr-defined]
    else:  # WSL: hand the URL to Windows; cmd needs a Windows working directory.
        subprocess.run(["/mnt/c/Windows/System32/cmd.exe", "/c", "start", url], cwd="/mnt/c", check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bridge-dir")
    parser.add_argument("--load-slot", type=int, help="Load this save slot once the main menu is up")
    parser.add_argument("--no-install", action="store_true")
    args = parser.parse_args()
    client = Client(args.bridge_dir, timeout=30)
    if alive(client):
        try:
            client.act("game.quit")
        except BridgeError as error:
            # The game may already be exiting: the bridge vanishing mid-request is the expected outcome.
            if alive(client):
                raise SystemExit(f"game.quit failed: {error.code}: {error.message}")
        if not wait_for(lambda: not alive(client), 60):
            parser.error("The game did not exit; close it and run again")
        time.sleep(2)  # The process releases the mod DLL shortly after the bridge stops.
    if not args.no_install:
        subprocess.run([sys.executable, str(ROOT / "scripts/install.py")], check=True)
    launch()
    if not wait_for(lambda: alive(client), 180, 2):
        parser.error("The bridge did not come up; check that the mod is enabled")
    client.wait({"main_menu": True}, timeout=60)
    if args.load_slot is not None:
        client.act("save.load", {"slot_id": args.load_slot})
        result = client.wait({"active_save": True, "input_idle": True}, timeout=120)
        print("loaded slot", args.load_slot, "day", result["flow"]["day"])
    else:
        print("game running at the main menu")


if __name__ == "__main__":
    main()
