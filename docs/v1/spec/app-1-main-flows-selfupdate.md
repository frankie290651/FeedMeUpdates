# A1 — Updater app: logging, CLI, flow orchestration, validation, server-stop wait, self-update, process/service probes

Source: `Application/Program.cs` lines 1–1375 (v1.6.5 code base; plugin `[Info("FeedMeUpdates","frankie290651","1.6.5")]`).
All line numbers refer to `Application/Program.cs` unless prefixed `plugin:` (= `FeedMeUpdates.cs`).
Functions outside 1–1375 are described only as far as needed to state what the exit paths of this range leave behind; they are marked "(out of range)".

Conventions: `<SD>` = server directory (`RunState.ServerDir`). "Start server" = start script (script mode) or service start (service mode).

---

## 0. Big picture of this range

```
Main(args)                      parse CLI -> RunState (static globals) -> pick flow by -update_id
 └─ HandleFlow(kind)
     1. Logger.Init(<SD>)                       (creates <SD> if missing!)
     2. ValidateFlow(kind)                      (BEFORE the server has stopped)
     3. WaitForRustDedicatedAndLog()            (infinite wait, 2 s poll) + AutoUpdateTask() (self-update)
     4. updating.lock check / create
     5a. Wipe flow:  WipeCycle -> marker -> start server -> exit
     5b. other flows: temp_backup -> [version guards] -> UpdateServer / UpdateOxide -> [steambid.temp]
                      -> [UpdatePlugins] -> [force WipeCycle] -> marker -> delete temp_backup -> start server
```

Process exit code is **always 0** on every handled path (success, validation failure, update failure, bad CLI). The only non-zero exits are unhandled exceptions (see D-list). The caller (plugin) never reads the exit code; the only result channel is `updateresult.json`.

---

## 1. Logger (lines 20–100)

| Item | Value |
|---|---|
| File | `<SD>/updater.log` (`Path.Combine(serverDir, "updater.log")`, l.34) |
| Line format | `[dd/MM/yy HH:mm:ss] [LEVEL] message` — **local time**, 2-digit year (l.69–78) |
| Levels | `INFO`, `WARN`, `ERROR` (l.87–89) |
| Sinks | every line goes to `Console.WriteLine` **and** the file; each sink wrapped in its own swallow-all try/catch (l.81–82) |
| Rotation | Only at `Init`: if file exists and `Length > 5*1024*1024` bytes (strictly greater, l.24, l.41) the file is opened with `FileMode.Create` = **truncated, no archive kept**. Otherwise `Append`. If file does not exist: `Create`. If reading the size throws: `Append`. |
| Open mode | `FileStream(logPath, mode, FileAccess.Write, FileShare.Read)`, `StreamWriter{AutoFlush=true}` (default UTF-8, no BOM on append) (l.58–59) |
| Thread safety | single static `lock(sync)` around init/write/close |
| Failure | any exception in `Init` → `writer = null`; logging silently degrades to console only (l.61–64). On Linux the plugin launches the app with `>/dev/null 2>&1` (plugin:2271–2282), so in that case **nothing is logged anywhere**. |
| Side effect | `Directory.CreateDirectory(serverDir)` (l.33) — creates the server dir if it does not exist (see D-12). |
| Close | `Logger.Close()` flush+dispose, idempotent (l.92–99). Called manually before every `return` in HandleFlow; no `finally`. |

Notes:
- Multi-line messages are written as-is (only the first line gets the prefix).
- Anything printed in `Main` before `HandleFlow` (CLI errors) uses `Console.WriteLine` only — never reaches `updater.log`.
- No log line is written per-run separator; the first line of a run is `"{KIND} flow started. Args: ..."` (l.296) which logs only: update_id, what, update_plugins, server_dir, steamcmd, start_script, isService, serviceName.
- The backup step (out of range, l.1672) logs one `Copied: <rel>` line per file, so on a real server the 5 MiB threshold is typically exceeded every run → the log effectively only ever holds the last 1–2 runs.

## 2. RunState (lines 102–132) and Program statics (140–143)

Static mutable globals, populated once in `Main`:

`UpdateId, What, UpdatePluginsFlag, ServerDir, SteamCmd, StartScript, RemoteBuild, RemoteOxide, UpdatedPlugins (List<string>), IsService, ServiceType, ServiceName, ShowServerConsole, ServerTmux, IsForce, NextWipeLevel, NextWipeUrl, NextWipeSeed, NextWipeMapsize, NextWipeKeepBps, NextWipeResetRustPlus, NextWipeDeletePlayerData, NextWipeDelPluginsData (raw arg), PdataFilesToDelete (decoded list), ServerIdentity, ServerName, ServerDescription`.

Program statics: `FMU_VERSION` (local plugin version, set by AutoUpdateTask), `isStandalone` (set by GetRemoteFMURelease), `Fails` (free-text fragment used to build `fail_reason` in restore markers, l.1770/1828), `__PluginRequestTimestamps` (uMod rate limiter, out of range).

`RunState.ServiceType` is parsed and stored but **never read anywhere** in Program.cs (dead argument).

---

## 3. CLI contract (Main l.148–271, SplitFlagToken l.913–924, ParseKeyValueArgs l.927–955, ParseBool l.958–965, RevertFromb64 l.274–286)

### 3.1 Parsing rules

1. No args → prints `No arguments provided. Exiting.` → return 0.
2. `args[0].Trim()` must start with `-`, else `First argument is not a flag. Exiting.` → 0.
3. First flag key (after stripping **all** leading `-`, split at first `=`) must equal `update_id` (case-insensitive), else `First flag is not -update_id. Exiting.` → 0. **`-update_id` must be the first argument.**
4. Value of update_id: either inline (`-update_id=VALUE`, if non-empty) or the next token `args[1]` taken verbatim (even if it starts with `-`). Missing → `Missing value for -update_id. Exiting.` → 0.
5. Remaining tokens → `ParseKeyValueArgs`:
   - tokens not starting with `-` that are not consumed as a value are **ignored**;
   - key = token without leading dashes up to first `=`; lower-cased; dictionary is case-insensitive; **last occurrence wins**;
   - value = text after `=` if non-empty; otherwise the next token **iff it does not start with `-`** (then consumed); otherwise `""`;
   - consequences: a value beginning with `-` can only be passed as `-key=-value`; `-key=` followed by a non-dash token consumes that token as value; unknown keys are silently accepted and ignored.
6. `ParseBool`: null/empty → false; `bool.TryParse` (`true`/`false`, case-insensitive, surrounding whitespace allowed); `"1"` → true; `"0"` → false; anything else (incl. `yes`) → false.
7. `RevertFromb64(v)`: null → `""`; if value starts with `b64:` (case-sensitive) the rest is standard Base64 → UTF-8 string; else returned unchanged. `b64:` with empty payload → `""`. **Invalid base64 throws `FormatException`, not caught** (D-10).

### 3.2 Flags

| Flag (case-insensitive) | RunState field | Type / default | Notes |
|---|---|---|---|
| `-update_id` (first!) | UpdateId | required | selects flow, see 3.3. Stored verbatim (case preserved). |
| `-what` | What | string, `""` | `both` / `server` / `oxide` (case-insens.). Testrun: defaulted to `both` if empty (l.255). Plugin sends `none` for wipe. |
| `-update_plugins` | UpdatePluginsFlag | string, `""` | must be `yes` or `no` (case-insens.) for Init/Testrun/Scheduled; **no default** → missing = validation failure. Not validated for Wipe. |
| `-server_dir` | ServerDir | string, default **current working directory** only if the flag is absent (null). If present but empty → `""` (then all `Path.Combine("", x)` become CWD-relative and Logger.Init fails silently). |
| `-steamcmd` | SteamCmd | string `""` | full path to steamcmd executable/script |
| `-start_script` | StartScript | string `""` | |
| `-remote_build` | RemoteBuild | string `""`; for Scheduled flow empty → `"no"` (l.264) | Steam build id expected by plugin |
| `-remote_oxide` | RemoteOxide | string `""`; for Scheduled flow empty → `"no"` (l.265) | Oxide release tag expected by plugin |
| `-isservice` | IsService | bool false | plugin sends `1`/`0` |
| `-servicetype` | ServiceType | string `""` | **unused** |
| `-servicename` | ServiceName | string `""` | |
| `-showserverconsole` | ShowServerConsole | bool false | plugin sends inverse of `RunServerScriptHidden`. Forced to false when IsService (l.248–249). |
| `-servertmux` | ServerTmux | string `""` | tmux session name for start script (Linux) |
| `-isforce` | IsForce | bool false | only acted on in the Scheduled flow (force wipe after update); also alters failure-marker shape (see 6.2) |
| `-nextwipelevel` | NextWipeLevel | string | |
| `-nextwipeurl` | NextWipeUrl | string, `b64:` decoded | |
| `-nextwipeseed` | NextWipeSeed | string | |
| `-nextwipemapsize` | NextWipeMapsize | string | |
| `-nextwipekeepbps` | NextWipeKeepBps | bool false | note: app default false, plugin config default true (always passed explicitly) |
| `-nextwiperesetrustplus` | NextWipeResetRustPlus | bool false | |
| `-nextwipedelplayerdata` | NextWipeDeletePlayerData | bool false | |
| `-nextwipedelpluginsdata` | NextWipeDelPluginsData | string | base64url (no padding) of a JSON string array, or raw JSON array; decoded by `DecodeFileListArgToList` (l.3649, out of range) |
| `-serveridentity` | ServerIdentity | string; empty → `"my_server_identity"` (set in ValidateFlow l.600/709 and again in WipeCycle l.3316) | |
| `-servername` | ServerName | string, `b64:` decoded | |
| `-serverdescription` | ServerDescription | string, `b64:` decoded | |

### 3.3 Flow selection (l.251–270)

| update_id | Flow | Pre-adjustment |
|---|---|---|
| `init` (case-insens.) | Init | none (so `-remote_build no -remote_oxide no` must be passed explicitly, see V-Init) |
| `testrun` (case-insens.) | Testrun | `What` empty → `both` |
| `wipe` (case-insens.) | Wipe (custom wipe, no update) | none |
| regex `^\d{8}$` | Scheduled | empty RemoteBuild/RemoteOxide → `no` |
| anything else | prints `Unrecognized update_id value: '<v>'. Exiting without actions.` → return 0 (no marker, no log file, server not restarted) |

### 3.4 How the plugin actually calls it (plugin:3700–3785, 2121–2292) — the de-facto contract

- Always passes all 25 flags in the order of the table above (update_id first), **always as separate tokens** `-key value`.
  - Windows: `-key "value"` (double-quoted, no escaping).
  - Linux: `-key 'value'` (single-quoted) inside `/bin/bash -c "nohup \"<exe>\" <args> >/dev/null 2>&1 &"` with working dir = ServerDirectory. With ShowUpdaterConsole: `gnome-terminal -- bash -lc 'exec '<exe>' <args>'`, or tmux session **`feedmeupdate`** (`new-session -d -s feedmeupdate ...` / `send-keys -t feedmeupdate ... C-m`).
  - Windows hidden: direct `Process.Start(exe, args)` with WorkingDirectory = exe dir, `CreateNoWindow`; with console: `cmd.exe /c start "FeedMeUpdates" "<exe>" <args>`.
- Values are whitelisted by the plugin to `^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$` (plugin:3788–3794); `-nextwipeurl`, `-servername`, `-serverdescription` are always sent as `b64:<standard base64 of UTF-8>` (even empty → `b64:`). Total length guard 32500 chars (Windows command-line limit ≈ 32767): description dropped first, else launch aborted.
- update_id values: `init` (plugin has no LocalSteamBuildID yet, plugin:3518), `testrun` (command `feedme.testrun`, what=both, real remote args), `wipe` (custom wipe, what=`none`, remote args `no`), or a random 8-digit decimal `Random.Next(0,100000000).ToString("D8")`.
- Scheduled: `-remote_build` = build id detected by plugin if server update requested else `no`; `-remote_oxide` = tag if oxide update requested else `no`.
- **Ordering:** the plugin starts the app first and issues `quit` to the server 0.5 s later (plugin:3401–3414). The app therefore always starts while the server is still running.

---

## 4. HandleFlow (lines 293–578) — common prologue

1. `Logger.Init(<SD>)` (l.295).
2. Log `"{KIND} flow started. Args: ..."` (KIND = `INIT|TESTRUN|SCHEDULED|WIPE`).
3. `updatingLockPath = <SD>/updating.lock` (hard-coded name; the plugin's `UpdaterLockFileName` config is NOT passed to the app — README says "leave as is").
4. `ValidateFlow(kind)` — on false: `Logger.Close(); return 0`. (ValidateFlow has already written a failed marker and tried to start the server, see §5.) **No lock is created, the server-stop wait is skipped.**
5. Log `"{KIND} validation passed."`
6. `WaitForRustDedicatedAndLog()` (§7) — blocks until server/service stopped, then runs self-update (§8).
7. Lock (l.312–323):
   - if `updating.lock` exists → WARN, `CreateUpdateresultAndStartScript(<SD>, UpdateId, "Blocked by updating.lock file", StartScript)` (§6.2: writes failed marker, deletes `temp_backup`, starts server), log `Program exits (lock present).`, return 0. Lock is left in place.
   - else `File.Create(lock).Dispose()` — empty file. Not wrapped in try/catch.
8. **The app never deletes `updating.lock`** on any path. It is removed by the plugin on next boot (`ProcessUpdaterLockIfPresent`, plugin:1221–1232, called after marker processing, plugin:788–789) and it is read by the wrapper start scripts (see K-1).

### 4.1 Wipe flow (`-update_id wipe`), l.325–391

No backup, no update, no plugin update.

1. Log `CUSTOM WIPE request detected.`
2. If `NextWipeDelPluginsData != ""` → `PdataFilesToDelete = DecodeFileListArgToList(arg)`; if result count is 0 → WARN `Plugins datafile argument interpretation failed. No plugin data will be deleted.` and continue.
3. `wipeResult = WipeCycle(out wipeingErrors)` (out of range, l.3308; returns `"yes"`, accumulates soft problems as `[text]` fragments in the out string; has no internal try/catch around file operations).
4. Exception in 2–3 → ERROR `CUSTOM WIPE: WipeCycle error: <msg>`; `CreateUpdateresultAndStartScript(<SD>, UpdateId, ex.Message, StartScript)` → failed marker (wipe shape) + start server; return 0. (Wipe may be half-done; there is no backup to restore.)
5. Success marker `<SD>/updateresult.json` (indented JSON):
   ```json
   { "result": "success", "fail_reason": null, "timestamp": "<DateTime.UtcNow 'o' format>",
     "update_id": "wipe", "wiped": "<wipeResult>", "wipe_info": "<wipeingErrors>" }
   ```
   Write failure → ERROR only, continue.
6. Start server: script mode → `StartServerScript(StartScript, <SD>)`; service mode → `RestartRustService(ServiceName)` (return value ignored). Exceptions → ERROR only.
7. Log `WIPE: Flow complete. Exiting.`; return 0.

State left: lock present; marker success; no temp_backup touched (a stale one is NOT cleaned on the success path).

### 4.2 Init / Testrun / Scheduled — backup (l.393–400)

`CreateTempBackup(<SD>, UpdateId, out backupPath)` (out of range l.1617): copies whole `<SD>` to `<SD>/temp_backup` excluding `temp_backup`, `updater.log`, `FeedMeUpdates.exe`, `FeedMeUpdates`.
Failure → ERROR, `CreateUpdateresultAndStartScript(..., "Failed backup creation", ...)` (failed marker; `temp_backup` deleted; server started); return 0.

### 4.3 Scheduled flow body (l.402–463)

If `What` ∈ {server, both}:
1. `steamBuildId = GetRemoteRustBuild()` (out of range l.1954: `steamcmd +login anonymous +app_info_print 258550 +quit`, 30 s timeout, parses `branches → public → buildid`; returns `"no"` on any failure).
2. If `RemoteBuild` empty or `no` → `Fails = "server update (different version detected)"` → `RestoreFromBackupOrFail` → exit. (Checked after the steamcmd call.)
3. If `steamBuildId != RemoteBuild` (OrdinalIgnoreCase) → ERROR `Remote build mismatch ...`, same `Fails`, restore → exit.
4. `RunUpdateServerWithRestoreOnFail` (l.877–892): `UpdateServer()`; on false `Fails = "server update"`, restore → exit.

If `What` ∈ {oxide, both}:
1. `(tag,url) = GetRemoteOxideRelease()` (out of range l.2042).
2. `RemoteOxide` empty/`no` → `Fails = "oxide update (different version detected)"`, restore → exit.
3. `tag != RemoteOxide` (OrdinalIgnoreCase) → same → exit.
4. `RunUpdateOxideWithRestoreOnFail` (l.895–910): `UpdateOxide()`; on false `Fails = "oxide update"`, restore → exit.

Purpose of the guards: the plugin made its decision (protocol compatibility, scheme rules) for a specific build/tag; if upstream moved in the countdown interval the run is aborted as failed so the plugin re-evaluates on next boot.

`RestoreFromBackupOrFail` (out of range l.1693–1820) never returns normally: both its success path and `HandleRestoreFailure` call `Environment.Exit(0)`. So every `Logger.Close(); return 0;` / `return false` after it in this range is dead code. Its effects: copy `temp_backup` back over `<SD>`; write marker `{result:"failed", fail_reason:"Failed "+Fails, backup_cycle:true, server_restored:"yes", timestamp:"yyyy-MM-ddTHH:mm:ssZ", update_id}`; `CleanTempBackup`; **start `start_script` only** (no service start, D-2). On restore failure: marker `fail_reason:"Failed restore after failed "+Fails`, `server_restored:false`; `CleanTempBackup` + start script only if start_script non-blank.

### 4.4 Init / Testrun body (l.464–485)

1. `steamBuildId = GetRemoteRustBuild()` — only logged (`"{KIND}: Steam build id determined: ..."`).
2. `RunUpdateServerWithRestoreOnFail`, then `RunUpdateOxideWithRestoreOnFail` (always both; no version guards).
3. Init only: write `<SD>/steambid.temp` containing `steamBuildId` verbatim (no newline; may be the literal `no`, D-14). Failure → ERROR only. The plugin reads this on boot, stores it as `LocalSteamBuildID` in its datafile and deletes the file (plugin:982–1013). This is how the plugin leaves the "init" state.

### 4.5 Common tail (l.487–577)

1. Plugins: if `UpdatePluginsFlag == yes` (case-insens.) → `UpdatePlugins()`; false result → WARN `Plugin update pass reported errors. Proceeding.` (never fails the run). Else log skip.
2. Force wipe: only if `kind == Scheduled && IsForce`:
   - log `FORCE WIPE detected.`; decode plugin-datafile list as in 4.1; `WipeCycle`.
   - **Exception → ERROR `SCHEDULED: WipeCycle error`, WARN `IMPORTANT! SERVER IS UPDATED BUT MUST BE WIPED MANUALLY.`, `Logger.Close(); return 0`** — no marker, no temp_backup cleanup, no server start (D-1).
3. Success marker `<SD>/updateresult.json`:
   ```json
   { "result": "success", "fail_reason": null,
     "updated_plugins": [...],          // only when update_plugins == yes (may be empty array)
     "backup_cycle": true, "server_restored": "no",
     "timestamp": "<UtcNow 'o'>",
     "update_id": "init" | "testrun" | "<8 digits as given>",
     "wiped": "no" | "yes", "wipe_info": "<text>" }
   ```
   Write failure → ERROR only; flow continues (server is started with no marker).
4. `CleanTempBackup(<SD>)` (deletes `<SD>/temp_backup` recursively; failures only logged).
5. Start server (same as 4.1 step 6; exceptions/false only logged).
6. Log `"{KIND}: Flow complete. Exiting."`; return 0.

### 4.6 Exit-path matrix

L = `updating.lock` present after exit, M = marker, B = `temp_backup`, S = server start attempted.

| # | Path | L | M | B | S |
|---|---|---|---|---|---|
| E1 | Bad/missing CLI, unknown update_id (Main) | not created | none | untouched | **no** |
| E2 | Invalid `b64:` value → unhandled exception in Main | not created | none | untouched | **no** |
| E3 | ValidateFlow failure | not created (pre-existing stays) | failed, reason per §5 | **deleted if present** (if start target configured) | yes — **while old server may still be running** (D-3) |
| E4 | Server never stops | — (blocks forever) | none | — | no |
| E5 | Lock already present | stays | failed `Blocked by updating.lock file` | **deleted** | yes |
| E6 | `File.Create(lock)` throws | no | none | untouched | **no** (unhandled exception) |
| E7 | Wipe: WipeCycle throws | yes | failed (ex.Message), wipe shape | n/a (stale one deleted) | yes |
| E8 | Wipe success | yes | success | untouched | yes |
| E9 | Backup creation fails | yes | failed `Failed backup creation` | deleted (partial) | yes |
| E10 | Scheduled: remote_build missing / mismatch, remote_oxide missing / mismatch | yes | failed `Failed server update (different version detected)` / `Failed oxide update (different version detected)`, `server_restored:"yes"` | deleted after full restore | **script only; never the service** |
| E11 | UpdateServer / UpdateOxide fails | yes | failed `Failed server update` / `Failed oxide update` | deleted after restore | script only |
| E12 | Restore itself fails | yes | failed `Failed restore after failed <Fails>`, `server_restored:false` | deleted iff start_script non-blank (!) | script only |
| E13 | Force WipeCycle throws | yes | **none** | **left** | **no** |
| E14 | Success | yes | success | deleted | yes |

---

## 5. ValidateFlow (lines 581–860)

Every failure: `Logger.Error(...)`, then `CreateUpdateresultAndStartScript(<SD>, UpdateId, <reason>, StartScript)` → return false.
Checks in order; first failure wins. Reason strings are the `fail_reason` in the marker (they reach Discord through the plugin).

### V-Wipe (kind == Wipe), l.583–680 — returns true immediately after these (none of the common checks run)
1. ServerDir empty → `Wipe validation failed: server dir not specified`
2. `!Directory.Exists(ServerDir)` → `Wipe validation failed: server directory doesn't exist` (unreachable in practice, D-12)
3. ServerIdentity empty → set to `my_server_identity` (Rust's default identity).
4. OS block — executed for Windows with `\` separators and for Linux with `/`; **on any other OS no checks at all**:
   - if `NextWipeDelPluginsData` non-empty: `<SD>/oxide/data` must exist → `Wipe validation failed: plugin data directory doesn't exist`
   - `<SD>/server/<identity>` must exist → `... server identity directory doesn't exist`
   - `<SD>/server/<identity>/cfg` must exist → `... server cfg directory inside server identity doesn't exist`
   - `<SD>/server/<identity>/cfg/server.cfg` must exist → `... server.cfg not found`

Not validated for Wipe: start_script / serviceName, RustDedicated presence, update_plugins, what.

### V-Init/Testrun, l.681–689
- `What` must equal `both` → `Init validation failed: what!='both' (<what>)` / `Testrun validation failed: ...`

### V-Scheduled, l.690–797
- If `IsForce`: identical block to V-Wipe 1–4 with prefix `ForceWipe validation failed: ...`
- `What` ∈ {both, server, oxide} → else `Scheduled validation failed: invalid what (<what>)`

### Common (Init, Testrun, Scheduled), l.799–857
`label` = `Init` / `Testrun` / `Scheduled`.
1. `UpdatePluginsFlag` ∈ {yes, no} → `<label> validation failed: invalid update_plugins (<v>)`
2. `<SD>/RustDedicated.exe` or `<SD>/RustDedicated` must exist (either name accepted on either OS) → `<label> validation failed: RustDedicated missing`
3. If Init, Testrun, or What ∈ {server, both}: `SteamCmd` non-empty and `File.Exists` → `<label> validation failed: steamcmd missing`
4. Script mode (`!IsService`): `StartScript` non-empty and `File.Exists(StartScript)` (path as given; relative paths resolve against CWD here but against `<SD>` later in StartServerScript l.1382) → reason is **always** `Init validation failed: start_script missing` regardless of flow.
5. Service mode: `ServiceName` non-empty → `Init validation failed: serviceName missing` (again always "Init"). No check that the service exists.
6. Init only: `RemoteBuild` and `RemoteOxide` must both equal `no` → `Init validation failed: remote args must be 'no'`.

Not validated anywhere: that steamcmd is executable, write permission on `<SD>`, free disk space for the backup, ServerDir non-empty for non-wipe flows, values of seed/mapsize/level (the plugin sanitises these).

---

## 6. Helpers in range and the two marker writers they depend on

### 6.1 In range
- `IsWhatServerOrBoth` / `IsWhatOxideOrBoth` (l.867–874): case-insensitive compare with `both`/`server`/`oxide`.
- `RunUpdateServerWithRestoreOnFail` / `RunUpdateOxideWithRestoreOnFail` (l.877–910): see 4.3; the `false` return is unreachable because restore exits the process.
- `SplitFlagToken`, `ParseKeyValueArgs`, `ParseBool`, `RevertFromb64`: see §3.1.

### 6.2 `CreateUpdateresultAndStartScript(server_dir, update_id, failReason, start_script)` (out of range, l.3195–3301) — used by every validation failure, lock block, backup failure, custom-wipe exception
- Writes `<server_dir>/updateresult.json` (indented), `timestamp` format `yyyy-MM-ddTHH:mm:ssZ` (UTC):
  - if `!IsForce && update_id == "wipe"` (**case-sensitive**): `{result:"failed", fail_reason, timestamp, update_id, wiped:"no", wipe_info:""}`
  - else: `{result:"failed", fail_reason, [updated_plugins if flag=="yes" (case-sensitive) and list non-empty], backup_cycle:false, server_restored:false, timestamp, update_id, wiped:"no", wipe_info:""}`
- Then: script mode & start_script non-blank → `CleanTempBackup` + `StartServerScript`; service mode & ServiceName non-blank → `CleanTempBackup` + `RestartRustService`. Otherwise WARN `... not specified; skipping start.` All exceptions logged only.

### 6.3 Marker field inventory (external contract with plugin, plugin:1103–1181)
`result` (`success`|`failed`), `fail_reason` (string|null), `updated_plugins` (string array, optional), `backup_cycle` (bool), `server_restored` (**mixed types**: string `"no"`/`"yes"` or boolean `false`), `timestamp` (two different formats), `update_id`, `wiped` (`yes`|`no`), `wipe_info` (string). The plugin parses with regexes (not a JSON parser), accepts aliases `updateId`, `error`, `wipe_result`, `wipeinfo`; uses only result, update_id, fail_reason, wiped, wipe_info, updated_plugins. On `result == success` it promotes `RemoteSteamBuildID → LocalSteamBuildID`; on `wiped == yes` it resets all NextWipe* config; it archives the file to `<SD>/<MarkersSubfolder>/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json`; `failed` increments the retry counter (`MaxAttempts`).

---

## 7. WaitForRustDedicatedAndLog (lines 968–999)

- Script mode: log `Waiting for RustDedicated(.exe) to stop...`; loop `while (IsProcessRunning("RustDedicated")) Thread.Sleep(2000);` then log `... stopped.`
- Service mode: log `Waiting for service <name> to stop...`; loop `while (IsServiceRunning(ServiceName)) Thread.Sleep(2000);` then `<name> service stopped.` (the RustDedicated process itself is not checked in service mode).
- **No timeout, no max iterations** in either loop.
- Then unconditionally `AutoUpdateTask()`; true → `Autoupdate process completed. System updated.`; false → `Autoupdate process completed.` (the "System updated" text is also printed when nothing needed updating, see D-17).
- There is no settle delay after the process disappears (file handles are assumed released).

### 7.1 IsProcessRunning(name) (l.1361–1372)
`Process.GetProcessesByName("RustDedicated").Length > 0`; any exception → false (treated as "stopped"). Machine-wide, all users, no path/owner filter. Process objects are not disposed. Name has no `.exe` on either OS.

### 7.2 IsServiceRunning(name) (l.1296–1358)
- Blank name → false.
- Windows: run `sc query "<name>"` (no shell, no window, stdout+stderr captured as UTF-8, `WaitForExit(4000)` after reading). Regex `STATE\s*:\s*(\d+)` (ignore case): running iff code ∈ {4 RUNNING, 2 START_PENDING, 3 STOP_PENDING}. If the regex does not match: running iff output contains `RUNNING` (case-insens.). So STOPPED(1), PAUSED(7), PAUSE_PENDING(6), CONTINUE_PENDING(5), "service does not exist" (1060), access denied → all "not running".
- Non-Windows: run `systemctl is-active --quiet <EscapeArg(name)>` (found via PATH, no sudo), `WaitForExit(4000)`, running iff exit code 0. `EscapeArg` wraps in single quotes when the name contains a space or `"` (ineffective without a shell, D-19).
- Any exception (binary missing, `ExitCode` read on a still-running process after the 4 s wait) → false = "not running" (fail-open).

---

## 8. Self-update (AutoUpdateTask l.1006–1170, IsFrameworkDependent l.1173–1184, GetRemoteFMURelease l.1187–1289)

Runs on **every** invocation of every flow, after the server stopped and **before** the lock is created and before the backup. There is no flag or config to disable it.

### 8.1 Procedure
1. Plugin paths: Windows `<SD>\oxide\plugins\FeedMeUpdates.cs` and `...\FeedMeUpdatesGUI.cs`; otherwise `<SD>/oxide/plugins/FeedMeUpdates.cs`, `.../FeedMeUpdatesGUI.cs`.
2. Local version = 3rd string of `[Info("Title","Author","Version")]` found by regex `\[Info\(\s*"(.*?)"\s*,\s*"(.*?)"\s*,\s*"(.*?)"\s*\)\]` in the first 200 lines of the plugin file (`ParseInfoAttribute`, l.2808). Not found / unreadable → WARN `Unable to get local FMU version from plugin file.` → return false. **The app has no version of its own**; the plugin file's version is the version of the whole system.
3. `GetRemoteFMURelease()`:
   - `GET https://api.github.com/repos/frankie290651/FeedMeUpdates/releases/latest`, header `User-Agent: FeedMeUpdates/1.0` (GitHub rejects requests without UA), timeout 20 s, no auth, no Accept header, no retry.
   - Non-2xx → WARN `GetRemoteFMURelease: GitHub API returned <code> <reason>` → `("no","")`.
   - tag = `tag_name` (string) else `name`.
   - assets: list of `(name.ToLowerInvariant(), browser_download_url)` in API order.
   - `isStandalone = !IsFrameworkDependent()`; `IsFrameworkDependent` = `AppContext.GetData("FX_DEPS_FILE")` or `AppContext.GetData("FX_DEPS_FOLDER")` is a non-empty string (the `APP_CONTEXT_DEPS_FILES` read at l.1181 is unused).
   - **Asset selection (first match in API order wins):**

     | asset name contains | chosen when |
     |---|---|
     | `win` and `standalone` | Windows + self-contained app |
     | `win`, not `standalone` | Windows + framework-dependent app |
     | not `win`, `standalone` | non-Windows + self-contained |
     | neither | non-Windows + framework-dependent |

     So the release must publish four zip assets whose lower-cased names are distinguished only by the substrings `win` and `standalone` (e.g. `...win-standalone.zip`, `...win.zip`, `...linux-standalone.zip`, `...linux.zip`); exact names are not checked in code (unverified against real releases).
   - Zero assets, or exception → `("no","")`. Assets present but no match → `(tag,"")`.
4. If `tag == "no"` or url empty → return false (silently).
5. `CompareVersionStrings(tag, local)` (l.2853; `System.Version` parse with fallbacks: first `\d+(\.\d+){0,3}` run, so `v1.6.5` works). `<= 0` → `No FMU updates required.` → return true.
6. Newer: 
   - temp zip `<TMP>/FMU_update_<guid N>.zip`, extract dir `<TMP>/FMU_extracted_<guid N>` (`Path.GetTempPath()`).
   - Download with `HttpClient{Timeout=5 min}`, UA `FeedMeUpdates/1.0`, `EnsureSuccessStatusCode`; body buffered then copied to file. Failure → ERROR `Download failed: ...` / `Error writing temp file: ...`, delete zip, return false. Zero-length → ERROR, return false.
   - `ZipFile.ExtractToDirectory(zip, extractRoot, overwriteFiles:true)`; failure → cleanup, return false.
   - `origin = FindExtractOrigin(extractRoot) ?? extractRoot` (helper written for Oxide zips; for FMU zips effectively `extractRoot`).
   - For every file under origin (recursive), by **case-sensitive `EndsWith` on the full path** (all four tests are evaluated for each file):
     | path ends with | action |
     |---|---|
     | `FeedMeUpdates.cs` | `File.Delete(<plugin>)`; `File.Move(file, <SD>/oxide/plugins/FeedMeUpdates.cs)` — **live replacement, effective at next boot** |
     | `FeedMeUpdatesGUI.cs` | same to `.../FeedMeUpdatesGUI.cs` (installed even if it was not present before) |
     | `FeedMeUpdates` or `FeedMeUpdates.exe` | Windows: delete + move to `<SD>\FeedMeUpdates.exe.new`; else `<SD>/FeedMeUpdates.new` |
     | `FMU_CHECKSUMS.txt` | delete + move to `<SD>/FMU_CHECKSUMS.txt` |
     Any exception → ERROR `Error copying FMU files: ...`, cleanup temp, return false (files moved so far stay moved).
   - Cleanup temp dir + zip; log `FMU plugin successfully updated, .new file created and checksum file created.` (printed regardless of which files were actually found); return true.
7. The staged files are always placed in `<SD>` (the `-server_dir`), not next to the running executable. The running binary is never replaced by the app itself (on Windows it is locked).

### 8.2 Second stage — done by the plugin on next boot (plugin:2826–3012), for reference
- Looks for `<ServerDirectory>/FeedMeUpdates.exe.new` (Windows) or `/FeedMeUpdates.new`; and `<ServerDirectory>/FMU_CHECKSUMS.txt`.
- **Checksum file format:** the *entire* file content, read as ASCII, trimmed, with spaces, `-` and `:` removed, must be either 64 hex chars or a base64/base64url string decoding to exactly 32 bytes = SHA-256 of the staged binary. No filename column, single hash only (a `sha256sum`-style `hash  name` line is NOT accepted; an internal newline is not stripped).
- Constant-time compare; on match: delete `UpdaterExecutablePath`, move `.new` there, `chmod +x` on Linux, delete checksum file. On any failure both staged files are deleted and the update is abandoned.
- The new plugin `.cs` is installed with no verification at all.

---

## 9. Implicit knowledge / workarounds worth preserving

K-1 **`updating.lock` is a three-party protocol, not just a mutex.** The shipped wrapper scripts (`Scripts/startserver.sh`, `.bat`) run RustDedicated in a `while true` loop, sleep 5 s after it exits, and `exit` iff `updating.lock` exists, otherwise restart the server. So: app creates the lock after the server stops → wrapper notices and ends its loop → app later starts a fresh wrapper → plugin deletes the lock on boot. The app never deletes the lock. The 5 s sleep in the script vs. the app's 2 s poll + self-update is the only thing that makes this work (see D-4).

K-2 **App is launched before the server quits** and must outlive it. Linux: `nohup ... &` via bash; under systemd this only survives with `KillMode=process`, `SendSIGKILL=no` (README §12) — default `control-group` kill mode would kill the updater with the server. `NoNewPrivileges=false` is required so the updater (child of the service) can `sudo -n systemctl start`. `Restart=on-failure` + `SuccessExitStatus=0 9 SIGKILL` so a clean `quit` does not trigger systemd auto-restart during the update.

K-3 **Windows/NSSM:** service must go to STOPPED when RustDedicated exits by itself (`nssm set AppExit 0 Exit`, plus the two special exit codes 4294967295 and 3221225786, and disabling SCM failure actions), otherwise the wait loop never ends. START_PENDING/STOP_PENDING count as "still running" so the app does not start working on a half-stopped service.

K-4 The textual fallback `output contains "RUNNING"` in `IsServiceRunning` is most likely for localized Windows where the `STATE` label is translated (e.g. Italian `STATO`) while the state keyword stays English (likely intent; unverified).

K-5 Service mode only works when the service runs RustDedicated directly (README): the app waits on service state only, and restore paths assume a script.

K-6 Process name `RustDedicated` without extension works on both OSes with `GetProcessesByName`.

K-7 GitHub API requires a `User-Agent`; `FeedMeUpdates/1.0` is used for all GitHub calls.

K-8 Updater binary cannot overwrite itself (Windows file lock) → two-stage update via `.new` + checksum promoted by the plugin while the app is not running.

K-9 Build/tag guards in the Scheduled flow (4.3) protect against upstream changing between detection and execution; the plugin's protocol-compatibility decision is tied to the exact versions it passed.

K-10 `steambid.temp` is the hand-off of the first known build id from app to plugin (Init flow only); without it the plugin re-runs Init on every boot.

K-11 Arguments that can contain arbitrary text (name, description, map URL) are `b64:`-prefixed base64; the plugin-datafile list is base64url JSON. This exists because the command line passes through `cmd`/`bash -c`/tmux `send-keys`/gnome-terminal quoting layers.

K-12 Default identity `my_server_identity` is Rust's own default when `+server.identity` is not set.

K-13 `ShowServerConsole` is forced off in service mode (no console to show).

K-14 Either `RustDedicated` or `RustDedicated.exe` is accepted on any OS (e.g. Wine/Proton setups or simply to avoid an OS switch).

K-15 Self-update happens while the server is down so that the new plugin `.cs` is in place when Oxide compiles plugins at next boot.

---

## 10. Defects and risks

Severity: H = server left down / data at risk / security; M = wrong behaviour with workaround; L = cosmetic or unlikely.

**D-1 (H) Force-wipe exception leaves server down with no marker.** l.518–524. If `WipeCycle` throws during a Scheduled+IsForce run (it has no internal try/catch around `Directory.GetFiles`/`File.Delete`; e.g. a locked `.sav`, permission error), the app logs and returns: no `updateresult.json`, `temp_backup` left on disk (full copy of the server), lock left, **server never restarted**. The custom-wipe path (l.342–348) handles the same exception correctly. On force-wipe day this means an offline server until an admin intervenes.

**D-2 (H) Restore paths never start a service.** l.415/425/445/455/886/904 → `RestoreFromBackupOrFail`/`HandleRestoreFailure` (l.1791–1807, 1847–1863) only call `StartServerScript` when `start_script` is non-blank and ignore `RunState.IsService`. In service mode any failed update or version mismatch (E10–E12) ends with the server stopped (or, if a start script is also configured, with the script being launched outside the service manager). Verified by reading l.1693–1868.

**D-3 (H) Validation runs before the server has stopped; failure path starts a second server.** l.302–306 vs l.310. The plugin launches the app 0.5 s before `quit`. On any ValidateFlow failure the app immediately writes a marker and starts the start script / `sc start` / `systemctl start` while the old instance is still running or shutting down. Script mode: a second wrapper+RustDedicated is launched against the same identity/ports (and the old looping wrapper also restarts the server because no lock exists → two wrappers permanently). Service mode: `sc start` fails with "already running"/"stop pending", then the server finishes quitting → server down. Also deletes any existing `temp_backup` (l.3267/3287).

**D-4 (H) Lock is created too late for the wrapper-script protocol.** l.310 → l.322. Sequence after RustDedicated exits: app notices within ≤2 s, then runs `AutoUpdateTask` (GitHub API call with 20 s timeout, possibly a download with 5 min timeout) and only then creates `updating.lock`. The shipped wrapper checks for the lock exactly 5 s after exit. If self-update takes > ~3 s (slow/blocked GitHub, rate limit latency, real download) the wrapper restarts RustDedicated; the app then creates the lock, backs up and overwrites files of a **running** server, and finally launches a second wrapper. The plugin of the restarted server deletes the lock on boot. Timing-dependent; verified from code + scripts, not reproduced.

**D-5 (H) Infinite wait, fail-open probes.** l.975–987, 1346–1349, 1368–1371. (a) No timeout: if the server hangs on shutdown, or another RustDedicated instance runs on the same machine (multi-server hosts; `GetProcessesByName` is machine-wide), or NSSM/systemd auto-restarts the service, the app waits forever and nothing is reported. (b) Every probe error returns "not running": wrong service name (`sc` 1060), `systemctl` missing/slow (>4 s → `ExitCode` throws), access denied → the app proceeds to back up/overwrite a live server. (c) Linux: `systemctl is-active` returns non-zero for `deactivating` and `activating (auto-restart)`, so the loop can end while RustDedicated is still saving/exiting (systemd semantics; not runtime-tested). (d) Windows localized fallback treats `STOP_PENDING` as stopped when the `STATE` regex does not match.

**D-6 (H, security) Unconditional, unauthenticated self-update.** l.990, 1006–1170. Every run downloads and installs code from the `latest` GitHub release of a hard-coded repo: the plugin `.cs` is replaced with no verification; the binary's checksum file comes from the same zip (integrity only, no signature). No opt-out, no pinning, no backup of the old plugin (it is replaced before `temp_backup` is taken, so a restore does not roll it back). A compromised release/account gives code execution on every server (plugin runs inside the game server; updater may run as root). Also means a broken release bricks all installations automatically.

**D-7 (M) Partial self-update is never retried.** l.1103–1151 + plugin:2971–3003. "Update needed" is decided solely from the plugin file's version. If the plugin `.cs` is replaced but staging the binary fails, or the plugin later rejects the `.new` (checksum mismatch, chmod failure → both staged files deleted), the plugin version already equals the release → the app binary is never updated → plugin/app version skew permanently.

**D-8 (M) Framework-dependent builds: only the apphost is staged.** l.1117–1129. Only files ending in `FeedMeUpdates`/`FeedMeUpdates.exe` are taken; `FeedMeUpdates.dll`, `.runtimeconfig.json`, `.deps.json` are ignored. Unless the framework-dependent asset is published as a single file, the actual code is not updated. Unverified against real release contents.

**D-9 (M) Suffix matching on full paths.** l.1107–1130. `EndsWith` (case-sensitive, not a filename compare) matches e.g. `MyFeedMeUpdates.cs` or any extension-less file whose name ends in `FeedMeUpdates`; if a zip contains both `FeedMeUpdates` and `FeedMeUpdates.exe` the last one enumerated wins. `File.Delete` then `File.Move` is non-atomic: if the move fails the plugin file is gone and the server boots without FMU (nothing then clears the lock or reads the marker).

**D-10 (M) No top-level exception handler.** `Main` l.148–271. `RevertFromb64` (l.282) throws on malformed base64; `File.Create(lock)` (l.322) throws on permission/IO errors. Result: crash with non-zero exit, nothing in `updater.log`, no marker, server not restarted (E2/E6).

**D-11 (M) Lock-present path can sabotage a concurrent run.** l.312–319. A second invocation that finds the lock calls `CreateUpdateresultAndStartScript`, which deletes `temp_backup` (the other run's only rollback copy), overwrites its marker and starts the server while the first run is still updating. Also the check-then-create is not atomic (l.312/322). Conversely a stale lock (server started without the plugin deleting it) blocks every future update but still restarts the server and reports `failed`.

**D-12 (M) Logger.Init creates the server directory.** l.33. A mistyped `-server_dir` is created on disk, which makes the "server directory doesn't exist" checks (l.593, l.702) unreachable; validation then fails later with a less helpful reason, and `updater.log`/`updateresult.json` are written into the bogus directory where the plugin never looks.

**D-13 (M) Wipe flow skips start-target validation.** l.583–680. A custom wipe with a missing start script / empty service name wipes the server and then cannot start it (only an ERROR/WARN line). RustDedicated presence is not checked either.

**D-14 (M) `steambid.temp` may contain `no`.** l.466, 476–478. In Init the steamcmd build query result is written without checking; on query failure the literal `no` becomes the plugin's LocalSteamBuildID, causing a spurious "update available" cycle on the next check. Also in Init/Testrun the query is made before `UpdateServer`, so the recorded id can be older than what was actually installed.

**D-15 (M) Wasteful/misordered guards in Scheduled flow.** l.393–458. Full backup is taken, then steamcmd/GitHub are queried, and on a mismatch a **full restore of an unmodified server** is performed. The "argument missing/`no`" check happens after the network call. With `what=both`, a successful server update followed by an Oxide tag mismatch rolls back the server update too. On a force-wipe day with a second hotfix build this turns the whole force wipe into a failed run (no wipe performed; whether the plugin re-triggers the force wipe afterwards is unverified).

**D-16 (L) Dead code.** `Logger.Close(); return 0;` after every `RestoreFromBackupOrFail` call (l.416–417, 426–427, 446–447, 456–457) and `return false` in l.887–888/905–906 are unreachable (restore calls `Environment.Exit(0)`); `RunState.ServiceType`; `appContextDeps` (l.1181); `EscapeArg` quoting; directory-exists checks (D-12); unused `using`s (`System.Numerics`, `System.Globalization`).

**D-17 (L) Misleading log/return semantics in AutoUpdateTask.** Returns true both for "updated" and "already up to date" → caller logs `System updated.` (l.1038, 991–994). Final success message (l.1156) claims plugin, `.new` and checksum were written without checking that any were found.

**D-18 (L) Duplicated logic.** ValidateFlow contains four near-identical copies of the identity/cfg checks (Wipe×{Win,Linux}, ForceWipe×{Win,Linux}; l.603–678, 712–787) that differ only in separator and message prefix; non-Windows/non-Linux OS silently skips them. Success-marker construction is duplicated (l.351–361, 529–545), as is the "start server" block (l.368–386, 555–573, plus 3261–3300). Path building mixes string concatenation with OS-specific separators and `Path.Combine`.

**D-19 (L) `EscapeArg` in IsServiceRunning** (l.1351–1357) adds shell quoting to a non-shell `Arguments` string; a service name containing a space would be split into two args. `sc query "<name>"` does no escaping of `"` in the name.

**D-20 (L) Wrong labels in messages.** Start-script/service validation failures always say `INIT`/`Init validation failed` (l.833–843) even in Testrun/Scheduled; these strings reach Discord.

**D-21 (L) Marker inconsistencies.** `server_restored` is `"no"`/`"yes"` (string) or `false` (bool); `timestamp` is ISO "o" with 7 fractional digits on success, second-precision on failure; restore-failure markers lack `wiped`/`wipe_info`; marker write is not atomic (plain `File.WriteAllText`) and a write failure still lets the server start with no marker (plugin then never promotes the build id and will re-trigger the update). `CreateUpdateresultAndStartScript` compares `update_id == "wipe"` and `UpdatePluginsFlag == "yes"` case-sensitively although flow selection is case-insensitive.

**D-22 (L) CLI edge cases.** Empty-string `-server_dir ""` yields `ServerDir=""` (CWD-relative operation, no log file) instead of the CWD default; values starting with `-` are dropped unless `=` form is used; `\d` in `^\d{8}$` accepts non-ASCII digits; on Windows the plugin's `-key "value"` quoting breaks if a value ends with `\` (e.g. `C:\rust\` → the closing quote is escaped and the next flag is swallowed) — plugin-side, but part of the contract.

**D-23 (L) Log rotation discards history** (truncate at >5 MiB, no `.1` file), and with per-file backup lines this happens almost every run. Local-time timestamps with 2-digit year and no timezone.

**D-24 (L) Asset heuristics.** l.1238–1277. Any asset whose name lacks `win` is treated as a Linux asset (including a stray `FMU_CHECKSUMS.txt`, `.cs` or source asset if listed first); a name containing `darwin` matches `win`. GitHub unauthenticated rate limit (60 req/h per IP) is not handled beyond logging the status. `IsFrameworkDependent` relies on undocumented host properties (`FX_DEPS_FILE`; `FX_DEPS_FOLDER` is not known to exist) — unverified for single-file publishes.

**D-25 (M, cross-reference, out of range)** `ParseVersion` l.2881–2882: `while (parts.Length < 3) ss += ".0";` never changes `parts` → infinite loop (growing string) when the release tag contains a single number with no dot (e.g. tag `v2` or `2`), reached from AutoUpdateTask l.1034. Verified by reading; would hang every updater run after the server has stopped.

**D-26 (M, cross-reference, out of range)** `HandleRestoreFailure` l.1851 deletes `temp_backup` after a *failed* restore (when start_script is set), destroying the only good copy; `CreateUpdateresultAndStartScript` likewise deletes it on every early failure.

---

## 11. Open questions

1. Should self-update be optional/configurable, and should it really run before the lock/backup and in the Wipe flow? (Today: always, unconditional.)
2. What are the exact release asset names and zip contents (single-file? does the zip include `FeedMeUpdatesGUI.cs`? is `FMU_CHECKSUMS.txt` a bare hash)? The code only implies the substrings `win` / `standalone`.
3. Is `updating.lock` meant to be owned by the app (create + delete) or is "plugin deletes on boot" intentional because the wrapper script needs it to persist? Who should create it so the wrapper race (D-4) disappears — the plugin before `quit`?
4. Should `-isforce` be honoured in Init/Testrun (currently ignored), and should a force wipe still be performed when the update part fails or mismatches?
5. After a failed force `WipeCycle`, is "leave server down" intentional (to avoid running an un-wiped server on wipe day) or an oversight? No marker is written either way.
6. Is `-servicetype` planned for something (NSSM vs sc, systemd vs other)? Currently unused.
7. Intended behaviour when another RustDedicated instance runs on the same host (multi-server machines)?
8. Should a mismatch between `remote_build`/`remote_oxide` and upstream be a `failed` run (counts toward `MaxAttempts`) or a benign "skipped"?
9. Is accepting `RustDedicated.exe` on Linux / `RustDedicated` on Windows deliberate?
10. Plugin-side (not verified in depth): when the plugin fails to launch the app (invalid args/exe) it still calls `quit` in the init and custom-wipe paths (plugin:3518–3520, 4033–4036) — intended?
