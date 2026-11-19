"""Stage the mod as a Steam Workshop item and upload it.

The item's page lives in workshop/: item.json (id, visibility, title, repo_url), description.bbcode and
preview.png. The first upload creates the item and stores its id in item.json; later runs update it.

  --steam-client   upload through the Steam client that is running and signed in (no password needed)
  --account NAME   upload with steamcmd (asks for the password and Steam Guard code)

With neither, the item is only staged and the steamcmd command is printed.
"""
from __future__ import annotations
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from install import DEFAULT_GAME_DIR, ROOT, build, dotnet  # noqa: E402

APP_ID = 4348910
WORKSHOP = ROOT / "workshop"
VISIBILITY = {"public": 0, "friends": 1, "hidden": 2}


def default_steamcmd() -> Path | None:
    for candidate in (Path(r"E:\Games\steamcmd\steamcmd.exe"), Path("/mnt/e/Games/steamcmd/steamcmd.exe")):
        if candidate.is_file():
            return candidate
    found = shutil.which("steamcmd")
    return Path(found) if found else None


def windows_path(path: Path) -> str:
    """Windows tools started from WSL need Windows paths."""
    return str(path) if os.name == "nt" else subprocess.check_output(["wslpath", "-w", str(path)], text=True).strip()


def vdf(pairs: dict[str, str]) -> str:
    lines = [f'\t"{key}"\t\t"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"' for key, value in pairs.items()]
    return '"workshopitem"\n{\n' + "\n".join(lines) + "\n}\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    how = parser.add_mutually_exclusive_group()
    how.add_argument("--steam-client", action="store_true", help="Upload through the running Steam client")
    how.add_argument("--account", help="Upload with steamcmd, signing in as this Steam account")
    parser.add_argument("--changenote", help="Change note for this upload (default: the mod version)")
    parser.add_argument("--visibility", choices=sorted(VISIBILITY), help="Change the item's visibility and remember it")
    parser.add_argument("--game-dir", type=Path, default=DEFAULT_GAME_DIR)
    parser.add_argument("--steamcmd", type=Path, default=default_steamcmd(), help="steamcmd executable")
    parser.add_argument("--stage-dir", type=Path, help="Where to stage the item; must be on a Windows drive (default: next to steamcmd)")
    parser.add_argument("--no-build", action="store_true", help="Stage the existing build")
    args = parser.parse_args()
    steamcmd = args.steamcmd.resolve() if args.steamcmd and args.steamcmd.is_file() else None
    if steamcmd is None and not args.steam_client:
        parser.error("steamcmd not found; pass --steamcmd, or use --steam-client")
    if args.stage_dir is None and steamcmd is None:
        parser.error("Pass --stage-dir (a folder on a Windows drive)")
    stage = (args.stage_dir or steamcmd.parent / "workshop_staging").resolve() / "probably_not_human"

    item_file = WORKSHOP / "item.json"
    item = json.loads(item_file.read_text(encoding="utf-8"))
    if args.visibility:
        item["visibility"] = VISIBILITY[args.visibility]
    description = (WORKSHOP / "description.bbcode").read_text(encoding="utf-8").strip()
    if "{repo_url}" in description:
        if not item.get("repo_url"):
            parser.error("Set repo_url in workshop/item.json: the description links to the client")
        description = description.replace("{repo_url}", item["repo_url"])
    if len(description) > 8000 or len(item["title"]) > 128:
        parser.error(f"Steam limits: description 8000 characters (now {len(description)}), title 128")

    files = build(args.game_dir.resolve()) if not args.no_build else [ROOT / "mod/bin/Release/netstandard2.1/ProbablyNotHuman.dll", ROOT / "mod/manifest.xml"]
    version = ET.parse(ROOT / "mod/manifest.xml").getroot().findtext("ModVersion")

    content = stage / "content"
    if content.exists():
        shutil.rmtree(content)
    content.mkdir(parents=True)
    for source in [*files, WORKSHOP / "preview.png"]:
        shutil.copy2(source, content / source.name)
    print(f"Staged v{version} in {content}: " + ", ".join(sorted(path.name for path in content.iterdir())))

    request = {
        "appid": str(APP_ID),
        "publishedfileid": str(item["publishedfileid"]),
        "contentfolder": windows_path(content),
        "previewfile": windows_path(content / "preview.png"),
        "visibility": str(item["visibility"]),
        "title": item["title"],
        "description": description,
        "changenote": args.changenote or f"v{version}",
    }

    def record(published: str) -> None:
        if published != "0" and published != str(item["publishedfileid"]):
            item["publishedfileid"] = published
        item_file.write_text(json.dumps(item, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if args.steam_client:
        request_file = stage / "request.json"
        request_file.write_text(json.dumps(request, indent=2, ensure_ascii=False), encoding="utf-8")
        executable, native = dotnet()
        uploader = subprocess.Popen([executable, "run", "--project", native(ROOT / "tools/WorkshopUpload/WorkshopUpload.csproj"), "-c", "Release", f"-p:GameDir={native(args.game_dir.resolve())}", "--", native(request_file)],
                                    stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
        for line in uploader.stdout:
            print(line, end="")
            created = re.match(r"publishedfileid=(\d+)", line)
            if created:  # recorded as soon as the item exists, so a failed upload does not create a second item next time
                record(created.group(1))
        if uploader.wait() != 0:
            raise SystemExit("Upload failed; see the output above.")
    else:
        vdf_file = stage / "item.vdf"
        vdf_file.write_text(vdf(request), encoding="utf-8", newline="\n")
        command = [str(steamcmd), "+login", args.account or "<steam account>", "+workshop_build_item", windows_path(vdf_file), "+quit"]
        if not args.account:
            print("Not uploaded. Use --steam-client, or run steamcmd yourself:\n  " + " ".join(f'"{part}"' if " " in part else part for part in command))
            return
        subprocess.run(command, cwd=steamcmd.parent)
        published = re.search(r'"publishedfileid"\s+"(\d+)"', vdf_file.read_text(encoding="utf-8"))
        if not published or published.group(1) == "0":
            raise SystemExit("steamcmd did not report an item id; read its output above. Nothing was recorded.")
        record(published.group(1))
    print(f"Item {item['publishedfileid']}: https://steamcommunity.com/sharedfiles/filedetails/?id={item['publishedfileid']}")


if __name__ == "__main__":
    main()
