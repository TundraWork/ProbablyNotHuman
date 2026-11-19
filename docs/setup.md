# Setup

[← Back to the README](../README.md)

## Requirements

- Probably Stolen on Windows
- Python 3.10+
- An MCP client, or any agent that can run shell commands

## 1. Install the mod

Subscribe to [Probably Not Human](https://steamcommunity.com/sharedfiles/filedetails/?id=3842466338) on the Steam Workshop. Start the game, open **Mods**, activate **Probably Not Human** and restart the game.

To build the mod from source instead, see [Development](development.md#building-the-mod).

## 2. Install the client

Install it on the machine where your agent runs. That can be Windows, or WSL on the same PC.

```sh
pip install https://github.com/TundraWork/ProbablyNotHuman/archive/refs/heads/main.zip
```

On Linux and WSL, `pip` may refuse to install outside a virtual environment. Use `pipx install` with the same URL in that case.

## 3. Check the connection

Start the game (the main menu is enough) and run:

```sh
probably-not-human health
```

It should print some JSON with the game version and a timestamp.

## 4. Add the MCP server

**Claude Code**

```sh
claude mcp add probably-stolen -- probably-not-human mcp
```

**Codex CLI**, in `~/.codex/config.toml`:

```toml
[mcp_servers.probably-stolen]
command = "probably-not-human"
args = ["mcp"]
```

**Claude Desktop, Cursor and other clients that use a JSON config:**

```json
{
  "mcpServers": {
    "probably-stolen": {
      "command": "probably-not-human",
      "args": ["mcp"]
    }
  }
}
```

Restart the client after changing its config.

If your agent doesn't support MCP, it can run the same commands in a shell (`probably-not-human describe`, `observe`, `act`, `wait`) or use the Python library. See [Client](client.md).

## 5. Play

Load a save you don't mind losing, or tell the agent which slot to load, then ask it to play:

> Play Probably Stolen with the probably-stolen tools. Load save slot 3 and run the shop for one day, then tell me how it went. Don't touch the other save slots.

The MCP server has five tools: `health`, `describe` (lists every action with its JSON schema, and the wait conditions), `observe` (reads the game state), `action` (does one thing) and `wait` (blocks until the game reaches a condition).

## Troubleshooting

**`probably-not-human: command not found`**

pip's scripts folder is not on your `PATH`. Use `python -m probably_not_human` instead, e.g. `python -m probably_not_human health`. In an MCP config, set the command to `python` and the arguments to `["-m", "probably_not_human", "mcp"]`.

**`health` fails with `bridge_unavailable`, `stale_heartbeat` or `discovery_failed`**

- Make sure the game is running and the mod is active (it only loads after the restart that follows activation).
- The client looks for the mod's folder at `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\probably_not_human`. On WSL it searches `/mnt/c/Users/*` and gives up if it finds more than one match. Pass the folder yourself in that case:

  ```sh
  probably-not-human --bridge-dir "/mnt/c/Users/<you>/AppData/LocalLow/Questing Goose Studio/Probably Stolen/ModData/probably_not_human" health
  ```

**The mod stopped working after a game update**

The mod patches the game's input code, so updates can break it. Check the Workshop page for a new version.

## Uninstalling

1. Deactivate the mod in the **Mods** menu and restart the game, then unsubscribe.
2. `pip uninstall probably-not-human`
3. Optionally delete the `ModData\probably_not_human` folder mentioned above. It only holds request files and screenshots.
