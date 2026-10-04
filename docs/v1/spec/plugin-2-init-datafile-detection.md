# P2 — Plugin: Init/Unload, datafile, marker & lock, update detection

Source: `FeedMeUpdates.cs` (plugin v1.6.5), lines 735–1685 read in full, plus 22–160 (config/state).
Helpers outside the range were read only to understand usage (HTTP wrapper 157–380, scheme parser 383–571,
Discord 575–731, remote helpers 1685–2050, countdown/launch 2056–2292, `OnServerInitialized` 3513–3602,
arg builder 3700–3794, wipe timer 3992–4057) and the app's `HandleFlow` (`Application/Program.cs` 293–560, 1764–1840, 1954–1996).
"Plugin" = Oxide plugin, "app" = companion updater. Line numbers refer to `FeedMeUpdates.cs` unless prefixed `app:`.

Statements marked **(unverified)** depend on Oxide/Mono/Steam behaviour that could not be confirmed from the two source files.

---

## 1. Context: state and config used by this range

### 1.1 Config (`oxide/config/FeedMeUpdates.json`, class `ConfigData`, lines 24–73)

Keys read in this range (all are an external contract; names are case-sensitive JSON property names):

| Key | Type / default | Use in this range |
|---|---|---|
| `ServerDirectory` | string `""` | Base for `steambid.temp`, marker, lock, markers folder, Oxide DLL lookup. Empty => steam temp, marker and lock handling are all skipped. |
| `UpdaterExecutablePath` | string `""` | Validated once at Init (result cached in `updaterValidAtInit`). |
| `ServerStartScript` | string `""` | If empty and `RustOnService=false`, Init overwrites it with a platform default and saves the config (754–755). |
| `RustOnService` | bool false | See above. |
| `HttpTimeoutMs` | int 3000 | `<= 0` coerced to 3000 at Init (751). |
| `StartupScan` | bool false (README example: true) | Used by `OnServerInitialized` (3524). |
| `MaxAttempts` | int 0 | 0 = unlimited. See 4.3. |
| `CheckIntervalMinutes` | int 10 | `max(1, value)` (1602). |
| `CountdownMinutes` | int 5 | Used by countdown (outside range). |
| `OnlyServerProtocolUpdate` | bool false | UpdateLogics only (1572). Ignored in scheme mode. |
| `UseScheme`, `SchemeFile` | bool false, string `""` | Selects `SchemeResult` vs `UpdateLogics`. |
| `DailyRestartTime`, `MinutesBeforeRestart` | string `""`, int 60 | Daily-restart guard timers started in Init (892–905). |
| `DiscordNotificationsEnabled`, `DiscordWebhookUrl` | bool, string | Result notification sent from marker processing. |
| `NextWipeDeletePluginDatafiles`, `NextWipeMapsize`, `NextWipeSeed`, `NextWipeRandomSeed` | | Sanitised in Init (793–847). |
| `CustomWipeDay`, `CustomWipeTime`, `NextWipeServerName`, `NextWipeServerDescription`, `NextWipeMapUrl`, `NextWipeLevel`, `NextWipeKeepBps`, `NextWipeDeletePlayerData` | | Reset by `ResetNextWipeValues` (1188–1199). |
| `UpdaterMarkerFileName` | `"updateresult.json"` | Plugin side only. **The app hardcodes `updateresult.json`** (app:359, 543, 1769, 1827). |
| `UpdaterLockFileName` | `"updating.lock"` | Plugin side only. **The app hardcodes `updating.lock`** (app:297). |
| `MarkersSubfolder` | `"markers"` | Archive folder under `ServerDirectory`. |

### 1.2 Runtime fields touched (lines 94–153)

`enablePlugin` (true), `SystemUpdating` (app self-update promotion in progress), `pendingServerUpdate` / `pendingOxideUpdate`,
`tempPendingServerUpdate` / `tempPendingOxideUpdate`, `localOxideVersion`, `localProtocol`, `localBuildFromServerVersion`,
`LocalServerVersionString`, `LocalSteamBuildID` (""), `cachedRemoteBuild`, `cachedRemoteOxide`, `checkTimer`,
`isFetchingRemote` (volatile), `countdownActive` (volatile), `cachedPluginVersion`, `trynumber` (0),
`tmuxAvailable`, `gnomeAvailable`, `DoRestartTimeCheck`, `targetHour/targetMinute`, `scheme`, `lastNetworkFail`,
`updaterValidAtInit` (true), `fwTimer`, `DailyRestartCheckRepeater`, `pluginDatafilesToRemoveString`,
compat cache `lastOxideCompat` / `lastOxideProto` / `lastCompatNote`.
Dev-only compile-time switches: `debugging=false`, `quitBeforeExecOnDebugging=true` (98–99).

### 1.3 Datafile

`DataFileName = "FeedMeUpdatesData"` (87) => `oxide/data/FeedMeUpdatesData.json` via `Interface.Oxide.DataFileSystem`.
Shape: flat JSON object `Dictionary<string,string>` with exactly three known keys:

| Key | Meaning |
|---|---|
| `TryNumber` | Integer as string. See 4.3. |
| `LocalSteamBuildID` | Steam build id (public branch, app 258550) the plugin believes is installed. |
| `RemoteSteamBuildID` | Build id an in-flight server update is targeting; promoted to `LocalSteamBuildID` on the next boot if the marker says success. Removed after promotion. |

---

## 2. Function-by-function behaviour

### 2.1 `Init()` (738–907) — runs at plugin load, before the server has finished booting

Sequence (order matters):

1. Register permission `feedme.run` (740). Cache plugin version string, `"unknown"` on error (741).
2. Register Covalence commands `feedme.testrun`, `feedme.status`, `feedme.version`, `feedme.set` (743–746). (`feedme.set` is not in the README.)
3. Load config (748–762): `Config.ReadObject<ConfigData>() ?? new ConfigData()`.
   - `HttpTimeoutMs <= 0` => 3000.
   - If `ServerStartScript` empty and not `RustOnService`: set to `C:\rust-server\start_server.bat` (Windows) or `/home/rust/rust-server/start_server.sh` (otherwise) and **write the config file**.
   - Any exception: log `Error loading config: <msg>`, continue with an all-defaults `ConfigData` (all paths empty). Nothing is written.
4. Validate updater executable (764–782) via `ValidateUpdaterExecutableAtInit` (3634–3697): path non-empty; relative path resolved against `ServerDirectory` if that candidate exists; `Path.GetFullPath`; must exist, not be a directory, length > 0; on Linux resolve symlink and require `test -x`. Result stored in `updaterValidAtInit`; never re-evaluated. A later launch attempt with `updaterValidAtInit == false` is aborted (2125–2130).
   Logs: `Updater executable validated at init: <path>` / `Updater executable validation failed at init: <reason>` / `Updater executable validation error at init: <msg>`.
5. Log `FeedMeUpdates initialized (v<version>).`
6. `ProcessSteamTempAndDataFile()` (2.4) — each of 6–9 is wrapped in its own try/catch that only logs.
7. `ProcessTryNumber()` (2.3).
8. `ProcessUpdaterMarkerIfPresent()` (2.6).
9. `ProcessUpdaterLockIfPresent()` (2.8).
10. `AutoUpdateTask()` (791; body at 2826) — background task that promotes `FeedMeUpdates(.exe).new` if present; sets `SystemUpdating` while running. Outside this range.
11. Sanitise wipe config **in memory only** (793–847; not written to disk here, but any later `Config.WriteObject(configData)` — `feedme.set`, `ResetNextWipeValues` — persists the sanitised values):
    - `pluginDatafilesToRemoveString = BuildFileListSingleArg(ParseFileListStrictJsonArray(NextWipeDeletePluginDatafiles))` — strict JSON array of strings -> JSON re-serialised -> base64url (no padding), falling back to standard base64, or `""`. If config value is non-empty but the result is empty: log `List of plugin datafiles to delete at wipe in invalid. Plugin datafile removal disabled` (config value is left as is).
    - `NextWipeMapsize`: must parse as int in 1000..6000, otherwise blanked with a log message.
    - `NextWipeRandomSeed == true`: generate a random `uint` (4 random bytes from `System.Random`) and store it as decimal in `NextWipeSeed` — **a new seed on every plugin load**. Otherwise `NextWipeSeed` must match `^[0-9]+$`, length <= 10, value <= `uint.MaxValue` (3113–3133), else blanked with a log.
12. `enablePlugin = !(MaxAttempts != 0 && trynumber >= MaxAttempts)` (850–851). Only consumer: `StartPeriodicCheck` (1598).
13. Read local versions (853–875): `localProtocol = server.Protocol?.ToString()`, `localBuildFromServerVersion = server.Version?.ToString()` (errors logged, values stay null); `localOxideVersion = GetOxideVersionFromDll()`; `LocalServerVersionString`. Log line:
    `Local detected: protocol=<p|null> | server.Version=<v|null> | LocalSteamBuildID=<id|<empty>> | Oxide=<v|unknown>`.
    `GetOxideVersionFromDll` (1804–1835) probes, in order, under `ServerDirectory`: `oxide/managed/Oxide.Rust.dll`, `RustDedicated_Data/Managed/Oxide.Rust.dll`, `oxide/Oxide.Rust.dll`, `Oxide.Rust.dll`; takes `ProductVersion` (else `FileVersion`), normalises to first `\d+\.\d+\.\d+`; returns the literal `"unknown"` if nothing is found.
    **These local values are read once per plugin load and never refreshed.**
14. If `UseScheme`: `ReadSchemeFile()` (530–571). Exception => `UseScheme=false` in memory + log. Invalid rows => `UseScheme=false` + per-row errors logged. **Missing path / missing file => only a log line, `UseScheme` stays true** (see D7).
15. `DetectGnomeAndTmux()` (3328–3362): Linux only; `sh -c "command -v <name> >/dev/null 2>&1 && printf 1 || printf 0"` for `gnome-terminal`, `gnome-terminal-server`, `tmux`.
16. Daily-restart guard (892–905): if `DailyRestartTime` non-empty and `MinutesBeforeRestart > 0` and the time parses (`HH:mm`/`H:mm`): `timer.Once(60 - DateTime.Now.Second)` to align on a minute boundary, then call `DailyRestartCheckerMethod()` and repeat `timer.Every(60f)`. The checker (3244–3263) computes minutes to the next occurrence (local time) and, when `round(remaining) == MinutesBeforeRestart` **exactly**, destroys `checkTimer` and itself and logs `Periodic check for updates has been disabled due to proximity to the daily restart.` It does not cancel a running countdown or an in-flight detection.
17. `fwTimer = timer.Every(60f, WipeTimeCheck)` (906) — started unconditionally (also when `enablePlugin` is false).

Not done in Init: no update detection is started here. Detection starts from `OnServerInitialized` (3513–3602), summarised in 2.12 because it is the only caller that gives the range its meaning.

### 2.2 `Unload()` (910–927)

Destroys `checkTimer`, `DailyRestartCheckRepeater`, `fwTimer` and nulls them. Does not cancel background tasks or reset flags. (Oxide destroys a plugin's timers on unload anyway — **unverified**.)

### 2.3 Try number: `ProcessTryNumber` / `ReadTryNumber` / `WriteTryNumber` (934–980)

- `ReadTryNumber`: reads datafile; if key `TryNumber` exists and is non-empty, `trynumber = Int32.Parse(...)`, return true. Parse error => log `Error reading datafile: ...`, return false.
- `ProcessTryNumber`: if read failed, `WriteTryNumber()`; i.e. on first run writes `TryNumber = "0"`.
- `WriteTryNumber`: read-modify-write of the dictionary. Always returns true in practice (the inner writer swallows exceptions).
- **The only other caller of `WriteTryNumber` is the last line of `StartUpdaterExecutable` (2291)**, executed after a launch (or after a launch exception), but **not** on the early `return`s inside that method (invalid exe, missing exe, bad args, debug quit).

### 2.4 `ProcessSteamTempAndDataFile` (983–1019)

- `ServerDirectory` empty => log `ServerDirectory not configured; skipping steambid.temp handling.` and return (LocalSteamBuildID stays empty).
- If `<ServerDirectory>/steambid.temp` exists: read all text, `Trim()`.
  - Non-empty => `LocalSteamBuildID = id`, save to datafile, log.
  - Empty => log `steambid.temp present but empty.`
  - In both cases delete the file (failure only logged) and **return without loading the datafile value**.
  - Read exception => log, fall through to datafile load.
- Otherwise `LoadLocalSteamFromDataFile()`; if a non-empty value exists, set `LocalSteamBuildID` and log.

No validation of the id's format is done (the app can write the literal `no`, see 4.1).

### 2.5 Datafile primitives (1022–1096)

- `LoadLocalSteamFromDataFile` — true only if key exists and non-empty.
- `SaveLocalSteamToDataFile` — sets `LocalSteamBuildID` key (`""` if null).
- `SaveRemoteSteamToDataFile(remoteBuild)` — sets `RemoteSteamBuildID`, logs `RemoteSteamBuildID '<id>' saved in datafile.`
- `RemoveRemoteSteamFromDataFile` — removes the key if present, logs.
- `ReadDataFileDict` — `DataFileSystem.ReadObject<Dictionary<string,string>>`; **any error (including a corrupt file) returns an empty dictionary** after logging `Generic datafile read error`. It never returns null (so all the `dict == null` checks are dead).
- `WriteDataFileDict` — `DataFileSystem.WriteObject`; errors logged and swallowed.

Every mutation is an unlocked read-modify-write of the whole file. All current callers run on the main thread.

### 2.6 `ProcessUpdaterMarkerIfPresent` (1103–1181)

Input: `<ServerDirectory>/<UpdaterMarkerFileName ?? "updateresult.json">`. If `ServerDirectory` empty or file absent: return silently.

Steps:
1. Read whole file as text. Fields are extracted by regex, **not** by a JSON parser (`ExtractJsonString`, 2.7).
2. `result == "success"` (exact, case-sensitive) => `trynumber = 1`; `result == "failed"` => `trynumber++`. In-memory only (see 4.3).
3. Extract with fallbacks: `update_id` | `updateId`; `fail_reason` | `error`; `wiped` | `wipe_result`; `wipe_info` | `wipeinfo`.
4. `updated_plugins`: regex `"updated_plugins"\s*:\s*\[(.*?)\]` (IgnoreCase, Singleline), then every `"([^"]+)"` inside.
5. `NotifyDiscordUpdateResult(result, update_id, hasPlugins, plugins, failureReason, wiped, wipe_info)` (653–731). Message formats (external, user-visible):
   - update: `[FeedMeUpdates] Update finished: <RESULT|UNKNOWN> (ID: <id|unknown>)\n` + `Update executed SUCCESSFULLY.` [+ `\nUpdated plugins: a, b`] or `Update FAILED.` [+ `\nFailure reason: <r>`]
   - wipe (`update_id == "wipe"`): `[FeedMeUpdates] wipe finished: ...` + `Wipe executed SUCCESSFULLY.` / `Wipe FAILED.` [+ reason]
   - both: `\nServer wiped.` if `wiped == "yes"` else `\nServer not wiped`; if `wipe_info != ""`: `\nWipe ALERTS reported:` then one `\n - <w>` per `]`-separated chunk with leading `[` trimmed (wipe_info format is `[alert1][alert2]`).
   - Sent as `{"content":"..."}` POST to the webhook, forced through the WebClient backend, fire-and-forget.
6. Console dump: `=== Updater marker found ===`, `result: ...`, `update_id: ...`, `Full payload: <raw file>`, `=== End marker ===`.
7. Promotion (1141–1166), only if `result.Trim()` equals `success` case-insensitively:
   - if `wiped.Trim()` equals `yes` (case-insensitive) => `ResetNextWipeValues()` (1184–1203): blanks `CustomWipeDay`, `CustomWipeTime`, `NextWipeServerName`, `NextWipeServerDescription`, `NextWipeMapUrl`, `NextWipeLevel`, `NextWipeSeed`, `NextWipeMapsize`, `NextWipeDeletePluginDatafiles`; sets `NextWipeRandomSeed=false`, `NextWipeKeepBps=true`, `NextWipeDeletePlayerData=false`; writes the config file. (`NextWipeResetRustPlus` and `ServerIdentity` are not reset.)
   - if datafile has non-empty `RemoteSteamBuildID`: `LocalSteamBuildID = remote`, save, remove remote key, log `Marker success: RemoteSteamBuildID '<id>' promoted to LocalSteamBuildID and removed from datafile.`; else log `Marker success but no RemoteSteamBuildID to promote.`
   - Not success: log `Marker does not indicate success; no promotion performed.` **`RemoteSteamBuildID` is left in the datafile.**
8. Archive (1168–1178): create `<ServerDirectory>/<MarkersSubfolder ?? "markers">` if missing; move marker to `marker_<update_id | 32-hex GUID>_<dd_MM_yy_HH-mm-ss local>.json`. Failure only logged (marker stays and will be processed again next boot).
9. Whole method wrapped in try/catch => `Error reading marker: <msg>`; an exception anywhere before step 7 skips promotion and archiving.

Marker contract actually produced by the app (indented JSON via System.Text.Json):
- success (update flows): `result`, `fail_reason: null`, optional `updated_plugins` (array, only if `update_plugins=yes`), `backup_cycle: true`, `server_restored: "no"`, `timestamp` (ISO-8601 "o"), `update_id` (`init` | `testrun` | 8 digits), `wiped` (`yes`/`no`), `wipe_info` (app:527–547).
- success (custom wipe): `result`, `fail_reason: null`, `timestamp`, `update_id: "wipe"`, `wiped`, `wipe_info` (app:351–357).
- failed before backup: `result: "failed"`, `fail_reason`, [`updated_plugins`], `backup_cycle: false`, `server_restored: false`, `update_id`, `wiped: "no"`, `wipe_info: ""` (app:3195–3250).
- failed + restored: `result: "failed"`, `fail_reason: "Failed <what>"`, `backup_cycle: true`, `server_restored: "yes"`, `timestamp`, `update_id` — **no `wiped`, no `wipe_info`** (app:1771–1779).
- failed + restore failed: same with `fail_reason: "Failed restore after failed <what>"`, `server_restored: false` — **no `wiped`, no `wipe_info`** (app:1829–1837).
Note the mixed types (`server_restored` is `"no"`, `"yes"` or boolean `false`).

### 2.7 `ExtractJsonString(json, key)` (1206–1218)

1. `"<key>"\s*:\s*"([^"]*)"` (IgnoreCase) => `WebUtility.HtmlDecode(group1)`.
2. else `"<key>"\s*:\s*([^,\}\n\r]+)` => trimmed, quotes trimmed, HtmlDecoded (so JSON `null` yields the **string** `"null"`, `false` yields `"false"`).
3. else null. Exceptions => null.
No JSON unescaping: System.Text.Json's default encoder emits `\u0022`, `\u0027`, `\u003C`, `\u002B` etc., which reach Discord/console verbatim. The key match is not anchored to top level.

### 2.8 `ProcessUpdaterLockIfPresent` (1221–1232)

If `<ServerDirectory>/<UpdaterLockFileName ?? "updating.lock">` exists: log `Warning: updater lock file present at <path>. Updater may have been interrupted.`, delete it, log `Lock file removed.` (or `Could not remove lock file: ...`).

### 2.9 `SchemeResult()` (1239–1408) — detection when `UseScheme` is true

Returns `bool[3] { updateServer, updateOxide, shithappens }`. The third element is never true on a normal return (it is only set immediately before a `throw`).

Data gathering:
1. `remoteOxide = GetRemoteOxideVersion()` — GET `https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`, timeout `HttpTimeoutMs`, tag from `"tag_name"\s*:\s*"v?([^"]+)"`. Null/empty (timeout, non-200, no tag) => **return {false,false,false} without checking the server**. Exception => rethrown. Otherwise `cachedRemoteOxide = remoteOxide`.
2. `OxideChanged = Normalize(remoteOxide) != Normalize(localOxideVersion)` where Normalize = first `\d+\.\d+\.\d+` match (else strip from `+`/`-`).
3. If `OxideChanged`: `sameProtocol = GetOxideCompatibilityInfo(...)` (true = same protocol, false = changed, null = unknown/error); `protocolUnknown = (sameProtocol == null)`. If not changed, `protocolUnknown` stays true but is irrelevant.
4. `remoteBuild = GetRemoteRustBuild()` — runs `<SteamCmdPath> +login anonymous +app_info_print 258550 +quit`, parses `"branches" { ... "public" { ... "buildid" "<5–12 digits>"`.
   - non-empty: `cachedRemoteBuild = remoteBuild`; if `LocalSteamBuildID` empty => `throw new Exception("LocalSteamBuildID empty")`; else `ServerChanged = (remoteBuild != LocalSteamBuildID)` (ordinal string compare).
   - empty/null (steamcmd missing or unparsable output): `ServerChanged = false` silently.
5. `scheme.instructions == null` => return {false,false,false}.

Rule matching — iterate instructions in file order, **first match wins** (`break`). Each instruction has event flags `ServerEvent`, `OxideUpdateEvent`, and protocol flags `s` (match), `c` (mismatch), `u` (unknown), `e` (error), plus action flags `UpdateServer`/`UpdateOxide` (`server` / `oxide` / `both`). `oxide` without parentheses = all four protocol flags.

| OxideChanged | Protocol state | ServerChanged | An instruction matches iff | Result |
|---|---|---|---|---|
| true | unknown (null) | true | `OxideUpdateEvent && (u || e) && ServerEvent` | `{si.UpdateServer, si.UpdateOxide}` |
| true | unknown (null) | false | `OxideUpdateEvent && (u || e) && !ServerEvent` | `{si.UpdateServer, si.UpdateOxide}` |
| true | same | true | `OxideUpdateEvent && s && ServerEvent` | idem |
| true | same | false | `OxideUpdateEvent && s && !ServerEvent` | idem |
| true | changed | true | `OxideUpdateEvent && c && ServerEvent` | idem |
| true | changed | false | `OxideUpdateEvent && c && !ServerEvent` | idem |
| false | n/a | true | `!OxideUpdateEvent && ServerEvent && UpdateServer` | `{true, false}` (the rule's `UpdateOxide` is ignored) |
| false | n/a | false | never | `{false,false}` |
| any | any | any | no instruction matched | `{false,false}` |

Consequences that must be preserved or consciously changed:
- The server condition is an **exact** match: when Oxide changed, a rule without `server` matches only if the server did *not* change, and a rule with `server` only if it did. So with only `oxide(...)=...` and `server=server` rules, **nothing happens when both changed**; an explicit `oxide(...)+server=...` rule is required.
- `u` and `e` are indistinguishable here: both fire whenever the compatibility lookup returned null.
- A `server=oxide` rule never matches (requires `UpdateServer`); `server=both` behaves as `server=server`.
- The action is taken literally even for a component that did not change (e.g. `oxide(s)=both` with unchanged server launches a server update targeting the currently installed build id).
- `OnlyServerProtocolUpdate` and the `pendingServerUpdate` short-circuit (2.10) are not applied in scheme mode.

### 2.10 `UpdateLogics()` (1411–1593) — default detection

Returns `{updateServer, updateOxide, shithappens}`; also writes `tempPendingServerUpdate` / `tempPendingOxideUpdate` (reset to false on entry). Any exception is rethrown (the `shithappens` flag is therefore never observable as true).

Inputs:
- `remoteOxide` as in 2.9 step 1. Null/empty => **return {false,false} immediately, server not checked**.
- `OxideChanged` as in 2.9 step 2.
- Oxide protocol class (only computed when `OxideChanged`), from `GetOxideCompatibilityInfo` result + `note` (1505–1527):
  - `s` = returned true ("protocols match")
  - `c` = returned false ("protocols differ", or inferred from commit-message score >= 6)
  - `e` = returned null and note starts with `exception` or contains `failed` / `error` / `lookup failed` (in practice only the `exception: ...` note; also set when the call itself throws)
  - `u` = returned null otherwise (`no oxide tag`, `local protocol unknown`, `no commit info` — which is also what all HTTP failures of the commit lookup produce —, `oxide protocol not found`, empty note)
- `ServerChanged` via `DetermineServerChanged` (1452–1486):
  - if `pendingServerUpdate` is already true => true **without running steamcmd** (`remoteBuild` stays null; `cachedRemoteBuild` keeps its previous value);
  - else run steamcmd; empty result => false; steamcmd exception => rethrow; `LocalSteamBuildID` empty => `throw "LocalSteamBuildID empty"`; else `remoteBuild != LocalSteamBuildID`.

Complete decision table:

| # | remoteOxide fetched | OxideChanged | Protocol class | ServerChanged | OnlyServerProtocolUpdate | Result (server, oxide) |
|---|---|---|---|---|---|---|
| 1 | no (null/empty) | – | – | not evaluated | any | (false, false) |
| 2 | exception | – | – | – | any | exception => cycle skipped |
| 3 | yes | true | s / c / u / e (any) | true | any | **(true, true)** |
| 4 | yes | true | s | false | any | (false, true) |
| 5 | yes | true | u | false | any | (false, true) |
| 6 | yes | true | c | false | any | (false, false) — wait for the Rust build |
| 7 | yes | true | e | false | any | (false, false) |
| 8 | yes | false | – | true | false | (true, false) |
| 9 | yes | false | – | true | true | (false, false) — wait for Oxide |
| 10 | yes | false | – | false | any | (false, false) |
| 11 | yes | any | any | steamcmd throws / LocalSteamBuildID empty | any | exception => cycle skipped |

Notes: the compatibility lookup (up to 6 GitHub API calls) is performed before the server check even in row 3 where its result is irrelevant. `OnlyServerProtocolUpdate` only affects row 9; it does not look at the protocol at all (README wording is misleading).

### 2.11 `StartPeriodicCheck()` (1596–1606)

If `!enablePlugin` return silently. Destroy existing `checkTimer`; `checkTimer = timer.Every(max(1, CheckIntervalMinutes) * 60f, StartUpdateDetectionAsync)`; log `Periodic checks every <n> minutes started.`; additionally one detection after 2 s (`timer.Once(2f, ...)`).
Callers: `OnServerInitialized` (if `checkTimer == null`, 3601) and `feedme.status on|enable` (2308). `checkTimer` is destroyed by: `feedme.status off`, the daily-restart guard, the custom-wipe countdown (4043–4047), `Unload`.

### 2.12 `StartUpdateDetectionAsync()` (1609–1678)

1. If `isFetchingRemote`: log `Remote fetch already in progress; skipping this detection cycle.` and return.
2. Snapshot the four pending/temp flags; set `temp* = pending*`; `isFetchingRemote = true`.
3. `Task.Run` (thread pool):
   - `LogicsResult = UseScheme ? SchemeResult() : UpdateLogics()`; copy to `tempPending*`; read `cachedRemoteBuild` / `cachedRemoteOxide` into locals.
   - marshal to main thread with `timer.Once(0f, ...)`: `pending* = tempPending*`; log `Detection complete: pendingServerUpdate=<b> pendingOxideUpdate=<b>`; if `pendingServerUpdate` and `remoteBuild` non-empty => `SaveRemoteSteamToDataFile(remoteBuild)` (**at detection time, before the countdown**); if either pending => `BeginUpdateCountdown(remoteBuild, remoteOxide)`.
   - exception: log `Detection error: <msg> - skipping this cycle and restoring previous pending/temp flags.` and restore the four flags (from the worker thread).
   - finally `isFetchingRemote = false` (runs before the main-thread callback executes).

There is no gating on `countdownActive`, `SystemUpdating`, `nextUpdateIsForce` or `enablePlugin` inside this function; `BeginUpdateCountdown` itself refuses if a countdown is active (`Countdown already active; skipping new countdown.`) or if `SystemUpdating` (`App update is not over. Skipping cycle.`), leaving the pending flags set.

Downstream contract (outside range, for orientation): the countdown broadcasts `Notice: update detected. Server will restart to apply updates in <n> minute(s).`, then every 60 s `ATTENTION: server will restart for update in <n> minute(s).` (or `... for monthly force wipe ...`), and at zero reads `pendingServerUpdate` / `pendingOxideUpdate` **at that moment** to build the launch: `-update_id <8 random digits> -what server|oxide|both|none -remote_build <id|no> -remote_oxide <tag|no> ...` (full list at 3706–3733), then runs console command `quit` 0.5 s later. In the `Scheduled` flow the app re-queries Steam and GitHub and **fails the update if the live build id / tag differs from `-remote_build` / `-remote_oxide`** (app:420, 450), restoring the backup with `fail_reason` `Failed server update (different version detected)` / `Failed oxide update (different version detected)`.

`OnServerInitialized` (3513–3602), the entry point:
1. `LocalSteamBuildID` empty => launch app with `-update_id init -what both -remote_build no -remote_oxide no`, then `quit`. No countdown, no check of `enablePlugin` / `MaxAttempts`.
2. Else if `StartupScan && !SystemUpdating`: reset pending flags, run `UpdateLogics()` / `SchemeResult()` **synchronously on the main thread**, and if anything is pending save `RemoteSteamBuildID`, launch the app immediately (random 8-digit id, no countdown) and `quit`. Otherwise log `StartupScan: remote Oxide fetch failed; skipping update for safety.` (if `lastNetworkFail`) or `StartupScan: no updates available at startup.`
3. `SetWipeUnixTimestampOverrideFromConfig()`; `if (checkTimer == null) StartPeriodicCheck()`.

---

## 3. External contracts touched by this range (summary)

- Files (all relative to `ServerDirectory` unless noted):
  - `steambid.temp` — written by the app at the end of a successful `init` flow (app:476–478), content = bare build id text (or the literal `no`); read+deleted by the plugin at Init.
  - `updateresult.json` — written by the app; read, then moved by the plugin to `markers/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json`.
  - `updating.lock` — created (empty) by the app (app:322); deleted only by the plugin at Init.
  - `oxide/data/FeedMeUpdatesData.json` — keys `TryNumber`, `LocalSteamBuildID`, `RemoteSteamBuildID`.
  - `oxide/config/FeedMeUpdates.json` — rewritten by Init (default start script) and `ResetNextWipeValues`.
  - Oxide DLL probe paths (2.1 step 13); scheme file at absolute `SchemeFile`.
- Processes: `<SteamCmdPath> +login anonymous +app_info_print 258550 +quit` (stdout+stderr redirected, no shell, no window); `sh -c "command -v ..."`; `/bin/sh -c 'test -x "<path>"'` (Linux).
- Network: `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`; compat lookups `GET .../commits/<tag>` and `.../releases/tags/<tag>` for both `<tag>` and `v<tag>`, then `.../commits/<target_commitish>`; Discord webhook POST `{"content": "..."}`. User-Agent `FeedMeUpdates` (WebClient backend only); `Content-Type: application/json` for POST. No auth token => GitHub unauthenticated rate limit (60 requests/hour per IP) applies; HTTP 403 is logged as `Possible GitHub rate limit.`
- Timers: detection every `max(1, CheckIntervalMinutes)` min + one at +2 s; daily guard every 60 s aligned to the minute; wipe check every 60 s.
- `update_id` values: `init`, `testrun`, `wipe`, or 8 decimal digits (`Random.Next(0, 100000000).ToString("D8")`); the app rejects anything else (app:262–270).
- Permission `feedme.run`; commands `feedme.testrun`, `feedme.status [on|off|enable|disable]`, `feedme.version`, `feedme.set <option> <value>`.

---

## 4. Implicit knowledge and workarounds

### 4.1 Lifecycle of `LocalSteamBuildID` / `RemoteSteamBuildID` / `steambid.temp`

Why it exists: the plugin has no direct way to learn the installed **Steam build id**. `server.Version` / `server.Protocol` are Rust's own version numbers, not the Steam depot build id that `steamcmd app_info_print` reports. So the plugin keeps its own belief of the installed build id and compares it with the remote one.

1. **Bootstrap (first ever boot, or datafile lost):** `LocalSteamBuildID` is empty => `OnServerInitialized` launches the app with `update_id=init` and quits the server immediately (this is the README's "on first boot FMU will stop, update, and restart"). The app's Init flow backs up, queries the remote build id via steamcmd (**before** updating), force-updates server and Oxide, then writes `steambid.temp` with that id and a success marker with `update_id: init`.
2. **Next boot:** Init finds `steambid.temp`, adopts it as `LocalSteamBuildID`, persists it to the datafile and deletes the temp file. The marker is `success` but there is no `RemoteSteamBuildID`, so nothing is promoted. `steambid.temp` exists because the app does not know about (or write to) the Oxide datafile; it is a one-shot hand-over file.
3. **Detection:** each cycle compares steamcmd's public-branch build id with `LocalSteamBuildID`. When a server update is decided, the remote id is stored as `RemoteSteamBuildID` *before* the countdown, and passed to the app as `-remote_build`.
4. **Update run:** the app refuses to proceed if the build id it sees differs from `-remote_build` (so that the id later promoted is the one actually installed).
5. **Boot after the run:** marker `success` => `RemoteSteamBuildID` becomes `LocalSteamBuildID` and is removed. Marker `failed` => no promotion; the stale `RemoteSteamBuildID` stays (see D5).
6. **Self-healing properties:** if the belief is wrong (manual update, build published between steps, sentinel `no` written by the app when its steamcmd query failed — app:1954–1996 returns `"no"`), the next detection sees a mismatch and performs one redundant server update, after which the belief is correct. Deleting `oxide/data/FeedMeUpdatesData.json` is the de-facto "reset": it re-triggers the init flow on the next boot.
7. The comparison is a plain string inequality — no "newer than" semantics; a rollback on Steam's side also triggers an update.

### 4.2 Lock file

The app creates `updating.lock` after the Rust process has exited and **never deletes it** (verified: the only references are app:297–322). The plugin's boot-time delete is therefore the *normal* unlock path, not an anomaly path; the "Updater may have been interrupted" warning is printed after every run. If the app is started while the lock exists it writes a failed marker `Blocked by updating.lock file` and starts the server. Init processes the marker before deleting the lock.

### 4.3 Try number and `MaxAttempts`

Intent (inferred): stop retrying an update that keeps failing. Actual mechanics:
- Persisted value changes only inside `StartUpdaterExecutable` (written right after each launch) and on first run (`0`).
- At boot: `trynumber` = persisted value; marker `success` => 1; marker `failed` => +1 (memory only).
- `enablePlugin = !(MaxAttempts != 0 && trynumber >= MaxAttempts)`; it only prevents `StartPeriodicCheck` from starting the periodic timer.
- Worked example, `MaxAttempts = 3`: boot after success => memory 1, launch writes 1; fail => boot reads 1, memory 2, checks enabled, launch writes 2; fail => boot reads 2, memory 3 => periodic checks disabled for this boot.
- See D1–D3 for why this does not actually bound retries.

### 4.4 Other hard-won details

- **Detection needs Oxide's release feed first.** If GitHub is unreachable or rate limited, no detection of any kind happens (rows 1 of both tables). `StartupScan` logs "skipping update for safety". This is deliberate: updating Rust without a matching Oxide build leaves the server without Oxide (and therefore without this plugin). Rows 6, 7 and 9 of the UpdateLogics table encode the same caution ("wait for the other half").
- **Rate-limit budget:** every cycle costs 1 GitHub call; while a new Oxide tag is pending and its protocol cannot be determined, each cycle costs up to 7 (results that are null are not cached). With the default 10-minute interval this stays under 60/hour; with 1-minute intervals it does not.
- **HTTP backend juggling:** compat lookups and Discord posts force the `WebClient` backend and restore the previous one afterwards. Likely reason: these calls are made from the main thread (launch path, Init), and the Oxide `webrequest` backend completes via main-thread callbacks, so a synchronous wait on the main thread can only time out **(unverified)**. Two backends exist at all presumably because one or the other fails on some hosts (Mono TLS / missing User-Agent) **(unverified)**.
- **`timer.Once(0f, ...)` is used as "post to main thread"** from worker threads, because Oxide/Unity APIs (broadcasts, console commands, config) must run on the main thread.
- **Steam build id regex `[0-9]{5,12}`** inside `branches -> public` only; staging/other branches are ignored (README: staging not supported). The app id `258550` is the Rust dedicated server.
- **Oxide version comparison** uses only the first `X.Y.Z` of both sides (DLL `ProductVersion` may carry `+hash` or `-suffix`; the GitHub tag may carry a leading `v`).
- **Minute alignment** of the daily-restart guard (`60 - Now.Second`) exists because the guard uses exact equality on rounded minutes.
- **Startup scan launches without countdown** on the assumption nobody is connected right after boot.
- **Marker fallbacks** (`updateId`, `error`, `wipe_result`, `wipeinfo`) are leftovers of older marker formats; the current app never emits them.
- **Init-time validation of the updater binary** (exists, non-empty, +x on Linux, symlink resolved) was added so that a broken binary is reported at boot rather than at the moment the server is about to quit.

---

## 5. Defects and risks

Severity: H = can take the server down / loop / lose automation; M = wrong behaviour with workaround; L = cosmetic or narrow.

**D1 (H) Server is shut down even when the updater was not launched.** `StartUpdaterExecutable` returns `void` and aborts silently on invalid/missing executable or argument validation failure (2125–2130, 2166–2195), but every caller then calls `PerformSaveAndQuitGracefully()` unconditionally (3518–3520, 3577–3580, 2095–2098, 2375–2378, 4033–4036). Concrete scenario: plugin installed with a default/broken config (all paths empty; also the config-exception path at 758–762) => `updaterValidAtInit=false`, `LocalSteamBuildID` empty => init flow => server quits at every boot and nothing restarts it. Same for any detected update when the binary lost its +x bit.

**D2 (H) `MaxAttempts` does not stop update loops.** `enablePlugin` is checked only in `StartPeriodicCheck` (1598). The init flow and `StartupScan` in `OnServerInitialized`, the wipe timer and `feedme.testrun` ignore it. With `StartupScan=true` (README example) a persistently failing update yields: boot -> scan -> launch -> fail/restore -> boot -> ... indefinitely, each iteration doing a full backup/restore.

**D3 (M) Try-number bookkeeping is inconsistent.** (a) The post-marker value is never persisted at boot (1113–1116 vs 2291), so the "disabled" state lasts one boot only: after any restart without a new marker the stale lower value is read and checks are re-enabled. (b) Success sets 1, not 0, so `MaxAttempts=1` disables periodic checks after every *successful* update; effective failure budget is `MaxAttempts-1`. (c) `result` is compared case-sensitively at 1113–1115 but case-insensitively/trimmed at 1143. (d) If the marker cannot be archived it is re-counted on every boot.

**D4 (M/H) Marker without `wipe_info` crashes marker processing when Discord is enabled.** `NotifyDiscordUpdateResult` does `if (wipe_info != "") wipe_info.Split(...)` (680–683, 718–721); `ExtractJsonString` returns null when the key is absent, which is the case for both restore markers (app:1771–1779, 1829–1837). The NullReferenceException is caught at 1180: no Discord message for exactly the failures that matter most, marker not printed, not archived, left in place and re-processed (and `trynumber++`) on every boot until another run overwrites it. With Discord disabled the path is not hit.

**D5 (M) Stale `RemoteSteamBuildID` can be promoted.** It is written at detection time (1654–1657) and never cleared on failure or when the countdown is aborted (manual/daily restart during the countdown). Promotion (1147–1155) happens on *any* success marker, regardless of `update_id` or `what`. Scenario: server update to build B fails and is restored; later a custom wipe (`update_id=wipe`) or an oxide-only run (possible in scheme mode) succeeds => `LocalSteamBuildID := B` although A is installed => the pending server update is never detected again.

**D6 (M) Pending flags can change under a running countdown.** Detection keeps running during the countdown and overwrites `pendingServerUpdate` / `pendingOxideUpdate` (1650–1651), while the countdown reads them only at expiry (2085–2095) with the `remoteBuild` / `remoteOxide` captured at its start. If `CheckIntervalMinutes < CountdownMinutes` and a later cycle returns (false,false) (e.g. a GitHub hiccup, row 1), the server still quits and the app is launched with `-what none` (a restart with backup and no update). If a later cycle adds a component, the captured id/tag for it is stale or empty and the app fails with "different version detected". Not reachable with default 10/5 settings.

**D7 (M) Scheme enabled but never loaded => updates silently stop.** `ReadSchemeFile` only logs when `SchemeFile` is empty or missing (532–542) and leaves `UseScheme=true` with `scheme.instructions == null`; `SchemeResult` then returns (false,false) forever (1305–1308). Same when `UseScheme` is switched on at runtime via `feedme.set` (2508), which does not load the file. An empty/comment-only file is accepted as valid with the same effect.

**D8 (M) Oxide compatibility cache is never invalidated by tag.** Both detection functions assign `cachedRemoteOxide = remoteOxide` (1256, 1430) *before* calling `GetOxideCompatibilityInfo`, whose cache condition is `oxideTag == cachedRemoteOxide && lastOxideCompat.HasValue` (1881). The tag comparison is therefore always true; once a true/false result exists it is returned for every later tag until the plugin reloads. Scenario: tag X classified "protocol changed" (row 6, waiting); Oxide then publishes X+1 with the same protocol => still classified "changed".

**D9 (M) `localOxideVersion == "unknown"` causes an endless update loop.** If the DLL probe fails (1834), `OxideChanged` is always true, the compat lookup returns `u`/`s`, and every cycle requests an Oxide update (rows 4/5), restarting the server every `CheckIntervalMinutes` (and at every boot with `StartupScan`).

**D10 (M) steamcmd invocation can hang detection forever** (1695–1710, just outside the range but called by it). stdout is read with `ReadToEnd()` with no timeout; stderr is redirected but never read (pipe can fill); the `WaitForExit(60000)` after `ReadToEnd` is ineffective and the process is never killed. A hung steamcmd leaves `isFetchingRemote=true` forever => every later cycle logs "Remote fetch already in progress" and detection is dead until reload. In the startup scan the same call runs on the main thread and freezes the server. Also `app_info_print` is used without `+app_info_update 1`, so steamcmd may answer from its local appinfo cache **(unverified; well-known steamcmd behaviour)**.

**D11 (M) Synchronous network/process work on the main thread.** `OnServerInitialized` runs `UpdateLogics`/`SchemeResult` inline (3539–3542): one steamcmd run plus up to 7 HTTP calls of `HttpTimeoutMs` each block the server tick. If the active HTTP backend is Oxide `webrequest`, a main-thread synchronous wait likely always times out, making the startup scan report "remote Oxide fetch failed" **(unverified)**.

**D12 (M) `NextWipeResetRustPlus` is not reset after a wipe** (1188–1199), contrary to the README ("resets all NextWipe* values"); Rust+ pairing data would be wiped again on every later wipe until changed by hand.

**D13 (L/M) Scheme matching quirks** (2.9): `e` flag is equivalent to `u`; exact server match means "both changed" needs an explicit combined rule; `server=oxide` is dead; `server=both` ignores Oxide; actions may target an unchanged component. None is rejected by the validator. In UpdateLogics the `e`/`u` split is real but `e` is reachable only on exceptions; an HTTP failure of the commit lookup is classified `u` and therefore *does* update Oxide (row 5), which contradicts the cautious intent.

**D14 (L) Dead / misleading code.** `shithappens` (third array element) is never true on return; `"An error occurred while completing update logic function."` (1643, 3548) is unreachable. `dict == null` checks (951, 1027, 1069, 1148) are unreachable. `WriteTryNumber`'s catch is unreachable. Redundant `else` at 799–803; `sizeValue == 0 ||` at 809; `return` at 874 practically unreachable. Marker key fallbacks never produced by the app. `Unload` null checks duplicated. `UpdaterMarkerFileName` / `UpdaterLockFileName` are configurable in the plugin but hardcoded in the app — changing them breaks the protocol.

**D15 (L) Lock handling.** Lock is never removed by the app (4.2), so the warning is a false alarm on every run and the lock offers no protection once the server has booted. If two app instances are ever started (e.g. `feedme.testrun` or the custom-wipe trigger during a countdown), the second one writes a failed marker over the first one's and runs the start script while the first is still updating.

**D16 (L) Thread-safety.** `pending*`/`temp*` flags, `cachedRemote*`, `lastOxide*`, `currentHttpBackend` are plain fields written from worker threads (1639–1640, 1668–1671, compat function, Discord task) and read on the main thread; `currentHttpBackend` is globally flipped and restored by concurrent callers. `Puts` and `timer.Once` are called from worker threads **(their thread-safety in Oxide is unverified)**. `enablePlugin` can be set false by the self-update task (2872) and then overwritten by Init at 850–851.

**D17 (L) Regex-based marker parsing.** No JSON unescaping (`\u0027` etc. leak into Discord text); JSON `null` becomes the string `"null"`; `update_id` from the file is used unsanitised in the archive file name; `File.Move` fails if the destination exists; the `markers` folder is never pruned. The raw marker is echoed to the console.

**D18 (L) Init side effects.** A bogus default `ServerStartScript` path is written into the config when the field is empty (754–755) and later passed to the app as `-start_script`. `NextWipeRandomSeed` regenerates the seed on every load, and in-memory sanitisation is persisted as a side effect of unrelated config writes. `UseScheme=false` set in memory on a bad scheme is persisted by the next `feedme.set`. A corrupt datafile is treated as empty and the next write drops all other keys (=> init flow on next boot).

**D19 (L) Daily-restart guard uses exact equality** (3254): if the plugin loads inside the window (remaining < `MinutesBeforeRestart`) or a timer tick skips the exact minute, checks are not suppressed that day. `feedme.status on` re-enables checks after suppression, and prints "periodic checks enabled" even when `enablePlugin` is false.

**D20 (L, unverified) Plugin hot-reload.** If Oxide calls `OnServerInitialized` for plugins loaded after server start, `oxide.reload FeedMeUpdates` re-runs the startup scan and can quit a populated server with no countdown.

---

## 6. Open questions

1. What is `MaxAttempts` meant to bound — consecutive failures of the same target, or any failures? Should it also gate the init flow, `StartupScan` and wipes, and how is the block lifted (new remote version, manual command, restart)?
2. Should detection really be skipped entirely when the Oxide release feed is unreachable, or should a server-only decision still be possible (subject to `OnlyServerProtocolUpdate`)?
3. Intended meaning of `OnlyServerProtocolUpdate`: code = "never update the server alone; wait until a new Oxide release exists"; README = "only when the server changes and Oxide protocol changes".
4. Scheme semantics: is the exact server match (a rule without `server` not firing when the server also changed) intended? Should `e` be distinct from `u`? Should actions be allowed to target unchanged components?
5. Row 5 vs row 7: is "protocol unknown => update Oxide" the desired default, given that network failures land in `u`?
6. Should `RemoteSteamBuildID` be tied to the `update_id` that created it and cleared on failure/abort? Could v2 read the installed build id directly (e.g. from `steamapps/appmanifest_258550.acf`, which the app already knows about at app:2277) and drop the belief/hand-over mechanism (`steambid.temp`) altogether?
7. Who owns the lock: should the app remove it on exit, and should a stale lock block or merely warn?
8. Should the startup scan / init flow skip the countdown even when players are connected (plugin reload case)?
9. Is `feedme.set` an official command (absent from the README)?
10. Are the legacy marker keys (`updateId`, `error`, `wipe_result`, `wipeinfo`) and the configurable marker/lock file names still needed?
