# Development

[← Back to the README](../README.md)

## Building the mod

The mod is built against your own installed game assemblies; proprietary DLLs and decompiled game source are not distributed. Targeted installation: application version **0.46D** (build `VERSION 100-Rev13`), Unity **2021.3.45f2**.

Requires .NET SDK 6+ and Python 3.10+. From this repository:

```sh
python3 scripts/install.py            # build and copy into Mods/probably_not_human (close the game first)
python3 scripts/restart_game.py       # quit a running game cleanly, install, relaunch through Steam, wait for the menu
python3 scripts/restart_game.py --load-slot 3   # ...and load a save
```

The installer defaults to `E:\Games\SteamLibrary\steamapps\common\Probably Stolen` (or its `/mnt/e/...` WSL path); override with `--game-dir`. On WSL it can use the Windows .NET SDK. It installs only `ProbablyNotHuman.dll` and `manifest.xml`, and does not activate the mod or touch saves.

Start the game, open **Mods** and activate **Probably Not Human**. The mod declares **no capabilities** (`<Capabilities />`), so there is nothing to approve: it follows the modding rules that take effect on December 10, 2026 ([modding guide](https://github.com/dragonpan2/probably-stolen-modding/blob/main/ModdingGuide.md)):

- No `files`: the mailbox and screen captures go through `ModHelper`'s mod-data helpers (`ModData/probably_not_human`).
- No `reflection-external`: private game members are reached through `ModReflect` (`Get`/`Set`/`Call`/`CallStatic`/`Method<T>`/`Target`/`FindType`).
- No `dynamic-code`: input redirection uses Harmony patches on a generated, static target list (`mod/InputReaders.g.cs`) rather than IL scanning at runtime.
- No `[ModRestricted]` save APIs: there is no save duplication or history loading. For those, use the game's own Load Game UI through `ui.click`.

The game's own `ModScanner` reports the current build as clean: no detected or deprecated capabilities, not refused.

## Tests and maintenance

```sh
dotnet build mod/ProbablyNotHuman.csproj -c Release -p:GameDir="<game folder>"
python3 -m unittest discover -s tests
```

After a game update, regenerate the input-reader list from the game assembly (Mono.Cecil; skips loader, Steam, Workshop, trust and `[ModRestricted]` code):

```sh
dotnet run --project tools/InputReaders -- "<game>/Probably Stolen_Data/Managed/Assembly-CSharp.dll" mod/InputReaders.g.cs
```

## Publishing to the Steam Workshop

The game has no upload button, so items are uploaded from outside the game by a Steam account that owns it. The Workshop page is kept in [`workshop/`](../workshop): `item.json` (item id, visibility, title, `repo_url`), `description.bbcode` and `preview.png` (pixel art drawn by `make_preview.py`, standard library only).

```sh
python3 scripts/publish_workshop.py                   # build and stage only
python3 scripts/publish_workshop.py --steam-client    # ...and upload through the running Steam client
python3 scripts/publish_workshop.py --steam-client --visibility public --changenote "What changed"
```

`--steam-client` runs [`tools/WorkshopUpload`](../tools/WorkshopUpload), a small program that talks to the Steam client you are already signed in to, so it needs no password. It is built against the game's own `Steamworks.NET` and `steam_api64.dll`. `--account <steam login>` uploads with Valve's `steamcmd` instead, which asks for the password and Steam Guard code.

The script stages `ProbablyNotHuman.dll`, `manifest.xml` and `preview.png` in `workshop_staging/probably_not_human` (next to `steamcmd` by default, or under `--stage-dir`). The first upload creates the item and records its id in `workshop/item.json`; later runs update that item. Bump `<ModVersion>` (and `<Version>` in the `.csproj`) before an update, and keep `<GameVersions>` current.

Every upload is reviewed by the game's moderators before other players can see it.

## Code layout

`GameplayAdapter` (state and gameplay actions), `TradeSurface` and `NightSurface` (tablet and night workflows), `SaveSurface`, `ServiceSurface` (phone and network), `SurfaceAdapter`/`SurfaceObservation` (tools, paper, scanners), `UIAdapter` and `ViewGeometry` (generic UI), `VirtualInput` (frame-scheduled input), `Flow` (wait conditions), `ScreenCapturer`, `FileMailbox` (transport), `BridgeRuntime` (main-thread dispatch).
