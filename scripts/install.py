"""Build against your installed game and copy only the mod DLL and manifest."""
from __future__ import annotations
import argparse
import os
from pathlib import Path
import shutil
import subprocess
from typing import Callable


DEFAULT_GAME_DIR = Path(r"E:\Games\SteamLibrary\steamapps\common\Probably Stolen") if os.name == "nt" else Path("/mnt/e/Games/SteamLibrary/steamapps/common/Probably Stolen")
ROOT = Path(__file__).resolve().parents[1]


def dotnet() -> tuple[str, Callable[[Path], str]]:
    """The dotnet executable, and a function giving a path in the form it expects (the Windows SDK works from WSL)."""
    found = shutil.which("dotnet")
    if found is not None:
        return found, str
    windows_dotnet = Path("/mnt/c/Program Files/dotnet/dotnet.exe")
    if windows_dotnet.is_file():
        return str(windows_dotnet), lambda path: subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()
    raise SystemExit("Install .NET SDK 6 or newer (Windows SDK also works from WSL)")


def build(game: Path) -> list[Path]:
    """Build the mod against the game in `game`; returns the files that make up the mod folder."""
    if not (game / "Probably Stolen_Data/Managed/Assembly-CSharp.dll").is_file():
        raise SystemExit("--game-dir must contain Probably Stolen_Data/Managed/Assembly-CSharp.dll")
    executable, native = dotnet()
    subprocess.run([executable, "build", native(ROOT / "mod/ProbablyNotHuman.csproj"), "-c", "Release", "--nologo", f"-p:GameDir={native(game)}"], check=True)
    return [ROOT / "mod/bin/Release/netstandard2.1/ProbablyNotHuman.dll", ROOT / "mod/manifest.xml"]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=DEFAULT_GAME_DIR)
    options = parser.parse_args()
    game = options.game_dir.resolve()
    files = build(game)
    destination = game / "Mods/probably_not_human"
    destination.mkdir(parents=True, exist_ok=True)
    try:
        for source in files:
            shutil.copy2(source, destination / source.name)
    except OSError as exc:
        parser.error(f"Cannot install mod files. Close Probably Stolen before installing, then retry. ({exc})")
    print(f"Installed {destination}. Activate 'Probably Not Human' in the game's Mods menu, then restart the game. No approval records or saves were changed by this installer.")


if __name__ == "__main__":
    main()
