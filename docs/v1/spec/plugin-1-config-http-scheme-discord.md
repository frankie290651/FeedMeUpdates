# P1 — FeedMeUpdates.cs lines 1–740: config, constants, runtime state, resilient HTTP, scheme parsing, Discord notifications

Source: `/home/claude/frankie290651/feedmeupdates/FeedMeUpdates.cs` (plugin v1.6.5). All line numbers refer to that file unless prefixed with `Program.cs:`.
Lines 1–740 were read in full. Lines outside the range were read only to establish how the in-range code is used; such statements are marked "(usage, outside range)".

---

## 0. File header and plugin identity (L1–20)

- Usings (L1–14): `System`, `System.Collections.Generic`, `System.Globalization`, `System.Diagnostics`, `System.IO`, `System.Linq`, `System.Net`, `System.Runtime.InteropServices`, `System.Text`, `System.Text.RegularExpressions`, `System.Threading.Tasks`, `Oxide.Core`, `Oxide.Core.Libraries.Covalence`, `System.Security.Cryptography`.
- Namespace `Oxide.Plugins`, class `FeedMeUpdates : CovalencePlugin` (L16–20). It is a Covalence (game-agnostic API) plugin, not a `RustPlugin`.
- `[Info("FeedMeUpdates", "frankie290651", "1.6.5")]` (L18). **External contract:** the companion app's self-update compares the GitHub release version against this `[Info(...)]` version string by reading the `.cs` file (README §11). The attribute format/position must stay parseable by whatever the app uses.
- `[Description("Highly configurable plugin for Oxide framework to orchestrate Server/Oxide/Plugins updates.")]` (L19).
- Plugin file name must be `FeedMeUpdates.cs` in `oxide/plugins` (README §6, §11); config file is therefore `oxide/config/FeedMeUpdates.json`.

---

## 1. Configuration (L22–83)

### 1.1 `ConfigData` (L24–73)

Private nested POCO, serialized by Oxide (`Config.ReadObject<ConfigData>()` / `Config.WriteObject(obj, true)`) to `oxide/config/FeedMeUpdates.json`. JSON key names are exactly the property names (no attributes). All keys are an external contract (users edit this file; `feedme.set` edits it; README §8 documents it).

| Key | Type | Default | Line | Meaning / use (usage mostly outside range) |
|---|---|---|---|---|
| `ServerDirectory` | string | `""` | 26 | Root folder containing `RustDedicated(.exe)`. Base for marker, lock, `steambid.temp`, `markers/`, `FeedMeUpdates(.exe).new`, `FMU_CHECKSUMS.txt`. |
| `SteamCmdPath` | string | `""` | 27 | Full path to steamcmd. |
| `UpdaterExecutablePath` | string | `""` | 28 | Path to companion app. If relative, resolved against `ServerDirectory` at launch (L2172–2176). |
| `ShowUpdaterConsole` | bool | `false` | 29 | Launch updater in a visible console (Windows `cmd /c start`; Linux gnome-terminal / tmux session `feedmeupdate` / fallback nohup). |
| `ServerStartScript` | string | `""` | 31 | Script used by the app to restart the server. If empty and `RustOnService=false`, Init overwrites it with a platform default and persists the config (L752–756): Windows `C:\rust-server\start_server.bat`, otherwise `/home/rust/rust-server/start_server.sh`. |
| `RunServerScriptHidden` | bool | `false` | 32 | Hide script console. NOTE: README example shows `true`; code default is `false`. |
| `ServerTmuxSession` | string | `""` | 33 | tmux session name for showing the server console on Linux. |
| `RustOnService` | bool | `false` | 35 | Server runs as service. |
| `ServiceName` | string | `""` | 36 | Service identifier. |
| `ServiceType` | string | `""` | 37 | Service type hint; passed to the app as `-serviceType` (L3717). |
| `HttpTimeoutMs` | int | `3000` | 39 | Timeout for every HTTP call. Normalised to 3000 if `<= 0` at Init (L751) and again inside the HTTP wrappers (L199, L230) and `GetRemoteOxideVersion` (L1767). |
| `StartupScan` | bool | `false` | 40 | Run update detection synchronously in `OnServerInitialized`. NOTE: README example shows `true`; code default is `false`. |
| `MaxAttempts` | int | `0` | 41 | 0 = unlimited. If non-zero and datafile `TryNumber >= MaxAttempts`, plugin sets `enablePlugin=false` (L850). |
| `CheckIntervalMinutes` | int | `10` | 42 | Poll interval; clamped to min 1 (L1602). |
| `CountdownMinutes` | int | `5` | 43 | In-game countdown; clamped to min 1 (L2069). |
| `UpdatePlugins` | bool | `false` | 44 | Ask app to update uMod plugins. |
| `OnlyServerProtocolUpdate` | bool | `false` | 45 | Default-logic policy flag. |
| `UseScheme` | bool | `false` | 46 | Use scheme rule file instead of default logic. Can be flipped to `false` in memory by the parser (L569, L883). |
| `SchemeFile` | string | `""` | 47 | Path to the scheme file; used verbatim (no resolution against `ServerDirectory`). |
| `DailyRestartTime` | string | `""` | 48 | `HH:mm`; suppresses scanning before the admin's own daily restart. |
| `MinutesBeforeRestart` | int | `60` | 49 | Width of that suppression window. |
| `DiscordNotificationsEnabled` | bool | `false` | 51 | Master switch for Discord. |
| `DiscordWebhookUrl` | string | `""` | 52 | Webhook URL (a secret). |
| `BeforeForceWipeRange` | int | `15` | 54 | Minutes before official force wipe to start the wipe cycle. |
| `CustomWipeDay` | string | `""` | 55 | Custom wipe date (local). |
| `CustomWipeTime` | string | `""` | 56 | Custom wipe time (local). |
| `ServerIdentity` | string | `""` | 57 | Identity folder under `server/`. |
| `NextWipeServerName` | string | `""` | 58 | New `server.hostname` at wipe. |
| `NextWipeServerDescription` | string | `""` | 59 | New `server.description` at wipe. |
| `NextWipeMapUrl` | string | `""` | 60 | New `server.levelurl` at wipe. |
| `NextWipeLevel` | string | `""` | 61 | New `server.level`. |
| `NextWipeSeed` | string | `""` | 62 | New seed (decimal uint); blanked in memory at Init if invalid (L835–846). |
| `NextWipeRandomSeed` | bool | `false` | 63 | If true Init generates a random uint and stores it in `NextWipeSeed` in memory (L825–832). |
| `NextWipeMapsize` | string | `""` | 64 | Map size; blanked in memory at Init unless integer in 1000–6000 (L804–824). |
| `NextWipeKeepBps` | bool | `true` | 65 | Keep blueprints. |
| `NextWipeResetRustPlus` | bool | `false` | 66 | Wipe `player.token.db`. NOT reset by `ResetNextWipeValues` (L1184–1203). |
| `NextWipeDeletePlayerData` | bool | `false` | 67 | Delete player data. |
| `NextWipeDeletePluginDatafiles` | string | `""` | 68 | JSON array encoded as a string, of file names in `oxide/data`. |
| `UpdaterMarkerFileName` | string | `"updateresult.json"` | 70 | Marker written by the app. README: "leave as is". The app hard-codes `updateresult.json` (`Program.cs:1769`, `1827`), so changing this key breaks marker detection. |
| `UpdaterLockFileName` | string | `"updating.lock"` | 71 | Lock file name; plugin deletes it at boot (L1221–1232). |
| `MarkersSubfolder` | string | `"markers"` | 72 | Subfolder of `ServerDirectory` where processed markers are archived. |

`private ConfigData configData;` (L75) — single mutable instance, read from background threads without synchronisation.

Behavioural notes on config loading (usage, L748–762):
- `Config.ReadObject<ConfigData>() ?? new ConfigData()`; missing keys take the defaults above. New keys are **not** written back to disk unless some later code path calls `Config.WriteObject`.
- If reading throws (e.g. malformed JSON) the plugin logs `Error loading config: <msg>` and continues with an all-default in-memory config; the file on disk is untouched at that moment but will be overwritten by the next `Config.WriteObject(configData, true)` (L755, L1201, L2653).
- Several Init steps mutate `configData` in memory only (`UseScheme`, `NextWipeMapsize`, `NextWipeSeed`, `NextWipeDeletePluginDatafiles`). Those mutations get persisted as a side effect of any later full-object write (`feedme.set` L2653, `ResetNextWipeValues` L1201).

### 1.2 `LoadDefaultConfig()` (L78–81)

Oxide hook invoked when no config file exists (or on reset). Writes `new ConfigData()` (all defaults) with `Config.WriteObject(..., true)` (sync=true → flush to disk immediately). No other side effects. Note that on a fresh install every path is `""`, so the plugin is non-functional until the admin edits the file.

---

## 2. Constants (L85–89)

- `DataFileName = "FeedMeUpdatesData"` (L87) → Oxide datafile `oxide/data/FeedMeUpdatesData.json`, read/written as `Dictionary<string,string>` (usage, L1081–1096). Keys used (all string values) — **external contract across restarts/versions**:
  - `TryNumber` — consecutive failed-attempt counter, integer as string (L955–957, L971).
  - `LocalSteamBuildID` — Steam build id currently installed (L1028–1030, L1044).
  - `RemoteSteamBuildID` — build id being installed; promoted to `LocalSteamBuildID` after a `success` marker, then removed (L1056, L1148–1154).
- `UAValue = "FeedMeUpdates"` (L145, declared in the runtime-state region) — `User-Agent` header value on the WebClient backend.

---

## 3. Runtime state (L91–155)

All are instance fields, lost on plugin reload. None is protected by a lock except `httpBackendTested` (§4).

| Field | Type / initial | Line | Purpose |
|---|---|---|---|
| `lastOxideCompat` | `bool? = null` | 94 | Cached result of the last Oxide/Rust protocol compatibility check (`true` same protocol, `false` changed, `null` unknown). |
| `lastOxideProto` | `string = null` | 95 | Cached Oxide-side protocol string for that check. |
| `lastCompatNote` | `string = null` | 96 | Cached human note for that check. Cache is used only when `oxideTag == cachedRemoteOxide && lastOxideCompat.HasValue` (L1881) — i.e. "unknown" results are never cached and are re-fetched every cycle. |
| `debugging` | `bool = false` | 98 | Compile-time debug switch; never assigned anywhere. When true, extra `[Compat]`, `[Countdown]`, `[StartExe]`, `[UpdateLogics Cycle]` log lines. |
| `quitBeforeExecOnDebugging` | `bool = true` | 99 | Only read when `debugging` is true (L2157): then the updater is NOT launched and the server just save+quits. Never assigned. |
| `enablePlugin` | `bool = true` | 101 | Global kill switch. Set false when `MaxAttempts` exceeded (L850) or app self-update promotion throws (L2872). |
| `SystemUpdating` | `bool = false` | 103 | True while the updater app's `.new` binary is being promoted at boot; blocks countdown (L2063) and startup scan (L3524). |
| `DailyRestartCheckRepeater` | `Timer` | 105 | 60 s repeating timer for the daily-restart guard. |
| `pendingServerUpdate`, `pendingOxideUpdate` | `bool = false` | 107–108 | Committed detection result (main thread). |
| `tempPendingServerUpdate`, `tempPendingOxideUpdate` | `bool = false` | 110–111 | Scratch detection result written from the background detection thread. |
| `localOxideVersion` | `string = null` | 113 | Local Oxide version read from `Oxide.Rust.dll`. |
| `localProtocol` | `string = null` | 114 | `server.Protocol` as string. |
| `localBuildFromServerVersion` | `string = null` | 115 | `server.Version` as string. |
| `LocalServerVersionString` | `string = ""` | 117 | Copy of the above for `feedme.version`. |
| `LocalSteamBuildID` | `string = ""` | 119 | Installed Steam build id (from `steambid.temp` or datafile). Empty ⇒ "init" flow on `OnServerInitialized` (L3515). |
| `cachedRemoteBuild`, `cachedRemoteOxide` | `string = null` | 121–122 | Last remote values fetched. |
| `checkTimer` | `Timer` | 124 | Periodic detection timer; `null` ⇔ checks OFF (status command L2328). |
| `isFetchingRemote` | `volatile bool = false` | 125 | Re-entrancy guard for detection. |
| `countdownActive` | `volatile bool = false` | 126 | Re-entrancy guard for countdown. |
| `cachedPluginVersion` | `string = null` | 128 | `Version.ToString()` or `"unknown"`. |
| `trynumber` | `int = 0` | 130 | In-memory copy of `TryNumber`. |
| `tmuxAvailable`, `gnomeAvailable` | `bool = false` | 132–133 | Linux terminal capability detection results. |
| `DoRestartTimeCheck` | `bool = false` | 135 | Daily restart guard enabled. |
| `targetHour`, `targetMinute` | `int = 0` | 137–138 | Parsed `DailyRestartTime`. |
| `scheme` | `Scheme = new Scheme()` | 140 | Parsed scheme (package-private field, no modifier). |
| `lastNetworkFail` | `bool = false` | 142 | Result of last `GetRemoteOxideVersion`; shown by `feedme.status` as `netFail=YES/NO`, and used for the StartupScan log message (L3585). |
| `lastOxideStatusCode` | `int = 0` | 143 | Last HTTP status of the Oxide "latest release" call: real status, `-1` (timeout/transport), or `0` (exception). Shown as `lastOxideStatus=`. |
| `UAValue` | const `"FeedMeUpdates"` | 145 | See §2. |
| `updaterValidAtInit` | `bool = true` | 147 | Result of updater executable validation at Init; if false, launching is refused (L2125). |
| `minutesBeforeForceWipe` | `int? = null` | 149 | Force-wipe countdown state. |
| `fwTimer` | `Timer` | 150 | 60 s repeating wipe-window check (L906). |
| `nextUpdateIsForce` | `bool = false` | 151 | The pending cycle is the monthly force wipe; changes Discord start text (L642) and countdown text (L2103). |
| `minutesBeforeCustomWipe` | `int? = null` | 152 | Custom-wipe countdown state. |
| `pluginDatafilesToRemoveString` | `string = ""` | 153 | Pre-built single CLI arg from `NextWipeDeletePluginDatafiles`. |

---

## 4. Resilient HTTP wrapper (L157–381)

### 4.1 State (L159–162)

- `enum HttpBackend { Unknown, WebRequest, WebClient }`.
- `currentHttpBackend = Unknown` — **not** volatile, read/written from the main thread and thread-pool threads, and temporarily overridden by two callers (§4.9).
- `httpBackendTested` (volatile) + `httpBackendLock` — double-checked "run once" guard.

Two backends exist:
- **WebRequest** = Oxide's `webrequest.Enqueue` library (callback API, callbacks delivered on the server main thread).
- **WebClient** = `System.Net.WebClient` run on a thread-pool thread.

### 4.2 `EnsureHttpBackendDetected()` (L165–193)

- Runs once per plugin load (guarded by `httpBackendTested` + lock; flag is set **before** the probe, so a failed probe is never retried).
- Probe: `webrequest.Enqueue("https://api.github.com/", null, callback, this)` — a GET with no headers and no body. Callback ignores `code`/`response` and schedules `timer.Once(0f, …)` that sets `currentHttpBackend = WebRequest` (L174–180).
- In parallel `Task.Run`: wait 1500 ms; if backend is still `Unknown`, set it to `WebRequest` (L181–186).
- Only if `webrequest.Enqueue` throws synchronously is `WebClient` selected (L188–191).
- Net effect: the backend becomes `WebRequest` in every case except a synchronous throw. The probe's result is not evaluated. During the first ≤1.5 s (while `Unknown`) requests go through WebClient (see 4.3).
- Side effects: one unauthenticated request to `https://api.github.com/` per plugin load (counts against the GitHub rate limit of the server's IP).
- Called from a non-main thread in practice (inside `Task.Run` via `SendDiscordNotification`, or from the detection task); it calls `webrequest.Enqueue` from whichever thread got there first.

### 4.3 `ResilientHttpGetSync(url, headers = null, timeoutMs = 0)` → `(int statusCode, string body)` (L196–224)

- Calls `EnsureHttpBackendDetected()`.
- Effective timeout: `timeoutMs` if `> 0`, else `configData.HttpTimeoutMs` if `> 0`, else `3000` ms (L199).
- Runs the request inside `Task.Run` and blocks the **calling thread** with `task.Wait(effectiveTimeout)` (L216).
  - If backend == `WebRequest`: `await HttpGetViaWebRequestAsync(url)` (headers are NOT passed). If that throws: log `WebRequest GET failed, fallback WebClient: <msg>`, set `currentHttpBackend = WebClient` (sticky), retry once via `HttpGetViaWebClientAsync(url, headers)`.
  - Otherwise (`WebClient` or `Unknown`): `HttpGetViaWebClientAsync(url, headers)`.
- Returns `task.Result` if finished in time; `(-1, null)` on timeout; on any exception logs `ResilientHttpGetSync error: <msg>` and returns `(-1, null)`.
- The inner task is **not cancelled** on timeout; it keeps running and its result is discarded.
- No retries. No redirect/ETag/rate-limit handling.
- Callers (usage, outside range), all with `headers = null`:
  - `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest` (L1768), parsed with regex `"tag_name"\s*:\s*"v?([^"]+)"` (case-insensitive) (L1786).
  - `GET https://api.github.com/repos/OxideMod/Oxide.Rust/commits/{UrlEncode(tag)}` (L1905, L1929) and `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/tags/{UrlEncode(tag)}` (L1917), where tag is tried both with and without leading `v` (L1888–1890); `target_commitish` extracted with `"target_commitish"\s*:\s*"([^"]+)"`.

### 4.4 `ResilientHttpPostSync(url, headers, payload, timeoutMs = 0)` (L227–255)

Same structure as GET. WebRequest branch calls `HttpPostViaWebRequestAsync(url, payload)` (headers dropped); fallback log text `WebRequest POST failed, fallback WebClient: <msg>`; error log `ResilientHttpPostSync error: <msg>`. Only caller: `SendDiscordNotification` (L605).

### 4.5 `HttpGetViaWebRequestAsync(url)` (L258–273)

- `webrequest.Enqueue(url, null, (code, response) => timer.Once(0f, () => tcs.TrySetResult((code, response))), this)`.
- No custom headers, no timeout argument (Oxide's default request timeout applies).
- The `TaskCompletionSource` is completed from an Oxide **timer callback**, i.e. on a later main-thread frame. Only a synchronous exception from `Enqueue` faults the task.
- Status code is whatever Oxide reports (Oxide reports transport failures as a code/response pair rather than throwing, so they do NOT trigger the WebClient fallback).

### 4.6 `HttpPostViaWebRequestAsync(url, payload)` (L276–291)

- `webrequest.Enqueue(url, payload ?? "", callback, this)` — same pattern; **no HTTP method and no headers are passed** (see defect D4).

### 4.7 `HttpGetViaWebClientAsync(url, headers = null)` (L294–334)

- New `WebClient` per call. Sets `User-Agent: FeedMeUpdates`. Copies caller headers except `User-Agent` (case-insensitive); each header set is individually try/caught and silently ignored on failure (restricted headers).
- `DownloadStringTaskAsync(url)`; success ⇒ `(200, body)` — the real status code is not read, every non-throwing response is reported as 200.
- `WebException` ⇒ if `Response is HttpWebResponse`: `(realStatus, responseBodyText)`; otherwise `(-1, null)` (DNS/TLS/connect failures). Reading the error body is wrapped in a swallow-all try.
- Any other exception ⇒ `(-1, ex.Message)` — **the body field carries the error message**.
- No explicit timeout on the WebClient itself (the outer `task.Wait` is the only timeout).

### 4.8 `HttpPostViaWebClientAsync(url, headers, payload)` (L337–379)

- Same, plus `Content-Type: application/json` always set (L344); caller's `User-Agent` and `Content-Type` are ignored.
- `UploadStringTaskAsync(url, "POST", payload ?? "")`; success ⇒ `(200, body)` (so Discord's real `204 No Content` is reported as 200).
- Same error mapping as GET.
- No `WebClient.Encoding` is set, so the payload is encoded with the runtime default encoding.

### 4.9 Temporary backend override (callers)

Two callers force WebClient by saving `currentHttpBackend`, setting it to `WebClient`, and restoring it in `finally`:
- `SendDiscordNotification` (L601–618).
- `GetOxideCompatibilityInfo` (L1892–1893, L2046–2049; usage, outside range).

Reason (implicit knowledge): the WebRequest path as written cannot send headers or a POST method, and cannot be awaited from the main thread (§7, K1–K3). Discord needs `Content-Type: application/json` + POST; the GitHub commit/tag lookups are called from the main thread during launch.

---

## 5. Scheme parsing (L383–573)

### 5.1 Data model (L385–406)

- `Scheme` (private): `isValid` (default false), `SchemePath` (""), `instructions` (`List<schemeInstruction>`, default **null**).
- `schemeInstruction` (public): `isValid`, `RowNum` (1-based physical line number), `ErrorColumn` (never set — always 0), `ErrorText`, event flags `OxideUpdateEvent`, `OxideProtocolMatchEvent` (flag `s`), `OxideProtocolMismatchEvent` (flag `c`), `OxideProtocolUnknownEvent` (flag `u`), `OxideProtocolErrorEvent` (flag `e`), `ServerEvent`, and actions `UpdateServer`, `UpdateOxide`.

### 5.2 File format (external contract; README §16)

- Plain text, one rule per line: `events=action`.
- Whole line is trimmed. Empty lines and lines starting with `//` are skipped (L553). No other comment syntax; no inline comments.
- `action` (right of the **first** `=`; split limit 2, L422): case-insensitive `server` | `oxide` | `both` (L438–457).
- `events` (left): one or more event tokens joined by `+` (empty tokens dropped, each trimmed, L459).
  - `server` — server build changed.
  - `oxide(<flags>)` — Oxide changed; flags, **lowercase only**: `s` same protocol, `c` changed protocol, `u` unknown protocol, `e` error fetching protocol; whitespace inside the parentheses is ignored; regex `oxide\s*\(([^)]*)\)` case-insensitive (L480).
  - `oxide` with no (matching) parentheses ⇒ all four flags set (L481–489).

### 5.3 `ReadSchemeInstruction(int row, string rowContent)` → `schemeInstruction` (L409–527)

Pure function (no I/O). Returns an instruction with `isValid=false` and an `ErrorText` on failure. Order of checks and exact error texts:

1. Blank ⇒ `Empty instruction` (L414–418) (unreachable from `ReadSchemeFile`, which filters blanks).
2. No `=` ⇒ `Invalid instruction format, expected 'events=action'`.
3. Either side empty after trim ⇒ `Empty left or right side in instruction`.
4. Action not one of the three ⇒ `Action '<right>' is not valid (expected 'server', 'oxide' or 'both')`.
5. No tokens on the left ⇒ `No events specified on left side`.
6. For each token, in order:
   - If the token **contains** `server` (substring, case-insensitive) and no server token seen yet ⇒ `ServerEvent=true`.
   - Else if the token **contains** `oxide` (substring, case-insensitive) and no oxide token has set flags yet ⇒ parse flags as in 5.2. Errors: `Oxide parentheses present but empty` (only for literally `()`), `Unknown oxide protocol event flag: '<c>'`.
   - Else ⇒ `Unknown event specifier: '<ev>' (expected 'server' or 'oxide(...)')`.
7. Valid iff `ServerEvent || OxideUpdateEvent`; else `No valid events parsed`.
8. Any exception ⇒ `Error parsing instruction: <msg>`.

### 5.4 `ReadSchemeFile()` (L530–571)

- Called once, from `Init`, only if `configData.UseScheme` (L877–886). Exceptions thrown here (e.g. `File.ReadAllLines` I/O error) are caught by Init, which logs `Scheme reading error: <msg>`, sets `UseScheme=false` and logs `UseScheme has been disabled. Plugin is going back to default behaviour.`
- `SchemeFile` empty ⇒ log `Scheme file path not configured.` and **return** (UseScheme stays true, `scheme.instructions` stays null).
- File missing ⇒ log `No scheme file found at: "<path>"` and **return** (same).
- Otherwise: `File.ReadAllLines` (default encoding/BOM detection), set `scheme.SchemePath`, `scheme.instructions = new List`, `scheme.isValid = true`; parse each non-blank, non-`//` line; any invalid line ⇒ `scheme.isValid=false`. **All** parsed instructions (valid and invalid) are added to the list.
- Valid ⇒ log `Scheme correctly parsed. Now running on scheme behaviour.`
- Invalid ⇒ log `Unable to parse scheme file. The following instructions are invalid:` then one line per bad rule `(<RowNum>) <ErrorText>`, then `UseScheme has been disabled. Plugin is going back to default behaviour.` and `configData.UseScheme = false` (in memory only).
- The file is read only at plugin load; changing `SchemeFile`/`UseScheme` with `feedme.set` (L2508–2514) does not re-parse.

### 5.5 Rule evaluation semantics (usage, `SchemeResult` L1239–1408 — summarised because it defines what the flags mean)

Inputs computed per cycle: `OxideChanged` (normalised remote tag ≠ local version), `ServerChanged` (remote build ≠ `LocalSteamBuildID`), `sameProtocol` (`true`/`false`/`null`).
Rules are scanned top to bottom; **first matching rule wins** (`break`):
- If Oxide changed: only rules with an oxide event are considered. Protocol must match the rule's flags: `null` result matches rules having `u` **or** `e` (the two flags are indistinguishable at evaluation time); `true` matches `s`; `false` matches `c`. In addition the rule's `server` presence must equal `ServerChanged` exactly (rule has `server` ⇔ server changed). Then the rule's action sets update-server / update-oxide.
- If Oxide did not change: only rules **without** an oxide event are considered, and only when `ServerChanged && ServerEvent && UpdateServer`; result is server-only (the `oxide` part of `both` is ignored, `server=oxide` never matches).
- No match ⇒ no update. `scheme.instructions == null` ⇒ no update (L1305–1308).
- Consequence worth preserving or consciously changing: when both server and Oxide changed, a plain `server=server` rule does not fire; only an `oxide(...)+server=...` rule can.

---

## 6. Discord notifications (L575–733)

### 6.1 `EscapeJsonString(s)` (L578–585)

`null` ⇒ `""`. Replaces, in order: `\` → `\\`, `"` → `\"`, CR removed, LF → `\n`. Nothing else is escaped (tabs, other control characters, non-ASCII pass through raw).

### 6.2 `SendDiscordNotification(message)` — `async Task` (L588–624)

- No-op unless `DiscordNotificationsEnabled == true` and `DiscordWebhookUrl` non-empty.
- Payload (exact): `{"content":"<EscapeJsonString(message)>"}`. No `username`, embeds, `allowed_mentions`, etc.
- Headers dictionary `{ "Content-Type": "application/json" }` (redundant — WebClient path sets it anyway).
- Forces `currentHttpBackend = WebClient` for the duration (save/restore in `finally`).
- `await Task.Run(() => ResilientHttpPostSync(DiscordWebhookUrl, headers, payload, configData.HttpTimeoutMs))` ⇒ `POST <webhook>` with `User-Agent: FeedMeUpdates`, `Content-Type: application/json`.
- Result is logged from a `timer.Once(0f, …)` (main thread): `Discord notification sent successfully.` when 200–299, else `Error sending Discord notification: HTTP <code> - <body>`. Exceptions ⇒ `Error sending Discord notification: <msg>`.
- Fire-and-forget: both callers discard the task (`_ = SendDiscordNotification(msg)`). No retry, no 429 handling, no 2000-character truncation.

### 6.3 `NotifyDiscordUpdateStart(updateServer, updateOxide, remoteOxideVersion, protocolChanged)` (L627–650)

Called from `StartUpdaterExecutable` (L2164) just before the updater process is spawned and the server quits. No-op if Discord disabled. Message format (external-ish contract — admins may parse/recognise it):

- `updateType`: `Server & Oxide` / `Server` / `Oxide` / `No update`.
- If `No update` ⇒ exactly `[FeedMeUpdates] Starting custom wipe.`
- Else, if `nextUpdateIsForce` append ` (ForceWipe)` to `updateType`, then:
  ```
  [FeedMeUpdates] Starting update: <updateType>\n
  Remote Oxide version: <remoteOxideVersion or "unknown">\n     (only if updateOxide)
  Protocol NUMBER CHANGE | No protocol change                   (only if updateOxide; no trailing newline)
  ```
  When `updateOxide` is false the message ends with a trailing `\n` after the first line.

### 6.4 `NotifyDiscordUpdateResult(result, updateId, pluginsUpdated, pluginsList, failureReason, wiped, wipe_info)` (L653–731)

Called from `ProcessUpdaterMarkerIfPresent` (L1133) during `Init` on the boot after an updater run, with values regex-extracted from the marker (`result`, `update_id`|`updateId`, `fail_reason`|`error`, `wiped`|`wipe_result`, `wipe_info`|`wipeinfo`, `updated_plugins`). No-op if Discord disabled.

Two near-identical branches:

- `updateId == "wipe"`:
  ```
  [FeedMeUpdates] wipe finished: <RESULT upper-cased trimmed, or UNKNOWN> (ID: <updateId>)\n
  Wipe executed SUCCESSFULLY.        (if result, lower-cased+trimmed, == "success")
  Wipe FAILED.                       (otherwise)
  \nFailure reason: <failureReason>  (only on failure and non-empty)
  \nServer wiped.                    (if wiped == "yes", exact, case-sensitive)
  \nServer not wiped                 (otherwise; note: no trailing period)
  \nWipe ALERTS reported:            (if wipe_info != "")
  \n - <alert>                       (one per alert)
  ```
- otherwise:
  ```
  [FeedMeUpdates] Update finished: <RESULT or UNKNOWN> (ID: <updateId or "unknown">)\n
  Update executed SUCCESSFULLY.
  \nUpdated plugins: <a, b, c>       (success only, if pluginsUpdated && list non-empty; joined with ", ")
  Update FAILED.
  \nFailure reason: <failureReason>
  \nServer wiped. | \nServer not wiped
  \nWipe ALERTS reported: ...
  ```
- `wipe_info` wire format (contract with the app): a single string of concatenated bracketed alerts `[alert one][alert two]`. Parsed by `Split("]", RemoveEmptyEntries)`, then `TrimStart('[').Trim()` per element.
- `update_id` values (contract): `init`, `testrun`, `wipe`, or an 8-digit zero-padded random number (`Random.Next(0, 100000000).ToString("D8")`, L2132).

---

## 7. Implicit knowledge and workarounds

- **K1 – Oxide `webrequest` callbacks and `timer` callbacks run on the server main thread.** Anything that blocks the main thread waiting for them can never complete. The code works around this only partially (by running detection in `Task.Run` and by forcing WebClient for Discord and for compat lookups). A v2 design must never block the main thread on Oxide webrequest, or must use a pure thread-pool HTTP client everywhere.
- **K2 – GitHub API requires a `User-Agent`** (403 without one). The WebClient path sets `User-Agent: FeedMeUpdates` explicitly; the WebRequest path relies on Oxide's own default UA (unverified what Oxide sends).
- **K3 – GitHub unauthenticated rate limit** (60 req/h per IP). Game-hosting IPs are often shared. Per detection cycle the plugin issues 1 request for `releases/latest`, plus up to 6 for protocol compatibility whenever the result is "unknown" (not cached), plus 1 probe per plugin load. The code logs `[FeedMeUpdates] Possible GitHub rate limit.` on 403 (L1781) and treats any network failure as "do not update" (`lastNetworkFail`, "skipping update for safety", L3585).
- **K4 – Two HTTP stacks because either can be broken on a given host.** `System.Net.WebClient` inside the Unity/Mono runtime can fail (TLS/certificate store, proxy), while Oxide's webrequest is the "blessed" path but is callback/main-thread bound and awkward for headers/methods. The enum/fallback machinery exists for that reason; `feedme.status` prints `backend=<…>` for diagnostics.
- **K5 – Short, configurable timeout (`HttpTimeoutMs`, default 3000 ms) enforced by `Task.Wait`**, because some calls happen on the main thread (startup scan, `feedme.testrun`, compat check at launch) and would freeze the game server. The underlying request is abandoned, not cancelled.
- **K6 – Discord webhook specifics:** POST JSON `{"content": "..."}` with `Content-Type: application/json`; success is `204 No Content` (mapped to 200 here). Plain-text messages only.
- **K7 – `timer.Once(0f, …)` is used as "marshal to main thread"** before calling `Puts` or mutating plugin state from a background thread (L176, L265, L283, L607, L622). The convention is not applied consistently (`Puts` is also called directly from pool threads at L209, L221, L240, L252).
- **K8 – Tag naming:** Oxide release tags may or may not carry a leading `v`; the regex strips an optional `v` (L1786) and compat lookups try both variants (L1888–1890).
- **K9 – Scheme file strictness:** one bad line disables the whole scheme and falls back to default logic rather than running a partially understood policy.
- **K10 – The result notification is sent on the *next boot*** (from the marker), because the plugin is not running while the updater works and the app itself has no Discord code (verified: no "discord" string in `Program.cs`). The start notification is sent by the plugin right before it quits the server.
- **K11 – Default `ServerStartScript` is auto-filled and persisted** (L752–756) so the app always receives a non-empty start script argument when not running as a service.
- **K12 – `debugging` / `quitBeforeExecOnDebugging`** are developer switches: with both true the plugin runs the whole detection/countdown but quits without launching the updater.

---

## 8. Defects and risks

Severity: H = high, M = medium, L = low. Everything below was verified by reading the cited lines unless marked "unverified".

### High

- **D1 (H) – NullReferenceException in `NotifyDiscordUpdateResult` aborts marker processing.** L680 and L718 test `wipe_info != ""`; when the marker has no `wipe_info` key, `ExtractJsonString` returns `null` (L1120, L1206–1218), `null != ""` is true and `wipe_info.Split(...)` (L683/L721) throws. The app's restore markers omit `wiped` and `wipe_info` (`Program.cs:1771–1779` and `1829–1837`). The call at L1133 sits inside the outer try of `ProcessUpdaterMarkerIfPresent`, so the exception jumps to L1180 (`Error reading marker: …`) and skips: the Discord message, the marker dump to console, promotion logic, and — critically — moving the marker to `markers/`. Scenario: Discord enabled + an update fails and the backup is restored ⇒ the marker stays in place forever; every subsequent boot re-reads it, increments the in-memory `trynumber` (L1116) and fails again; the admin never gets the failure notification. Only occurs when `DiscordNotificationsEnabled=true` (early return at L655 otherwise).
- **D2 (H) – WebRequest backend cannot complete when the caller is the main thread.** `HttpGetViaWebRequestAsync` completes its TCS only from `timer.Once(0f, …)` (L265), which fires on a main-thread frame; `ResilientHttpGetSync` blocks the caller in `task.Wait` (L216). Main-thread callers: StartupScan in `OnServerInitialized` (L3540/3542 → L1768) and `feedme.testrun` (L2363). Once the backend has become `WebRequest` (always after ~1.5 s from the first HTTP call, see D3) these calls always time out: server frozen for `HttpTimeoutMs`, log `[FeedMeUpdates] Oxide fetch timeout.`, `lastNetworkFail=true`, and StartupScan reports `remote Oxide fetch failed; skipping update for safety`; `feedme.testrun` launches the updater with an empty remote Oxide version. It only works when that call happens to be the very first HTTP call of the plugin load (backend still `Unknown` ⇒ WebClient).
- **D3 (H) – Backend "detection" detects nothing.** L174–191: the probe callback sets `WebRequest` regardless of status code, and the 1.5 s fallback also sets `WebRequest`. `WebClient` is chosen only on a synchronous throw from `Enqueue`. There is also no runtime fallback on real failures: Oxide reports transport errors through the callback, not by throwing, so the `catch` at L207/L238 practically never runs. Result: a host where Oxide webrequest is broken but WebClient works stays broken; the "resilient" wrapper is effectively "WebClient for the first 1.5 s, WebRequest afterwards".
- **D4 (H, latent) – `HttpPostViaWebRequestAsync` is not a POST.** L281 calls `webrequest.Enqueue(url, body, callback, owner)` without a method or headers. Oxide's `Enqueue` defaults the method to GET (unverified against the installed Oxide version; in the public Oxide API the 5th parameter `RequestMethod` defaults to `GET`), and no `Content-Type` is sent. Latent because the only caller forces WebClient (L602), but any new caller, or the race in D5, would hit it.
- **D5 (H) – Unsynchronised save/restore of the global `currentHttpBackend`.** `SendDiscordNotification` (L601–618) and `GetOxideCompatibilityInfo` (L1892, L2048) each save the previous value, set `WebClient`, and restore later; the Discord one restores from a pool thread after an `await`. Concrete outcomes:
  - Boot with a marker present: the Discord result post (Init, L788→1133) is the first HTTP use. `prevBackend = Unknown` is captured *before* detection starts; the 1.5 s fallback sees `WebClient` and does nothing; depending on whether the probe callback or the `finally` runs last, the backend ends as `WebRequest` or is reset to `Unknown` for the rest of the session (which silently means WebClient). Behaviour is non-deterministic across boots.
  - Overlap of a Discord post with a compat check (both happen in `StartUpdaterExecutable`, L2149 then L2164, plus background detection) can restore a stale value in either direction.
  - While overridden, every unrelated concurrent request also uses WebClient.
  The field is also not `volatile`.

### Medium

- **D6 (M) – Headers are silently dropped on the WebRequest backend** (L206, L237, L263, L281). Any `headers` argument (auth token, `Accept`, `User-Agent`) is ignored. Currently all GET callers pass `null`, so impact is latent, but it blocks adding a GitHub token.
- **D7 (M) – Start notification can be lost.** `NotifyDiscordUpdateStart` is fire-and-forget (L649) and is immediately followed by spawning the updater and save+quit (L2095–2098, L2375–2378, L3518–3520, L3577–3580). If the process exits before the pooled POST finishes, nothing is sent and nothing is logged (the result log itself needs a main-thread timer tick). Timing relative to the quit is unverified.
- **D8 (M) – Start notification is sent before launch preconditions are checked.** L2164 precedes the checks for missing `UpdaterExecutablePath` (L2166), missing file (L2177) and argument validation (L2190). Discord says "Starting update…" although nothing starts (and the callers still quit the server).
- **D9 (M) – "init" flow sends garbage and wastes blocking requests.** `OnServerInitialized` passes `remoteOxideArg: "no"` (L3518); L2146–2151 then calls `GetOxideCompatibilityInfo("no", …)` on the main thread (GitHub lookups for tags `no` and `vno`, up to 6 requests × `HttpTimeoutMs` of main-thread blocking) and the Discord message reads `Remote Oxide version: no`.
- **D10 (M) – Missing/unconfigured scheme file silently disables all updates.** L532–542 return without clearing `UseScheme`; `scheme.instructions` stays `null`; `SchemeResult` then always returns "no update" (L1305–1308). Same for a scheme file containing only comments/blank lines (valid, zero rules). Only one log line at load indicates the problem; contrast with the invalid-line case which falls back to default logic.
- **D11 (M) – Webhook URL (a secret) is written to logs and chat.** `feedme.set discordwebhookurl <url>` logs `[Config] <who> changed: DiscordWebhookUrl = <url>` and echoes it to the caller (L2540, L2655–2656). It is also exposed via `GetGuiConfigSnapshot` (L2713). (Usage, outside range.)
- **D12 (M) – No Discord length limit handling.** Discord rejects `content` > 2000 chars with HTTP 400. Long `Updated plugins:` lists, long `fail_reason` or many wipe alerts make the whole notification fail (only a log line results). No truncation or splitting.
- **D13 (M) – Incomplete JSON escaping** (L578–585). Tabs and other control characters (< 0x20) are emitted raw ⇒ invalid JSON ⇒ HTTP 400. `fail_reason` / `wipe_info` come from the marker (exception messages, file paths) and may contain them. Also `ExtractJsonString` returns raw captured text with JSON escapes intact (e.g. Windows paths as `C:\\dir`), which are then escaped a second time, so Discord shows doubled backslashes.
- **D14 (M) – Timed-out requests are abandoned, not cancelled** (L216–217, L247–248). The WebClient has no own timeout; hung connections accumulate thread-pool work for the life of the process. For POST, a timeout is reported as failure even though Discord may still deliver the message.
- **D15 (M) – Config load failure leads to config overwrite.** After `Error loading config` (L758–762) the plugin continues with defaults; the next `Config.WriteObject(configData, true)` (L1201, L2653) replaces the admin's (syntactically broken but recoverable) file with defaults. In-memory "sanitising" mutations (`UseScheme=false` L569/L883, blanked `NextWipeMapsize`/`NextWipeSeed`, generated random seed) are likewise persisted by any later write.
- **D16 (M) – Scheme token matching is by substring and position.** L471/L478 use `IndexOf(...) >= 0`: `observer`, `server(x)`, `myoxide`, `oxideserver` (treated as *server*) are accepted. A malformed oxide token such as `oxide(sc` or `oxide[s]` fails the regex and is silently treated as "all flags" (L481–489) instead of being rejected, contradicting "validates strictly". Duplicate tokens (`server+server`) produce the misleading error `Unknown event specifier: 'server'`.

### Low

- **D17 (L) – `e` vs `u` flags are parsed separately but evaluated identically** (L1316–1347); README documents them as distinct events.
- **D18 (L) – Oxide flags are case-sensitive** (`oxide(S)` ⇒ error) while event/action names are case-insensitive (L501–506).
- **D19 (L) – Dead / write-only members:** `schemeInstruction.ErrorColumn` (never set), `Scheme.isValid` and `Scheme.SchemePath` (never read outside the parser), `debugging` and `quitBeforeExecOnDebugging` (never assigned), `HttpBackend` detection probe result (ignored), the `headers` dictionary built at L596–599 (redundant), `updateId ?? "unknown"` at L659 (unreachable null), `Empty instruction` branch (L414) unreachable from the only caller.
- **D20 (L) – Duplicated logic:** GET/POST wrappers (L196–255), GET/POST WebRequest (L258–291), GET/POST WebClient incl. identical error mapping (L294–379), and the two branches of `NotifyDiscordUpdateResult` (L657–690 vs L691–728) differ only in two words and the plugin list.
- **D21 (L) – Force wipe without pending updates is announced as "custom wipe".** L636–638 ignore `nextUpdateIsForce` when neither server nor Oxide is updated. Whether a force-wipe launch can have both flags false is unverified (depends on wipe code outside the range).
- **D22 (L) – Inconsistent comparisons of marker values:** `wiped == "yes"` is exact/case-sensitive in the notification (L672, L710) but trimmed/case-insensitive in marker handling (L1145); `result` is lower-cased+trimmed for Discord (L660, L694) but compared exactly at L1113–1116 for the try counter.
- **D23 (L) – WebClient error mapping overloads fields:** `(-1, ex.Message)` puts an error message in `body` (L332, L377); all WebClient successes are reported as 200; callers log `-1` as "timeout" even for DNS/TLS failures (L1773), which misleads diagnosis.
- **D24 (L) – `Puts` and `timer.Once` are called from thread-pool threads** (L176 via callback is fine; L209, L221, L240, L252, L607, L622, and `webrequest.Enqueue` at L174/L263/L281 from within `Task.Run`). Thread-safety of these Oxide APIs off the main thread is unverified.
- **D25 (L) – No `allowed_mentions` restriction:** text from the marker (plugin names, failure reasons) is posted verbatim; a value containing `@everyone` would ping (requires control over marker content or plugin names).
- **D26 (L) – Payload encoding not pinned to UTF-8** for `UploadStringTaskAsync` (L354); non-ASCII characters may be mis-encoded on hosts whose default encoding is not UTF-8 (unverified on Mono/Windows).
- **D27 (L) – README/code default mismatches:** README example shows `RunServerScriptHidden: true` and `StartupScan: true`; code defaults are `false` (L32, L40). README does not document the `feedme.set` command registered at L746. `UpdaterMarkerFileName` is configurable in the plugin but hard-coded in the app.
- **D28 (L) – `ReadSchemeFile` is only run at load**; `feedme.set usescheme true` / `schemefile …` at runtime leaves `scheme.instructions` stale or null (see D10 for the effect).
- **D29 (L) – Detection probe hits `https://api.github.com/` unauthenticated on every plugin load** for no benefit (D3), consuming rate limit.

---

## 9. Open questions

1. Which backend is *intended* to be primary? The code ends up on Oxide webrequest for the single periodic GitHub call and on WebClient for everything else. Was WebClient observed to fail on real hosts (TLS under Mono), or was Oxide webrequest the one that failed?
2. Should a missing/empty scheme file fall back to default logic (as an invalid file does) rather than disabling updates (D10)?
3. Intended difference between scheme flags `e` (error fetching protocol) and `u` (unknown protocol)? Today they are equivalent.
4. Intended scheme semantics when both server and Oxide changed: should `server=server` fire? Today only rules containing an oxide event are considered in that case.
5. Should `oxide` without parentheses really mean "any protocol state", and should malformed parentheses be an error?
6. Should the start notification be awaited (bounded) before quitting the server, and should it be sent only after the updater process has actually been spawned?
7. Is the Discord message text considered a stable contract (anyone parsing it), or free to redesign (embeds, truncation, mentions policy)?
8. Is `UpdaterMarkerFileName` meant to be configurable at all, given the app hard-codes `updateresult.json`?
9. Should `NextWipeResetRustPlus` be reset after a successful wipe like the other `NextWipe*` keys? (It is currently left untouched, L1184–1203.)
10. Should plugin-side "sanitised" config values (invalid map size/seed, disabled scheme) be persisted, or only applied in memory?
