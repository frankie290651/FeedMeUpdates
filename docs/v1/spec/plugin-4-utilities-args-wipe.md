# FeedMeUpdates v1.6.5 plugin — behaviour spec, part P4

Source: `/home/claude/frankie290651/feedmeupdates/FeedMeUpdates.cs` (plugin `[Info("FeedMeUpdates", "frankie290651", "1.6.5")]`, class `FeedMeUpdates : CovalencePlugin`).
Range covered in full: lines 22-160 (config + runtime state) and 2676-4167 (Utilities, Hooks, Validation & Argument Building, Wipe Helpers).
Other parts of the plugin and `Application/Program.cs` were read only where needed to understand how the covered code is used; statements about them are marked "(context)".

Conventions: "L1234" = line number in `FeedMeUpdates.cs`. "App Lnnn" = line in `Application/Program.cs`. `Puts(...)` = Oxide console/log output. All user-facing strings are quoted verbatim, including their typos, because log scrapers and users may rely on them.

Contents

1. Configuration and runtime state (L22-160)
2. Utilities region (L2676-3508)
3. Hooks region (L3510-3607)
4. Validation & argument building (L3609-3857) — includes the full CLI contract
5. Wipe helpers (L3859-4165)
6. External contracts (consolidated)
7. Implicit knowledge and workarounds
8. Defects and risks
9. Open questions

---

## 1. Configuration and runtime state (L22-160)

### 1.1 Config file

Oxide config `oxide/config/FeedMeUpdates.json`, deserialised into `ConfigData` (L24-73). JSON keys are the property names, no attributes, no versioning/migration. `LoadDefaultConfig` (L78-81) writes `new ConfigData()`.

| Key | Type | Default | Notes |
|---|---|---|---|
| ServerDirectory | string | "" | Root with RustDedicated. Used as base for marker, lock, `steambid.temp`, `.new` binary, checksum file |
| SteamCmdPath | string | "" | |
| UpdaterExecutablePath | string | "" | May be relative; see 4.2 |
| ShowUpdaterConsole | bool | false | |
| ServerStartScript | string | "" | At Init, if empty and `RustOnService` false, it is overwritten with `C:\rust-server\start_server.bat` (Windows) or `/home/rust/rust-server/start_server.sh` (other) and the config is saved (L752-756, context) |
| RunServerScriptHidden | bool | false | Sent inverted as `-showserverconsole` |
| ServerTmuxSession | string | "" | |
| RustOnService | bool | false | |
| ServiceName | string | "" | |
| ServiceType | string | "" | |
| HttpTimeoutMs | int | 3000 | `<= 0` is replaced by 3000 at Init |
| StartupScan | bool | false | README example shows true; code default is false |
| MaxAttempts | int | 0 | 0 = unlimited |
| CheckIntervalMinutes | int | 10 | Clamped to >= 1 where used |
| CountdownMinutes | int | 5 | Clamped to >= 1 for update countdown only; NOT clamped for custom wipe |
| UpdatePlugins | bool | false | |
| OnlyServerProtocolUpdate | bool | false | |
| UseScheme | bool | false | |
| SchemeFile | string | "" | |
| DailyRestartTime | string | "" | `HH:mm` or `H:mm`, server local time |
| MinutesBeforeRestart | int | 60 | Guard active only if > 0 |
| DiscordNotificationsEnabled | bool | false | |
| DiscordWebhookUrl | string | "" | |
| BeforeForceWipeRange | int | 15 | `<= 0` is replaced by 15 in memory when checked (L4015-4016) |
| CustomWipeDay | string | "" | day-first date, see 5.4 |
| CustomWipeTime | string | "" | `H:mm` / `HH:mm`, see 5.4 |
| ServerIdentity | string | "" | |
| NextWipeServerName | string | "" | base64 on CLI |
| NextWipeServerDescription | string | "" | base64 on CLI |
| NextWipeMapUrl | string | "" | base64 on CLI |
| NextWipeLevel | string | "" | |
| NextWipeSeed | string | "" | decimal uint, validated at Init |
| NextWipeRandomSeed | bool | false | |
| NextWipeMapsize | string | "" | int 1000..6000, validated at Init |
| NextWipeKeepBps | bool | true | |
| NextWipeResetRustPlus | bool | false | |
| NextWipeDeletePlayerData | bool | false | |
| NextWipeDeletePluginDatafiles | string | "" | A JSON array of strings, stored as a string |
| UpdaterMarkerFileName | string | "updateresult.json" | The app hard-codes `updateresult.json` (App L359, L3199); changing this key breaks the handshake |
| UpdaterLockFileName | string | "updating.lock" | The app hard-codes `updating.lock` (App L297) |
| MarkersSubfolder | string | "markers" | |

### 1.2 Constants and runtime fields

- `DataFileName = "FeedMeUpdatesData"` (L87) -> `oxide/data/FeedMeUpdatesData.json`, a flat `Dictionary<string,string>` with keys `LocalSteamBuildID`, `RemoteSteamBuildID`, `TryNumber` (context L946-1078).
- `UAValue = "FeedMeUpdates"` (L145) — User-Agent value for HTTP calls.
- `debugging = false`, `quitBeforeExecOnDebugging = true` (L98-99): compile-time debug switches; never assigned at runtime. With `debugging` true, `StartUpdaterExecutable` quits the server WITHOUT launching the updater (L2154-2161, context). Dead in production.
- `enablePlugin` (L101): false when `MaxAttempts != 0 && trynumber >= MaxAttempts` (L850). Only consulted by `StartPeriodicCheck` (L1598). Also set false in the (practically unreachable) catch of `AutoUpdateTask` (L2872).
- `SystemUpdating` (L103): true while the app self-update promotion (2.4) runs. Consulted by `BeginUpdateCountdown` (L2063) and `OnServerInitialized` (L3524, L3593).
- Timers: `checkTimer` (periodic update check), `DailyRestartCheckRepeater` (60 s), `fwTimer` (60 s, wipe check).
- `pendingServerUpdate/pendingOxideUpdate`, `tempPending*`, `cachedRemoteBuild`, `cachedRemoteOxide`, `LocalSteamBuildID`, `localOxideVersion`, `localProtocol`, `localBuildFromServerVersion`, `LocalServerVersionString`, `cachedPluginVersion`, `trynumber`.
- `isFetchingRemote`, `countdownActive` (volatile).
- `tmuxAvailable`, `gnomeAvailable` — set once at Init from `DetectGnomeAndTmux` (L888-890).
- `DoRestartTimeCheck`, `targetHour`, `targetMinute` — daily restart guard.
- `scheme`, `lastNetworkFail`, `lastOxideStatusCode`, `lastOxideCompat/Proto/CompatNote`.
- `updaterValidAtInit` (L147, default true) — result of 4.2, computed once at Init, never refreshed.
- Wipe state (L149-153): `int? minutesBeforeForceWipe`, `Timer fwTimer`, `bool nextUpdateIsForce`, `int? minutesBeforeCustomWipe`, `string pluginDatafilesToRemoveString`.

### 1.3 Init-time handling that feeds the covered code (context, L738-907)

Order at `Init`: register permission `feedme.run`; register commands `feedme.testrun`, `feedme.status`, `feedme.version`, `feedme.set`; read config; `ValidateUpdaterExecutableAtInit` (4.2); `ProcessSteamTempAndDataFile`; `ProcessTryNumber`; `ProcessUpdaterMarkerIfPresent`; `ProcessUpdaterLockIfPresent`; `AutoUpdateTask` (2.4); then wipe-value sanitising:

- `pluginDatafilesToRemoveString = BuildFileListSingleArg(ParseFileListStrictJsonArray(NextWipeDeletePluginDatafiles))` (L793). If config value non-empty but result empty: `Puts("List of plugin datafiles to delete at wipe in invalid. Plugin datafile removal disabled")`.
- `NextWipeMapsize`: must `int.TryParse` and be 1000..6000, else set to "" in memory with messages "Map size specified for the next wipe is out of range (accepted range 1000-6000). In case of wipe map size won't be changed." / "Map size specified for the next is invalid. In case of wipe map size won't be changed".
- `NextWipeRandomSeed` true: a random uint is generated with `System.Random.NextBytes(4)` and stored in `configData.NextWipeSeed` in memory (L825-832) — regenerated at every plugin load, not written to disk by this code path.
- else `NextWipeSeed` must pass `TryParseSeedDecimal`, otherwise set "" with "Seed value specified for the next is invalid. In case of wipe the seed won't be changed".
- `DetectGnomeAndTmux`.
- Daily restart guard: only if `DailyRestartTime` non-empty AND `MinutesBeforeRestart > 0` AND `ValidateRestartTime()`. Then `timer.Once(60 - DateTime.Now.Second, ...)` runs `DailyRestartCheckerMethod()` once and starts `DailyRestartCheckRepeater = timer.Every(60f, ...)` (aligning ticks to the top of the wall-clock minute).
- `fwTimer = timer.Every(60f, WipeTimeCheck)` (L906) — first tick 60 s after Init. NOT reached if Init returned early at L874 (exception while reading local values).

These sanitised values are in-memory only, but any later `Config.WriteObject(configData)` (from `feedme.set` L2653 or `ResetNextWipeValues` L1201) persists them.

---

## 2. Utilities region (L2676-3508)

### 2.1 `GetGuiConfigSnapshot()` (L2678-2741)

Returns `Dictionary<string, object>` (case-insensitive keys) with one entry for each of the 41 `ConfigData` properties, key = property name, value = current in-memory value (typed: string/bool/int). No redaction (includes `DiscordWebhookUrl`). Private, never called inside this file. It is evidently an inter-plugin API for a companion GUI plugin `FeedMeUpdatesGUI.cs` (not in the repo; the app's self-update installs a file of that name, App L1112-1116) reached through Oxide `Call("GetGuiConfigSnapshot")`. Writing goes through the `feedme.set` command. Treat method name + key set as an external contract.

### 2.2 `TryParseBoolFlexible(string value, out bool result)` (L2743-2776)

1. `bool.TryParse` (accepts true/false, case-insensitive, surrounding whitespace).
2. null/whitespace -> false (failure).
3. Trim + lower-invariant; true set: `1, yes, y, on, enable, enabled`; false set: `0, no, n, off, disable, disabled`.
4. Otherwise failure.
Used only by `feedme.set` for bool keys.

### 2.3 `TrySetExecutableUnix(string path)` (L2780-2823)

- Returns false if path blank, OS is not Linux, or file missing.
- Starts `/bin/sh` with arguments `-c '<chmod +x "PATH">'` where `"` in PATH is replaced by `\"` and the whole command is wrapped by `QuoteForShell` (single quotes). No redirection, `CreateNoWindow`. `WaitForExit(2000)`; no kill on timeout, exit code ignored.
- Then starts `/bin/sh -c '<test -x "PATH" && echo OK || echo FAIL>'`, stdout redirected, `ReadToEnd()` (unbounded), `WaitForExit(1000)`. Returns `stdout.Trim() == "OK"`.
- Exception: `Puts("TrySetExecutableUnix error: " + ex.Message)`, false.

### 2.4 Self-update promotion: `AutoUpdateTask()` (L2826-2877)

Purpose: second stage of FMU self-update. The app (first stage, App L1006-1157) downloads the latest GitHub release, replaces `oxide/plugins/FeedMeUpdates.cs` (and `FeedMeUpdatesGUI.cs`) directly, and stages the new app binary as a `.new` file plus a checksum file, because a running executable cannot replace itself. The plugin promotes the binary on the next boot.

Paths (string concatenation, not `Path.Combine`):
- Windows: `<ServerDirectory>\FeedMeUpdates.exe.new` and `<ServerDirectory>\FMU_CHECKSUMS.txt`
- other: `<ServerDirectory>/FeedMeUpdates.new` and `<ServerDirectory>/FMU_CHECKSUMS.txt`

Flow: called synchronously from Init (L791), but the work runs in `Task.Run` (thread pool):
1. If `Directory.Exists(ServerDirectory) && File.Exists(newExePath)`; otherwise nothing happens, silently.
2. `timer.Once(0f)`: `Puts("App update process started.")`, `SystemUpdating = true` (i.e. the flag is set on a later main-thread tick, not immediately).
3. `AutoUpdateProcess(newExePath, newExeShaPath, out _error)` on the worker thread.
4. `timer.Once(0f)`: on success `Puts("FeedMeUpdates app updated and temporary files removed.")`; on failure `Puts(_error)`. In both cases `SystemUpdating = false`.
5. Outer catch (L2869-2875): `Puts("Error in autoupdate process: " + ex.Message)` via timer, `enablePlugin = false`, `SystemUpdating = false`, `Puts("Plugin has been automatically disabled. Please consider reinstalling the executable app.")`. Because `AutoUpdateProcess` catches everything itself, this is reachable only if `Directory.Exists`/`File.Exists`/`timer.Once` throw.

### 2.5 `AutoUpdateProcess(newExePath, newExeShaPath, out _error)` (L2880-3012)

Returns true only when the binary was promoted. Steps and failure handling:

| Step | Failure | Action |
|---|---|---|
| `.new` exists | missing | `_error = "New executable not found at: {path}"`, false |
| checksum file exists | missing | delete `.new`; `_error = "{newExePath} found but no sha file found. New file deleted and app update aborted."` |
| `File.ReadAllBytes` + `Encoding.ASCII.GetString` | exception | delete both; `"Error reading checksum file: {msg}"` |
| cleanup: `Trim()`, remove all `" "`, `"-"`, `":"` | — | `Puts("[FMU DEBUG] checksum file bytes=… textLen=… cleanedLen=…")` |
| `TryParseHexOrBase64(cleaned)` must give exactly 32 bytes | fail | computes SHA-256 of `.new` only to log `"[FMU DEBUG] computed(from .new): {HEX}"`; delete both; `"{newExePath} found but no sha data found inside {shaPath}. New and sha files deleted and app update aborted."` |
| SHA-256 of `.new` (`FileShare.Read`) | exception | delete both; `"Error computing SHA256 of .new file: {msg}"` |
| log | — | `"[FMU DEBUG] expected (from FMU_CHECKSUMS.txt): {HEX}"`, `"[FMU DEBUG] computed (from .new):             {HEX}"` (upper-case hex, no separators) |
| compare, constant time (length check then XOR-accumulate) | mismatch | `Puts("[FMU ERROR] SHA mismatch: expected {e} but computed {c}. Deleting .new and checksum.")`; delete both; `"Sha not matching. Aborting app update."` |
| promote: `File.Delete(UpdaterExecutablePath)` (errors swallowed) then `File.Move(newExePath, UpdaterExecutablePath)` | exception | delete `.new` and checksum if they exist; `"Error promoting new executable: {msg}"` |
| Linux only: `TrySetExecutableUnix(UpdaterExecutablePath)` | false | throws `"WARNING: chmod +x failed on Linux target."` -> same promote-failure handler (binary is already moved) |
| delete checksum file | ignored | return true |
| any other exception | | delete both; `"An error occurred while comparing sha256. {msg} {newExePath} and {shaPath} files deleted and app update aborted."` |

Checksum file contract: the ENTIRE content of `FMU_CHECKSUMS.txt` must be one SHA-256 value: 64 hex chars (any case; spaces, `-`, `:` tolerated) or base64 of 32 bytes. Leading/trailing whitespace is trimmed; anything else (a file name as in `sha256sum` output, several lines, a UTF-8 BOM, interior newlines) makes the parse fail and both files are deleted.

Target of promotion is the raw `configData.UpdaterExecutablePath` (not the resolved path from 4.2).

### 2.6 `TryParseHexOrBase64(string input, out byte[] result)` (L3015-3091)

- Blank -> false.
- Cleans again (Trim, remove space, `-`, `:`).
- If even length and all chars hex: decode; success only if exactly 32 bytes.
- Else/afterwards: treat as base64: replace `-`->`+`, `_`->`/`, pad to multiple of 4 (`%4==2` -> `==`, `==3` -> `=`), `Convert.FromBase64String`; success only if 32 bytes.
- Otherwise false, result null.

### 2.7 `FileSha256Matches(string filePath, byte[] expectedHash)` (L3094-3110)

Public static; SHA-256 of file compared with `CryptographicOperations.FixedTimeEquals`. Not called anywhere (dead code; `AutoUpdateProcess` has its own inline comparison).

### 2.8 `TryParseSeedDecimal(string raw)` (L3113-3133)

True iff, after Trim: matches `^[0-9]+$`, length <= 10, parses as ulong, value <= 4294967295. Used at Init for `NextWipeSeed`.

### 2.9 `ParseFileListStrictJsonArray(string input)` (L3136-3229)

Hand-written parser for a JSON array of strings (Newtonsoft is not used). Returns the list, or an EMPTY list for anything invalid (no error reporting).
- Blank -> empty. Leading whitespace skipped; must start with `[`.
- Elements must be double-quoted strings. Escapes: `\" \\ \/ \b \f \n \r \t \uXXXX` (4 hex digits, appended as a UTF-16 unit). Unknown escape -> invalid.
- Separator `,`; terminator `]`; only whitespace may follow.
- `[]` -> empty list. A trailing comma (`["a",]`) is accepted. Non-string elements, unterminated strings, trailing garbage -> empty list.
- No validation of the strings as file names (no check for path separators or `..`).

### 2.10 Daily restart guard

`ValidateRestartTime()` (L3232-3241): if `DailyRestartTime` is blank or `TryParseHourMinute` fails: `Puts("DailyRestartTime can't be parsed to a valid HH:MM format. Please fix it.")`, `DoRestartTimeCheck = false`, false. On success sets `targetHour`, `targetMinute`.

`TryParseHourMinute` (L3266-3289): `TimeSpan.TryParseExact` with `hh\:mm` then `h\:mm` (invariant), requiring 0 <= TotalHours < 24; fallback `DateTime.TryParseExact` with `HH:mm`, `H:mm`. "24:00" and seconds are rejected.

`DailyRestartCheckerMethod()` (L3244-3263), every 60 s:
- `now = DateTime.Now` (server local); `target = today at targetHour:targetMinute:00`; if `target <= now` add one day.
- `remainingRounded = (int)Math.Round((target - now).TotalMinutes)`.
- If `remainingRounded == MinutesBeforeRestart` (exact equality): destroy `checkTimer` and `DailyRestartCheckRepeater`, `DoRestartTimeCheck = false`, `Puts("Periodic check for updates has been disabled due to proximity to the daily restart.")`.
- Nothing re-enables the checks afterwards; the design assumes an external scheduler restarts the server at `DailyRestartTime`, which reloads the plugin. It does not cancel an active update countdown, nor `fwTimer`.

### 2.11 tmux / gnome-terminal detection

`TmuxSessionExists(string sessionName, int timeoutMs = 2000)` (L3292-3325), public static: blank -> false. Runs `tmux has-session -t {sessionName}` (name unquoted, no redirection); on timeout kills the process and returns false; returns `ExitCode == 0`; any exception -> false. Only caller passes the literal `feedmeupdate`.

`DetectGnomeAndTmux()` (L3328-3362): non-Linux -> `{false,false}`. For each name runs `sh -c "command -v NAME >/dev/null 2>&1 && printf 1 || printf 0"`, reads stdout, `WaitForExit(500)`, true iff output is `1`. Returns `{ gnome, tmux }` where gnome = `gnome-terminal` OR `gnome-terminal-server` found, tmux = `tmux` found. Uses the PATH of the Rust server process.

How the results are used (context, `StartUpdaterExecutable` L2121-2292) — exact command lines, `{args}` being the string from 4.3:
- Windows, `ShowUpdaterConsole` true: `cmd.exe` with `/c start "FeedMeUpdates" "{exePath}" {args}`, `UseShellExecute = true`, working dir = directory of exe (fallback ServerDirectory).
- Windows, hidden: start `{exePath}` directly with `{args}`, `UseShellExecute=false`, `CreateNoWindow=true`, same working dir.
- Linux, `ShowUpdaterConsole` true and gnome available: `gnome-terminal -- bash -lc '<exec '{exePath}' {args}>'` (inner single quotes escaped as `'\''`).
- Linux, console wanted, no gnome, tmux available: if session `feedmeupdate` exists: `tmux send-keys -t feedmeupdate '<bash -lc "exec '{exePath}' {args}">' C-m`; else `tmux new-session -d -s feedmeupdate '<exec '{exePath}' {args}>'`.
- Linux, console wanted but neither available, or `ShowUpdaterConsole` false: `/bin/bash -c "nohup \"{exePath}\" {args} >/dev/null 2>&1 &"`, working dir = ServerDirectory.
- gnome/tmux variants set no working directory.

### 2.12 Permission and broadcast helpers

`HasPermissionOrConsole(IPlayer player)` (L3365-3373): true if player is null, `IsServer`, `IsAdmin`, or has permission `feedme.run`; otherwise sends "You do not have permission to run this command." and returns false.

`BroadcastToPlayers(string message)` (L3376-3384): `pl.Message(message)` for every `players.Connected`, then `Puts("Broadcast: {message}")`. Exceptions: `Puts("Broadcast error: " + msg)`. Plain text, no prefix, no localisation (Oxide `lang` API unused).

`BroadcastToAdmins(string message)` (L3387-3398): same, restricted to connected players with `IsServer || IsAdmin || feedme.run`; logs `Broadcast(admins): {message}`; error text `BroadcastToAdmins error: …`.

Player-facing strings (all call sites):
- `Notice: update detected. Server will restart to apply updates in {minutes} minute(s).` (L2075)
- `ATTENTION: server will restart for update in {remaining} minute(s).` (L2104)
- `ATTENTION: server will restart for monthly force wipe in {remaining} minute(s).` (L2106, when `nextUpdateIsForce`)
- `ATTENTION: server will restart for wipe in {n} minute(s).` (L4042, custom wipe)
- `ATTENTION: server is wiping.` (L4030)
Admin strings: `Updater launch aborted: invalid updater executable detected at init.`, `Updater executable not configured; cannot start updater.`, `Updater executable not found; check UpdaterExecutablePath.`, `Updater launch aborted: invalid arguments.`, `Error launching updater. Check permissions and executable path.`

### 2.13 `PerformSaveAndQuitGracefully()` and `RunSequence` (L3401-3436)

`RunSequence(params (float delay, Action action)[] steps)`: runs step i inside `timer.Once(delay_i, …)`, then chains step i+1 (delays are relative to the previous step). A throwing step logs `Sequence step error: …` and the chain continues.

`PerformSaveAndQuitGracefully()`: sequence of (0 s: empty action), (0.5 s: `Puts("FeedMeUpdates: quit...")`, `ConsoleSystem.Run(ConsoleSystem.Option.Server, "quit")`). Despite the name there is no explicit save; Rust's `quit` performs the save and shutdown. Outer exception: `PerformSaveAndQuitGracefully error: …`.

Every caller invokes it immediately AFTER `StartUpdaterExecutable(...)`, unconditionally (the launcher returns void): init flow L3518-3520, startup scan L3577-3580, countdown end L2095-2098, `feedme.testrun` L2375-2378, custom wipe L4033-4036. The updater is therefore already running for ~0.5 s plus shutdown time while the server is alive; the app copes by polling until the `RustDedicated` process (or the service) has stopped, every 2 s (App L968-989).

### 2.14 `ScoreCommitForRustProtocolFallback(string msg)` (L3439-3505)

Heuristic used by `GetOxideCompatibilityInfo` (L1988, context) when no protocol number `\d+\.\d+\.\d+` can be extracted from the commit message of the latest Oxide release: score >= 6 means "Oxide release is a protocol update" (returns compat=false with note `[UpdateLogics Cycle] Protocol update inferred from commit message (fallback, score={score})`, proto "unknown").

Message is lower-cased; regexes are case-insensitive. Score = sum of:
- Strong (also sets `strongHit`): `patch\s+for.*rust\s+update` 7; `\bprotocol\s+update\b` 8; `\brust\s+protocol\b` 8; `\bafter\s+rust\s+update\b` 7; `\bfollowing\s+rust\s+update\b` 7; `\brust\s+update\b` 6.
- Medium (always): `\bupdate\s+for\s+rust\b` 4; `\bfix\s+for\s+rust\b` 3; `\brust\s+patch\b` 3; `\bcompatib` 2; `\bserver\s+update\b` 2; `\bgame\s+update\b` 2.
- Weak (only when no strong hit): `\bpatch\b` 1; `\bupdate\b` 1; `\bfix\b` 1; `january|february|…|december` (no word boundaries) 1; `\bwipe\b` 2.
Blank -> 0. Without a strong hit the threshold 6 needs e.g. "update for rust" (4) + "update" (1) + month (1), or "patch"+"update"+"fix"+month+"wipe" (6).

---

## 3. Hooks region (L3510-3607)

Oxide hooks used by the whole plugin: `Init` (L738), `Unload` (L910), `LoadDefaultConfig` (L78), `OnServerInitialized` (L3513), `OnServerSave` (L3605, empty body, no effect).
`Unload` destroys `checkTimer`, `DailyRestartCheckRepeater`, `fwTimer`.

### 3.1 `OnServerInitialized()` (L3513-3602) — parameterless overload

A. Init flow — `LocalSteamBuildID` empty (first run, or datafile lost):
- `Puts("OnServerInitialized: LocalSteamBuildID is empty -> starting updater immediately (init flow).")`
- `StartUpdaterExecutable(updateServer:true, updateOxide:true, overrideUpdateId:"init", overrideWhat:"both", remoteBuildArg:"no", remoteOxideArg:"no")`
- `Puts("OnServerInitialized: scheduled save+quit after updater start (init).")`, `PerformSaveAndQuitGracefully()`, return.
- Purpose: the plugin cannot know which Steam build is installed, so on first boot it forces a full update; the app then writes `steambid.temp` in ServerDirectory, which the plugin consumes at next Init into the datafile key `LocalSteamBuildID` (context L983-1019).
- `enablePlugin`, `MaxAttempts`, `SystemUpdating` and `updaterValidAtInit` are not consulted before quitting.

B. Startup scan — `LocalSteamBuildID` set, `StartupScan` true, `SystemUpdating` false:
- `Puts("OnServerInitialized: StartupScan enabled -> checking for available updates now.")`
- Clears all four pending flags, then SYNCHRONOUSLY on the main thread runs `SchemeResult()` (if `UseScheme`) or `UpdateLogics()`; result `[server, oxide, error]`. On error flag: `Puts("An error occurred while completing update logic function.")`. Exceptions from these functions are NOT caught here.
- If any update: saves `RemoteSteamBuildID` (when server update and remote build known); `what` = both/server/oxide; `Puts("StartupScan: updates available (server={..}, oxide={..}) -> launching updater immediately (what={what}).")`; `StartUpdaterExecutable(updateServer, updateOxide, overrideUpdateId:null, overrideWhat:what, remoteBuildArg, remoteOxideArg)` (random 8-digit id, no countdown, no broadcast); `Puts("StartupScan: scheduling graceful save+quit after updater start.")`; quit; return.
- No update: `Puts("StartupScan: remote Oxide fetch failed; skipping update for safety.")` if `lastNetworkFail`, else `Puts("StartupScan: no updates available at startup.")`.

C. If the scan was skipped because `SystemUpdating`: `Puts("App update is not over. Skipping startup scan.")`.

D. Finally (only when no updater was launched): `SetWipeUnixTimestampOverrideFromConfig()` (5.1), and `StartPeriodicCheck()` if `checkTimer == null` (context L1596-1606: returns silently if `!enablePlugin`; `timer.Every(max(1,CheckIntervalMinutes)*60)` plus a first detection after 2 s).

---

## 4. Validation & argument building (L3609-3857)

### 4.1 `EncodeIfSpecialArg(string key, string rawValue)` (L3612-3631)

For keys `-nextwipeurl`, `-servername`, `-serverdescription` (case-insensitive) returns `"b64:" + Base64(UTF8(rawValue))` (standard alphabet with `+ / =`); null -> `"b64:"`; an empty string also yields `"b64:"`. Other keys: value unchanged. Purpose: free text (spaces, quotes, non-ASCII, shell metacharacters) must survive the command line and the whitelist in 4.4. The app decodes any value starting with `b64:` for these three arguments (App L274-286) and accepts plain values too.

### 4.2 `ValidateUpdaterExecutableAtInit(out string resolvedPath, out string reason)` (L3634-3697)

Sequence (first failure wins, `reason` text in quotes):
1. `UpdaterExecutablePath` blank -> "UpdaterExecutablePath is empty."
2. If not rooted and `ServerDirectory` set: use `Path.Combine(ServerDirectory, exePath)` if that file exists.
3. `Path.GetFullPath` (errors ignored).
4. Not existing -> "Updater executable not found: {path}"
5. Is a directory -> "Updater path is a directory: {path}"
6. Length 0 -> "Updater file has zero length: {path}"
7. Linux only: `target = TryResolveSymlinkUnix(path) ?? path`; `IsExecutableUnix(target)` false -> "Updater is not executable (+x missing): {target}"; if target differs from path: `Puts("Init: updater is a symlink -> resolved target: {target}")`; resolved = target.
8. Exception -> "Exception during updater validation: {msg}".

Caller (L764-782) logs "Updater executable validation failed at init: {reason}" / "Updater executable validated at init: {resolved}" / "Updater executable validation error at init: {msg}" and stores only the boolean `updaterValidAtInit`. `StartUpdaterExecutable` refuses to launch when it is false (L2125-2130) and then re-derives the path itself from config (relative-path fallback + `File.Exists` only).

### 4.3 `ValidateAndBuildArgsForStart(updateId, what, remoteBuildArgValue, remoteOxideArgValue, out argsForProcess, out failReason)` (L3700-3785)

Builds the single argument string for the updater. Arguments are emitted in exactly this order (the app REQUIRES `-update_id` to be the first token, App L156-171). Each argument is always present, even when empty.

| # | Argument | Source | Value / encoding |
|---|---|---|---|
| 1 | `-update_id` | caller | `init`, `testrun`, `wipe`, or random 8 decimal digits `Random.Next(0,100000000).ToString("D8")` (L2132). App dispatch: init / testrun / wipe / `^\d{8}$` = scheduled; anything else: app exits doing nothing |
| 2 | `-what` | caller | `both`, `server`, `oxide`, `none` (custom wipe) |
| 3 | `-update_plugins` | UpdatePlugins | `yes` / `no` |
| 4 | `-server_dir` | ServerDirectory | raw |
| 5 | `-steamcmd` | SteamCmdPath | raw |
| 6 | `-start_script` | ServerStartScript | raw |
| 7 | `-remote_build` | caller | Steam build id (digits), or `no`, or empty. Rules at L2184: `no` when update id is `init` or the server is not being updated; else the detected remote build or "" |
| 8 | `-remote_oxide` | caller | Oxide release tag without leading `v`, or `no`, or empty; same rules (L2185) |
| 9 | `-isService` | RustOnService | `1` / `0` |
| 10 | `-serviceType` | ServiceType | raw |
| 11 | `-serviceName` | ServiceName | raw |
| 12 | `-showserverconsole` | RunServerScriptHidden | INVERTED: hidden -> `0`, visible -> `1`. The app forces it off when isService (App L248) |
| 13 | `-servertmux` | ServerTmuxSession | raw |
| 14 | `-isforce` | `nextUpdateIsForce` (runtime) | `1` / `0`. App wipes only when the flow is "scheduled" (8-digit id) and isforce=1 (App L501) |
| 15 | `-nextwipelevel` | NextWipeLevel | raw |
| 16 | `-nextwipeurl` | NextWipeMapUrl | `b64:` + base64 |
| 17 | `-nextwipeseed` | NextWipeSeed (after Init sanitising / random generation) | raw decimal |
| 18 | `-nextwipemapsize` | NextWipeMapsize (after Init sanitising) | raw decimal |
| 19 | `-nextwipekeepbps` | NextWipeKeepBps | `1` / `0` |
| 20 | `-nextwiperesetrustplus` | NextWipeResetRustPlus | `1` / `0` |
| 21 | `-nextwipedelplayerdata` | NextWipeDeletePlayerData | `1` / `0` |
| 22 | `-nextwipedelpluginsdata` | `pluginDatafilesToRemoveString` (computed at Init, 5.5) | base64url WITHOUT padding and WITHOUT `b64:` prefix, of the UTF-8 JSON array; or empty |
| 23 | `-serveridentity` | ServerIdentity | raw |
| 24 | `-servername` | NextWipeServerName | `b64:` + base64 |
| 25 | `-serverdescription` | NextWipeServerDescription | `b64:` + base64 |

Null config strings become "". Wipe-related arguments are always sent, on every launch, regardless of whether a wipe will happen.

Length guard (L3735-3756): `counter` accumulates `key.Length + value.Length` (after encoding; quotes and separating spaces are not counted). The check `counter > 32500` is evaluated only while processing `-serverdescription` (the last key): then `Puts("WARINING: ServerDescription too long, skipping it to avoid command line length limit. Please change it manually after wipe!")`, the description value becomes "" (plain empty, not `b64:`), the counter is recomputed, and if still > 32500 the build fails with `failReason = "Params too long"`. 32500 is modelled on the 32767-character Windows `CreateProcess` limit.

Safety check (L3758-3765): every final value must pass `IsSafeArgValue`; else `failReason = "Unsafe value for {key}"`.

Rendering:
- Windows: `{key} "{value}"` joined by single spaces (no escaping; safe only because `"` is forbidden).
- other: `{key} {QuoteForShell(value)}` i.e. `-key 'value'`.
Exception -> `failReason = "Args build exception: {msg}"`.

On failure the caller logs `Argument validation failed: {reason}`, tells admins `Updater launch aborted: invalid arguments.` and returns without launching (L2190-2195).

App-side parsing facts the format relies on (App L912-965): keys are case-insensitive; `-key=value` in one token is also accepted; a value token is consumed only if it does not start with `-` (otherwise the flag gets ""); booleans accept `true/false/1/0`, anything else false; missing `server_dir` defaults to the app's working directory.

### 4.4 `IsSafeArgValue(string v)` (L3788-3794)

null -> true. False if it contains NUL, CR, LF, or any of `" ' ` ; | & $ < > ( ) { } [ ] * ? ~ %`. Finally must match `^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$`. So the effective whitelist is ASCII letters, digits, space, `_ . - / : @ + = , \`. (The explicit blacklist is redundant with the whitelist.) Non-ASCII letters, parentheses, `#`, `!`, `^`, tab are rejected.

### 4.5 `IsExecutableUnix(string path)` (L3797-3822)

`/bin/sh -c '<test -x "PATH">'` (same quoting as 2.3), `WaitForExit(1000)`; timeout -> kill, false; returns exit code 0; exceptions/null process -> false.

### 4.6 `TryResolveSymlinkUnix(string path)` (L3825-3849)

`/bin/sh -c '<readlink -f "PATH">'`, stdout read to end, `WaitForExit(1000)` (kill on timeout but output still used), returns trimmed output or null if empty/exception. `readlink -f` canonicalises any path, not only symlinks.

### 4.7 `QuoteForShell(string s)` (L3852-3855)

`'` + s with every `'` replaced by `'\''` + `'`. null -> `''`. An identical local function `EscapeShellArg` exists in `StartUpdaterExecutable` (L2226).

---

## 5. Wipe helpers (L3859-4165)

### 5.1 `SetWipeUnixTimestampOverrideFromConfig()` (L3862-3941)

Purpose: make Rust's built-in wipe timer (the convar the README calls `wipetimer.wipeUnixTimestampOverride`, shown to players as next-wipe information) show FMU's next wipe. Called exactly once per boot, at the end of `OnServerInitialized` when no update was launched.

Branch A — no custom wipe (`CustomWipeDay` or `CustomWipeTime` blank):
- `(_, nextWipeUtc, nextWipeLocal) = MinutesUntilMonthlyForceWipe()`.
- default date -> `Puts("Fallback wipe override: unable to estimate next force wipe date.")`, return.
- `unixTs = (long)(nextWipeUtc - DateTime.UnixEpoch).TotalSeconds`; `<= 0` -> `Puts("Fallback wipe override: timestamp is invalid ({unixTs}).")`, return.
- `Puts("Fallback wipe override: setting convar wipeUnixTimestampOverride={unixTs} (UTC={yyyy-MM-dd HH:mm:ss}, Local={yyyy-MM-dd HH:mm:ss}).")`
- `ConsoleSystem.Run(ConsoleSystem.Option.Server, "wipeUnixTimestampOverride {unixTs}")` (command sent WITHOUT the `wipetimer.` prefix). Nothing is written to server.cfg in this branch.

Branch B — custom wipe configured:
- Day: Trim, replace `-` by `/`, `DateTime.TryParseExact` with `d/M/yyyy`, `dd/MM/yyyy`, `d/MM/yyyy`, `dd/M/yyyy` (invariant, day first, 4-digit year). Failure: `Puts("SetWipeUnixTimestampOverrideStrict: CustomWipeDay '{raw}' does not match required formats.")`, return.
- Time: Trim, `TryParseExact` with `H:mm`, `HH:mm`. Failure: `Puts("SetWipeUnixTimestampOverrideStrict: CustomWipeTime '{raw}' does not match required formats.")`, return.
- Build `DateTime(year, month, day, hour, minute, 0, Unspecified)`, convert with `TimeZoneInfo.ConvertTimeToUtc(x, TimeZoneInfo.Local)`. Exception (e.g. non-existent local time in a DST gap): `Puts("SetWipeUnixTimestampOverrideStrict: UTC conversion error: {msg}. Aborted.")`, return.
- `unixTs <= 0` -> `Puts("SetWipeUnixTimestampOverrideStrict: computed timestamp '{unixTs}' is invalid; aborted.")`.
- `Puts("SetWipeUnixTimestampOverrideStrict: setting wipeUnixTimestampOverride={unixTs} (UTC={…}).")`, then three console commands: `wipeUnixTimestampOverride {unixTs}`, `server.writecfg`, `server.readcfg`.
- No check that the timestamp is in the future or earlier than the next force wipe.
- The app's WipeCycle later removes the `wipeUnixTimestampOverride` line from server.cfg (App L3445-3456) — counterpart of the `server.writecfg` here.
- Outer catch: `Puts("SetWipeUnixTimestampOverrideStrict: unexpected error: " + msg)`.

### 5.2 `MinutesUntilMonthlyForceWipe(TimeZoneInfo tz = null)` (L3944-3989) — force wipe time algorithm

Returns `(int minutesRemaining, DateTime nextWipeUtc, DateTime nextWipeInServerTz)`.
1. `utcNow = DateTime.UtcNow`.
2. First Thursday of (year, month): `firstOfMonth` = day 1 at 00:00 UTC; `daysToThursday = ((int)Thursday(4) - (int)firstOfMonth.DayOfWeek + 7) % 7`; day = `1 + daysToThursday` (1..7).
3. Candidate = that day at 19:00:00 UTC, for the current UTC year/month.
4. If `candidate <= utcNow`: month+1 (13 -> month 1 of year+1) and recompute.
5. `minutesRemaining = (int)Math.Ceiling(Math.Max(0.0, (candidate - utcNow).TotalMinutes))`.
6. `nextWipeInServerTz = TimeZoneInfo.ConvertTimeFromUtc(candidate, tz ?? Local)` (fallback Local on exception) — used for logging only.

Everything is computed in UTC; the hour is the constant 19 all year (no DST adjustment of any kind); server time zone does not influence the trigger.

### 5.3 `WipeTimeCheck()` (L3992-4057) — state machine, ticked by `fwTimer` every 60 s

State: `minutesBeforeForceWipe` (null until first tick), `minutesBeforeCustomWipe` (null while no valid custom wipe is configured), `nextUpdateIsForce`, `fwTimer`.

Each tick:
1. Force counter: null -> computed from 5.2; otherwise decremented by 1 (no re-sync with the clock).
2. Custom counter: null -> `MinutesUntilCustomWipe(CustomWipeDay, CustomWipeTime)` (re-evaluated every tick while null, so a custom wipe added at runtime via `feedme.set` is picked up); otherwise decremented by 1 (later config edits are ignored).
3. Force branch (always entered): if counter `== 0`, or (after replacing `BeforeForceWipeRange <= 0` by 15) counter `<= BeforeForceWipeRange`: set `nextUpdateIsForce = true`, destroy `fwTimer`, return. Nothing else happens: no broadcast, no update check, no launch.
4. Custom branch (only if custom counter non-null and step 3 did not return):
   - counter `<= 0`: broadcast "ATTENTION: server is wiping."; destroy `fwTimer`; `StartUpdaterExecutable(updateServer:false, updateOxide:false, overrideUpdateId:"wipe", overrideWhat:"none", remoteBuildArg:"no", remoteOxideArg:"no")`; `Puts("Custom wipe countdown end: scheduling graceful save+quit...")`; `PerformSaveAndQuitGracefully()`.
   - else if counter `<= CountdownMinutes`: broadcast "ATTENTION: server will restart for wipe in {n} minute(s)." (every tick), and destroy `checkTimer` and `DailyRestartCheckRepeater` (pauses update detection so it cannot race the wipe).

Semantics of the force wipe flow as a whole:
- Entering the window (default 15 min before first Thursday 19:00 UTC) only arms `nextUpdateIsForce`. The wipe itself happens when the normal periodic detection (or startup scan) next launches an update: the countdown text becomes "…for monthly force wipe…", the Discord start message gets " (ForceWipe)" (L642-643), and `-isforce 1` makes the app run its WipeCycle after a successful scheduled update. The design assumption is that Facepunch publishes the mandatory server build around that time and FMU detects it.
- The flag is never reset in the running session and is not persisted; after the restart, the next first-Thursday candidate is next month, so the flag stays false.
- Once armed, the wipe timer is destroyed: custom wipes are not evaluated any more in that session.
- After a marker with `result=success` and `wiped=yes`, the plugin resets `CustomWipeDay`, `CustomWipeTime`, `NextWipeServerName`, `NextWipeServerDescription`, `NextWipeMapUrl`, `NextWipeLevel`, `NextWipeSeed`, `NextWipeRandomSeed=false`, `NextWipeMapsize`, `NextWipeKeepBps=true`, `NextWipeDeletePlayerData=false`, `NextWipeDeletePluginDatafiles` and saves the config (context L1184-1203; `NextWipeResetRustPlus` and `ServerIdentity` are not reset).

Custom wipe flow: update id `wipe`, `what=none`; the app performs only the WipeCycle and restart, no update (App L325-380). Discord start text: `[FeedMeUpdates] Starting custom wipe.`

### 5.4 `MinutesUntilCustomWipe(string dateStr, string timeStr, TimeZoneInfo tz = null)` (L4060-4093)

- Either blank -> null.
- Date: Trim, `-` -> `/`; exact formats `d/M/yyyy`, `dd/MM/yyyy`, `d/MM/yyyy`, `dd/M/yyyy` (invariant); fallback `DateTime.TryParse` with culture `it-IT` (accepts further Italian day-first forms, 2-digit years, month names, also ISO `yyyy/MM/dd`); else null.
- Time: Trim; exact `H:mm`, `HH:mm`; fallback `DateTime.TryParse` invariant with `NoCurrentDateDefault` (accepts e.g. `11:30 PM`, `23:30:15`; only hour and minute are used); else null.
- Target = date + hour:minute:00 as Unspecified, converted with `TimeZoneInfo.ConvertTimeToUtc(target, tz ?? Local)` (retry with Local); any exception -> null.
- Returns `(int)Math.Ceiling(Math.Max(0.0, (targetUtc - UtcNow).TotalMinutes))`; a time in the past yields 0.

Accepted input, canonical: `CustomWipeDay = "05/12/2025"` or `5-12-2025` (day/month/year), `CustomWipeTime = "23:30"` or `7:05`, both in the server's local time zone. Ambiguous local times (DST fall-back) resolve to standard time by .NET rules; non-existent local times (spring-forward gap) throw and yield null.

### 5.5 `BuildFileListSingleArg(List<string> items)` (L4096-4134) and `JsonEscape` (L4137-4163)

- null/empty -> "". Items trimmed; empty ones dropped; none left -> "".
- Builds a compact JSON array `["a","b"]` with `JsonEscape` (escapes `\ " \b \f \n \r \t`, other chars < 0x20 as `\u00xx` lower-case hex; everything else verbatim, including non-ASCII).
- UTF-8 -> base64 -> base64url (strip `=`, `+`->`-`, `/`->`_`). If that fails `IsSafeArgValue` it falls back to standard base64, then to "" (both fallbacks unreachable in practice).
- App side: `DecodeFileListArgToList` (App L3649-3698) converts base64url back (re-pads), falls back to treating the arg as raw JSON, requires a JSON array, keeps non-empty string elements.

---

## 6. External contracts (consolidated)

Files (all relative to `ServerDirectory` unless noted):
- `oxide/config/FeedMeUpdates.json` — keys in 1.1.
- `oxide/data/FeedMeUpdatesData.json` — `LocalSteamBuildID`, `RemoteSteamBuildID`, `TryNumber` (all strings).
- `FeedMeUpdates.exe.new` (Windows) / `FeedMeUpdates.new` (other) — staged app binary.
- `FMU_CHECKSUMS.txt` — single SHA-256 (hex or base64), nothing else.
- `updateresult.json` (config `UpdaterMarkerFileName`), `updating.lock` (config `UpdaterLockFileName`), `markers/` (config `MarkersSubfolder`), `steambid.temp` — handled in other parts; names are hard-coded in the app.
- `oxide/plugins/FeedMeUpdates.cs`, `oxide/plugins/FeedMeUpdatesGUI.cs` — replaced by the app.

Processes started by code in this range: `/bin/sh -c 'chmod +x "P"'`, `/bin/sh -c 'test -x "P" && echo OK || echo FAIL'`, `/bin/sh -c 'test -x "P"'`, `/bin/sh -c 'readlink -f "P"'`, `sh -c "command -v NAME >/dev/null 2>&1 && printf 1 || printf 0"` for `gnome-terminal`, `gnome-terminal-server`, `tmux`; `tmux has-session -t feedmeupdate`. Updater launch command lines: 2.11. tmux session name `feedmeupdate` and Windows console title `FeedMeUpdates` are fixed.

Server console commands issued: `quit`, `wipeUnixTimestampOverride <unix seconds>`, `server.writecfg`, `server.readcfg`.

Network: none in this range (the checksum file and binary are downloaded by the app).

Permission: `feedme.run`. Commands: `feedme.status`, `feedme.version`, `feedme.testrun`, `feedme.set <option> <value>`.

Inter-plugin API: `GetGuiConfigSnapshot()` -> dictionary of the 41 config keys.

Updater CLI: 4.3 (25 arguments, fixed order, `-update_id` first; `b64:` prefix convention; base64url list for `-nextwipedelpluginsdata`; update id values `init` / `testrun` / `wipe` / 8 digits; `-what` values `both` / `server` / `oxide` / `none`; `no` sentinel for remote build/oxide).

Timers/constants: wipe check 60 s; daily-restart check 60 s aligned to minute; quit delay 0.5 s; chmod wait 2000 ms; test/readlink wait 1000 ms; `command -v` wait 500 ms; tmux check 2000 ms; CLI length budget 32500; map size 1000-6000; seed 0-4294967295; force wipe = first Thursday 19:00 UTC; default force window 15 min; commit-score threshold 6.

---

## 7. Implicit knowledge and workarounds

1. Two-stage self-update. A running executable cannot overwrite itself (Windows locks it), so the app only stages `*.new` + checksum and the plugin promotes it at the next boot, when the app is (normally) no longer running. The plugin file, in contrast, is overwritten directly by the app while the server is down.
2. `SystemUpdating` exists so no updater launch happens while the binary is being swapped (startup scan and countdown are skipped).
3. chmod after promotion: the binary comes out of a zip without the execute bit; hence `chmod +x` and a `test -x` verification, and the same `test -x` check at Init (a frequent install mistake on Linux). `readlink -f` supports installations where the updater path is a symlink.
4. Executability and symlink checks shell out to `/bin/sh` because the Oxide/Mono sandbox offers no POSIX permission API.
5. The updater must be launched BEFORE `quit` because the plugin dies with the server; the app tolerates this by waiting for `RustDedicated`/the service to stop. On Linux `nohup … &` detaches it from the dying parent. The README's service advice exists for the same reason: systemd `KillMode=process` / `SendSIGKILL=no` and the NSSM stop settings prevent the service manager from killing the updater, which is a child of the server process.
6. `-update_id` first and the fixed 8-digit format are how the app recognises a legitimate invocation; double-clicking the binary does nothing.
7. Base64 for server name, description and map URL: these contain spaces, quotes, `&`, `?`, `%`, non-ASCII. Everything else is restricted to a conservative whitelist instead of being escaped — one rule that is safe for cmd.exe, CreateProcess and POSIX shells alike.
8. The 32500 budget and dropping only the description: Rust server descriptions can be very long (multi-line with `\n`), and Windows caps a command line at 32767 chars. The wipe is considered more important than the description.
9. `-showserverconsole` is the inverse of `RunServerScriptHidden`; the app also forces it off for services.
10. tmux fallback when gnome-terminal is absent, and plain `nohup` when neither exists: "show console" must also work on headless Linux hosts. If session `feedmeupdate` is still alive from a previous run, the command is typed into it with `send-keys` instead of creating a duplicate session (which would fail).
11. Hand-written JSON array parser and JSON writer: avoids a dependency on Newtonsoft types inside the plugin, and the list is stored as a string so that it can be edited through `feedme.set` and passed as one CLI token.
12. Daily restart guard: an external scheduler restarts the server daily; an update countdown overlapping that restart would kill the server mid-update. Ticks are aligned to the minute so the equality test can hit.
13. Custom-wipe countdown destroys the update-check and daily-restart timers so a detected update cannot start a competing updater.
14. `wipeUnixTimestampOverride` is set on every boot so the in-game wipe timer shows the next wipe; for custom wipes it is also persisted with `server.writecfg`, and the app removes the line from server.cfg during the wipe so the stale value does not survive.
15. Init flow: the installed Steam build id is not discoverable from inside the game, so first boot forces a full update to establish `LocalSteamBuildID` (via `steambid.temp`).
16. Commit-message scoring: Oxide release commits do not always contain the protocol number; wording like "Rust update"/"protocol update" is used to infer a protocol change. Month names and "wipe" are weak hints for the monthly forced update.
17. Italian (`it-IT`) fallback date parsing and day-first formats reflect the author's locale; documentation examples are day/month/year.
18. Force wipe is deliberately not executed at a fixed time: it piggybacks on detection of the actual Facepunch build, because wiping before the new build is installed would be pointless.
19. The sample start scripts (`Scripts/startserver.sh|bat`) loop forever and only stop looping when `updating.lock` exists 5 s after the server exits — the lock file doubles as a signal to the wrapper script.

---

## 8. Defects and risks

Severity: high = server left down, unintended wipe, or code execution; medium = wrong behaviour in plausible conditions; low = cosmetic/rare.

### High

H1. Server quits even when the updater was not launched (L3518-3520, L3577-3580, L2095-2098, L2375-2378, L4033-4036; launcher L2121-2292 returns void). Any abort — `updaterValidAtInit` false, path missing, "Unsafe value for …", "Params too long", exception from `Process.Start` — is followed by `quit`. Scenario: `ServerDirectory = C:\Program Files (x86)\Rust` (parentheses rejected by 4.4): every detected update shuts the server down and nothing restarts it. The Discord "Starting update" message has already been sent (L2164) in most of these cases.

H2. Boot loop / shutdown on a fresh install (L3515-3521 with L985-989, L2125-2130). With the default config (all paths empty) `LocalSteamBuildID` is empty, so `OnServerInitialized` takes the init flow, the launch is refused, and the server quits. With a supervising loop script or service it restarts and quits again indefinitely. `MaxAttempts`/`enablePlugin` are not consulted in the init or startup-scan paths.

H3. Past or stale custom wipe date wipes the server 60 s after every plugin load (L4086-4087 returns 0 for past times; L4028-4036). Scenarios: an admin types a past date; or a custom wipe whose marker is not `success` + `wiped=yes` (failure, restore) — the date is not cleared, so after restart the wipe fires again one minute after Init, in a loop, without any countdown. It can fire before `OnServerInitialized` if the server takes more than 60 s to load (timer behaviour during load: unverified).

H4. A scheduled custom wipe cannot be cancelled or changed at runtime (L3999-4002). Once `minutesBeforeCustomWipe` is non-null it only counts down; clearing or editing `CustomWipeDay/Time` with `feedme.set` has no effect until the plugin is reloaded.

H5. `nextUpdateIsForce` is sticky and purely time-window based (L4006-4024, L3721).
 - Any update launched inside the window is treated as the force wipe, including an Oxide-only update or a startup-scan update at e.g. 18:50 UTC -> full wipe before the real forced update, then possibly a second wipe when the real build arrives.
 - A countdown started before the window and ending inside it gets `-isforce 1` (args are built at launch time).
 - If no update arrives and the server keeps running, the next update days later is still treated as a force wipe.
 - If the server restarts after 19:00 UTC but before the Facepunch build is detected (crash, daily restart), the flag is lost and the forced build is installed without FMU's wipe actions.

H6. Promotion can destroy the only updater binary (L2980-3003). The live executable is deleted first, then `File.Move`. If the move fails (target directory not writable, different/unavailable volume, relative `UpdaterExecutablePath` resolved against another working directory) the handler deletes the `.new` file too: no updater remains. `updaterValidAtInit` was computed before promotion and stays true; the next update then hits "Updater executable not found" and H1. Same end state if chmod fails on Linux (binary present but not executable; `nohup` launch fails silently, server quits).

H7. App and plugin versions can diverge permanently (L2982-2983 with App L1020-1039, L1105-1129). The app decides whether to self-update by comparing the release tag with the version in the plugin file, and it overwrites the plugin file immediately. If the plugin-side promotion then fails for any reason (checksum parse, mismatch, locked target because the old app process is still running on Windows — delete error is swallowed at L2982 and the move fails because the destination exists), the `.new` file is deleted and the version comparison no longer triggers a download: new plugin + old app forever. The README claim that the plugin "disables itself" on promotion failure is not what the code does; it only logs `_error`.

H8. Privilege escalation through `feedme.set` (L2382-2671 context; L2135, L2172-2182). Any in-game admin or holder of `feedme.run` can set `UpdaterExecutablePath`, `SteamCmdPath`, `ServerStartScript`, `ServiceName` and then run `feedme.testrun`; the launcher uses the live config value (only `File.Exists` is checked), so an arbitrary existing program is executed as the server's OS user, and `SteamCmdPath` is executed by the periodic check. The executable path itself is never passed through `IsSafeArgValue` and is embedded in `bash -c "nohup \"{exePath}\" …"` / `cmd /c start`, so shell metacharacters in it are interpreted.

H9. Windows: a value ending in a backslash breaks the whole command line (L3769, whitelist L3793 allows `\`). `-server_dir "C:\rust\"` — the `\"` is parsed as an escaped quote by the Windows argument rules, so the remaining arguments are swallowed into one value. A trailing backslash on `ServerDirectory`, `SteamCmdPath`, `ServerStartScript` is a very common way to write Windows paths. Result: the app receives garbage paths; combined with H1 the server is already down.

H10. Exceptions in the startup scan abort `OnServerInitialized` (L3539-3542; `UpdateLogics`/`SchemeResult` rethrow, e.g. L1432-1436, L1588-1592, L1283, steamcmd failure L1712-1716). Oxide swallows the hook exception, and the rest of the hook never runs: no `SetWipeUnixTimestampOverrideFromConfig`, no `StartPeriodicCheck`. Update detection is silently off for the whole session.

### Medium

M1. Startup scan blocks the main thread and cannot work with the WebRequest backend (L3539-3542 with L196-224, L258-273). `UpdateLogics` runs steamcmd (`ReadToEnd`, up to 60 s wait) and HTTP synchronously on the server thread. When the selected HTTP backend is Oxide `webrequest`, the completion is delivered on the main thread, which is blocked in `task.Wait`, so the call always times out after `HttpTimeoutMs` and the scan reports "remote Oxide fetch failed; skipping update for safety."

M2. Force wipe hour is fixed at 19:00 UTC all year (L3960). If the real forced update follows UK local time (i.e. 18:00 UTC during British Summer Time) the window opens ~1 h late in summer and the forced build is installed without `-isforce`. External fact unverified — must be checked against Facepunch's schedule and Rust's own wipe-timer defaults. The same error would be written into `wipeUnixTimestampOverride`.

M3. Minute counters drift (L3997, L4002). Values are computed once and then decremented per timer tick; server hitches, timer drift or clock adjustments are never corrected. Over a long uptime the force window and the custom wipe time shift. The custom wipe can be late by the accumulated drift.

M4. `SystemUpdating` race (L2846-2850, L3524). The flag is set through `timer.Once(0f)` from a worker thread, after the hash work has been queued. When the plugin is (re)loaded on a running server, `OnServerInitialized` follows Init immediately and can start a startup-scan update while the binary is being replaced. Also `enablePlugin`/`SystemUpdating` are written and `Puts` is called from the worker thread (L2872-2874, all `Puts` inside `AutoUpdateProcess`), and `timer.Once` is called off the main thread; thread-safety of these Oxide APIs is unverified.

M5. Custom wipe ignores every guard (L4026-4037): not `SystemUpdating`, not `countdownActive`, not `enablePlugin`, not `updaterValidAtInit`. If an update countdown (started more than `CountdownMinutes` before the wipe time, or already running) ends around the same time, two updater processes are started.

M6. Arming the force flag stops custom-wipe processing (L4008-4011, L4020-4022), and a custom wipe scheduled after the force wipe is silently discarded by the post-wipe config reset. Conversely, the custom check is skipped on the tick that returns early.

M7. Daily restart guard uses exact equality (L3254). If the plugin loads when less than `MinutesBeforeRestart` remain (boot 30 min before the restart with a 60 min guard), or a tick is delayed across a minute boundary, the guard never fires that day. It also does not stop an already running countdown or the wipe timer.

M8. `ShowUpdaterConsole` on Linux picks gnome-terminal whenever the binary exists (L2230-2241, L3359), without checking for a usable display; there is no fallback if it fails to open. On a headless host with gnome-terminal installed the updater never runs and the server has already quit. Also both gnome and tmux launches inherit the server's working directory instead of ServerDirectory.

M9. Windows console mode goes through `cmd.exe` (L2204-2207), whose command-line limit is 8191 characters, far below the 32500 budget (L3744). Long name/description/URL values break the launch only in this mode.

M10. Length guard logic (L3738-3756): the limit is tested only on the last key; quotes/spaces are not counted (about 100 chars); an over-long server name or map URL causes the (possibly short) description to be dropped first and then fails anyway. The guard relies on `Dictionary` enumeration order, as does the "`-update_id` first" contract (L3706-3733, L3769/3775) — order is not guaranteed by the type.

M11. Values set through `feedme.set` bypass Init-time sanitising. `NextWipeSeed`, `NextWipeMapsize` are validated only at Init; `pluginDatafilesToRemoveString` is computed only at Init (L793), so a list changed at runtime is not sent (or a removed list is still sent) until reload. Any later config save also persists Init's in-memory edits (blanked sizes/seeds, the generated random seed).

M12. Checksum provides integrity only (L2898-2978): the hash file comes from the same archive as the binary, so it does not authenticate the download. The file format is over-strict (single bare hash; a BOM or `hash  filename` layout causes deletion of a valid update), and `Replace("-", "")` before base64 handling (L2913-2916, L3021-3024) corrupts base64url hashes containing `-`, making L3069's `-`->`+` mapping dead.

M13. Inconsistent custom-wipe parsing (L3892-3906 vs L4066-4077). The countdown accepts lenient forms (it-IT fallback, `11:30 PM`), the override setter only the strict ones: the wipe happens but the in-game timer is not set. If the `it-IT` culture is unavailable in the runtime, `GetCultureInfo` throws outside the try block (L4069) and every tick of `WipeTimeCheck` aborts before the force-wipe evaluation (culture availability unverified).

M14. DST gap (L4083-4091): a custom wipe time that does not exist locally makes `MinutesUntilCustomWipe` return null silently — the wipe never happens and nothing is logged by the countdown path (the override path logs a conversion error once at boot).

M15. `updaterValidAtInit` is never refreshed (L764-782, L2125). Fixing the path at runtime does not help until reload; conversely the binary can disappear after Init. The resolved path is discarded, and `AutoUpdateProcess`/launcher use different resolution rules for relative paths (L2983 vs L3647-3651 vs L2172-2176).

M16. `wipeUnixTimestampOverride` is sent without the `wipetimer.` prefix (L3888, L3933). Whether Rust resolves the unprefixed name is unverified; if not, the feature is a no-op with a misleading success log. No check that the custom timestamp is in the future; in branch A nothing is persisted, so the value is only set when `OnServerInitialized` reaches its end (not after H10).

M17. App-side token rule: a value starting with `-` is taken as the next flag (App L942). The plugin does not reject such values (e.g. a `ServerTmuxSession` or `NextWipeLevel` beginning with `-`), and the value is silently dropped.

M18. Race between wrapper script and lock (context, App L310-322; `Scripts/startserver.*`): the app creates `updating.lock` only after the server process is gone and after its own GitHub self-update check, while the sample scripts test for the lock 5 s after the server exits. A slow check lets the script restart the server while the updater proceeds. Belongs to the app spec; listed here because the plugin's quit timing (2.13) is part of it.

### Low

L1. Dead/duplicated code: `FileSha256Matches` (L3094-3110) unused; `GetGuiConfigSnapshot` unused in-repo; `OnServerSave` empty; `debugging` branches; outer catch of `AutoUpdateTask`; base64 fallbacks in `BuildFileListSingleArg` (L4126-4131); `== 0` branch duplicating the range branch (L4006-4012); blacklist in `IsSafeArgValue` redundant with the regex; `QuoteForShell` duplicated as `EscapeShellArg` (L2226); `JsonEscape` vs `EscapeJsonString` (L578); cleanup of the checksum text done twice (L2913, L3021); `EncodeIfSpecialArg` allocates its key set per call; unused deconstructed `minutesRemaining` (L3872).
L2. `PerformSaveAndQuitGracefully` does not save; first sequence step is empty.
L3. Shell quoting in 2.3/4.5/4.6: the path sits inside double quotes in the `sh -c` script, so `$`, backticks and backslashes in the path are expanded by the shell (config-controlled input).
L4. `TmuxSessionExists` passes the name unquoted and tmux matches `-t` targets by prefix, so a session named e.g. `feedmeupdate2` counts as existing; `send-keys` then types the command into whatever runs there.
L5. `TrySetExecutableUnix`: no kill on chmod timeout, exit code ignored, unbounded `ReadToEnd`. `DetectGnomeAndTmux` reads stdout before the 500 ms wait, so the timeout is ineffective.
L6. `TrySetExecutableUnix`/checks are Linux-only while the non-Windows path names (`FeedMeUpdates.new`) apply to every non-Windows OS.
L7. Random seed: `System.Random`, full uint range (L827-831). Whether Rust accepts seeds above 2147483647 in `server.seed` is unverified. Regenerated at each load, so the value announced/logged before a restart is not the one used.
L8. `ParseFileListStrictJsonArray` is not strict (trailing comma accepted), returns an empty list for both "no files" and "invalid", and does not constrain entries to plain file names (path traversal is left to the app).
L9. Month-name weak pattern has no word boundaries ("may" matches "maybe"); `Regex` on an unbounded commit message each cycle.
L10. `BeforeForceWipeRange <= 0` cannot disable force-wipe automation (forced to 15); there is no off switch for the feature at all.
L11. Typos in contract strings: "WARINING", "in invalid", "for the next is invalid".
L12. Paths built by concatenation with a hard-coded separator (L2832-2838).
L13. `fwTimer` is not created if Init returns early at L874; `WipeTimeCheck` then never runs (no wipe automation, silently).
L14. `ResetNextWipeValues` leaves `NextWipeResetRustPlus` set after a wipe (context L1184-1199), so Rust+ pairing data is reset again at every following wipe.

---

## 9. Open questions

1. Is the force wipe meant to follow 19:00 UTC or 19:00 UK local time? (M2)
2. Should the force wipe be executed even when no new build is detected within some time after the scheduled moment, and should the flag expire?
3. Should updates detected inside the pre-wipe window but before the forced build (Oxide-only, hotfix) really trigger the wipe? `BeforeForceWipeRange` is documented as "minutes before official force wipe to begin the wipe cycle", which suggests the author expected the wipe to start at that moment, yet the code only arms a flag.
4. Intended behaviour for a custom wipe date in the past: ignore, warn, or wipe immediately (current)?
5. Is `GetGuiConfigSnapshot` a supported API for `FeedMeUpdatesGUI`? Should the webhook URL be exposed?
6. Is `MaxAttempts` supposed to stop the init flow, the startup scan and custom wipes as well as periodic checks?
7. Why does the fallback branch of the override setter not call `server.writecfg` while the custom branch does? Is `server.readcfg` after `writecfg` needed?
8. Should `feedme.set` be able to change executable paths from in-game at all?
9. Is `UpdaterExecutablePath` allowed to be relative or to live outside `ServerDirectory`? The staging file names are fixed to `ServerDirectory` and `FeedMeUpdates(.exe)`.
10. Intended exact format of `FMU_CHECKSUMS.txt` (plural name suggests several entries).
11. Should `StartupScan` skip the player countdown deliberately (server just booted) — and should it also honour the force-wipe window?
12. What should happen when the daily-restart guard has disabled checks but the restart does not occur?
