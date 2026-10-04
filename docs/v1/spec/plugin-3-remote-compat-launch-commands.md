# P3 — Plugin spec: remote helpers, Oxide lookup, protocol compatibility, countdown, updater launch, commands

Source: `FeedMeUpdates.cs` (plugin v1.6.5, `[Info("FeedMeUpdates", "frankie290651", "1.6.5")]`, line 18), lines 1682-2680, plus the helpers those lines depend on (cited where used). Line numbers refer to that file unless prefixed `App:` (`Application/Program.cs`).

Conventions: "main thread" = Unity/Oxide main thread (hooks, commands, timer callbacks). "worker" = `Task.Run` thread pool thread. `Puts` = Oxide console/log output, automatically prefixed with `[FeedMeUpdates]`.

---

## 0. Context needed to read this spec

### 0.1 Config (`oxide/config/FeedMeUpdates.json`, class `ConfigData`, lines 24-73)

All keys and defaults (exact JSON property names, PascalCase):

| Key | Type | Default | Notes relevant to this range |
|---|---|---|---|
| ServerDirectory | string | "" | root of Rust server; base for Oxide DLL lookup, cwd of updater (some launch modes) |
| SteamCmdPath | string | "" | full path to steamcmd executable; executed directly |
| UpdaterExecutablePath | string | "" | may be relative; resolved against ServerDirectory if that candidate exists |
| ShowUpdaterConsole | bool | false | selects launch mode (see 5.3) |
| ServerStartScript | string | "" | Init replaces empty value with `C:\rust-server\start_server.bat` (Windows) / `/home/rust/rust-server/start_server.sh` (else) when `RustOnService` is false, and persists it (752-756) |
| RunServerScriptHidden | bool | false | passed inverted as `-showserverconsole` |
| ServerTmuxSession | string | "" | passed as `-servertmux` |
| RustOnService | bool | false | `-isService` |
| ServiceName / ServiceType | string | "" | `-serviceName` / `-serviceType` |
| HttpTimeoutMs | int | 3000 | Init forces `<=0` to 3000 (751) |
| StartupScan | bool | false (README example says true) | |
| MaxAttempts | int | 0 | 0 = unlimited |
| CheckIntervalMinutes | int | 10 | `Math.Max(1, …)` at use (1602) |
| CountdownMinutes | int | 5 | `Math.Max(1, …)` at use (2069) |
| UpdatePlugins | bool | false | `-update_plugins yes|no` |
| OnlyServerProtocolUpdate | bool | false | |
| UseScheme / SchemeFile | bool / string | false / "" | |
| DailyRestartTime / MinutesBeforeRestart | string / int | "" / 60 | |
| DiscordNotificationsEnabled / DiscordWebhookUrl | bool / string | false / "" | webhook URL is a secret |
| BeforeForceWipeRange | int | 15 | |
| CustomWipeDay / CustomWipeTime | string | "" | |
| ServerIdentity | string | "" | |
| NextWipeServerName / NextWipeServerDescription / NextWipeMapUrl | string | "" | base64-encoded on the CLI |
| NextWipeLevel / NextWipeSeed / NextWipeMapsize | string | "" | |
| NextWipeRandomSeed | bool | false | |
| NextWipeKeepBps | bool | true | |
| NextWipeResetRustPlus / NextWipeDeletePlayerData | bool | false | |
| NextWipeDeletePluginDatafiles | string | "" | JSON array as string |
| UpdaterMarkerFileName | string | "updateresult.json" | |
| UpdaterLockFileName | string | "updating.lock" | |
| MarkersSubfolder | string | "markers" | |

### 0.2 Runtime state used by this range (lines 91-155)

- `lastOxideCompat` (bool?), `lastOxideProto`, `lastCompatNote` — single-slot cache of the last compatibility result (94-96).
- `debugging = false`, `quitBeforeExecOnDebugging = true` — hard-coded developer switches, not configurable (98-99).
- `enablePlugin` — false when `MaxAttempts != 0 && trynumber >= MaxAttempts` (850) or when the self-update task throws (2872).
- `SystemUpdating` — true while the app self-update promotion task runs at boot (2849-2873).
- `pendingServerUpdate`, `pendingOxideUpdate` — decisions of the last detection cycle, written on main thread (1650-1651).
- `localOxideVersion` (from DLL, 867), `localProtocol` (`server.Protocol.ToString()`, 859), `LocalServerVersionString` (`server.Version`, 868), `LocalSteamBuildID` (datafile / `steambid.temp`).
- `cachedRemoteBuild`, `cachedRemoteOxide` — last successfully fetched remote values.
- `checkTimer` — periodic detection timer; `null` means "checks OFF".
- `countdownActive` (volatile), `isFetchingRemote` (volatile).
- `cachedPluginVersion` — `Version.ToString()` or "unknown" (741).
- `trynumber` — persisted failure counter.
- `tmuxAvailable`, `gnomeAvailable` — detected once at Init on Linux only (888-890, 3328-3362) via `sh -c "command -v <name> >/dev/null 2>&1 && printf 1 || printf 0"`; gnome = `gnome-terminal` OR `gnome-terminal-server` found on PATH. Always false on non-Linux.
- `lastNetworkFail`, `lastOxideStatusCode` — last Oxide release fetch outcome, shown by `feedme.status`.
- `UAValue = "FeedMeUpdates"` — User-Agent for the WebClient backend.
- `updaterValidAtInit` — result of the one-time updater validation in Init (764-782).
- `nextUpdateIsForce` — set true by the wipe timer when inside the force-wipe window (4008, 4019); never reset.
- `pluginDatafilesToRemoveString` — computed only in Init from `NextWipeDeletePluginDatafiles` (793).

### 0.3 Datafile

`oxide/data/FeedMeUpdatesData.json` (`DataFileName = "FeedMeUpdatesData"`, line 87), a flat `Dictionary<string,string>`. Keys: `LocalSteamBuildID`, `RemoteSteamBuildID`, `TryNumber`. Read/write helpers swallow all exceptions (1081-1096); a read error returns an EMPTY dictionary, so the next write silently drops the other keys.

### 0.4 HTTP wrapper behaviour that this range depends on (lines 157-334)

`ResilientHttpGetSync(url, headers, timeoutMs)`:
- Effective timeout = `timeoutMs` if `>0`, else `HttpTimeoutMs` if `>0`, else 3000 ms.
- Runs the request in `Task.Run` and blocks the CALLING thread with `task.Wait(timeout)`. On timeout returns `(-1, null)`; the request itself is NOT cancelled. Any exception returns `(-1, null)`.
- Backend selection: `currentHttpBackend` starts `Unknown`. First use fires `webrequest.Enqueue("https://api.github.com/")` and a 1500 ms delayed task; either one sets the backend to `WebRequest`. It becomes `WebClient` only if `Enqueue` throws synchronously, or if a WebRequest GET throws. So: calls made within about 1.5 s of the very first HTTP use go through WebClient; afterwards everything goes through Oxide `webrequest` unless a caller forces WebClient.
- WebRequest backend: `webrequest.Enqueue(url, null, cb, this)` — the `headers` parameter is ignored (no custom User-Agent). The callback then does `timer.Once(0f, …)` before completing the task.
- WebClient backend: `User-Agent: FeedMeUpdates`, plus caller headers (User-Agent never overridden); success always reports status 200; `WebException` yields the HTTP status and body, or `-1`; other exceptions yield `(-1, ex.Message)`.

No request in this range sends an Authorization header; all GitHub calls are anonymous (60 requests/hour/IP limit).

---

## 1. Remote Rust build id

### 1.1 `GetRemoteRustBuild()` (1685-1717)

Purpose: return the latest public-branch build id of the Rust Dedicated Server.

- Precondition: `SteamCmdPath` non-empty and `File.Exists`. Otherwise `Puts("steamcmd not found: " + path)` and return `null` (1689-1693).
- Process: `FileName = SteamCmdPath`, `Arguments = "+login anonymous +app_info_print 258550 +quit"` (exact string, 1698). App id `258550` = Rust Dedicated Server. `UseShellExecute=false`, `CreateNoWindow=true`, stdout AND stderr redirected. No working directory set (inherits the server's cwd). No environment changes.
- Reads all of stdout with `ReadToEnd()` (blocking), THEN `proc.WaitForExit(60000)` (result ignored), then returns `ParseBuildId(output)`. stderr is never read. Exit code is never checked. The process is never killed.
- Errors: any exception is logged (`"GetRemoteRustBuild error: " + ex.Message`) and RE-THROWN (1712-1716). A parse failure returns `null` without logging.
- Same command line is used by the app for its own check (`App:1966`), and the app updates with `+force_install_dir "<dir>" +login anonymous +app_update 258550 validate +quit` (`App:2199`).

Branch: only `public`. Staging/other branches are not supported (README line 46).

### 1.2 `ParseBuildId(steamOutput)` (1720-1740)

Text-based parse of the VDF dump printed by `app_info_print`:
1. null/empty returns `null`.
2. Find first occurrence of `"branches"` (with quotes, `OrdinalIgnoreCase`). Not found returns `null`.
3. First `{` after it; extract the brace-balanced block (`ExtractBalancedBlock`).
4. Inside that block find first `"public"` (with quotes, case-insensitive); first `{` after it; extract balanced block.
5. Regex on the public block: `"buildid"\s*"([0-9]{5,12})"` with `IgnoreCase`. Return group 1 or `null`.

`ExtractBalancedBlock(text, openIndex)` (1743-1760): counts `{`/`}` from `openIndex`; returns the substring including both braces when depth returns to 0; `null` if unbalanced or if `text[openIndex] != '{'`. It does not understand quoted strings (a brace inside a quoted VDF value would miscount).

Expected input shape:
```
"branches"
{
    "public"
    {
        "buildid"   "12345678"
        "timeupdated" "..."
    }
    "staging" { ... }
}
```

### 1.3 How the result is used (callers)

- Default logic `UpdateLogics` (1452-1486): if `pendingServerUpdate` is already true, steamcmd is NOT called again (server is considered changed). Otherwise `null/empty` means "server not changed" (silent). Non-empty is stored in `cachedRemoteBuild`; if `LocalSteamBuildID` is empty an exception `"LocalSteamBuildID empty"` is thrown; server changed iff `remoteBuild != LocalSteamBuildID` (ordinal string inequality, not numeric greater-than).
- Scheme logic `SchemeResult` (1276-1300): same comparison, but always calls steamcmd.
- Order within a cycle: Oxide release is fetched FIRST; if that fails the cycle returns "no updates" and steamcmd is not run at all (1426-1429, 1252-1255).
- When a server update is pending the remote build id is written to datafile key `RemoteSteamBuildID` (1654-1657, 2365-2368, 3566-3569); it is promoted to `LocalSteamBuildID` on next boot only if the marker says success (1143-1155).

---

## 2. Remote Oxide version

### 2.1 `GetRemoteOxideVersion()` (1763-1797)

- GET `https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest` via `ResilientHttpGetSync(url, null, timeout)`; timeout = `HttpTimeoutMs` if `>0` else 3000.
- status `-1`: `lastNetworkFail=true`, `lastOxideStatusCode=-1`, `Puts("[FeedMeUpdates] Oxide fetch timeout.")`, return `null`. (Also covers any non-HTTP failure, not only timeouts.)
- status `!= 200` or empty body: `lastNetworkFail=true`, `lastOxideStatusCode=status`, `Puts("[FeedMeUpdates] Oxide fetch HTTP {status}.")`; if 403 also `Puts("[FeedMeUpdates] Possible GitHub rate limit.")`; return `null`.
- Success: `lastNetworkFail=false`, `lastOxideStatusCode=200`. Tag extracted by regex (no JSON parser): `"tag_name"\s*:\s*"v?([^"]+)"` (`IgnoreCase`). A leading `v` is stripped. Returns the tag (e.g. `2.0.6543`), or `null` when the regex does not match (in that case `lastNetworkFail` stays false).
- Exception: `lastNetworkFail=true`, `lastOxideStatusCode=0`, logged, re-thrown.
- The message strings already contain `[FeedMeUpdates]` and `Puts` adds the prefix again (cosmetic double prefix).

Use by callers: `null/empty` aborts the whole detection cycle with "no update" (1426, 1252). Non-empty goes to `cachedRemoteOxide`. "Oxide changed" iff `NormalizeVersionString(remote) != NormalizeVersionString(localOxideVersion)` (string inequality, so a remote that is OLDER than local also counts as a change).

### 2.2 `GetOxideVersionFromDll()` (1804-1835)

Called once in Init (867). Probes, in order, the first existing file among:
1. `<ServerDirectory>/oxide/managed/Oxide.Rust.dll` (does not exist in a standard install)
2. `<ServerDirectory>/RustDedicated_Data/Managed/Oxide.Rust.dll` (the real location)
3. `<ServerDirectory>/oxide/Oxide.Rust.dll`
4. `<ServerDirectory>/Oxide.Rust.dll`

For each existing file: `FileVersionInfo.GetVersionInfo`, take `ProductVersion` else `FileVersion`; if non-empty return `NormalizeVersionString(raw)`. Per-file exceptions are swallowed and the next candidate is tried. Returns the literal string `"unknown"` if nothing worked. With empty `ServerDirectory` the paths are relative to the process cwd.

The value is never refreshed at runtime (fine, because a new Oxide requires a restart).

### 2.3 `NormalizeVersionString(v)` (1838-1848)

- null/empty returned unchanged.
- First match of `\d+\.\d+\.\d+` is returned (so `2.0.6543.0` and `2.0.6543+abc` both give `2.0.6543`).
- Else cut at first `+` (index > 0), else at first `-` (index > 0), else return unchanged.

---

## 3. Protocol compatibility check

### 3.1 `CleanRef(s)` (1855-1867)

Removes every `char.IsControl` character and the zero-width characters U+200B, U+200C, U+200D, U+FEFF (BOM), U+2060, then `Trim()`. Applied only to the Oxide tag inside `GetOxideCompatibilityInfo`.

### 3.2 `GetOxideCompatibilityInfo(oxideTag, out localProto, out oxideProto, out note)` (1870-2050)

Meaning of return value: `true` = remote Oxide release targets the same Rust protocol as the running server; `false` = protocol changed; `null` = unknown/error. `note` explains why.

Step by step:
1. `localProto = localProtocol` (the running server's `server.Protocol`, e.g. `2580.270.1` — three dot-separated numbers).
2. Empty tag: `note="no oxide tag"`, return `null`. Empty local protocol: `note="local protocol unknown"`, return `null`. (These two returns do not touch the cache.)
3. `oxideTag = CleanRef(oxideTag)`.
4. Cache: if `cachedRemoteOxide` is non-empty AND `oxideTag == cachedRemoteOxide` AND `lastOxideCompat.HasValue`, return the cached `lastOxideCompat` / `lastOxideProto` / `lastCompatNote` with no network I/O (1881-1886). `null` results are never served from cache (always re-queried).
5. Variants to try: `[tag, "v"+tag]` if the tag does not start with `v`/`V`, else `[tag, tag-without-first-char]`.
6. Forces `currentHttpBackend = WebClient` for the duration and restores the previous value in `finally` (1892-1893, 2046-2049). `timeout = HttpTimeoutMs` (0 falls back inside the wrapper).
7. For each variant, in order, until a commit JSON is obtained (each variant wrapped in try/catch; exceptions are only logged when `debugging`):
   a. GET `https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{UrlEncode(variant)}` — a tag name is accepted as a git ref. 200 with body: use it.
   b. Else GET `https://api.github.com/repos/OxideMod/Oxide.Rust/releases/tags/{UrlEncode(variant)}`; on 200 extract `"target_commitish"\s*:\s*"([^"]+)"` (IgnoreCase); if found GET `https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{UrlEncode(commitish)}`; 200 with body: use it.
   Worst case 6 sequential blocking requests, each up to `HttpTimeoutMs`.
8. No commit JSON: `note="no commit info"`, cache set to (null, null, note), return `null`.
9. Commit message: first match of `"message"\s*:\s*"([^"]+)"` (IgnoreCase) in the raw JSON; the capture is passed through `WebUtility.HtmlDecode` (not JSON-unescaped, so `\n` stays as two characters backslash+n).
10. Protocol extraction, first hit wins:
    a. in the message: `protocol[^\d\n\r]{0,10}(\d+\.\d+\.\d+)` (IgnoreCase) — the word "protocol", up to 10 non-digit non-newline characters, then `N.N.N`.
    b. else in the message: any `(\d+\.\d+\.\d+)`.
    c. else in the WHOLE commit JSON: regex (a) again (1978-1982). The commit JSON of this endpoint includes file names and diff patches.
11. Still nothing: heuristic `ScoreCommitForRustProtocolFallback(message)` (see 3.3). If `score >= 6`: `note = "[UpdateLogics Cycle] Protocol update inferred from commit message (fallback, score={score})"`, cache = (false, "unknown", note), return `false`. Else `note="oxide protocol not found"`, cache = (null, null, note), return `null`.
12. `oxideProto == localProto` (ordinal string equality): `note="protocols match"`, cache (true, proto, note), return `true`.
13. Otherwise `note="protocols differ"`, cache (false, proto, note), return `false`.
14. Any exception: `note = "exception: " + ex.Message`, cache (null, null, note), return `null`.

Exact `note` values (external contract for the callers that classify them): `"no oxide tag"`, `"local protocol unknown"`, `"no commit info"`, `"oxide protocol not found"`, `"protocols match"`, `"protocols differ"`, `"[UpdateLogics Cycle] Protocol update inferred from commit message (fallback, score=N)"`, `"exception: …"`.

How callers use it:
- `UpdateLogics` (1488-1527) maps: `true` to flag `s` (same), `false` to `c` (changed); `null` with note starting with `exception` or containing `failed`/`error`/`lookup failed` to `e` (error); any other `null` to `u` (unknown). Decision (1539-1556): Oxide changed + server changed: update BOTH. Oxide changed + server NOT changed: update Oxide only if `s` or `u`; if `c` or `e` do nothing (wait for the Rust build).
- `SchemeResult` (1272-1273) only keeps `sameProtocol` and `protocolUnknown = (result == null)`; it cannot distinguish `u` from `e` (both scheme flags are evaluated under `protocolUnknown`, 1316-1347).
- `StartUpdaterExecutable` (2146-2151) calls it again only to compute `protocolChanged` for the Discord start message.

### 3.3 `ScoreCommitForRustProtocolFallback(msg)` (3439-3505)

Empty message scores 0. Message lower-cased; each pattern contributes once.
- Strong (sets `strongHit`): `patch\s+for.*rust\s+update` +7; `\bprotocol\s+update\b` +8; `\brust\s+protocol\b` +8; `\bafter\s+rust\s+update\b` +7; `\bfollowing\s+rust\s+update\b` +7; `\brust\s+update\b` +6.
- Medium (always): `\bupdate\s+for\s+rust\b` +4; `\bfix\s+for\s+rust\b` +3; `\brust\s+patch\b` +3; `\bcompatib` +2; `\bserver\s+update\b` +2; `\bgame\s+update\b` +2.
- Weak (only when no strong hit): `\bpatch\b` +1; `\bupdate\b` +1; `\bfix\b` +1; any English month name +1; `\bwipe\b` +2.
- Threshold used by the caller: `>= 6` means "protocol changed".

Rationale (author comment 1986): Oxide commit messages for a forced-wipe patch often do not contain the protocol number (typical text: "Patch for <Month> Rust update").

---

## 4. Countdown

### 4.1 `BeginUpdateCountdown(remoteBuild, remoteOxide)` (2056-2118)

Called on the main thread at the end of a detection cycle when `pendingServerUpdate || pendingOxideUpdate` (1659-1662).

- If `countdownActive`: `Puts("Countdown already active; skipping new countdown.")`, return.
- If `SystemUpdating`: `Puts("App update is not over. Skipping cycle.")`, return.
- `minutes = Math.Max(1, CountdownMinutes)`.
- Immediately: chat broadcast to all connected players `Notice: update detected. Server will restart to apply updates in {minutes} minute(s).` and console `Countdown started: {minutes} minute(s) — server={updateServer} oxide={updateOxide}` (em dash), where `updateServer = pendingServerUpdate || remoteBuild non-empty`, `updateOxide = pendingOxideUpdate || remoteOxide non-empty` (these two locals are used for the log line ONLY).
- `countdownActive = true`.
- `timer.Once(60f, tick)`; each tick decrements `remaining`:
  - `remaining > 0`: broadcast `ATTENTION: server will restart for update in {remaining} minute(s).` or, when `nextUpdateIsForce`, `ATTENTION: server will restart for monthly force wipe in {remaining} minute(s).`; re-arm `timer.Once(60f, tick)`.
  - `remaining <= 0`: `remoteBuildArg = pendingServerUpdate ? remoteBuild : null`, `remoteOxideArg = pendingOxideUpdate ? remoteOxide : null`; call `StartUpdaterExecutable(updateServer: pendingServerUpdate, updateOxide: pendingOxideUpdate, remoteBuildArg, remoteOxideArg)` using the pending flags AS THEY ARE AT THAT MOMENT; `Puts("Countdown end: scheduling graceful save+quit...")`; `PerformSaveAndQuitGracefully()`; `countdownActive = false`.
- Total delay = `minutes` x 60 s. With `minutes = 1` players see only the initial notice.
- `BroadcastToPlayers` (3376-3384) sends `IPlayer.Message` to `players.Connected` and logs `Broadcast: {message}`.
- There is no cancel path. Unloading the plugin destroys Oxide timers, leaving `countdownActive` irrelevant.
- The initial notice always says "update" even in the force-wipe window.

### 4.2 `PerformSaveAndQuitGracefully()` (3401-3420)

Two chained Oxide timers (0 s no-op, then 0.5 s): `Puts("FeedMeUpdates: quit...")` and `ConsoleSystem.Run(ConsoleSystem.Option.Server, "quit")`. No explicit `server.save`; Rust's `quit` saves on shutdown. No kick message. It is unconditional: it does not know whether the updater was actually launched.

---

## 5. Updater launch

### 5.1 `StartUpdaterExecutable(updateServer, updateOxide, overrideUpdateId=null, overrideWhat=null, remoteBuildArg=null, remoteOxideArg=null)` (2121-2292)

Call sites and argument sets:

| Flow | Lines | updateServer/updateOxide | update_id | what | remote_build / remote_oxide |
|---|---|---|---|---|---|
| Init (LocalSteamBuildID empty at OnServerInitialized) | 3518 | true/true | `init` | `both` | forced `no` / `no` |
| StartupScan | 3577 | per decision | random 8 digits | `both`/`server`/`oxide` | build or `no` / tag or `no` |
| Countdown end | 2095 | pending flags | random 8 digits | computed | idem |
| `feedme.testrun` | 2375 | true/true | `testrun` | `both` | fetched values (may be empty) |
| Custom wipe | 4033 | false/false | `wipe` | `none` | `no` / `no` |

Sequence:
1. `!updaterValidAtInit`: `Puts("Cannot launch updater: updater executable failed validation at init.")`, admin broadcast `Updater launch aborted: invalid updater executable detected at init.`, return.
2. `updateId` = override, else `new Random().Next(0, 100000000).ToString("D8")` (8 decimal digits, zero padded). The app accepts exactly `init`, `testrun`, `wipe` (case-insensitive) or `^\d{8}$`; anything else makes the app exit without action (`App:251-270`).
3. `what` = override, else `both` / `server` / `oxide` / `none`.
4. If `updateOxide` and a tag is present, call `GetOxideCompatibilityInfo` (blocking, main thread) and set `protocolChanged = (result == false)`.
5. Developer mode: when `debugging` is true and `quitBeforeExecOnDebugging` is true, the method calls `PerformSaveAndQuitGracefully()` and returns WITHOUT launching (2154-2162).
6. `NotifyDiscordUpdateStart(updateServer, updateOxide, remoteOxideVersion, protocolChanged)` — sent BEFORE the executable is validated (2164). Message formats (627-650), only if `DiscordNotificationsEnabled`:
   - no update of either kind: `[FeedMeUpdates] Starting custom wipe.`
   - otherwise `[FeedMeUpdates] Starting update: {Server & Oxide|Server|Oxide}[ (ForceWipe)]\n` + (if Oxide) `Remote Oxide version: {tag|unknown}\n` + (if Oxide) `Protocol NUMBER CHANGE` or `No protocol change`.
   - Posted as JSON `{"content":"…"}` to `DiscordWebhookUrl`, fire-and-forget on a worker.
7. Executable resolution (2166-2182): empty path: `Puts("UpdaterExecutablePath not configured.")` + admin broadcast `Updater executable not configured; cannot start updater.`, return. Relative path: if `<ServerDirectory>/<path>` exists use it. Not existing: `Puts("Updater executable not found: " + exePath)` + admin broadcast `Updater executable not found; check UpdaterExecutablePath.`, return.
8. Remote arg values (2184-2185): if update_id is `init`: `no`. Else `-remote_build` = `updateServer ? (remoteBuildArg ?? "") : "no"`; `-remote_oxide` = `updateOxide ? (remoteOxideArg ?? "") : "no"`.
9. `ValidateAndBuildArgsForStart` (5.2). Failure: `Puts("Argument validation failed: " + reason)`, admin broadcast `Updater launch aborted: invalid arguments.`, return.
10. `Puts($"Preparing to launch updater: {exePath} {args} (ShowUpdaterConsole={…})")` — the full command line is logged.
11. Launch (5.3). No check of the returned `Process`, no wait, no handshake.
12. Any exception: `Puts("Error launching updater: " + ex.Message)`, admin broadcast `Error launching updater. Check permissions and executable path.`.
13. After the try/catch (reached on normal completion or exception, NOT on the early `return`s above): `WriteTryNumber()` persists the current `trynumber` to datafile key `TryNumber` (no increment here; increment happens at next boot when the marker says `failed`, 1113-1116).

The method returns `void`; callers cannot tell whether a launch happened and always quit the server afterwards.

`BroadcastToAdmins` (3387-3398): messages connected players that are server/admin or have `feedme.run`; logs `Broadcast(admins): {message}`.

### 5.2 Command-line contract (`ValidateAndBuildArgsForStart`, 3700-3785)

Arguments are emitted in exactly this order, each as `-key value` (two tokens). `-update_id` MUST be first (the app reads it positionally, `App:173-188`); the remaining keys are parsed as key/value pairs and looked up lower-cased by the app (`App:192-218`).

| # | Arg | Value |
|---|---|---|
| 1 | `-update_id` | `init` / `testrun` / `wipe` / 8 digits |
| 2 | `-what` | `both` / `server` / `oxide` / `none` |
| 3 | `-update_plugins` | `yes` / `no` |
| 4 | `-server_dir` | ServerDirectory |
| 5 | `-steamcmd` | SteamCmdPath |
| 6 | `-start_script` | ServerStartScript |
| 7 | `-remote_build` | build id, `no`, or empty |
| 8 | `-remote_oxide` | tag, `no`, or empty |
| 9 | `-isService` | `1` / `0` |
| 10 | `-serviceType` | ServiceType |
| 11 | `-serviceName` | ServiceName |
| 12 | `-showserverconsole` | `0` if RunServerScriptHidden else `1` |
| 13 | `-servertmux` | ServerTmuxSession |
| 14 | `-isforce` | `1` if `nextUpdateIsForce` else `0` |
| 15 | `-nextwipelevel` | NextWipeLevel |
| 16 | `-nextwipeurl` | `b64:` + base64(UTF-8 NextWipeMapUrl) |
| 17 | `-nextwipeseed` | NextWipeSeed |
| 18 | `-nextwipemapsize` | NextWipeMapsize |
| 19 | `-nextwipekeepbps` | `1` / `0` |
| 20 | `-nextwiperesetrustplus` | `1` / `0` |
| 21 | `-nextwipedelplayerdata` | `1` / `0` |
| 22 | `-nextwipedelpluginsdata` | `pluginDatafilesToRemoveString` (computed at Init) |
| 23 | `-serveridentity` | ServerIdentity |
| 24 | `-servername` | `b64:` + base64(UTF-8 NextWipeServerName) |
| 25 | `-serverdescription` | `b64:` + base64(UTF-8 NextWipeServerDescription) |

- Base64 encoding (`EncodeIfSpecialArg`, 3612-3631) applies only to keys 16, 24, 25; an empty value is sent as `b64:`. The app decodes anything starting with `b64:` (`App:274-286`).
- Length guard (3735-3756): running total of `key.Length + value.Length`; if it exceeds 32500 when reaching `-serverdescription`, that value is replaced by an empty string and a warning is logged (`WARINING: ServerDescription too long, …`); if still over 32500 the launch fails with reason `Params too long`. The check only triggers on the `-serverdescription` key. (32500 is just below the Windows `CreateProcess` 32767-character limit.)
- Safety check on EVERY value after encoding (`IsSafeArgValue`, 3788-3794): reject NUL/CR/LF; reject any of `" ' \` ; | & $ < > ( ) { } [ ] * ? ~ %`; must match `^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$`. Failure reason `Unsafe value for {key}`. Space and backslash are allowed. Non-ASCII is rejected (hence base64 for free-text values).
- Quoting: Windows `-key "value"`; non-Windows `-key 'value'` with `'` escaped as `'\''` (`QuoteForShell`, 3852-3855). Joined by single spaces.
- The executable path itself is NOT passed through `IsSafeArgValue`.

App-side normalisation worth knowing: empty `-remote_build` / `-remote_oxide` become `no` only for 8-digit ids (`App:262-266`); `testrun` with empty `-what` defaults to `both`; if `-isService` is true the app forces `showserverconsole` false (`App:248-249`); missing `-server_dir` falls back to the app's cwd (`App:223`).

### 5.3 Launch modes (2200-2284)

Windows:
- `ShowUpdaterConsole=true`: `cmd.exe` with `Arguments = /c start "FeedMeUpdates" "<exePath>" <args>`, `UseShellExecute=true`, `CreateNoWindow=false`, cwd = directory of the exe (fallback ServerDirectory). `start` with a window title opens a new, independent console window.
- `ShowUpdaterConsole=false`: start `<exePath>` directly with `<args>`, `UseShellExecute=false`, `CreateNoWindow=true`, no redirection, cwd = directory of the exe.

Non-Windows (Linux), local helper `EscapeShellArg` identical to `QuoteForShell`:
- `ShowUpdaterConsole=true` and `gnomeAvailable`: `gnome-terminal` with `Arguments = -- bash -lc '<exec '<exePath>' <args>>'` (inner single quotes escaped as `'\''`). `UseShellExecute=false`. No cwd set.
- `ShowUpdaterConsole=true`, no gnome, `tmuxAvailable`: session name is the hard-coded `feedmeupdate` (NOT `ServerTmuxSession`).
  - `TmuxSessionExists("feedmeupdate")` runs `tmux has-session -t feedmeupdate`, 2 s timeout (kill on timeout), true iff exit code 0 (3292-3325).
  - Exists: `tmux send-keys -t feedmeupdate '<bash -lc "exec '<exePath>' <args>">' C-m` (types the command into the existing session and presses Enter).
  - Else: `tmux new-session -d -s feedmeupdate '<exec '<exePath>' <args>>'` (detached session that ends when the updater exits). The operator can watch with `tmux attach -t feedmeupdate`.
  - No cwd set.
- `ShowUpdaterConsole=true`, neither available, OR `ShowUpdaterConsole=false`: `/bin/bash` with `Arguments = -c "nohup \"<exePath>\" <args> >/dev/null 2>&1 &"`, `UseShellExecute=false`, `CreateNoWindow=true`, cwd = ServerDirectory.

In all modes the plugin does not wait; the caller then issues `quit` about 0.5 s later. The app is responsible for waiting until the server process/service has stopped.

### 5.4 Updater validation at Init (context; 3634-3697)

Sets `updaterValidAtInit` once: path non-empty, resolved (relative paths against ServerDirectory), exists, not a directory, length > 0; on Linux additionally `readlink -f` resolution and `test -x` via `/bin/sh -c` (1 s timeouts). The flag is never recomputed, even after the self-update promotes a new binary or after `feedme.set updaterexecutablepath`.

---

## 6. Commands

Registered in Init with `AddCovalenceCommand` (743-746): `feedme.testrun`, `feedme.status`, `feedme.version`, `feedme.set`. Covalence commands work from server console/RCON and from in-game chat/console.

Permission `feedme.run` registered at 740. Gate `HasPermissionOrConsole` (3365-3373): allowed if player is null, `IsServer`, `IsAdmin`, or has `feedme.run`; otherwise replies `You do not have permission to run this command.`.

### 6.1 `feedme.status [on|enable|off|disable]` (2299-2333)

- `on` / `enable` (case-insensitive): `StartPeriodicCheck()` (1596-1606: returns silently if `!enablePlugin`; otherwise destroys and recreates `checkTimer` with interval `Math.Max(1, CheckIntervalMinutes)` minutes, logs `Periodic checks every {n} minutes started.`, and schedules one detection after 2 s). Reply `FeedMeUpdates: periodic checks enabled.`; log `Periodic checks enabled via command.`.
- `off` / `disable`: destroy `checkTimer`, set to null. Reply `FeedMeUpdates: periodic checks disabled.`; log `Periodic checks disabled via command.`. Not persisted (next boot re-enables). Does not stop a running countdown, the wipe timer (`fwTimer`) or the daily-restart timer.
- any other argument: reply `Usage: feedme.status [on|off]`.
- no argument: reply `FeedMeUpdates status: {ON|OFF}. pendingServerUpdate={bool} pendingOxideUpdate={bool} LocalSteamBuildID={id|<empty>} backend={Unknown|WebRequest|WebClient} httpTimeoutMs={n} lastOxideStatus={n} netFail={YES|NO} scheme={ON|OFF}`; log `Status requested. {ON|OFF}. pendingServerUpdate=… pendingOxideUpdate=… backend=… …` (the log line omits LocalSteamBuildID). ON/OFF reflects only `checkTimer != null`.

### 6.2 `feedme.version` (2336-2349)

Reply and log: `FeedMeUpdates v{pluginVersion} | Rust version={server.Version} | build={LocalSteamBuildID|<empty>} | protocol={localProtocol|<unknown>} | Oxide v{NormalizeVersionString(localOxideVersion)|unknown}`.

### 6.3 `feedme.testrun` (2352-2380)

No arguments, no confirmation, no countdown, no player warning.
1. Log `[{Console|playerName}] invoked testrun: immediate update (server+oxide).`
2. `pendingServerUpdate = pendingOxideUpdate = true`.
3. Synchronously on the main thread: `cachedRemoteBuild = GetRemoteRustBuild()` (steamcmd), `cachedRemoteOxide = GetRemoteOxideVersion()`; exceptions give `null`.
4. If a build id was obtained: write datafile `RemoteSteamBuildID`.
5. After 0.5 s: `StartUpdaterExecutable(true, true, "testrun", "both", build, tag)`, log `Testrun: scheduling graceful save+quit...`, `PerformSaveAndQuitGracefully()`.

It ignores `countdownActive`, `SystemUpdating`, `enablePlugin`, `isFetchingRemote`. If a remote value could not be fetched the corresponding CLI value is the empty string (the app's testrun flow then decides, `App:410`, `App:440`).

### 6.4 `feedme.set <option> <value…>` (2382-2671) — undocumented in README

- Fewer than 2 args: replies `Usage: feedme.set <option> <value>` and `Example: feedme.set startupscan true`.
- `key = args[0].ToLowerInvariant()`; `value = string.Join(" ", args.Skip(1))` (multi-word values re-joined with single spaces).
- On success: mutate the in-memory `configData`, `Config.WriteObject(configData, true)` (persists the WHOLE in-memory config, including values Init normalised or generated), log `[Config] {Console|playerName} changed: {Property} = {value}`, reply `FeedMeUpdates config updated: {Property} = {value}`.
- Unknown key: `Unknown config option: {key}`. Bad bool: `Value must be a boolean (true/false, on/off, yes/no, 1/0).` Bad int: `Value must be an integer.` Exception: `Error while changing config: {message}`. In all these cases nothing is saved.
- Bool parsing `TryParseBoolFlexible` (2743-2776): `bool.TryParse`, else (trimmed, lower-case) `1,yes,y,on,enable,enabled` = true; `0,no,n,off,disable,disabled` = false.
- Int parsing: `int.TryParse` with current culture; negatives and zero accepted; no range checks.

Keys (lower-case, exact) and the config property they set:

| Key (aliases) | Property | Type |
|---|---|---|
| serverdirectory | ServerDirectory | string |
| steamcmdpath | SteamCmdPath | string |
| updaterexecutablepath | UpdaterExecutablePath | string |
| showupdaterconsole | ShowUpdaterConsole | bool |
| serverstartscript | ServerStartScript | string |
| runserverscripthidden | RunServerScriptHidden | bool |
| servertmuxsession | ServerTmuxSession | string |
| rustonservice | RustOnService | bool |
| servicename | ServiceName | string |
| servicetype | ServiceType | string |
| httptimeoutms | HttpTimeoutMs | int |
| maxattempts | MaxAttempts | int |
| checkintervalminutes | CheckIntervalMinutes | int |
| countdownminutes | CountdownMinutes | int |
| startupscan | StartupScan | bool |
| updateplugins | UpdatePlugins | bool |
| onlyserverprotocolupdate, protocolonly | OnlyServerProtocolUpdate | bool |
| usescheme | UseScheme | bool |
| schemefile | SchemeFile | string |
| dailyrestarttime | DailyRestartTime | string |
| minutesbeforerestart | MinutesBeforeRestart | int |
| discordnotificationsenabled | DiscordNotificationsEnabled | bool |
| discordwebhookurl | DiscordWebhookUrl | string |
| beforeforcewiperange | BeforeForceWipeRange | int |
| serveridentity | ServerIdentity | string |
| customwipeday | CustomWipeDay | string |
| customwipetime | CustomWipeTime | string |
| nextwipeservername | NextWipeServerName | string |
| nextwipeserverdescription | NextWipeServerDescription | string |
| nextwipemapurl | NextWipeMapUrl | string |
| nextwipelevel | NextWipeLevel | string |
| nextwipeseed | NextWipeSeed | string |
| nextwiperandomseed, randomseed | NextWipeRandomSeed | bool |
| nextwipemapsize | NextWipeMapsize | string |
| nextwipekeepbps, keepbps | NextWipeKeepBps | bool |
| nextwiperesetrustplus, resetrustplus | NextWipeResetRustPlus | bool |
| nextwipedeleteplayerdata, deleteplayerdata | NextWipeDeletePlayerData | bool |
| nextwipedeleteplugindatafiles | NextWipeDeletePluginDatafiles | string |
| updatermarkerfilename | UpdaterMarkerFileName | string |
| updaterlockfilename | UpdaterLockFileName | string |
| markerssubfolder | MarkersSubfolder | string |

All 41 config properties are settable. String values get NO validation (no path existence, no date/time format, no map size range, no seed check, no JSON check).

What a runtime `feedme.set` does NOT do (only the raw field changes; derived state is computed once in Init):
- `checkintervalminutes`: timer keeps the old interval until `feedme.status on` or reload.
- `usescheme` / `schemefile`: scheme file is not (re)parsed.
- `dailyrestarttime` / `minutesbeforerestart`: `targetHour/targetMinute`, `DoRestartTimeCheck` and the timer are not updated.
- `nextwipedeleteplugindatafiles`: `pluginDatafilesToRemoveString` is not recomputed, so the CLI still carries the Init-time list.
- `nextwiperandomseed`: no seed is generated (generation happens in Init, 825-832).
- `nextwipemapsize` / `nextwipeseed`: Init-time range/format validation (804-847) is bypassed.
- `customwipeday` / `customwipetime`: `minutesBeforeCustomWipe` is computed once on the first wipe-timer tick and then only decremented (3999-4002), so a change after the first minute of uptime is ignored until reload.
- `updaterexecutablepath`: `updaterValidAtInit` is not recomputed.
- `maxattempts`: `enablePlugin` is not recomputed.
- Values that ARE read live: `httptimeoutms`, `countdownminutes` (next countdown), `updateplugins`, `onlyserverprotocolupdate`, `steamcmdpath`, `serverdirectory` (for CLI, not for Oxide version), discord settings, service/script settings, `beforeforcewiperange`, `showupdaterconsole`, the NextWipe* strings/bools that are passed straight to the CLI.

`GetGuiConfigSnapshot()` (2678-2741) returns all config values as a case-insensitive dictionary; it has no caller anywhere in the file.

---

## 7. Implicit knowledge and workarounds worth preserving

1. **Rust build detection needs SteamCMD, not the game.** `server.Version` is not the Steam build id; the plugin compares the Steam build id of the `public` branch of app 258550 with a locally persisted id (`LocalSteamBuildID`). The local id cannot be read from the running server, which is why the very first boot runs an `init` update whose only purpose is to let the app write `steambid.temp` (`App:476`).
2. **Anonymous login works** for app 258550 (`+login anonymous`).
3. **Oxide is checked first and gates everything**: if GitHub is unreachable no update is attempted ("skipping update for safety") — avoids updating Rust to a build that Oxide has not been patched for.
4. **Oxide changed + protocol changed + Rust unchanged = wait.** An Oxide release for a new protocol is published around force wipe before/after the Rust build; installing it on the old server build would break the server. The protocol check exists purely for this.
5. **Protocol number lives in the Oxide.Rust commit message of the release tag**, not in the release JSON; and frequently it is not there at all, hence the keyword scoring fallback.
6. **Tag naming is not trusted**: both `X` and `vX` are tried, and the release's `target_commitish` is used when the tag ref lookup fails.
7. **`CleanRef` + the hex-dump debug code** (2140-2145) show that the Oxide tag at some point contained invisible characters (BOM / zero-width) that broke URL building or comparisons.
8. **Two HTTP backends** exist because Oxide `webrequest` and Mono `WebClient` each fail in some environments (TLS on Unity Mono for WebClient; main-thread callback dependence and no custom headers for `webrequest`). GitHub's API rejects requests without a User-Agent, hence `UAValue`. The compatibility check and Discord posts explicitly force WebClient.
9. **Regex instead of a JSON parser** everywhere (tag_name, target_commitish, message, marker file) — presumably to avoid Newtonsoft dependencies/compile issues in the Oxide sandbox.
10. **The updater must outlive the server process.** Each launch mode is a detachment technique: `cmd /c start` (Windows, new console), direct child with no window (Windows), `nohup … &` via bash (Linux), a detached tmux session, or a new gnome-terminal window. With systemd the README requires `KillMode=process` so the updater in the service cgroup is not killed with the server; with NSSM specific AppExit/stop settings.
11. **`bash -lc`** (login shell) in the gnome/tmux modes loads the user profile — needed for PATH / `DOTNET_ROOT` when the framework-dependent app build is used. The `nohup` mode uses plain `-c`.
12. **Single-quote shell quoting inside `ProcessStartInfo.Arguments`** relies on Mono's shell-like argument splitting on Unix (unverified against the Mono version shipped with Rust; .NET Core's splitter does not treat single quotes specially). The rewrite should use an argument list API or a self-written splitter-independent approach.
13. **Free-text values are base64-encoded** (`b64:` prefix) and everything else is whitelisted, because the command line passes through up to three quoting layers (Mono splitter, bash, tmux/gnome-terminal).
14. **32500-character cap** on the command line (Windows limit 32767).
15. **`-update_id` first, 8-digit random id** is how the app distinguishes scheduled runs from `init` / `testrun` / `wipe`.
16. **`remote_*` = `no`** is the sentinel for "do not update this component"; empty means "unknown, decide yourself".
17. **String inequality, not ordering**, for both build id and Oxide version: any difference triggers an update (also covers rollbacks).
18. **tmux session `feedmeupdate`** is reused if it exists (send-keys) so an operator can keep a persistent window for watching updates.
19. **`quit` 0.5 s after launch**: the app waits for the server to stop; the plugin gives no extra time.

---

## 8. Defects and risks

Severity: H = can leave the server down / wrong update decision / security; M = wrong behaviour in plausible situations; L = cosmetic or unlikely.

### 8.1 Server quits even when the updater was not launched — H
`StartUpdaterExecutable` returns `void` and has seven early-exit/failure paths (2125-2130, 2157-2161, 2166-2171, 2177-2182, 2190-2195, 2286-2290, plus silent launch failure). Every caller unconditionally calls `PerformSaveAndQuitGracefully()` right after (2098, 2378, 3520, 3580, 4036). Scenario: `ServerDirectory = C:\Program Files (x86)\rust` — `(` fails `IsSafeArgValue` — "Updater launch aborted: invalid arguments." — server quits and nothing restarts it. In the init flow (3515-3521) with a restart-loop start script this becomes an endless boot/quit loop, because `MaxAttempts`/`trynumber` only count markers and none is written.

### 8.2 Compatibility cache is not keyed by tag — M
1881: the cache test is `oxideTag == cachedRemoteOxide && lastOxideCompat.HasValue`, but both detection paths assign `cachedRemoteOxide = remoteOxide` (1430, 1256) BEFORE calling the check with that same tag, so the tag comparison is always true and the stored result of a PREVIOUS tag is returned. Scenario: Oxide release A gives `false` (protocol differs, server unchanged, plugin waits); later release B for the current protocol appears; the check returns the stale `false` and B is never installed until a Rust build change or a restart.

### 8.3 Blocking calls on the main thread — M/H
- `feedme.testrun` (2362-2363) and StartupScan (3540-3542) run steamcmd and HTTP synchronously on the main thread; steamcmd can take tens of seconds (or hang, 8.5), freezing the server.
- `StartUpdaterExecutable` step 4 can do up to 6 HTTP requests x `HttpTimeoutMs` on the main thread when the cache has no non-null value.
- With the `WebRequest` backend a synchronous call from the main thread waits for a callback that Oxide delivers on the main thread (and then for `timer.Once(0f)`), so it can only end by timeout with `(-1, null)`. Consequence: `feedme.testrun` and StartupScan fail to fetch the Oxide tag whenever the backend has already settled to `WebRequest` (more than about 1.5 s after the first HTTP use, e.g. after a Discord result post at boot). StartupScan then logs "remote Oxide fetch failed; skipping update for safety". Marked **unverified**: depends on Oxide's `webrequest` callback being marshalled to the main thread, which was not checked in Oxide source here.

### 8.4 Detection cycle can change the decision during a countdown — M
The periodic check is not suspended while `countdownActive`. The countdown end uses the live `pendingServerUpdate/pendingOxideUpdate` (2085-2095) but the build/tag captured at countdown start. If a cycle in between returns "nothing" (e.g. GitHub timeout sets both flags false at 1650-1651), the countdown still ends with `StartUpdaterExecutable(false, false)`: `what=none`, both remotes `no`, Discord says "Starting custom wipe.", and the server quits for nothing. Requires `CountdownMinutes >= CheckIntervalMinutes` (or a manual `feedme.status on`). Conversely a transition false-to-true sends an empty `-remote_build`.

### 8.5 steamcmd invocation can hang or deadlock — M
1707-1708: `ReadToEnd()` on stdout blocks until steamcmd closes the pipe, so `WaitForExit(60000)` is not an effective timeout and the process is never killed. stderr is redirected but never drained (pipe-full deadlock if steamcmd writes a lot to stderr). A hung steamcmd leaves `isFetchingRemote = true` forever, silently disabling all future checks ("Remote fetch already in progress; skipping…").

### 8.6 steamcmd result reliability — M (unverified)
- No `+app_info_update 1` and no cache invalidation: steamcmd is known to print cached app info, so the build id may be stale. Not verified against current steamcmd behaviour.
- First run after a steamcmd self-update, or a steamcmd run concurrent with another instance, may print no app info: parse returns `null`, which is indistinguishable from "no update" and is not logged.
- Exit code ignored.

### 8.7 Protocol extraction is fragile — M
- 1965: `"message":"([^"]+)"` stops at the first escaped quote (`\"`) in the JSON, truncating messages that contain quotes; matches the first `"message"` key whatever it belongs to.
- 1968: `HtmlDecode` is the wrong decoder for a JSON string.
- 1973: fallback takes ANY `N.N.N` in the message (e.g. an Oxide or dependency version) as the Rust protocol, giving a confident "protocols differ" (`false`), which blocks an Oxide-only update.
- 1980: searching the whole commit JSON includes diffs/patches; a code change touching a "protocol" string followed by a version can be mis-read.
- Scoring fallback: any release whose commit message says e.g. "Rust update" is declared a protocol change (score 6) even when it is not.

### 8.8 Network failures are classified as "unknown", not "error" — M
HTTP failures/rate limits in the commit lookup produce `note = "no commit info"` (1951) which `UpdateLogics` maps to `u` (1518-1521), and `u` permits an Oxide-only update (1547). So when GitHub is rate-limiting, an Oxide release for a NEW protocol can be installed on an old server build. Only real exceptions map to `e`.

### 8.9 Shared mutable HTTP backend flag — M
1892-1893/2048 and the Discord sender (601-617) save/overwrite/restore `currentHttpBackend` without locking, from different threads (worker for detection, worker for Discord, main for launch). Interleaving can restore a stale value or leave the backend permanently on `WebClient`/`WebRequest` contrary to detection. The restore also undoes a legitimate fallback switch made inside the wrapper (210).

### 8.10 Command/privilege issues in `feedme.set` — H (security)
- Anyone with the in-game permission `feedme.run` (or in-game admin) can set `steamcmdpath` or `updaterexecutablepath` to an arbitrary existing file and get it executed as the server's OS user (`steamcmdpath` on the next check; the updater path on the next launch, since only `File.Exists` is re-checked at 2177). `serverstartscript` is likewise handed to the app. This turns a game permission into OS command execution.
- 2655-2656: every changed value is logged and echoed, including `DiscordWebhookUrl` (a secret) — it ends up in the Oxide log file. 2198 logs the full updater command line.
- No validation of any string value; several settings appear to succeed but have no effect until reload (see 6.4), e.g. `usescheme true` at runtime leaves `scheme.instructions == null`, and `SchemeResult` then returns "no update" forever (1305-1308): updates are silently disabled.
- `Config.WriteObject` persists Init-time in-memory mutations (e.g. generated random seed, cleared map size, `UseScheme=false` after a scheme parse error).

### 8.11 Executable path is not escaped — M
`exePath` is interpolated raw into `"…"` (2205, 2272, 2280) and `'…'` (2235, 2249, 2261). A path containing `'`, `"`, `$`, a backtick or (Windows) `%`/`&` breaks the command or injects shell syntax. Only argument VALUES are whitelisted.

### 8.12 Windows trailing-backslash quoting — M
Values are emitted as `-key "value"` (3769) and backslash is whitelisted. `ServerDirectory = C:\rust\` produces `"C:\rust\"`, where `\"` is an escaped quote for the Windows argv parser: the rest of the command line is swallowed into `-server_dir`.

### 8.13 gnome-terminal chosen on presence, not usability — M
2230: `gnomeAvailable` means only "binary on PATH". On a headless box (no DISPLAY/D-Bus) `gnome-terminal` starts and exits with an error; `Process.Start` succeeds, no fallback to tmux/nohup is attempted, the server quits and no updater runs. `gnome-terminal-server` alone also sets the flag although the command executed is `gnome-terminal`.

### 8.14 tmux details — L/M
- `send-keys` into an existing `feedmeupdate` session types the command into whatever is running there (an editor, a still-running previous updater).
- `tmux has-session -t feedmeupdate` uses tmux target matching (prefix/fnmatch), so a session named e.g. `feedmeupdate-old` can match (unverified).
- No working directory is set in gnome/tmux modes (differs from the nohup and Windows modes).
- `TmuxSessionExists` interpolates the name unquoted (constant today).

### 8.15 Discord start notification is unreliable and premature — L/M
Sent before executable/argument validation (2164), so "Starting update" is posted for launches that are then aborted. It is fire-and-forget and the server quits about 0.5 s later, so the post may never complete. `what=none` reached through 8.4 is reported as "Starting custom wipe."

### 8.16 Launch success is never verified — M
`Process.Start` return value is ignored; with `bash -c "nohup … &"` bash returns 0 even if the binary cannot execute (wrong architecture, missing .NET runtime). No lock-file or handshake check before quitting.

### 8.17 Reliance on `Dictionary` enumeration order — L
`-update_id` must be first, but the order comes from `Dictionary<string,string>` insertion order (3706-3733, 3769/3775), which is not guaranteed. 3747-3748 removes and re-adds a key during processing (only the last key, so order survives in practice).

### 8.18 Length guard only protects one key — L
3744: the 32500 check is evaluated only when the loop reaches `-serverdescription` (the last key). The total is therefore checked, but only the description can be dropped; an oversized server name or map URL leads straight to `Params too long`. The counter ignores quotes and separators, and the Linux per-argument limit (128 KiB) is not considered.

### 8.19 `feedme.testrun` has no guards — M
Can be run while a countdown, a detection cycle or the self-update promotion (`SystemUpdating`) is in progress, causing a second updater launch attempt or a launch while the updater binary is being replaced. No warning to players, no confirmation.

### 8.20 `feedme.status on` reports success when disabled — L
`StartPeriodicCheck` returns silently when `enablePlugin` is false (1598-1599) but the command still answers "periodic checks enabled." Also `feedme.status` derefs `configData.UseScheme` without the null guard used two tokens earlier (2329).

### 8.21 Thread-safety of Oxide APIs — L/M
`Puts`, `timer.Once` and `webrequest.Enqueue` are called from worker threads throughout (e.g. 1648, 1667, 1691, 1773, 263-265). Oxide's timer library is not documented as thread-safe (unverified). Plain non-volatile fields (`pending*`, `cached*`, `last*`) are read/written across threads.

### 8.22 Dead / developer-only code — L
- `debugging` and `quitBeforeExecOnDebugging` are hard-coded; all `if (debugging)` blocks (incl. 2140-2145 with `System.Web.HttpUtility`, an extra assembly dependency) are dead in production. With `debugging=true` the plugin quits the server without ever launching the updater.
- `GetGuiConfigSnapshot` (2678-2741) is unused.
- Locals `updateServer/updateOxide` in `BeginUpdateCountdown` (2072-2073) only feed a log line and can disagree with what is launched.
- `WriteTryNumber()` at 2291 rewrites an unchanged value; its purpose is unclear.
- 1518-1521: both branches of the note classification set `oxide_u`.
- `GetOxideVersionFromDll` candidates 1, 3, 4 do not correspond to real Oxide layouts.
- `ExtractBalancedBlock`/`ParseBuildId`/steamcmd command are duplicated in the app (`App:1966-2000`).

### 8.23 Misc — L
- Double `[FeedMeUpdates]` prefix in log lines 1773, 1780, 1781.
- 1784-1787: if the body has no `tag_name` the function returns `null` with `lastNetworkFail=false`; StartupScan then logs "no updates available" rather than a failure.
- GitHub anonymous rate limit: 1 request per cycle plus up to 6 per cycle while the compatibility result is `null`; with `CheckIntervalMinutes` of 1-5 and an unknown protocol the 60/hour limit is exceeded, which (via 8.8) is treated as "unknown".
- Random `updateId` uses a fresh `System.Random` (collisions irrelevant, but ids are not unique/monotonic for marker file names).
- Countdown text always says "minute(s)"; the initial notice does not mention force wipe.

---

## 9. Open questions

1. Should an Oxide release with a different (older) version than local really trigger an update (string inequality), or only newer versions?
2. `u` (protocol unknown) currently permits an Oxide-only update while `e` (error) blocks it; given that HTTP failures land in `u`, which behaviour is intended for rate-limit/timeouts?
3. Is the heuristic threshold (score >= 6 = protocol changed) still wanted, or should v2 obtain the protocol from a more reliable source?
4. Why does `StartUpdaterExecutable` call `WriteTryNumber()` after launching? (No increment happens there.)
5. Is the hard-coded tmux session name `feedmeupdate` part of the contract for operators, and should `ServerTmuxSession` be unrelated to it as today?
6. Should `feedme.status off` be persisted and should it also suspend wipe scheduling and a running countdown?
7. `feedme.testrun` with a failed remote fetch sends empty `-remote_build/-remote_oxide`; is "let the app decide" the intended semantics?
8. Should `feedme.set` be usable by `feedme.run` holders for path/executable keys at all, and should it apply changes live (re-validate, restart timers, re-parse scheme, recompute plugin datafile list)?
9. Is detection meant to continue during a countdown (so a later cycle can alter what the countdown launches), or should the decision be frozen when the countdown starts?
10. On NSSM-managed Windows services, does the updater started as a child of RustDedicated survive NSSM's process-tree cleanup when the server exits? (Not determinable from the code; README settings suggest it was tuned by trial.)
11. Should the countdown be cancellable (command) and should the server refuse to quit when the updater could not be started?
