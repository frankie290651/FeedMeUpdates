# A4 — Updater app: service start, failure marker, WipeCycle, file-list decoding, server.cfg editing

Source: `Application/Program.cs` (3892 lines, .NET 8, single static class `Program`).
Primary range read in full: lines 3075-3892. Context read in full: 18-135 (Logger, RunState), 136-288 (Main / arg parsing), 293-580 (HandleFlow), 580-860 (ValidateFlow), 960-999, 1296-1372, 1693-1868, 2745-2788, 3036-3071. Plugin (`FeedMeUpdates.cs`) read only where it produces or consumes the contracts described here (lines 653-731, 793-803, 1103-1232, 3136-3190, 3612-3631, 3700-3794, 3862-3936, 4096-4134).

All line numbers refer to `Application/Program.cs` unless prefixed with `plugin:`.

Conventions: "ServerDir" = `RunState.ServerDir` (CLI `-server_dir`, default = process current directory, line 223). "identity" = `RunState.ServerIdentity`.

---

## 0. Context needed to understand this range

### 0.1 RunState fields used here (lines 102-132) and the CLI args that fill them (lines 194-246)

Arg keys are matched case-insensitively (dictionary is `OrdinalIgnoreCase`, keys lower-cased, line 929/952). Syntax accepted: `-key value` or `-key=value`; a following token that itself starts with `-` is NOT consumed as a value (line 942), so a value starting with `-` must use the `=` form or is lost (value becomes `""`). The first arg must be `-update_id`.

| CLI arg | RunState field | Parsing | Used in this range for |
|---|---|---|---|
| `-update_id` | `UpdateId` | raw; `init` / `testrun` / `wipe` (case-insensitive) or `^\d{8}$` | marker `update_id`, selection of failure-marker shape |
| `-update_plugins` | `UpdatePluginsFlag` | raw `yes`/`no` | failure-marker shape (`updated_plugins`) |
| `-server_dir` | `ServerDir` | raw; default cwd | all paths |
| `-start_script` | `StartScript` | raw | server start in failure path |
| `-isService` | `IsService` | `ParseBool`: `bool.TryParse`, or `"1"` = true, everything else false (958-965) | script vs service start |
| `-serviceName` | `ServiceName` | raw | service start |
| `-serviceType` | `ServiceType` | raw | **never read anywhere in the app** (dead) |
| `-isforce` | `IsForce` | ParseBool | failure-marker shape; force-wipe in Scheduled flow |
| `-nextwipelevel` | `NextWipeLevel` | raw | `server.level` |
| `-nextwipeurl` | `NextWipeUrl` | `RevertFromb64` | `server.levelurl` |
| `-nextwipeseed` | `NextWipeSeed` | raw | `server.seed` |
| `-nextwipemapsize` | `NextWipeMapsize` | raw | `server.worldsize` |
| `-nextwipekeepbps` | `NextWipeKeepBps` | ParseBool (absent = false = **delete BPs**) | blueprint deletion |
| `-nextwiperesetrustplus` | `NextWipeResetRustPlus` | ParseBool | Rust+ token deletion |
| `-nextwipedelplayerdata` | `NextWipeDeletePlayerData` | ParseBool | player data deletion |
| `-nextwipedelpluginsdata` | `NextWipeDelPluginsData` | raw string, decoded later by `DecodeFileListArgToList` into `PdataFilesToDelete` | plugin datafile deletion |
| `-serveridentity` | `ServerIdentity` | raw; empty -> `"my_server_identity"` | identity folder |
| `-servername` | `ServerName` | `RevertFromb64` | `server.hostname` |
| `-serverdescription` | `ServerDescription` | `RevertFromb64` | `server.description` |

`RevertFromb64` (274-286): if the value starts with the literal prefix `b64:` the remainder is decoded with **standard** Base64 (`Convert.FromBase64String`, not URL-safe) as UTF-8; otherwise the value is used verbatim. There is no try/catch: malformed base64 throws `FormatException` inside `Main` before the logger exists -> unhandled exception, process crashes, no marker, no server start. The plugin always sends these three args as `b64:` + standard base64 (plugin:3612-3631), `b64:` alone for empty.

`UpdatedPlugins` (`List<string>`) is filled by the plugin-update pass (outside this range) and read by the failure marker.

### 0.2 Logger (20-100)

- File: `<ServerDir>/updater.log`. `Init` creates ServerDir if missing (`Directory.CreateDirectory`).
- "Rotation" = truncation: if the file exists and is larger than `5 * 1024 * 1024` bytes at `Init`, it is opened with `FileMode.Create` (previous content discarded, no archive). Otherwise append. If size check throws -> append.
- Opened `FileAccess.Write, FileShare.Read`, `AutoFlush = true`. Any failure in `Init` -> `writer = null` and logging silently goes to console only.
- Line format: `[dd/MM/yy HH:mm:ss] [LEVEL] message`, LEVEL in `INFO`/`WARN`/`ERROR`, local time. Every line is also written to `Console.WriteLine`. Both writes are individually try/caught. Guarded by one lock.
- `Close()` flushes/disposes; called before every exit of `HandleFlow`. `CreateUpdateresultAndStartScript` logs but never closes the logger itself.
- Note: `ValidateFlow` failure for the Wipe flow can call `CreateUpdateresultAndStartScript` with a non-existent ServerDir; `Logger.Init` has by then already *created* that directory (line 33), so the "server directory doesn't exist" checks at 593/702 can never fire after `Logger.Init` succeeded.

### 0.3 How the functions in this range are called

`HandleFlow(kind)` (293-578), relevant order:

1. `Logger.Init`, log args.
2. `ValidateFlow(kind)` — on failure each branch calls `CreateUpdateresultAndStartScript(...)` and HandleFlow returns 0. **This happens before waiting for the server to stop** (see defect D2).
3. `WaitForRustDedicatedAndLog()` (968-999): polls every 2000 ms with no timeout until process `RustDedicated` is gone (script mode) or `IsServiceRunning(ServiceName)` is false (service mode), then runs the FMU self-update task.
4. Lock: if `<ServerDir>/updating.lock` exists -> `CreateUpdateresultAndStartScript(..., "Blocked by updating.lock file", ...)`, return 0. Else create it empty. **The app never deletes `updating.lock`**; the plugin deletes it on next boot (plugin:1221-1232), and the sample start scripts use its presence to leave their restart loop (`Scripts/startserver.sh`, `.bat`).
5. `kind == Wipe` (custom wipe, `-update_id wipe`), lines 325-391:
   - if `NextWipeDelPluginsData != ""`: `PdataFilesToDelete = DecodeFileListArgToList(...)`; if the result is empty only a WARN is logged ("Plugins datafile argument interpretation failed. No plugin data will be deleted.") — the wipe continues and this is not reported in `wipe_info`.
   - `wipeResult = WipeCycle(out wipeingErrors)`.
   - any exception -> ERROR log, `CreateUpdateresultAndStartScript(ServerDir, UpdateId, ex.Message, StartScript)`, return 0.
   - success marker written (see 2.3), then server started: script mode `StartServerScript(StartScript, ServerDir)`, service mode `RestartRustService(ServiceName)` (return value ignored). Exceptions only logged.
   - **No backup is taken in the Wipe flow** and `CleanTempBackup` is not called on success.
6. Other flows: backup, updates, plugin pass; then, **only if `kind == Scheduled && IsForce`** (501-525), the same decode + `WipeCycle` call. On exception: logs `SCHEDULED: WipeCycle error: ...` and `SCHEDULED: IMPORTANT! SERVER IS UPDATED BUT MUST BE WIPED MANUALLY.`, closes the logger and **returns 0 without writing a marker, without cleaning `temp_backup`, and without starting the server** (defect D1).
7. Success marker (527-551), `CleanTempBackup`, start script / service (return value of `RestartRustService` ignored).

`ValidateFlow` wipe-related pre-checks (583-680 for `Wipe`; 692-788 for Scheduled+IsForce; duplicated per OS with hard-coded `\\` vs `/`): ServerDir non-empty and exists; identity defaulted to `my_server_identity`; if `NextWipeDelPluginsData` non-empty then `<ServerDir>/oxide/data` must exist; `<ServerDir>/server/<identity>` must exist; `.../cfg` must exist; `.../cfg/server.cfg` must exist. Fail reasons are literal strings `"Wipe validation failed: ..."` / `"ForceWipe validation failed: ..."` with suffixes: `server dir not specified`, `server directory doesn't exist`, `plugin data directory doesn't exist`, `server identity directory doesn't exist`, `server cfg directory inside server identity doesn't exist`, `server.cfg not found`. For `kind == Wipe` validation returns true right after these checks: **start_script existence, service name, RustDedicated presence are NOT validated for the custom-wipe flow** (line 679). On an OS that is neither Windows nor Linux none of the directory checks run.

---

## 1. RestartRustService(string servName) -> bool (3078-3192)

Despite the name it only **starts** the service (never stops/restarts). The server is expected to be already stopped (HandleFlow waited for it).

### 1.1 Common

- Empty/whitespace name -> ERROR `RestartRustService: service name empty`, return false.
- Local helper `RunAndLog(exe, args, timeoutMs = 30000)` (3086-3127):
  - `ProcessStartInfo(exe, args)`, `RedirectStandardOutput/Error = true`, `UseShellExecute = false`, `CreateNoWindow = true`, both encodings UTF-8.
  - `Process.Start` returns null -> ERROR, return `-1`.
  - `proc.WaitForExit(timeoutMs)`, **then** `StandardOutput.ReadToEnd()`, then `StandardError.ReadToEnd()`.
  - Non-empty stdout logged at INFO (trimmed), non-empty stderr at WARN (trimmed).
  - If not exited: `proc.Kill(true)` (entire tree, exceptions swallowed), WARN `... timed out`, return `-2`.
  - Else return `proc.ExitCode`.
  - Any exception (e.g. executable not found) -> ERROR, return `-1`.
- Success criterion everywhere: **exit code == 0 of the start command**. There is no retry, no post-start verification that the unit/service is actually running, no wait for the game server to come up.

### 1.2 Windows (3129-3134)

- Command: `sc start "<servName>"` (name wrapped in double quotes; embedded quotes not escaped), timeout **15000 ms**.
- Return `exitCode == 0`. (`sc start` returns as soon as the SCM accepted the request; "already running" is a non-zero code and therefore counts as failure.)
- No elevation handling: the updater must already run with rights to start the service (it inherits the token of the Rust server process that launched it).

### 1.3 Other non-Linux OS (3136-3140)

WARN `RestartRustService: unsupported platform`, return false.

### 1.4 Linux / systemd (3142-3191)

1. `systemctl = ResolveSystemctlPath()` (2745-2788), logged:
   - run `which systemctl` (stdout read to end, then `WaitForExit(2000)`); if output non-empty and `File.Exists(output)` -> use it;
   - else scan `PATH` (if PATH unset use `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`) for a file named `systemctl`;
   - else the literal string `systemctl`.
2. `IsEffectiveRoot()` (3142-3168): read `/proc/self/status`, find the line starting `Uid:`, split on tab/space, take the **third token** (index 2 = effective UID) and compare to 0. If that fails/throws: fall back to env `USER` (or `Environment.UserName`) equal to `root` (case-insensitive). Otherwise false.
3. Root: `<systemctl> start <servName>`, timeout 30000 ms; on non-zero ERROR `systemctl exit code N`.
4. Not root: `sudo -n <systemctl> start <servName>`, timeout 30000 ms; on non-zero ERROR `RestartRustService: start failed. If you see 'a password is required' in logs, configure sudoers NOPASSWD for this user and command.`
5. `servName` is inserted **unquoted** on Linux.

Related, outside the range but part of the same contract: `IsServiceRunning` (1296-1358) uses `sc query "<name>"` and treats `STATE : 2|3|4` (START_PENDING, STOP_PENDING, RUNNING) as running, falling back to the substring `RUNNING`; on Linux it runs bare `systemctl is-active --quiet <name>` (no sudo, not the resolved path, 4000 ms wait) and treats exit code 0 as running.

### 1.5 Implicit knowledge behind this design

- **Absolute systemctl path**: a sudoers `NOPASSWD` rule matches the full command path. The README (section 12) builds the rule with `$(command -v systemctl)` and exactly `start <service>`; the app must therefore invoke exactly `sudo -n /abs/path/systemctl start <service>` — no extra flags, same path. Changing the command line (e.g. adding `--no-block`, using `restart`) breaks existing users' sudoers rules.
- **`sudo -n`**: never prompt; the updater has no TTY, a password prompt would hang forever.
- **Start only, not restart**: the game server has already quit on its own (plugin runs the countdown then quits). Only `start` is whitelisted in sudoers.
- README systemd unit requirements that the code silently depends on: `KillMode=process` (the updater is a child of RustDedicated and lives in the service's cgroup; with the default `control-group` systemd would kill the updater when the service stops), `NoNewPrivileges=false` (otherwise `sudo` cannot elevate from inside the unit), `Restart=on-failure` + `SuccessExitStatus=0 9 SIGKILL` (so a clean quit does not trigger an automatic restart while the updater is working), `KillSignal=SIGINT`, `SendSIGKILL=no`.
- README Windows/NSSM requirements: `AppExit 0 Exit`, `AppExit 4294967295 Exit`, `AppExit 3221225786 Exit` (so NSSM does not relaunch the server when it quits for update), `AppStopMethodSkip 6`, `AppStopMethodConsole 60000`, `sc failureflag <svc> 0` and deletion of `FailureActions` (Windows service recovery must not restart the server behind the updater's back).
- README: service mode only works when the service runs `RustDedicated` directly (the app waits on service state, not on the process).
- Detecting root through `/proc/self/status` rather than `geteuid` avoids P/Invoke; the `USER` fallback covers containers without `/proc`.

---

## 2. CreateUpdateresultAndStartScript(server_dir, update_id, failReason, start_script) (3195-3301)

Purpose: generic "abort early" exit — write a **failed** marker, then bring the server back up. Callers: every `ValidateFlow` failure, lock-file block (315), custom-wipe exception (345), temp-backup creation failure (396). (Restore paths use their own, different functions at 1693-1868 which only ever use the start script — see D5.)

### 2.1 Marker written

Path: `Path.Combine(server_dir, "updateresult.json")` (file name hard-coded although the plugin has a configurable `UpdaterMarkerFileName`). Written with `File.WriteAllText` (UTF-8 without BOM, overwrite, not atomic), `JsonSerializer` with `WriteIndented = true` and the default encoder. Timestamp: `DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")`.

Three shapes (key order as listed):

A. `!RunState.IsForce && update_id == "wipe"` (ordinal, lower-case only), 3200-3214:
```
result: "failed", fail_reason: <failReason>, timestamp, update_id, wiped: "no", wipe_info: ""
```
B. else, if `UpdatePluginsFlag == "yes"` (ordinal, case-sensitive here) and `UpdatedPlugins.Count > 0`, 3217-3235:
```
result: "failed", fail_reason, updated_plugins: [..], backup_cycle: false, server_restored: false, timestamp, update_id, wiped: "no", wipe_info: ""
```
C. else, 3236-3253: same as B without `updated_plugins`.

Then INFO `Created <path> with fail_reason='<reason>'`. Any exception while writing: ERROR `Error creating updateresult.json: <msg>` and execution **continues** to the start step.

### 2.2 Server start

- Script mode (`!IsService`): if `start_script` non-blank -> `CleanTempBackup(server_dir)` then `StartServerScript(start_script, server_dir)`, INFO `Started start_script: ...`; exceptions -> ERROR `Error starting start_script: ...`. Blank -> WARN `start_script not specified; skipping start.` (temp backup not cleaned, server stays down).
- Service mode: if `RunState.ServiceName` non-blank -> `CleanTempBackup` then `RestartRustService(ServiceName)`; **the bool result is ignored** and INFO `Started service: <name>` is logged regardless. Blank -> WARN `serviceName not specified; skipping start.`
- `CleanTempBackup` (3036-3071) deletes `<server_dir>/temp_backup` recursively, never throws.
- The function does not delete `updating.lock`, does not close the logger, does not exit the process.

### 2.3 All `updateresult.json` shapes (external contract with the plugin)

| Writer | result | fail_reason | updated_plugins | backup_cycle | server_restored | timestamp | update_id | wiped | wipe_info |
|---|---|---|---|---|---|---|---|---|---|
| Failure A (3202) | `"failed"` | string | – | – | – | `yyyy-MM-ddTHH:mm:ssZ` | as passed | `"no"` | `""` |
| Failure B/C (3219/3238) | `"failed"` | string | B only | `false` (bool) | `false` (bool) | same | as passed | `"no"` | `""` |
| Custom wipe success (351-357) | `"success"` | `null` | – | – | – | round-trip `"o"` | literal `"wipe"` | WipeCycle result (`"yes"`) | alerts string |
| Update success (529-541) | `"success"` | `null` | only if `update_plugins` = yes (ignore case) | `true` | `"no"` (string) | `"o"` | `"init"` / `"testrun"` / UpdateId | `"no"` or `"yes"` | `""` or alerts |
| Restore OK (1771) | `"failed"` | `"Failed " + Fails` | – | `true` | `"yes"` (string) | `...Z` | as passed | – | – |
| Restore failed (1829) | `"failed"` | `"Failed restore after failed " + Fails` | – | `true` | `false` (bool) | `...Z` | as passed | – | – |

Inconsistencies to normalise in v2: `server_restored` is bool in some shapes and `"yes"`/`"no"` strings in others; two timestamp formats; restore markers lack `wiped`/`wipe_info`.

How the plugin consumes it (plugin:1103-1181): regex-based extraction, not a JSON parser — `"key"\s*:\s*"([^"]*)"`, falling back to `"key"\s*:\s*([^,}\n\r]+)`, result passed through `WebUtility.HtmlDecode`. Keys read: `result`, `update_id` (alt `updateId`), `fail_reason` (alt `error`), `wiped` (alt `wipe_result`), `wipe_info` (alt `wipeinfo`), `updated_plugins` via `"updated_plugins"\s*:\s*\[(.*?)\]` then `"([^"]+)"`. On `result == success`: retry counter reset, `RemoteSteamBuildID` promoted; if additionally `wiped == yes` (trim, ignore case) the plugin resets all NextWipe config values. The marker is then moved to `<ServerDir>/<MarkersSubfolder>/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json`.

`wipe_info` format: zero or more alerts each wrapped in square brackets and concatenated with no separator: `[alert one][alert two]`. The plugin splits on `]`, strips a leading `[`, and prints each as a Discord bullet under `Wipe ALERTS reported:` (plugin:680-689, 718-727).

---

## 3. WipeCycle(out string wipingInfo) -> string (3308-3646)

Returns `"yes"` when it runs to the end (regardless of how many alerts were raised or whether anything was actually deleted); the initial value `"no"` is only observable if an exception propagates (the caller keeps its own `"no"`). All non-fatal problems are appended to `wipingInfo` as `[text]` and logged at WARN with the prefix `WIPE-CYCLE: CAUTION! `. Any exception (missing identity directory, locked file, permission denied) propagates to the caller mid-way, leaving a partial wipe.

### 3.1 Path setup (3312-3332)

- `identity` empty -> `"my_server_identity"` (Rust's default identity folder name).
- Windows: `pluginDir = ServerDir + "\oxide\data"`, `sDataDir = ServerDir + "\server\" + identity`, `cfgDir = sDataDir + "\cfg"`, `sp = "\"`.
- Linux: same with `/`.
- Any other OS: all four stay `""` (subsequent `Directory.GetFiles("")` throws -> wipe fails).
- Paths are built by plain string concatenation (no `Path.Combine`, no normalisation, no containment check).

### 3.2 Ordered operations

All directory listings use `Directory.GetFiles(dir)`: **top-level files only, not recursive**, re-listed before each step. All matching in steps 2-5 is `string.Contains` (ordinal, case-sensitive) applied to the **full path string**, not to the file name (see D3).

1. **Plugin datafiles** (3334-3364) — only if `PdataFilesToDelete.Count > 0`:
   - `pluginDir` missing -> alert `[Plugin data directory  not found.]` (two spaces, verbatim), continue.
   - else for each requested `filename`: look for a listed file whose path is **exactly equal** (ordinal) to `pluginDir + sp + filename`; if found `File.Delete`, INFO `WIPE-CYCLE: Plugin Datafile <filename> deleted.`; if not found alert `[Plugin Datafile <filename> not found.]`.
   - Consequences: exact file name including extension; case-sensitive even on Windows; files in sub-folders of `oxide/data` can never match (list is top-level only); no wildcards.
2. **Blueprints** (3365-3384) — only if `NextWipeKeepBps == false`: INFO `WIPE-CYCLE: Removing BP database.`; delete every file in `sDataDir` whose path contains `.blueprints`; per file INFO `Players blueprint file <name> deleted.`; none found -> alert `[No players blueprint db found. Please check manually.]`.
3. **Rust+ tokens** (3385-3404) — only if `NextWipeResetRustPlus`: INFO `Removing Rust+ token database.`; delete every file whose path contains `.tokens`; INFO `Rust+ token database deleted.`; none -> alert `[No Rust+ token database found. Please check manually.]`.
4. **Player data** (3405-3424) — only if `NextWipeDeletePlayerData`: INFO `Removing players data.`; delete every file whose path contains any of `.deaths`, `.identities`, `.states`, `relationship`, `.files`; per file INFO `Player data file <name> deleted.`; none -> alert `[No player data file found. Please check manually.]`.
5. **Map** (3425-3441) — always: INFO `Removing map data.`; delete every file whose path contains any of `.map`, `.sav`, `.dat`; per file INFO `Map file <name> deleted.`; none -> alert `[No map file found. Please check manually.]`.
6. **server.cfg edits** on `cfgpath = cfgDir + sp + "server.cfg"` (3442-3642), each edit is an independent read-modify-rewrite of the whole file:
   1. Always: remove key `wipeUnixTimestampOverride`. Removed -> INFO `wipeUnixTimestampOverride value removed.`; not removed with error -> alert `[wipeUnixTimestampOverride not removed (error: <e>)]`; absent -> silent.
   2. If `ServerName` non-empty: set `server.hostname` (forceWrite). OK -> INFO `Server hostname updated`; error -> alert `[Server hostname not updated (error: <e>)]`.
   3. If `ServerDescription` non-empty: set `server.description` (forceWrite). OK -> INFO `Server description updated`; error -> alert `[Server description not updated (error: <e>)]`.
   4. If `NextWipeUrl` non-empty (custom map):
      - remove `server.level`, then `server.worldsize`, then `server.seed`. Each: removed -> INFO `<key> removed`; error -> alert `[Error while removing <key> from cfg (error: <e>)]`; absent -> INFO `<key> was already unset`.
      - set `server.levelurl` = `NextWipeUrl` (forceWrite). OK -> INFO `servel.levelurl updated` (typo verbatim); error -> alert `[server.levelurl not updated (error: <e>)]`.
   5. Else (no URL):
      - **always** remove `server.levelurl` (removed -> INFO `server.levelurl removed`; error -> alert `[Error while removing server.levelurl from cfg (error: <e>)]`; absent -> INFO `server.levelurl was already unset`).
      - if `NextWipeLevel` non-empty: set `server.level`; error alert `[server.level not updated (error: <e>)]`.
      - if `NextWipeSeed` non-empty: set `server.seed`; error alert `[server.seed not updated (error: <e>)]`.
      - if `NextWipeMapsize` non-empty: set `server.worldsize`; error alert `[server.worldsize not updated (error: <e>)]`.
      - Keys whose NextWipe value is empty are left untouched (existing seed/size/level stay).
7. INFO `WIPE-CYCLE: wiping task done`, return `"yes"`.

Nothing else is touched: `cfg/users.cfg`, `cfg/bans.cfg`, sub-directories of the identity folder, `oxide/data` files not listed, logs.

Value validation is done by the plugin, not the app: map size must parse as int in 1000-6000 (plugin:804-823), seed must be a decimal uint (plugin:835-845), `NextWipeRandomSeed` generates a seed in the plugin before launch (plugin:831). The app writes whatever it receives.

### 3.3 Implicit knowledge in WipeCycle

- **Substring patterns instead of exact names**: Rust's save/db file names embed version numbers and map parameters and have companion files (`*.db-wal`, `*.db-journal`, rotated `*.sav.1`, `*.sav.2`...). Matching `.blueprints`, `.sav` etc. as substrings catches all of them regardless of the current save/protocol number. The rewrite must keep this breadth (e.g. match on file **name** with patterns like `*.sav*`, `*.map`, `player.blueprints.*`), but must not match on directory names.
- **Rust+ tokens are separate from player data** and kept by default so players do not have to re-pair the companion app after every wipe.
- **`.files` is under "player data", not "map"**: a map-only wipe leaves the `*.files*` database in place (see open question Q3).
- **`wipeUnixTimestampOverride` removal**: the plugin sets this convar at boot to drive the in-game wipe timer and, for custom wipes, persists it with `server.writecfg` (plugin:3933-3935). After the wipe the stale value must not survive in `server.cfg`.
- **levelurl vs level/seed/worldsize are mutually exclusive**: with a custom map URL the procedural parameters are removed; with procedural parameters the URL is removed.
- **Identity default `my_server_identity`** is RustDedicated's default when `+server.identity` is not passed.
- **`wiped: "yes"` drives the plugin's reset** of `CustomWipeDay/Time` and all `NextWipe*` settings; reporting `"no"` (or no marker) means the plugin keeps the wipe scheduled.

---

## 4. DecodeFileListArgToList(string? arg) -> List<string> (3649-3698)

Input: the raw `-nextwipedelpluginsdata` value. Output: list of file names (possibly empty). Never throws.

1. null/whitespace -> empty list.
2. Try URL-safe Base64: replace `-` -> `+`, `_` -> `/`; pad with `==` if `len % 4 == 2`, `=` if `== 3` (nothing for 1); `Convert.FromBase64String`; decode bytes as UTF-8 -> `json`.
3. If base64 decoding throws -> treat the **original** arg as raw JSON.
4. Blank json -> empty list.
5. `JsonDocument.Parse`; root must be an array, else empty list. For each element: only JSON strings that are non-empty are added, **verbatim** (no trim, no de-dup, no validation of characters or path separators). Non-string elements are silently skipped.
6. JSON parse error -> empty list (drops anything collected).

Producer side (plugin:793, 3136-3190, 4096-4134): config `NextWipeDeletePluginDatafiles` is a JSON array literal in a string; the plugin strict-parses it, trims entries, re-serialises as a compact JSON array and encodes it as Base64URL **without padding** (falls back to standard base64, then to `""`, if the value fails its safe-arg whitelist). An empty/invalid list yields `""`, so the arg is passed empty and nothing is deleted.

Why base64: the plugin refuses arg values containing quotes, brackets, `*`, `?`, etc. (plugin:3788-3794), so a JSON array cannot be passed literally; the same applies to hostname/description/URL (`b64:` prefix scheme). Note the two schemes differ: `b64:`+standard base64 for the three text args, bare URL-safe unpadded base64 for the file list.

---

## 5. server.cfg editing

File format assumed: one console command per line, `key<whitespace>value`; `#` and `;` treated as comment starters by the editor.

### 5.1 TryUpdateConfigValue(path, key, value, forceWrite, out error) -> bool (3701-3787)

- Blank `path` -> error `'path' param is null or empty.`, false. Blank `key` -> `'key' param is null or empty.`, false.
- Content: `File.ReadAllText(path)` if the file exists, else `""` (so with `forceWrite` a missing file is **created**; its directory must exist).
- Split: `Regex.Split(content, "(\r\n|\n)")` — capturing group, so the array alternates `line, separator, line, separator, ...`; original separators are preserved per line. A lone `\r` is not a separator.
- Value normalisation `NormalizeControlChars` (3859-3869): null/empty -> `""`; each `\r`, `\n`, `\t` -> one space; repeated collapse of double spaces to a single space; `Trim()`.
- Value formatting `BuildValue(existingKey, norm)` (3723-3733):
  - key equals `server.description` (ignore case): remove **all** `"` and `'` characters; wrap in double quotes only if the result contains a space character; else bare.
  - any other key: if `NeedsQuoting(norm)` (empty string, or contains any `char.IsWhiteSpace`) -> `"` + value with all `"` removed + `"`; otherwise the value verbatim (embedded `"` kept, `'` always kept).
  - No escaping exists; double quotes are simply dropped.
- Line match regex: `^\s*(?<key>[^\s#;=]+)\s+(?<value>.*)$` (no options besides Compiled).
- Scan lines (even indices): skip empty lines; skip lines whose trimmed text is empty or starts with `#` or `;`; skip non-matching lines; compare captured key with `key` using **ordinal, case-sensitive** equality. On the **first** match: replace the entire line with `<existingKey> <newValue>` (single space, leading indentation and any trailing inline comment discarded), stop scanning.
- If updated: concatenate all parts, `WriteAllTextNoBomAtomic`, return true (rewrites even if the value is unchanged).
- If not found and `forceWrite`: `sep` = `\r\n` if the content contains `\r\n` anywhere, else `\n`; new line `<key> <value>`; new content = `newLine + sep` for empty content, `content + newLine + sep` if content ends with `sep`, else `content + sep + newLine + sep`. Write, return true.
- If not found and not `forceWrite`: error `Chiave non trovata e forceWrite=false` if the file exists else `File non trovato: <path>` (Italian, verbatim), false. (Unreachable today: every caller passes `forceWrite = true`.)
- Any exception: `error = ex.Message`, false.

Duplicates: only the first occurrence is updated; later duplicates stay unchanged.

### 5.2 TryRemoveConfigValue(path, key, out error) -> bool (3790-3856)

- Same blank-param errors. File missing -> error `File non trovato: <path>`, false.
- Same split and same regex. For every line that matches the regex and whose key equals `key` (ordinal): drop the line **and its following separator**. All occurrences are removed (unlike update). Comment lines cannot match (the key character class excludes `#` and `;` and the key must begin at the first non-space char).
- If anything was removed: write, return true. Otherwise return false with **empty** error (callers interpret "false + empty error" as "already unset").
- Exception -> `error = ex.Message`, false.

Lines not recognised by either function (and therefore never updated/removed): a key with no value (`server.seed` alone — regex requires at least one whitespace after the key), `key=value` syntax, and any key written with different casing or a different spelling (e.g. `hostname`, `Server.Seed`).

### 5.3 NeedsQuoting(v) (3882-3888)

True if null/empty or any whitespace char; else false.

### 5.4 WriteAllTextNoBomAtomic(path, content) (3872-3879)

1. `tmp = path + ".tmp_" + Guid.NewGuid().ToString("N")` (same directory).
2. `File.WriteAllText(tmp, content, new UTF8Encoding(false))`.
3. `if (File.Exists(path)) File.Delete(path);`
4. `File.Move(tmp, path);`

Encoding: output always UTF-8 without BOM. Input is read with `File.ReadAllText` default (BOM detection, UTF-8 fallback), so an existing BOM is dropped on first rewrite. Reason for "no BOM": a BOM at the start of `server.cfg` would become part of the first command name and break that line when the Rust console parses the file.

---

## 6. External contracts in or touching this range (summary)

Files (all relative to ServerDir unless noted):
- `updateresult.json` — marker, shapes in 2.3 (name hard-coded in the app; plugin config `UpdaterMarkerFileName` must stay default).
- `updating.lock` — empty file created by the app after the server stopped, never removed by the app; removed by the plugin at boot; checked by the sample start-script loops after a 5 s sleep to decide whether to exit the loop. Name hard-coded in the app (plugin config `UpdaterLockFileName` must stay default).
- `updater.log` — log, truncated when > 5 MiB at start.
- `temp_backup/` — removed by `CleanTempBackup`.
- `oxide/data/<file>` — plugin datafiles deleted by exact name.
- `server/<identity>/` — save/db files deleted by substring: `.blueprints`; `.tokens`; `.deaths`, `.identities`, `.states`, `relationship`, `.files`; `.map`, `.sav`, `.dat`.
- `server/<identity>/cfg/server.cfg` — keys: `wipeUnixTimestampOverride` (removed), `server.hostname`, `server.description`, `server.level`, `server.seed`, `server.worldsize`, `server.levelurl`.
- `server/<identity>/cfg/server.cfg.tmp_<32 hex>` — transient temp file.
- `markers/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json` — plugin-side archive of the marker.

Processes started in this range:
- Windows: `sc start "<service>"` (15 s).
- Linux root: `<systemctl> start <service>` (30 s).
- Linux non-root: `sudo -n <systemctl> start <service>` (30 s).
- Linux helper: `which systemctl`.

CLI args: table in 0.1. Marker JSON fields: `result`, `fail_reason`, `updated_plugins`, `backup_cycle`, `server_restored`, `timestamp`, `update_id`, `wiped`, `wipe_info`.

---

## 7. Defects and risks

Severity: High = data loss or server left down in a plausible scenario; Medium = wrong behaviour needing manual repair; Low = cosmetic/unlikely.

### High

**D1. Force-wipe failure leaves the server down with no marker** (518-524). In the Scheduled+IsForce flow any exception from `WipeCycle` (identity dir listing fails, a save file is locked or read-only, permission denied on `server.cfg`'s directory...) is caught, logged, and the function returns 0: no `updateresult.json`, `temp_backup` not cleaned, `updating.lock` left, **server never started**. The server stays offline on force-wipe day until an admin intervenes, and Discord gets no result notification. The custom-wipe flow handles the same exception correctly (345).

**D2. Validation failures try to start the server before it has stopped** (302-306 vs 310, and every `CreateUpdateresultAndStartScript` call in 581-860). `ValidateFlow` runs before `WaitForRustDedicatedAndLog`. On a validation error the app immediately launches the start script / `systemctl start` while the old RustDedicated is still shutting down, and before `updating.lock` exists. Service mode: `systemctl start` on a still-active unit is a no-op with exit 0, the server then finishes quitting and stays down (with `Restart=on-failure` + `SuccessExitStatus` nothing restarts it). Script mode with the shipped loop scripts: the original loop is still alive (no lock) and a second script instance is started -> two launchers competing for the same ports/files. Also the failed marker may be consumed/rotated oddly because the old server is still up.

**D3. Deletion patterns are matched against the full path, not the file name** (3372, 3392, 3412, 3430). `file.Contains(".dat")` etc. is evaluated on e.g. `/srv/rust.data/server/main/player.blueprints.5.db`. If ServerDir or the identity contains any of the substrings — `.dat` (also matches `.data`, `.database`), `.sav`, `.map`, `.files`, `.states`, `relationship`, `.tokens`, `.blueprints`, `.deaths`, `.identities` — **every top-level file in the identity folder is deleted** in that step, e.g. blueprints are destroyed although `NextWipeKeepBps=true`, tokens although Rust+ reset is off. Example identities/paths that trigger it: identity `eu.main.database`, server dir `C:\rust.data\`, `/home/rust/my.saves/`. No backup exists in the custom-wipe flow. Even without that, `.dat` matches any file name containing `.dat` (`something.data`, `notes.dat.txt`) and `relationship` matches anywhere in a name.

**D4. Custom-wipe flow never validates the start target** (679) and ignores start failures (368-386, 3288). `ValidateFlow` returns true for `Wipe` without checking `start_script` / `serviceName`. With an empty/wrong start script the wipe is executed, the marker says success, `StartServerScript` throws (caught, logged) and the server stays down. In service mode the bool from `RestartRustService` is ignored everywhere (379, 566, 3288) and "Started service" is logged even when `sc`/`systemctl`/`sudo` failed (e.g. missing NOPASSWD) -> server down after a "successful" update, with a success marker that nobody reads because the plugin never boots.

**D5. Restore paths never start a service** (1791-1807, 1847-1863; outside the range but same responsibility). `RestoreFromBackupOrFail` and `HandleRestoreFailure` only call `StartServerScript(start_script)`; in service mode `start_script` is normally empty -> "start_script not specified; skipping start" -> server stays down after any failed update on service installations. `RestartRustService` is not used there.

**D6. `RunAndLog` timeout is ineffective and can hang forever** (3106-3118). `WaitForExit(timeoutMs)` is followed by `StandardOutput.ReadToEnd()` *before* the kill. If the child has not exited (e.g. `systemctl start` on a `Type=notify`/`forking` unit or one with a long `ExecStartPre`, or `sudo` waiting), `ReadToEnd` blocks until the child closes its pipes, so the `Kill` and the `-2` path are only reached after the child ended anyway. The updater then hangs indefinitely holding `updating.lock`. (Additionally, reading stdout fully before stderr can deadlock if stderr fills its pipe; unlikely for these commands.)

### Medium

**D7. Unconditional removal of `server.levelurl` when no NextWipeMapUrl is set** (3567-3587). A server that permanently runs a custom map via `server.levelurl` in `server.cfg` and performs a wipe (force or custom) without filling `NextWipeMapUrl` loses the line; next boot generates a procedural map from whatever level/seed/size are configured. Silent change of map type on wipe day.

**D8. Partial wipe reported as "not wiped"** (345, 3352-3433). `File.Delete` exceptions are not caught per file. If deletion fails midway (file locked by antivirus/backup tool, read-only attribute, permissions), earlier steps have already deleted plugin data/blueprints; the custom-wipe marker says `result: failed`, `wiped: "no"`, the plugin keeps the wipe scheduled and the server boots on a half-wiped state (e.g. BPs gone, old map still present).

**D9. `wiped: "yes"` even when nothing was wiped** (3437-3441, 3644). If no map file is found (wrong identity that happens to exist, already wiped), the cycle still returns `"yes"` with only an alert; the plugin then resets all NextWipe settings and custom wipe schedule.

**D10. Non-atomic "atomic" write of server.cfg** (3877-3878). Delete-then-move leaves a window with no `server.cfg`; a crash/kill there loses the file (content survives only as `server.cfg.tmp_<guid>`). If `File.Move` fails the original is already deleted and the tmp file is orphaned. The new file gets default ownership/permissions and replaces a symlink with a regular file. The file is rewritten up to 7 times per wipe (one per key). `File.Move(tmp, path, overwrite: true)` / `File.Replace` would be atomic.

**D11. Duplicate keys: update touches only the first occurrence** (3753-3756). Rust executes `server.cfg` top to bottom, so the last duplicate wins; if `server.seed` appears twice the wipe's new seed is written to the first line and overridden by the stale second one. Remove handles all duplicates, update does not.

**D12. Case-sensitive / exact key matching** (3750, 3831). Rust console commands are case-insensitive; a cfg containing `Server.Hostname "x"` or mixed-case keys is not recognised: update appends a second line (works only because the appended line comes last), remove does nothing and reports "already unset" — e.g. `Server.LevelUrl` survives while `server.level` is written, leaving contradictory settings.

**D13. `wipeUnixTimestampOverride` removal probably never matches** (3446) — *unverified*. The app looks for a key spelled exactly `wipeUnixTimestampOverride`. The plugin sets the convar and persists it with `server.writecfg`; Rust's writecfg is expected to write the convar's full, lower-cased name with its class prefix (README calls it `wipetimer.wipeUnixTimestampOverride`), which the ordinal comparison would not match. If so this step is a silent no-op and the stale override stays in `server.cfg`. Needs a check against a real cfg produced by `server.writecfg`.

**D14. Encoding corruption of non-UTF-8 cfg files** (3718, 3813, 3876). The file is decoded as UTF-8 unless it has a BOM; a `server.cfg` saved in Windows-1252/Latin-1 with accented or symbol characters in hostname/description is re-saved with U+FFFD replacement characters in every non-ASCII position, on every wipe, even for lines the wipe did not touch.

**D15. Marker text is escaped in a way the plugin cannot decode** (3211, 360 + plugin:1206-1218). `System.Text.Json`'s default encoder writes `'`, `"`, `<`, `>`, `&`, `+` and all non-ASCII characters as `\uXXXX`, and backslashes as `\\`. The plugin extracts values with a regex and only HTML-decodes them, so `fail_reason`/`wipe_info` containing exception messages with quoted paths (`Could not find a part of the path '/x/y'.`), Windows paths, or the Italian/accented strings appear in console/Discord with literal `\u0027`, `\\` sequences. A `]` inside an error message also breaks the plugin's alert splitting.

**D16. Lock/loop race with the shipped start scripts** (322 vs `Scripts/startserver.sh`/`.bat`; partly outside range). The loop script restarts the server 5 s after it exits unless `updating.lock` exists, but the app creates the lock only after polling (2 s interval) *and* after the network-bound self-update task (`AutoUpdateTask`, called inside `WaitForRustDedicatedAndLog`). If that takes more than ~5 s the script relaunches the server while the updater starts backing up/updating/wiping. The lock should be created before the server quits (by the plugin) or immediately when the process is seen gone.

**D17. Missing `-nextwipekeepbps` deletes blueprints** (240, 3365). `ParseBool(null)` is false, so any invocation of a wipe without that arg (manual run, future plugin/app version mismatch) wipes blueprints by default — the destructive option is the default. Plugin config default is `true` and the plugin always passes the arg, so only non-plugin invocations are affected.

**D18. Malformed `b64:` argument crashes the app before any logging/marker** (282). `Convert.FromBase64String` in `RevertFromb64` is unguarded and runs in `Main`. A truncated command line (the plugin itself guards a 32500-char limit, plugin:3744) leads to an unhandled exception: the server has already been told to quit and nothing restarts it.

### Low

**D19. Linux service name passed unquoted** (3178, 3185). A name containing spaces is split into several arguments (start of additional units or extra systemctl options). The value comes from the admin's config and the plugin's whitelist permits spaces. On Windows embedded `"` would break quoting (blocked by the plugin whitelist). `IsServiceRunning` quotes the name differently from `RestartRustService`.

**D20. systemctl path may not match the sudoers rule** (2745-2788, 3185) — *unverified in practice*. `which` under the service's PATH may yield `/usr/bin/systemctl` while the admin's rule (created from an interactive shell) says `/bin/systemctl` or vice versa on merged-/usr systems; sudo matches the literal path -> "a password is required". Fallback to bare `systemctl` has the same problem.

**D21. Identity is not validated** (3316-3331). Only null/empty is defaulted; whitespace, `..`, absolute-looking or separator-containing values are concatenated as-is. `identity = ".."` makes `sDataDir` the server root (`<ServerDir>/server/..`); the map step would then delete root-level files containing `.dat`/`.sav`/`.map` if `<ServerDir>/cfg/server.cfg` happens to exist (required by validation). Admin-controlled input, so low, but v2 should reject identities that are not a single path segment.

**D22. Plugin-datafile matching is fragile** (3349). String equality against `Directory.GetFiles` output: case-sensitive on Windows (`Kits.json` vs `kits.json` -> "not found"), no sub-folder support (many plugins store data in `oxide/data/<Plugin>/...`), sensitive to how ServerDir is written (trailing or mixed separators — *unverified* whether .NET returns the same concatenation in every case). Path traversal (`../config/x.json`) is not exploitable, but only by accident: such a string can never equal a top-level listing entry. v2 should validate explicitly (single file name or contained relative path) rather than rely on this.

**D23. Failed plugin-list decode is not reported** (333-336, 509-512). If the arg cannot be decoded the wipe proceeds without deleting plugin data and only `updater.log` mentions it; `wipe_info` and Discord stay silent, and `wiped: yes` makes the plugin clear the configured list.

**D24. Value sanitising alters content** (3859-3868, 3725-3731). Double spaces inside hostname/description are collapsed (breaks intentional spacing/ASCII alignment); all `"` are dropped, and for `server.description` all `'` too (`Bob's server` -> `Bobs server`) while `server.hostname` keeps apostrophes; a bare (space-less) hostname keeps embedded `"`. A whitespace-only description produces the line `server.description ` with an empty value.

**D25. Inline comments and indentation lost on update** (3754): the whole line is replaced by `key value`.

**D26. Shape selection inconsistencies in the failure marker** (3200, 3217): `update_id == "wipe"` and `UpdatePluginsFlag == "yes"` are case-sensitive while the rest of the app compares case-insensitively (`-update_id WIPE` runs the wipe flow but gets the non-wipe marker shape). `backup_cycle`/`server_restored` types differ between writers (see 2.3). Three nearly identical payload blocks.

**D27. Non-atomic marker write** (3212, 3233, 3251, 361, 545): `File.WriteAllText` directly on the final name; the server may be started (and the plugin may read) immediately afterwards — fine in practice because the write completes first, but a crash mid-write leaves a truncated marker that the regex-based reader half-parses.

**D28. Dead / duplicated code**:
- `RunState.ServiceType` / `-serviceType`: parsed, never used.
- `forceWrite=false` branch and its Italian error strings (3779): unreachable.
- Redundant inner `if (!string.IsNullOrEmpty(RunState.NextWipeUrl))` (3550); numerous redundant `kvchanged = false` / `_errorText = ""` resets.
- `WipeResult = "no"` initial value never returned.
- `IsEffectiveRoot` USER fallback effectively only reachable without `/proc`.
- Wipe decode+call block duplicated at 329-340 and 505-516; validation blocks duplicated per OS and per flow (603-678, 712-787) differing only in the separator; blueprint/token/player/map loops are four copies of one routine; `server_dir`/`start_script` parameters of `CreateUpdateresultAndStartScript` duplicate `RunState`.
- Log typo `servel.levelurl updated` (3555); alert text `Plugin data directory  not found.` has a double space.
- `ValidateFlow` 593/702 "server directory doesn't exist" unreachable once `Logger.Init` created the directory.

**D29. Mixed-language, non-stable error strings** end up in the user-facing `wipe_info` (`File non trovato: ...`).

**D30. Unsupported OS** (3319-3332): on macOS all paths are empty strings; the cycle dies with an `ArgumentException` from `Directory.GetFiles("")` after possibly having processed nothing — handled as D1/D8 depending on flow.

---

## 8. Open questions

- **Q1.** Should a wipe without `NextWipeMapUrl` really remove an existing `server.levelurl` (D7), or only when a `NextWipeLevel`/seed/size is supplied?
- **Q2.** What exact key does `server.writecfg` emit for the wipe-timer override, and should v2 remove it by case-insensitive match on both the short and the prefixed name (D13)?
- **Q3.** Is it intended that a map wipe keeps the `*.files*` database (deleted only with "player data"), and what real files are the `.dat` and `relationship` patterns meant to hit? The intent behind grouping needs confirming before turning substrings into precise globs.
- **Q4.** When seed/size are not supplied and `NextWipeRandomSeed` is false the old seed stays and the same procedural map regenerates after the wipe. Intended (admin sets them elsewhere) or should the app warn?
- **Q5.** Many setups pass `+server.seed/+server.worldsize/+server.level/+server.hostname` on the RustDedicated command line (the shipped example scripts do). Which wins against `server.cfg` on a given server build, and should v2 warn that cfg edits may be shadowed? Not determinable from this code.
- **Q6.** Should `wiped` be `"yes"` when the map step found nothing (D9)? Should a partial failure be reported distinctly (e.g. `partial`)?
- **Q7.** Service start: is `start` (not `restart`) with no post-check the desired contract, given users' existing sudoers rules whitelist exactly `systemctl start <unit>`? Should failure of the start command be reflected in the marker?
- **Q8.** `NextWipeResetRustPlus` is not reset by the plugin after a successful wipe (plugin:1188-1199) while the other NextWipe flags are — oversight or intentional persistent setting?
- **Q9.** Should plugin datafiles in sub-folders of `oxide/data` and wildcard patterns be supported (README says "exact filenames")?
- **Q10.** Is custom-wipe without any backup intentional (speed) or an omission? At minimum `server.cfg` could be copied before editing.
