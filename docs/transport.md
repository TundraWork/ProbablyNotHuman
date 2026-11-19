# Transport

[← Back to the README](../README.md)

The mailbox runs on Unity's main thread inside `ModData/probably_not_human`:

1. The client writes `inbox/<uuid>.json` by atomic rename: `{id, protocol: 1, session_id, method, params, expires_at}`. Methods are `observe`, `describe`, `act` and `wait`.
2. Every 50 ms the game takes up to four requests, writes a `claims/<id>` marker, deletes the request and dispatches it. Expired and other-session requests are refused.
3. The answer appears at `outbox/<uuid>.json` (`{id, protocol, ok, result | error}`); `wait` answers are deferred until satisfied or timed out.

`heartbeat.json` carries the session, game version and timestamp. `ModHelper` writes files by truncating and rewriting, so a read can catch a half-written file; the client retries reads (including WSL's `ENODATA`) and never resubmits an action. A game that stops after claiming a request answers it with `outcome_unknown` on the next start. A client timeout or session change raises `OutcomeUnknown`: observe and decide, never blindly retry an action that spends money or changes the day.

The folder is readable by any program running as your OS account: this is a trusted-local interface.
