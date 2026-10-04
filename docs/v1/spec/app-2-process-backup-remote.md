# A2 — Updater app: process/system helpers, server start, backup/restore, process execution, remote queries

Source: `Application/Program.cs` lines 1293-2156 (read in full), plus lines 18-135 (Logger, RunState) and the callers/helpers needed to understand usage (`HandleFlow` 293-578, `ValidateFlow` 581-860, `RunUpdate*WithRestoreOnFail` 877-910, `WaitForRustDedicatedAndLog` 968-999, `IsUnderAny` 2903-2913, `TmuxSessionExists` 2963-2996, `DetectGnomeAndTmux` 2999-3033, `CleanTempBackup` 3036-3071, `RestartRustService` 3078-3192, `CreateUpdateresultAndStartScript` 3195-3301). Plugin references are to `FeedMeUpdates.cs`.

Line numbers refer to `Application/Program.cs` unless prefixed with "plugin".

Conventions: "verified" = read directly in the code. "External knowledge" = behaviour of a third-party tool (systemd, tmux, SteamCMD, GitHub, .NET runtime) that I know but did not test here; treat it as likely-true and re-check. "Unverified" = I could not confirm it.

---

## 0. Context: Logger and RunState (lines 18-135)

### 0.1 Logger (20-100)

- Static, process-wide, guarded by one lock object.
- `Init(serverDir)` (27-66):
  - `Directory.CreateDirectory(serverDir)` (creates the server dir if missing — side effect).
  - Log file: `<serverDir>/updater.log` (external contract; README documents it).
  - "Rotation" is a truncation: if the file exists and is larger than `5 * 1024 * 1024` bytes (5 MiB) it is opened with `FileMode.Create` (old content discarded, no archive). Otherwise `FileMode.Append`. If the size check throws, Append. If file does not exist, Create.
  - Opened with `FileAccess.Write, FileShare.Read`, wrapped in a `StreamWriter` with `AutoFlush = true` (default UTF-8 without BOM).
  - Any exception → `writer = null`: logging silently degrades to console only.
  - Size is checked only at Init, i.e. once per updater run. A single run can grow the file without bound (see backup logging, D-17).
- Line format: `[dd/MM/yy HH:mm:ss] [LEVEL] message`, local time, levels `INFO`, `WARN`, `ERROR` (69-84). Every line is written to both `Console.WriteLine` and the file; each is wrapped in its own try/catch.
- `Close()` flushes and disposes, sets writer to null (92-99). After Close, log calls still go to console.
- Called from `HandleFlow` line 295 only.

### 0.2 RunState (102-132)

Global mutable static state filled from CLI args in `Main` (220-246). Fields used by the code in this spec:

| Field | CLI arg (case-insensitive key) | Notes |
|---|---|---|
| `UpdateId` | `-update_id` (must be the first arg) | `init`, `testrun`, `wipe`, or 8 digits |
| `ServerDir` | `-server_dir` | default: current directory |
| `SteamCmd` | `-steamcmd` | full path to steamcmd |
| `StartScript` | `-start_script` | |
| `RemoteBuild` | `-remote_build` | expected Steam build id, or `no` |
| `RemoteOxide` | `-remote_oxide` | expected Oxide tag, or `no` |
| `IsService` | `-isService` | `1`/`0`/`true`/`false` |
| `ServiceType` | `-serviceType` | parsed, stored, never used in my range |
| `ServiceName` | `-serviceName` | |
| `ShowServerConsole` | `-showserverconsole` | plugin sends `RunServerScriptHidden ? "0" : "1"` (plugin 3719) |
| `ServerTmux` | `-servertmux` | plugin sends `ServerTmuxSession` (plugin 3720) |

`Main` line 248-249: if `IsService && ShowServerConsole` then `ShowServerConsole = false`.

Module-level static `Fails` (line 142): free-text description of what failed, set by callers before calling restore; read by restore to build `fail_reason`. Values set in the code: `"server update (different version detected)"` (413, 423), `"oxide update (different version detected)"` (443, 453), `"server update"` (884), `"oxide update"` (902).

---

## 1. IsServiceRunning(srvName) — lines 1296-1358

Purpose: tell whether the OS service that runs RustDedicated is still running. Only caller: `WaitForRustDedicatedAndLog` line 984, in a `while (IsServiceRunning(name)) Thread.Sleep(2000);` loop with no upper bound.

Input: service name. Output: bool. Empty/whitespace name → `false` (1298-1299).

### Windows (1303-1328)

- Process: `sc` with arguments `query "<srvName>"`.
- ProcessStartInfo: `RedirectStandardOutput = true`, `RedirectStandardError = true`, `UseShellExecute = false`, `CreateNoWindow = true`, `StandardOutputEncoding = UTF8`, `StandardErrorEncoding = UTF8`.
- `Process.Start` null → `false`.
- Reads `StandardOutput.ReadToEnd() + StandardError.ReadToEnd()` (sequential, synchronous), then `WaitForExit(4000)`. Because ReadToEnd blocks until the pipes close, the 4000 ms is not an effective timeout.
- Parsing: regex `STATE\s*:\s*(\d+)` (IgnoreCase). If it matches and parses as int: running iff code is `4` (RUNNING), `2` (START_PENDING) or `3` (STOP_PENDING). So "stopping" counts as still running — correct for a wait-until-stopped loop.
- Fallback if the regex does not match: running iff the output contains `RUNNING` (case-insensitive).
- Exit code of `sc` is ignored. Service-not-found (error 1060) produces no STATE line and no "RUNNING" → `false` (treated as stopped).

### Non-Windows (1329-1344)

- Process: `systemctl` (resolved through PATH, unlike `RestartRustService` which uses `ResolveSystemctlPath()`), arguments `is-active --quiet <EscapeArg(srvName)>`.
- ProcessStartInfo: `UseShellExecute = false`, no redirection, `CreateNoWindow = true`.
- `WaitForExit(4000)`, then `return p.ExitCode == 0`.
- `EscapeArg` (1351-1357): if the name contains a space or a double quote it is wrapped in single quotes, with embedded `'` replaced by `'"'"'`; otherwise passed as-is.
- No `sudo`; `is-active` does not need privileges.

### Error handling

Any exception anywhere → `false` (1346-1349). This includes: `sc`/`systemctl` not found, and (Linux) `p.ExitCode` being read on a process that has not exited after 4 s (throws `InvalidOperationException`). In every failure case the answer is "not running", which lets the update proceed.

---

## 2. IsProcessRunning(procName) — lines 1361-1372

- `Process.GetProcessesByName(procName)`; true iff the array is non-empty. Exception → `false`.
- Only caller: `WaitForRustDedicatedAndLog` line 975 with the literal `"RustDedicated"` (no `.exe`, same on both OSes), polled every 2000 ms, no upper bound.
- Machine-wide check: it matches any process with that name, not the one belonging to `ServerDir`.
- Returned `Process` objects are not disposed.

Context (968-999): after the loop ends the code logs "stopped", then runs `AutoUpdateTask()` (FMU self-update) before anything else. The wait happens before the `updating.lock` check and before backup.

---

## 3. StartServerScript(scriptPath, workingDirectory) — lines 1375-1610

Purpose: launch the user's server start script so that the Rust server comes back and keeps running after the updater exits.

Callers: 373 (wipe flow), 560 (normal success), 1796 (after successful restore), 1852 (after failed restore), 3268 (`CreateUpdateresultAndStartScript`). All callers pass `RunState.ServerDir` as `workingDirectory` and wrap the call in try/catch that only logs. Service mode never calls it from 373/560/3268 (they call `RestartRustService` instead) but the two restore paths call it regardless of `IsService` (see D-2).

### 3.1 Pre-checks (1377-1386) — outside the try block

1. Logs: `[StartServerScript] Enter: scriptPath='…' workDir='…' ShowServerConsole=… IsService=… ServerTmux='…'`.
2. Empty/whitespace path → throws `ArgumentException("start script is empty")`.
3. If not rooted: `scriptPath = Path.Combine(workingDirectory, scriptPath)`.
4. Not existing → throws `FileNotFoundException("Start script not found", scriptPath)`.

Everything after is inside a try whose catch rethrows `new Exception("StartServerScript error: " + ex.Message, ex)` (1525-1528).

The function never waits for the server, never checks that RustDedicated actually appeared, and returns void. "Success" means "the launcher process object was non-null".

### 3.2 Windows (1391-1458)

`ext = Path.GetExtension(scriptPath).ToLowerInvariant()`. Only `.ps1` is special; every other extension (`.bat`, `.cmd`, `.exe`, anything) goes through `cmd.exe`.

Working directory in all four Windows branches: `Path.GetDirectoryName(scriptPath) ?? workingDirectory` — the script's own folder, not the server dir.

| ShowServerConsole | ext | FileName | Arguments | UseShellExecute | CreateNoWindow | Redirects |
|---|---|---|---|---|---|---|
| true (visible) | `.ps1` | `powershell.exe` | `-NoProfile -ExecutionPolicy Bypass -File "<scriptPath>"` | true | false | n/a |
| true (visible) | other | `cmd.exe` | `/c start "RustServer" "<scriptPath>"` | true | false | n/a |
| false (hidden) | `.ps1` | `powershell.exe` | `-NoProfile -ExecutionPolicy Bypass -File "<scriptPath>"` | false | true | stdout/stderr explicitly not redirected |
| false (hidden) | other | `cmd.exe` | `/c "<scriptPath>"` | false | true | stdout/stderr explicitly not redirected |

- Logs `[StartServerScript] Windows launch: <file> <args> (UseShellExecute=…, CreateNoWindow=…)`.
- `Process.Start` null → `Logger.Error("... Failed to start process (returned null)")`, no exception. Otherwise logs PID. Returns.
- Visible `.bat`: `start "RustServer" "<path>"` opens a new console window titled `RustServer`; the first quoted argument of `start` is the window title, which is why a dummy title is supplied before the quoted path (without it `start` would treat the path as the title).
- Hidden: the launcher (`cmd.exe`/`powershell.exe`) gets a hidden console of its own and remains the parent of the server for its whole lifetime.
- No process is waited on; the `Process` object is not disposed.

### 3.3 Linux / non-Windows (1460-1523)

Step A — make script executable (1460-1473): `/bin/chmod` with arguments `+x "<scriptPath>"`, `UseShellExecute=false`, no redirects, `CreateNoWindow=true`, `WaitForExit(2000)`. All exceptions swallowed. Exit code ignored.

Step B — capability detection (1475-1478) via `DetectGnomeAndTmux()` (2999-3033):
- Returns `{false,false}` unless `OSPlatform.Linux` (so macOS/FreeBSD always end up in nohup).
- For each name runs `sh` with arguments `-c "command -v <name> >/dev/null 2>&1 && printf 1 || printf 0"`, stdout redirected, `UseShellExecute=false`, `CreateNoWindow=true`; reads stdout to end, trims, `WaitForExit(500)`; present iff output is `1`. Exception → false.
- `gnome = Exists("gnome-terminal") || Exists("gnome-terminal-server")`; `tmux = Exists("tmux")`.
- It is a pure PATH check. It does not check `DISPLAY`/`WAYLAND_DISPLAY` or that a graphical session exists.
- Detection runs even in hidden mode (result unused there).

Step C — decision chain:

1. `ShowServerConsole == false` → `StartWithNoHup(scriptPath, workingDirectory)`; return (1480-1484).
2. `ShowServerConsole == true`:
   1. If `ServerTmux` non-empty AND tmux available → `SafeLaunchTmuxSession(scriptPath, ServerTmux)`. True → return. False → warn `tmux specific session failed, proceeding to next method...` (1486-1496).
   2. If gnome available → `SafeLaunchGnomeTerminal(scriptPath)`. True → return. False → warn (1498-1508).
   3. If tmux available → session name = `ServerTmux` if non-empty else the literal `rust-server`; `SafeLaunchTmuxSession`. True → return. False → warn (1510-1521). Note: if step 2.1 already failed with a named session, this retries the very same session name.
   4. `StartWithNoHup(scriptPath, workingDirectory)` (1523).

Resulting priority when visible: explicit tmux session > gnome-terminal > tmux session `rust-server` > nohup. (README line 214 lists them in a different, misleading order.)

#### StartWithNoHup(sp, wd) — 1530-1546

- `FileName = "/bin/bash"`
- `Arguments = -c "nohup '<sp>' >/dev/null 2>&1 &"`
- `UseShellExecute=false`, no redirects, `CreateNoWindow=true`, `WorkingDirectory = wd` (the server dir).
- Logs `[StartServerScript] nohup launch: /bin/bash <args>`.
- Null process → logs error only. bash is not waited on; its exit code is never read.
- Server stdout/stderr are discarded to `/dev/null`.
- Non-login, non-interactive bash: profile files are not sourced.

#### SafeLaunchGnomeTerminal(sp) — 1548-1572

- `FileName = "gnome-terminal"`
- `Arguments = -- bash -lc "exec '<sp>'"`
- `UseShellExecute=false`, no redirects, `CreateNoWindow=false`. No `WorkingDirectory` set → inherits the updater's current directory.
- Logs the command. Null process → error, return false. Otherwise `Thread.Sleep(500)` and return **true unconditionally** — exit code is never inspected.
- Exception (e.g. binary not found) → warn, return false.
- `bash -l` = login shell (sources `/etc/profile`, `~/.bash_profile`/`~/.profile`). `exec` replaces bash with the script so the terminal's child is the script itself.

#### SafeLaunchTmuxSession(sp, session) — 1574-1609

1. `tmux kill-session -t <session>` — `UseShellExecute=false`, no redirects, `CreateNoWindow=true`; `WaitForExit(500)`; result ignored. Purpose: remove a stale session of the same name so `new-session` does not fail with "duplicate session".
2. `tmux new-session -d -s <session> "bash -lc 'exec <sp>'"` — same PSI settings, no `WorkingDirectory`. Logged. Null → error, return false. Not waited on.
3. `Thread.Sleep(300)`.
4. Return `TmuxSessionExists(session)` (2963-2996): `tmux has-session -t <session>`, `WaitForExit(2000)` (kill + false on timeout), true iff exit code 0; empty name or exception → false.
- Exception → warn, return false.
- Neither `<session>` nor `<sp>` is quoted or escaped in any of the three tmux commands.
- Session is created detached (`-d`); the session ends when the script ends (because of `exec` and no `remain-on-exit`).

### 3.4 Summary matrix

| OS | Mode | Precondition | Launcher command |
|---|---|---|---|
| Win | visible | `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<p>"` (ShellExecute) |
| Win | visible | other | `cmd.exe /c start "RustServer" "<p>"` (ShellExecute) |
| Win | hidden | `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<p>"` (no window) |
| Win | hidden | other | `cmd.exe /c "<p>"` (no window) |
| Linux | hidden | any | `/bin/bash -c "nohup '<p>' >/dev/null 2>&1 &"` cwd=serverDir |
| Linux | visible | ServerTmux set + tmux | `tmux kill-session -t S` ; `tmux new-session -d -s S "bash -lc 'exec <p>'"` ; verify `tmux has-session -t S` |
| Linux | visible | gnome-terminal on PATH | `gnome-terminal -- bash -lc "exec '<p>'"` |
| Linux | visible | tmux on PATH | as tmux above with S = ServerTmux or `rust-server` |
| Linux | visible | fallback | nohup variant |
| other Unix | any | — | nohup variant (detection is Linux-only) |

On Linux the script is executed directly (needs the executable bit — hence the chmod — and normally a shebang); the extension is irrelevant. `.sh` is not special-cased.

---

## 4. Backup and restore — lines 1614-1870

### 4.1 CreateTempBackup(serverDir, update_id, out backupPath) — 1617-1690

Called once per run at line 393 for Init/Testrun/Scheduled flows (not for the custom Wipe flow, which returns at 391 without any backup). It runs after: server stop wait, FMU self-update (`AutoUpdateTask`), `updating.lock` creation (322). It runs before the remote version checks.

Behaviour:

1. `backupsRoot = <serverDir>/temp_backup`. Created if missing. If it already exists it is **reused as-is**: log says "files inside will be overwritten"; pre-existing files in it are not deleted.
2. Exclusion set (case-insensitive `HashSet`, full paths):
   - `<serverDir>/temp_backup` (the backup itself)
   - `<serverDir>/updater.log`
   - `<serverDir>/FeedMeUpdates.exe`
   - `<serverDir>/FeedMeUpdates`
   Only these exact root-level paths. `IsUnderAny` (2903-2913) matches a path equal to an excluded root or starting with `root + DirectorySeparatorChar`, `OrdinalIgnoreCase` (also on Linux).
3. Directory pass (1653-1660): `Directory.GetDirectories(srcRoot, "*", SearchOption.AllDirectories)`; every non-excluded directory is recreated under `temp_backup` (empty directories are preserved). Failure to create a directory → `Logger.Warn` only, continues.
4. File pass (1662-1679): `Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories)`; for every non-excluded file: ensure parent dir, `File.Copy(full, target, overwrite: true)`, log `Copied: <relative path>` at INFO (one line per file). **First file copy failure → `Logger.Error` and `return false`** (abort, partial backup left on disk).
5. Success: `backupPath = backupsRoot`, log, return true.
6. Any other exception → error log, return false.

What is copied: the entire server directory tree — game binaries, `Bundles`, `RustDedicated_Data`, `steamapps`, `oxide/` (plugins, config, data, logs), `server/<identity>/` (map, saves, player DBs), `markers/`, `updating.lock`, `updateresult.json` if any, `FMU_CHECKSUMS.txt`, `FeedMeUpdates(.exe).new`, etc. Nothing is filtered by relevance. README (lines 118, 505) tells users to plan disk space for a full copy.

Not done: no free-space check, no checksum/verification, no compression, no hard links, `update_id` parameter is unused.

Caller on failure (393-399): `CreateUpdateresultAndStartScript(..., "Failed backup creation", ...)` which writes a failure `updateresult.json` (`backup_cycle=false`, `server_restored=false`), calls `CleanTempBackup`, then starts the script or the service. The server comes back un-updated.

### 4.2 CleanTempBackup(serverDir) — 3036-3071 (outside my range, used by it)

`Directory.Delete(<serverDir>/temp_backup, true)`; exceptions only warned; then re-checks existence and logs success or "still present". Never throws.

### 4.3 RestoreFromBackupOrFail(serverDir, backupFolder, update_id, start_script) — 1693-1820

Callers: 415, 425, 445, 455 (scheduled-flow version mismatch — before anything was modified), 886 (`UpdateServer` failed), 904 (`UpdateOxide` failed). The caller sets `Fails` first.

**This function never returns normally.** Every path ends in `Environment.Exit(0)` (success path at 1811; all failure paths through `HandleRestoreFailure` at 1867). The `return true/false` statements and the callers' subsequent `Logger.Close(); return 0;` are unreachable.

Steps:

1. `backupFolder` empty or missing → error, `HandleRestoreFailure` (exits).
2. Directory pass (1709-1727): for each directory in the backup, create the corresponding directory in `serverDir` if missing. Failure → error, `HandleRestoreFailure` (exits).
3. File pass (1729-1763): for each file in the backup:
   - create parent dir if missing;
   - if the target exists AND its **file name** (lower-cased) does not contain `feedmeupdates`: `File.SetAttributes(target, Normal)` then `File.Delete(target)`; failure there is only a warning;
   - `File.Copy(file, target, overwrite: true)`; log `Restored file: <rel>`;
   - any exception → error, `HandleRestoreFailure` (exits) — restore stops at the first failing file.
4. It is an **overlay** restore: files present in `serverDir` but absent from the backup are left alone (nothing is deleted except the target of each copied file).
5. Marker (1767-1787): writes `<serverDir>/updateresult.json` (indented JSON):
   ```json
   {
     "result": "failed",
     "fail_reason": "Failed <Fails>",
     "backup_cycle": true,
     "server_restored": "yes",
     "timestamp": "<UTC yyyy-MM-ddTHH:mm:ssZ>",
     "update_id": "<update_id>"
   }
   ```
   Write failure only logged. No `wiped` / `wipe_info` / `updated_plugins` keys.
6. `CleanTempBackup(serverDir)`.
7. If `start_script` non-blank → `StartServerScript(start_script, serverDir)` (exception only logged). Else warn `start_script not specified; skipping start.` **No service branch.**
8. `Logger.Close(); Environment.Exit(0)`.
9. Outer catch → `HandleRestoreFailure` (exits).

### 4.4 HandleRestoreFailure(server_dir, update_id, start_script) — 1823-1868

1. Writes `<server_dir>/updateresult.json`:
   ```json
   {
     "result": "failed",
     "fail_reason": "Failed restore after failed <Fails>",
     "backup_cycle": true,
     "server_restored": false,
     "timestamp": "<UTC yyyy-MM-ddTHH:mm:ssZ>",
     "update_id": "<update_id>"
   }
   ```
   (`server_restored` is boolean `false` here, string `"yes"` on restore success, string `"no"` in the normal success marker at line 535.)
2. If `start_script` non-blank: `CleanTempBackup(server_dir)` **(deletes the backup)** and then `StartServerScript`; exceptions logged. Else warn and skip (backup is kept in this case). No service branch.
3. `Logger.Close(); Environment.Exit(0)`.

Process exit code is always 0, for success and for every failure.

### 4.5 Marker consumers (contract)

Plugin `ProcessUpdaterMarkerIfPresent` (plugin 1103-1181) reads with regex-based extraction: `result`, `update_id` (fallback `updateId`), `fail_reason` (fallback `error`), `wiped` (fallback `wipe_result`), `wipe_info` (fallback `wipeinfo`), `updated_plugins` array. It does not read `backup_cycle`, `server_restored` or `timestamp` in that function. `result == "success"` resets the retry counter, `"failed"` increments it. The marker is then moved to `<ServerDirectory>/<MarkersSubfolder>/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json`.

---

## 5. Process execution helpers — lines 1872-1947

### 5.1 RunProcessCaptureOutput(exe, args, timeout) → string (1875-1909)

- PSI: `UseShellExecute=false`, `RedirectStandardOutput=true`, `RedirectStandardError=true`, `CreateNoWindow=true`. No working directory, no encoding.
- `stdout = ReadToEnd()`, then `stderr = ReadToEnd()` (sequential, blocking), then `WaitForExit(timeout)`.
- On timeout: `Kill(true)` (entire process tree), warn `Process '<exe>' timed out after <s>s`, return `stdout + "\n" + stderr` (untrimmed).
- Normal: return `(stdout + "\n" + stderr).Trim()`.
- Exception → error log, return `""`.
- Only caller: `GetRemoteRustBuild` (1967).

### 5.2 RunProcessCaptureExitCode(exe, args, timeout) → int (1912-1947)

- Same PSI. Uses async `OutputDataReceived` → `Logger.Info(line)`, `ErrorDataReceived` → `Logger.Warn(line)`, `BeginOutputReadLine/BeginErrorReadLine`.
- Timeout → `Kill(true)`, warn, return `-1`. Start returned null → `-1`. Exception → `-2`. Else the exit code.
- **No callers anywhere in the file (dead code).** It is, however, the correct pattern (async reads + a real timeout) and should be the basis of the v2 process runner.

---

## 6. Remote queries — lines 1951-2154

### 6.1 GetRemoteRustBuild() → string (1954-1996)

- Returns the Steam public-branch build id as a decimal string, or the literal `"no"` on any failure.
- Preconditions: `RunState.SteamCmd` non-blank and `File.Exists` → otherwise warn, `"no"`.
- Command: `<steamcmd> +login anonymous +app_info_print 258550 +quit` (app id `258550` = Rust Dedicated Server). Timeout argument: 30 s (not effective, see D-6). No working directory set (the update call at 2214 does set it to the steamcmd folder).
- Empty output → warn, `"no"`. Parse failure → warn, `"no"`. Exception → error, `"no"`.
- Callers: 407 (Scheduled: compared against `-remote_build`, case-insensitive; mismatch or `"no"` ⇒ treated as failure "server update (different version detected)" and triggers a restore) and 466 (Init/Testrun: only logged, and in Init written verbatim — including `"no"` — to `<serverDir>/steambid.temp`, 476-477).

### 6.2 ParseBuildId(steamOutput) → string? (1999-2019) and ExtractBalancedBlock (2022-2039)

SteamCMD prints Valve KeyValues (VDF) text. Algorithm:

1. Find first occurrence of `"branches"` (with the quotes, case-insensitive). Not found → null.
2. Find the next `{`; extract the brace-balanced block starting there (`ExtractBalancedBlock`: depth counter over `{`/`}`, returns the substring including both braces, null if unbalanced or start is not `{`). It does not understand quoted strings (a brace inside a quoted value would miscount).
3. Inside the branches block find first `"public"` (case-insensitive), the next `{`, and its balanced block.
4. Regex on the public block: `"buildid"\s*"([0-9]{5,12})"` (IgnoreCase). Group 1 is the build id; no match → null.

Only the `public` branch is supported (README: staging not supported).

The plugin has a byte-for-byte duplicate of this parser (plugin 1720-1760) and its own SteamCMD invocation with the same arguments (plugin 1698) but a 60 s wait and stdout only. The two sides must agree because the app compares its own reading against the plugin's `-remote_build`.

### 6.3 GetRemoteOxideRelease() → (tag, url) (2042-2154)

- Request: `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`
  - New `HttpClient` per call, `Timeout = 20 s`.
  - Only header: `User-Agent: FeedMeUpdates/1.0` (GitHub rejects requests without a UA). No `Accept`, no auth token, no API version header.
  - Synchronous (`GetAwaiter().GetResult()`).
- Non-2xx → warn `GitHub API returned <code> <reason>`, return `("no", "")`.
- Tag: `tag_name` if it is a string, else `name`; empty → `"no"`.
- Assets: for each element of `assets`, take `name` (lower-cased) and `browser_download_url`; skip entries with empty URL or missing properties.
- Asset selection (single pass in API order, 2095-2141):
  - Track `firstZip`: first asset whose URL or name ends in `.zip`.
  - Track `firstNonLinuxZip`: first `.zip` whose name contains none of `linux`, `mac`, `darwin`.
  - Windows: name contains `windows` and not `linux`/`mac` → choose and stop. Else name contains `win` and not `linux`/`mac` and nothing chosen yet → tentatively choose, keep scanning for a `windows` one.
  - Non-Windows: name contains `linux` or `ubuntu` or `x86_64` or `x64` → choose and stop.
  - Fallbacks in order: (Windows only) `firstNonLinuxZip`; `firstZip`; `assets[0]`.
- No assets → url `""`.
- Returns `(tag, url)`; logs both. Any exception (network, timeout, JSON) → error, `("no", "")`.
- Callers: 437 (Scheduled: tag compared case-insensitively with `-remote_oxide`; mismatch ⇒ failure + restore) and 2355 inside `UpdateOxide` (uses url; fails if tag is `"no"` or url empty). So a scheduled Oxide update calls the API twice.
- External knowledge: Oxide.Rust releases currently publish `Oxide.Rust.zip` (Windows) and `Oxide.Rust-linux.zip`. With those names the Windows result comes from the `firstNonLinuxZip` fallback (neither contains "win") and the Linux result from the `linux` match. The plugin queries the same endpoint for detection (plugin 1768) and the tag string is the cross-process contract.

---

## 7. External contracts (complete list for this range)

Files (all relative to `-server_dir`):
- `updater.log` — log, truncated when > 5 MiB at startup.
- `temp_backup/` — full-tree backup directory.
- `updateresult.json` — result marker (field sets in 4.3, 4.4; success/other variants at 351-357, 529-541, 3202-3248).
- `updating.lock` — created by the app at 322, never deleted by the app (the plugin removes it at next boot per README). It is inside the backed-up tree.
- `steambid.temp` — written in Init flow with the raw result of `GetRemoteRustBuild` (may be `no`).
- `FeedMeUpdates`, `FeedMeUpdates.exe` — excluded from backup by exact root-level name.
- `RustDedicated` / `RustDedicated.exe` — process name `RustDedicated` is what the stop-wait polls.

External commands:
- `sc query "<name>"` (Windows service state).
- `systemctl is-active --quiet <name>` (Linux service state).
- `/bin/chmod +x "<script>"`.
- `sh -c "command -v <name> >/dev/null 2>&1 && printf 1 || printf 0"` for `gnome-terminal`, `gnome-terminal-server`, `tmux`.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<script>"`.
- `cmd.exe /c start "RustServer" "<script>"`; `cmd.exe /c "<script>"`.
- `/bin/bash -c "nohup '<script>' >/dev/null 2>&1 &"`.
- `gnome-terminal -- bash -lc "exec '<script>'"`.
- `tmux kill-session -t <s>`, `tmux new-session -d -s <s> "bash -lc 'exec <script>'"`, `tmux has-session -t <s>`; default session name `rust-server`.
- `<steamcmd> +login anonymous +app_info_print 258550 +quit`.

Network:
- `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`, `User-Agent: FeedMeUpdates/1.0`, 20 s timeout; fields used: `tag_name`, `name`, `assets[].name`, `assets[].browser_download_url`.

CLI args consumed here: `-server_dir`, `-steamcmd`, `-start_script`, `-remote_build`, `-remote_oxide`, `-isService`, `-serviceName`, `-showserverconsole`, `-servertmux`, `-update_id`.

Magic strings: `"no"` as the universal "unknown/none" sentinel for build id and Oxide tag; window title `RustServer`; tmux session `rust-server`; the plugin runs the updater itself in a tmux session called `feedmeupdate` (plugin 2244-2261).

Process exit code: always 0.

---

## 8. Implicit knowledge and workarounds worth keeping

1. **Wait for full stop before touching files.** STOP_PENDING/START_PENDING count as running on Windows (1324) because RustDedicated keeps files locked and is still saving while stopping. Keep this idea and extend it to Linux (see D-4).
2. **Detaching the server from the updater.** The updater exits right after launching; every Linux variant is chosen so the child survives: `nohup … &` inside a throw-away bash (double-fork effect + SIGHUP immunity), tmux detached session (owned by the tmux server), gnome-terminal (owned by gnome-terminal-server). On Windows `cmd /c start` and ShellExecute create an independent console. v2 must keep "server outlives updater" as a hard requirement (consider `setsid`).
3. **`start "RustServer" "<path>"`** — the dummy title is required; otherwise `start` consumes the quoted path as the window title and opens an empty console.
4. **`-ExecutionPolicy Bypass -NoProfile`** for `.ps1` — default Windows policy blocks unsigned scripts; NoProfile avoids user-profile side effects and startup delay.
5. **`chmod +x` before launch** — scripts copied from Windows/zip or restored from backup often lose the executable bit.
6. **`bash -lc`** in gnome/tmux variants — login shell so the user's profile environment (PATH, `LD_LIBRARY_PATH`, etc.) is present, as it would be when they start the server by hand. The nohup variant does not do this (inconsistent).
7. **`exec`** in gnome/tmux variants — the script replaces bash, so the terminal/session lifetime equals the server lifetime and signals reach the script directly.
8. **`tmux kill-session` before `new-session`** — a leftover session with the same name (from the previous server run) makes `new-session -s` fail with "duplicate session".
9. **Verify tmux with `has-session`** — `new-session -d` returns success even if the command inside dies immediately; checking that the session still exists a moment later is a cheap liveness probe.
10. **Fallback chain** — every visible-console method can be absent on a given box; nohup is the universal last resort.
11. **`updater.log` excluded from backup** — the logger holds it open for writing with `FileShare.Read`; copying it on Windows would hit a sharing violation and (given abort-on-first-error) fail the whole backup.
12. **Updater binary excluded from backup and never deleted on restore** (`feedmeupdates` name check, 1741) — a running `.exe` cannot be deleted/overwritten on Windows; one failed file would turn the whole restore into a "restore failed".
13. **`SetAttributes(Normal)` before delete** (1745) — read-only files cannot be deleted on Windows.
14. **Delete-then-copy on restore** — likely to cope with targets that `File.Copy(overwrite)` cannot overwrite (read-only/hidden attributes; on Linux, replacing an in-use binary by unlinking instead of writing in place avoids "text file busy").
15. **SteamCMD output is parsed textually** from the `branches → public → buildid` path, with brace balancing, because the output is VDF mixed with banner/progress lines and many other `buildid` keys (other branches). Do not grep the first `buildid`.
16. **stdout and stderr merged** for SteamCMD (1899/1902) — SteamCMD writes useful text to either stream depending on platform.
17. **`+login anonymous`** — Rust Dedicated Server (258550) is downloadable anonymously; no credentials are ever needed.
18. **GitHub requires a User-Agent** — hence `FeedMeUpdates/1.0`.
19. **Double version check ("different version detected")** — the app re-reads the remote build/tag and refuses to proceed if it differs from what the plugin announced, so that the plugin's bookkeeping (it promotes `RemoteSteamBuildID` to `LocalSteamBuildID` on success, plugin 1148-1154) stays truthful.
20. **Marker is written before the server is started** — the plugin reads it at boot, so it must exist before RustDedicated loads plugins.
21. **`IsService` forces hidden mode** (248) — a service has no console to show.
22. External knowledge, not handled by the code but well known: SteamCMD caches app info, so `app_info_print` can return a stale build id unless the cache is refreshed (`+app_info_update 1` before the print, or deleting `appcache/appinfo.vdf`). v2 should handle this explicitly.

---

## 9. Defects and risks

Severity: high = server left down, data loss, or wrong destructive action; medium = wrong result/false failure, robustness; low = cosmetic, dead code.

### High

**D-1. Failed restore deletes the only backup (1847-1852).** In `HandleRestoreFailure`, when a start script is configured, `CleanTempBackup` runs before starting the script. Scenario: restore fails at file N (locked file, disk full, permission). Server dir is now half-old/half-new and `temp_backup` — the only complete copy of saves, player data and plugin data — is deleted. Verified.

**D-2. Restore paths ignore service mode (1791-1807, 1847-1863).** Both restore outcomes only call `StartServerScript(start_script)`. In service mode the README tells users to leave `ServerStartScript` empty, so after any failed update the code logs "start_script not specified; skipping start" and exits: the server stays down until a human intervenes. `RestartRustService` is never called from these paths. (If a script happens to be configured in service mode, the server is started outside the service instead.) Verified.

**D-3. gnome-terminal launch is reported successful without any check (1548-1566), and detection ignores DISPLAY (3030).** On a headless box that merely has `gnome-terminal` installed, with visible mode and no working explicit tmux session, `gnome-terminal` starts, fails with "cannot open display", but `Process.Start` was non-null so the function returns true; tmux/nohup fallbacks are skipped. Server never starts, marker says success. Verified in code; the gnome-terminal failure mode is external knowledge.

**D-4. Linux service "stopped" detection fires too early (1331-1343).** Only exit code 0 of `systemctl is-active` counts as running. External knowledge: for a unit in `deactivating` state `is-active` returns non-zero, so the wait loop ends while RustDedicated is still saving/shutting down; backup and SteamCMD then run against a live process (inconsistent save copy, locked/busy files). Additionally (verified): if `systemctl` takes more than 4 s, `p.ExitCode` throws → caught → `false` → "stopped"; if `systemctl` is missing → "stopped". Fail-open in a place that must fail closed.

**D-5. Stop-wait is machine-wide and unbounded (1361-1372, 975).** `GetProcessesByName("RustDedicated")` matches every Rust server on the host. On a multi-server machine the updater waits forever for the other instances; the server being updated has already quit, so it stays down indefinitely. No timeout, no log while waiting. Conversely an exception returns false and the update proceeds on a running server. Verified.

**D-6. SteamCMD query can hang the whole updater (1892-1895).** `ReadToEnd()` on stdout then stderr runs before `WaitForExit(timeout)`, so the 30 s timeout only starts after the process has closed its pipes; a hung SteamCMD (network stall, self-update prompt) blocks forever with the server down. Classic deadlock is also possible if the child fills the stderr pipe while the parent is blocked reading stdout. Same pattern in `IsServiceRunning` Windows branch (1318-1319). Verified.

**D-7. Restore is an overlay, not a rollback (1729-1763).** Files added by the failed update are not removed. Scenario: SteamCMD or the Oxide zip extraction partially completes and adds new assemblies/files; restore puts old files back but leaves the new ones → mixed-version install that is reported as `server_restored: "yes"`. Verified.

**D-8. Stale `temp_backup` is reused (1625-1633).** If a previous run left `temp_backup` behind (crash, power loss, D-2 service-mode failed restore which keeps it, or `Directory.Delete` failure), the next backup overwrites files in place but never removes files that no longer exist in the server. A later restore then resurrects old files (old map/save files after a wipe, removed plugins, old DLLs). Verified.

### Medium

**D-9. Full-tree backup cost (1653-1679).** Copies the entire server directory (game binaries, bundles, saves; typically many GB and tens of thousands of files) on every update, synchronously, with the server down, materialising full `GetDirectories`/`GetFiles` arrays, and writing one flushed log line plus one console line per file. Restore copies everything back the same way. This dominates downtime. No free-space pre-check: disk-full shows up as a mid-copy failure. Verified (size figures are general knowledge).

**D-10. Backup and full restore happen even when nothing was changed (393 vs 407-455).** The remote build/tag verification runs after the backup; on mismatch the code performs a complete restore of an untouched tree (and D-1/D-2/D-7 risks apply for no benefit). The check should precede the backup and a mismatch should just restart the server. Verified.

**D-11. A failed remote query is reported as "different version detected" (407-427, 437-457).** `GetRemoteRustBuild` returning `"no"` (timeout, SteamCMD glitch) or GitHub returning 403 (rate limit: unauthenticated API allows 60 requests/hour per IP — external knowledge) is indistinguishable from a real mismatch; it produces a failed update, a full restore, and a misleading reason. No retry in either query. Verified.

**D-12. tmux success probe race (1599-1602).** `new-session` is not waited for; 300 ms later `has-session` decides. If the tmux server is slow to start, the probe fails and the code falls through to gnome/nohup, launching a second server instance while the tmux one is also starting (two servers on the same identity/ports). Conversely a script that dies after 300 ms counts as success. Verified in code; timing risk is a judgment.

**D-13. Unquoted/unescaped interpolation into command lines (1403-1432, 1465, 1532, 1555, 1581, 1592, 2976, 1308, 1334).** Script path and tmux session name are pasted into shell strings. A script path containing a space breaks the tmux variant (`exec /path with space/start.sh`); a path containing `'` breaks nohup/gnome/tmux; `"` breaks all (argument splitting); a session name with spaces or shell metacharacters is interpreted by tmux/sh. `cmd /c "<path>"` misbehaves with `&`, `(`, `)` in paths. Values come from the admin's own config so this is robustness more than privilege escalation, but it is command injection by construction. `EscapeArg` (1351) produces shell quoting for a call that has no shell (`UseShellExecute=false`), so a name with a space is passed with literal quote characters — wrong. Verified. v2 should use `ProcessStartInfo.ArgumentList` and avoid shell strings.

**D-14. tmux target matching (1581, 1592, 2976).** External knowledge: `-t name` is matched as an exact name, then as a prefix/pattern, so `kill-session -t rust` can kill a different session such as `rust-server-2`, and `has-session` can report true for the wrong session. Use `-t =name`. Related (verified): the plugin runs the updater in session `feedmeupdate` (plugin 2261); if the admin sets `ServerTmuxSession` to that name, `kill-session` kills the updater itself mid-run.

**D-15. Start failure is invisible.** `StartServerScript` returns void, treats a null `Process` as a logged error only (1449-1452, 1545), never checks launcher exit codes (nohup, gnome), and callers swallow exceptions (383, 570, 1799, 1855). The marker (already written) says success/restored while the server is down, and nothing reads the marker until a server boots. Verified.

**D-16. Inconsistent working directory and environment across launch variants.** Windows: script folder (1417, 1443). nohup: server dir (1541). gnome/tmux: whatever the updater's cwd is (not set). Login shell for gnome/tmux, non-login for nohup. A start script using relative paths works in one mode and fails in another. Verified.

**D-17. Log flooding.** One INFO line per copied/restored file (1672, 1755) can add several MB per run to `updater.log`, which is only truncated at the next startup when above 5 MiB — destroying the history of the previous (possibly failed) run. Verified.

**D-18. Stale build id from SteamCMD cache (1966).** No `+app_info_update 1` / cache clear. External knowledge; if it occurs the app and plugin can disagree or both be stale, causing false "different version detected" failures (see D-11). Unverified in practice.

**D-19. Symlinks/special files in the server tree.** `File.Copy` follows file symlinks (copies content) and the recursive enumeration's handling of directory symlinks/junctions was not checked; a symlinked large directory (e.g. shared `Bundles`) could be duplicated, or a restore could replace a link with a regular file. Unverified.

**D-20. `IsServiceRunning` on localised Windows (1321-1327).** The regex needs the English label `STATE`. If `sc` output is localised the code falls back to searching `RUNNING`, in which case STOP_PENDING/START_PENDING are treated as stopped. Unverified (depends on `sc` localisation).

### Low

**D-21. Dead code.** `RunProcessCaptureExitCode` (1912-1947) has no callers. `return true`/`return false` after `Environment.Exit` in restore (1812, and after every `HandleRestoreFailure` call) and callers' `Logger.Close(); return 0/false` after `RestoreFromBackupOrFail` (416-417, 426-427, 446-447, 456-457, 887-888, 905-906) are unreachable. `update_id` parameter of `CreateTempBackup` is unused. `RunState.ServiceType` is unused here. The bool return of `RestoreFromBackupOrFail` is meaningless.

**D-22. `Environment.Exit(0)` deep inside helpers (1811, 1867).** Hidden control flow; prevents callers from cleaning up and makes testing impossible. Exit code 0 on every failure.

**D-23. Marker schema inconsistency.** `server_restored` is `"yes"` (string, 1776), `false` (bool, 1834), `"no"` (string, 535). Timestamp is `yyyy-MM-ddTHH:mm:ssZ` in failure markers and round-trip `"o"` in success markers. Restore markers lack `wiped`/`wipe_info`. Marker building is duplicated in at least six places.

**D-24. Duplicated logic.** PowerShell args built twice (1403, 1427). Backup, restore and `CopyDirectoryOverwrite` (2916) are three copies of the same tree walk. `ParseBuildId`/`ExtractBalancedBlock`, the SteamCMD query, `TmuxSessionExists` and `DetectGnomeAndTmux` are duplicated between plugin and app. Step 2.3 of the Linux chain retries the same named tmux session that already failed in 2.1.

**D-25. Oxide asset heuristics are loose (2111-2128).** `win` matches inside `darwin` (only `mac` and `linux` are excluded in that test); `x64`/`x86_64` on Linux would match a Windows x64 asset if one were listed first; `assets[0]` fallback can pick a non-zip. Harmless with today's two assets; fragile if Oxide changes naming. Verified in code.

**D-26. Oxide release fetched twice per scheduled run (437, 2355)** with a possible change in between (the second call's result is what is installed, without re-checking against `-remote_oxide`). New `HttpClient` per call.

**D-27. Brace matcher ignores quoting (2022-2039).** A `{`/`}` inside a quoted VDF value (e.g. a branch description) would break parsing → `"no"`.

**D-28. Path handling.** Exclusions and `IsUnderAny` are case-insensitive on Linux too (a file named `Updater.log` or a folder `Temp_Backup` would be silently skipped). Exclusion of the updater binary only works when it sits in the server root under its default name, although `UpdaterExecutablePath` is configurable. `StartServerScript` resolves a relative script against the server dir (1383) while `ValidateFlow` checks it relative to the process cwd (831).

**D-29. Minor resource/robustness points.** `Process` objects not disposed (1365, 1448, 1471, 1544, 1562, 1587, 1599). Directory-creation failure during backup is only a warning (1659) and then surfaces as a copy failure. On timeout `RunProcessCaptureOutput` returns untrimmed output. Detection of gnome/tmux runs even in hidden mode. `Logger.Init` creates `serverDir` if it does not exist (33) before validation.

**D-30. Visible gnome/tmux sessions vanish when the server exits** (because of `exec` and no hold/remain-on-exit), so crash output is lost; in hidden mode all script output goes to `/dev/null`. Diagnosability issue rather than a bug.

---

## 10. Open questions

1. Service mode + failed update: should the service be started after a restore (almost certainly yes — D-2 looks like an oversight)? Should the backup be kept when restore fails (D-1)?
2. Is the overlay-style restore intentional (to preserve files created during the update) or should restore be a true rollback/mirror (D-7)?
3. Why is the whole server directory backed up rather than only what SteamCMD/Oxide can modify and what cannot be re-downloaded (saves, oxide data/config/plugins)? Is a "validate" re-download an acceptable recovery path for game binaries?
4. Intended priority on Linux visible mode: README says "nohup, gnome-terminal, tmux"; the code does named tmux → gnome → default tmux → nohup. Which is wanted, and should gnome-terminal be attempted at all without a display?
5. Is `ServiceType` meant to select NSSM vs sc vs systemd behaviour? It is passed by the plugin but unused in this range.
6. Multi-server hosts: must the stop-wait be scoped to the server in `ServerDir` (by PID passed from the plugin, or by executable path)?
7. Should a failed remote query (as opposed to a real mismatch) abort the update, retry, or proceed? Currently it aborts as a mismatch.
8. Should the Windows working directory be the script folder or the server dir? Linux nohup uses the server dir.
9. Is the `feedmeupdates` file-name exemption in restore meant to cover only the executable, or also the plugin `.cs`/config/data files (which also match and are merely overwritten instead of deleted-then-copied)?
10. Should `steambid.temp` ever contain `no`? (Init flow writes whatever the query returned.)
11. Is always exiting with code 0 a deliberate choice (e.g. to keep service managers from reacting), or should v2 expose real exit codes?
