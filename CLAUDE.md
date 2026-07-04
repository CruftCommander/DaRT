# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

DaRT (DayZ RCon Tool) is a Windows Forms (.NET Framework 4.8, C#) GUI client for the BattlEye RCon protocol, used to administer DayZ mod servers (player list, kick/ban management, live chat/log console, banner-based auto-refresh, player database). It builds on the [BattleNET](https://github.com/marceldev89/BattleNET) library for the RCon wire protocol.

The author states in `README.md` that the project is **not actively developed** — treat changes as maintenance/bugfix-oriented rather than expecting ongoing feature work, unless the user says otherwise.

## Build

```bash
# From repo root, using the Visual Studio solution (targets x86 only — Debug|x86 / Release|x86)
msbuild DaRT.sln /p:Configuration=Release /p:Platform=x86
# or open DaRT.sln in Visual Studio and build there
```

There is no test project and no CLI test runner — verification is manual (run the GUI, or run `DaRT.exe` with CLI args, see below).

Known quirk (documented in README): DaRT may fail on the very first build after a clone; a second build succeeds. Cause not diagnosed — don't treat a first-build failure as a real regression until you've rebuilt once.

## Running

DaRT has two mutually exclusive modes selected by whether CLI args are passed to `DaRT.exe` (`Program.cs:32`):

- **No args** → launches the WinForms GUI (`GUImain`).
- **Args present** → headless console mode; allocates a console (`AllocConsole`) and runs one of:
  - `-command=<cmd>` — sends a single RCon command (or the built-ins `players`/`bans`/`admins`) and exits.
  - `-script=<path>` — runs a line-oriented script file (see Script Mode below) and exits.

Common flags: `-ip=`, `-port=`, `-password=`/`-pass=`/`-pw=`, `-output=<file>` (tee console output to a file), `-close` (skip the "press any key" prompt), `-loop=N` (script mode: repeat N times, `-1` = forever).

### Script mode mini-language (`Program.cs`, script region)
Plain lines are sent verbatim as RCon commands. Special tokens:
- `//...` — comment line, skipped.
- `wait=<ms>` — sleep.
- `exit` / `quit` / `close` — stop the script.
- `exec=<command>` — run an arbitrary RCon command.
- `kickAll[=reason]`, `banAll[=reason]` — kick/ban every currently connected player.
- `if <a> <op> <b>:<then>[:<else>]` — inline conditional (`op` is `>`, `<`, `=`/`==`); branches are themselves script lines.
- Substitution variables usable in any line: `%p` (player count), `%a` (admin count), `%b` (ban count), `%r` (a random online player's name), `%l` (current loop iteration).

## Architecture

### RCon transport (`Classes/RCon.cs`)
`RCon` wraps `BattleNET.BattlEyeClient` and is the sole point of contact with the game server. Key things to know before touching it:

- **Request/response correlation is manual and polling-based.** `Send()` throttles outgoes to >10ms apart (BattlEye drops packets sent too fast) and records the packet id in `_sent`. Async replies arrive via the `BattlEyeMessageReceived` event (`HandleMessage`) and are stashed in `_received` keyed by id. Getter methods (`getPlayers()`, `getBans()`, `getAdmins()`) send a command, then busy-poll `GetResponse(id)` with `Thread.Sleep(10)` up to a configurable tick count (`Settings.Default.playerTicks` / `banTicks`) before giving up and returning an empty list. There is no async/await path — any new blocking query should follow this same send-then-poll pattern.
- **`HandleMessage` is a giant `if/else if` dispatcher** keyed on message text prefixes (`(Global)`, `Scripts Log:`, `Verified GUID (`, `RCon admin #`, etc.), each gated by a matching `Settings.Default.showXxx` boolean and mapped to a `LogType` (`Enums/LogTypes.cs`). Unrecognized messages fall through to a `LogType.Debug` "UNKNOWN:" line. **When adding support for a new BattlEye log/message type, add both a `Settings.Default.showXxx` setting (`Properties/Settings.settings`) and a matching `LogType` enum member, then a new branch here** — the two are meant to move together.
- Reconnection is automatic: `HandleDisconnect` on `ConnectionLost`/`SocketException` spins a background thread (`Reconnect`/`HandleReconnect`) that loops `Disconnect()` → `Connect()` every 5s until it succeeds; `_reconnecting` suppresses duplicate log spam and duplicate reconnect loops during this window.
- Admin chat "call" detection (`IsCall`) flags a message as important (triggers window-flash) if it mentions "admin" or, optionally, the configured admin name (`Settings.Default.name`) — used for the taskbar-flash-on-admin-call feature.
- `getRawBans()`/`getRawPlayers()`/`getRawAdmins()` are **dead code** — they currently just `return null;` with the old implementation commented out below. `-command=players/bans/admins` in console mode (`Program.cs`) calls these and will NPE; don't assume they work.
- Constructed with a nullable `GUImain _form` — passing `null` (as `Program.cs` does for console mode) is intentional and `RCon` guards every UI callback with `if (_form != null)`. Preserve that guard in any new code path through `RCon`.

### GUI (`Windows/GUImain.cs`, ~3700 lines)
This is the monolithic core of the app — one `Form` class handling connection state, three main tabs (Players / Bans / Player Database), the console/chat/logs views, and all background polling. Notable patterns to preserve when editing it:

- **Tab-driven polling.** A `Timer` tick (and the `tabControl_SelectedIndexChanged` handler) checks `tabControl.SelectedTab.Text` (literal strings "Players", "Bans", "Player Database") and spins a matching background `Thread` (`thread_Player`, `thread_Bans`, `thread_Database`, `thread_Admins`, `thread_Banner`, `thread_Sync`, `thread_News`) only if that tab is active and a same-type request isn't already pending (`pendingPlayers`/`pendingBans`/`pendingDatabase` flags). Follow this "check pending flag, spin background thread, thread marshals back via `Invoke`" pattern for any new polling feature rather than calling RCon methods directly from UI event handlers.
- **All settings are strongly-typed `Settings.Default.*` properties** (`Properties/Settings.settings`, generated into `Settings.Designer.cs`) — there is no ad hoc config file parsing in the GUI. New user-configurable behavior should be added as a new `Settings.settings` entry, not a hardcoded constant.
- **SQLite persistence** via `Mono.Data.Sqlite` against `data\db\dart.db` (relative to the exe), created on first run with `CREATE TABLE IF NOT EXISTS`: `players` (seen-player history: ip/guid/name/last-seen/location), `comments` (per-GUID admin notes), `hosts` (saved connection profiles). `GUImain`'s constructor also has one-time migration logic that imports legacy separate `players_old.db` / `hosts_old.db` / `comments_old.db` files if present — this is a historical upgrade path, not something new code needs to replicate.
- Log rendering funnels through `Log(message, LogType, isCall)`, which timestamps (if `showTimestamps`), colors by chat/filter type (if `colorChat`/`colorFilters`), appends to an in-memory ring buffer capped at `Settings.Default.buffer`, and optionally writes to a log file (`saveLog`) and flashes the taskbar icon (`flash` + `isCall`).
- `GUIcrash` is a custom unhandled-exception dialog wired up in `Program.cs` (`Application.ThreadException` / `AppDomain.UnhandledException`) — when the debugger is attached, exceptions instead bubble normally so you get the VS exception dialog.

### Domain model classes (`Classes/`)
Plain data-holder classes with multiple constructor overloads for different call sites (no builder pattern) — e.g. `Player` has 4 constructors and `Ban` has 4, each used by a different code path (live RCon response parsing vs. SQLite row hydration vs. quick-ban UI). When adding a field, check all constructor overloads of that class rather than assuming one canonical constructor.

- `Player`, `Ban`, `BanIP`, `BanOffline`, `Kick`, `Message`, `Location`, `LogItem` — mirror the RCon protocol's `players`/`bans` text responses and the UI's ban/kick dialogs.
- `Enums/LogTypes.cs` — see GUI section above; keep in sync with `RCon.HandleMessage` and `Settings.settings` show-flags.

### Helpers (`Helpers/`)
`ColumnSorter.cs` / `ListViewColumnSorter.cs` (ListView column click-to-sort), `ExtendedRichTextBox.cs` (custom RichTextBox component used for the console/chat/log views), `FlashWindow.cs` (P/Invoke taskbar flash, used for admin-call notifications).

## Cross-cutting notes

- Third-party binaries (`BattleNET.dll`, `HtmlAgilityPack.dll`, `Mono.Data.Sqlite.dll`, `sqlite3.dll`) are committed under `DaRT\data\lib\` and referenced via `HintPath`, not restored via NuGet/Composer — there is no package manager step in the build.
- The project is `Platform=x86`-only (BattleNET / native sqlite3.dll are 32-bit); don't add `AnyCPU`/x64 configs without also confirming the native SQLite DLL situation.
- Country flag icons live under `DaRT\data\img\flags\` and are matched to players by GeoIP/HTML-scraping (`HtmlAgilityPack` dependency) — relevant if touching player-location display.
