# Client: command line, Python library, MCP server

[← Back to the README](../README.md)

The client is one dependency-free Python package with three faces. All of them send the same four requests to the mod: `observe`, `describe`, `act` and `wait`.

## Command line and Python

```sh
python3 -m probably_not_human health
python3 -m probably_not_human describe                       # action names, JSON schemas, wait conditions
python3 -m probably_not_human observe --include game --compact
python3 -m probably_not_human act customer.next '{}'
python3 -m probably_not_human wait '{"customer_ready": true}' --wait-timeout 30
```

The bridge folder is found automatically on Windows and single-profile WSL once the mod has run; otherwise pass `--bridge-dir` (`%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\ModData\probably_not_human`).

```python
from probably_not_human import Client, BridgeError, OutcomeUnknown

client = Client()
state = client.observe(["game", "trade"], compact=True)
client.act("day.open")
client.wait({"phase": "OPEN"}, timeout=30)          # no sleeps: wait on game.flow instead
png = client.capture()                               # screen capture as a local PNG path
```

## Waiting on the game

Every observation carries `flow`, a small set of conditions an agent can wait on. The `wait` request is answered by the game the frame its conditions hold (or at its timeout with `satisfied: false`), so scripts never sleep or poll.

| Condition | Meaning |
| --- | --- |
| `scene`, `active_save`, `main_menu` | Scene name; a save is loaded; the main menu is up |
| `phase`, `day`, `cash` | `MORNING`/`OPEN`/`CLOSED`…; current day; credits (null outside a run) |
| `customer_callable` | The bell would call the next customer now |
| `customer_arrived`, `customer_ready`, `no_customer` | A customer is at the counter; ready for their opening lines; nobody there |
| `dialogue`, `dialogue_idle`, `dialogue_actions_pending` | `none`/`choice`/`waiting`/`playing`; no line is being typed out; line consequences (goods laid out, payments, departures) still scheduled, about a second after their line |
| `queued_customers`, `counter_goods` | Customers left today; customer goods on the counters |
| `trade_open`, `response_choice`, `night_menu_open`, `input_idle` | Trade tablet open; an item-choice prompt is up; bedroll menu open; no gesture running |
| `afterhours` | `in_store`/`choose_destination`/`travelling`/`at_location` (string conditions reject values they never take) |
| `night_report_day`, `captures_pending`, `shooting_mode`, `transition` | Day of the latest night report; captures not yet written; gun raised; a fade to black is running (the morning report opens as it lifts) |
| `modal` | Panel covering the store: `daily_report`, `calendar`, `wilde_network`, `escape_menu`, `game_over`, `run_summary`, an item's panel over the counter (`newspaper`, `paper`, `book`, `handnote`, `multi_page`, `panel:<name>` such as `panel:wanted_panel`), or `null` |

Values match exactly, or use `{"ge": n}` / `{"le": n}` for numbers. `"frames": n` waits n rendered frames.

## MCP server

`python3 -m probably_not_human mcp` is a standard-library stdio MCP server with the tools `health`, `observe` (`include`, `compact`; MCP defaults to `compact: true`), `describe`, `action` and `wait`.

Client configuration is in the [setup guide](setup.md#4-connect-your-agent). Without installing the package, use `"command": "python3"`, `"args": ["-m", "probably_not_human", "mcp"]` and set `"cwd"` to a clone of this repository.

Supported MCP revisions: `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`. Stdout is JSON-RPC only; tool failures return `isError: true` with the bridge's error code.
