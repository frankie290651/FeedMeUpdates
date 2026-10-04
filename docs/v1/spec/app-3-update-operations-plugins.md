# A3 — Updater app: update operations, misc helpers, file helpers

Source: `/home/claude/frankie290651/feedmeupdates/Application/Program.cs`
Range read in full: lines 18-135 (Logger, RunState) and 2158-3078 (update operations, plugin pass, misc helpers, file/dir helpers).
Read for context only: 400-565 (main flow call sites), 864-910 (restore-on-fail wrappers), 1010-1050 (self-update use of helpers), 1465-1610 (start-script use of tmux/gnome helpers), 1616-1860 (backup/restore), 2042-2154 (`GetRemoteOxideRelease`), 3160-3295 (service restart, failure marker).

All line numbers refer to `Application/Program.cs` unless stated otherwise. "Verified" means read in the code; statements about third-party behaviour (SteamCMD, uMod, tmux, GitHub) that are not provable from the code are marked "unverified".

---

## 0. Context: Logger and RunState (lines 18-135)

### 0.1 Logger (20-100)

- Log file: `<serverDir>/updater.log` (36). `Init(serverDir)` creates `serverDir` if missing (33).
- "Rotation" is really truncation: if the file exists and is larger than `MaxSizeBytes = 5 * 1024 * 1024` bytes (24, 41) it is opened with `FileMode.Create` (truncated, nothing archived). Otherwise `Append`. If the size check throws, `Append`. If the file does not exist, `Create`.
- Opened with `FileAccess.Write, FileShare.Read`, `AutoFlush = true` (58-59). Any exception in `Init` sets the writer to null: logging then goes to console only, silently (61-64).
- Line format: `[dd/MM/yy HH:mm:ss] [LEVEL] message`, local time (69-78). Levels: `INFO`, `WARN`, `ERROR`. Each line goes to `Console.WriteLine` and the file, each wrapped in its own swallow-all try (81-82). A static lock serialises writes.
- `Close()` flushes/disposes and nulls the writer (92-99). Callers call `Logger.Close()` before every process exit path.
- Size is checked only at `Init`; a single run can grow the file without bound (the code in this range logs one line per copied file, see 2936).

### 0.2 RunState (102-132)

Static mutable globals populated from CLI args. Fields used by the code in range:

| Field | Used by |
|---|---|
| `SteamCmd` | `UpdateServer` executable path |
| `ServerDir` | all three update functions, `CleanTempBackup` |
| `UpdatePluginsFlag` | `UpdatePlugins` gate (must equal `yes`, case-insensitive) |
| `UpdatedPlugins` (`List<string>`) | filled by `UpdatePlugins`, written to marker as `updated_plugins` |
| `ShowServerConsole`, `ServerTmux` | consumers of `DetectGnomeAndTmux` / `TmuxSessionExists` |
| `IsService`, `ServiceName` | consumer of `ResolveSystemctlPath` |

Other fields (`UpdateId`, `What`, `StartScript`, `RemoteBuild`, `RemoteOxide`, `ServiceType`, `IsForce`, `NextWipe*`, `PdataFilesToDelete`, `ServerIdentity`, `ServerName`, `ServerDescription`) are not touched in this range.

A separate static on `Program`: `private static readonly List<DateTime> __PluginRequestTimestamps = new();` (143) backs the rate limiter.

---

## 1. UpdateServer() — lines 2161-2338

Signature: `private static bool UpdateServer()`. Returns `true` = Rust server considered updated; `false` = failure.
Caller: `RunUpdateServerWithRestoreOnFail` (877-892). On `false` it sets `Fails = "server update"`, calls `RestoreFromBackupOrFail(...)` (which restores `temp_backup`, writes a failed marker, starts the server and calls `Environment.Exit(0)`).

### 1.1 Preconditions (2166-2197)

Each failure logs an ERROR and returns `false` with no retry:

1. `RunState.SteamCmd` null/whitespace.
2. `File.Exists(steamCmd)` false. (So the value must be a full path to a file, not a command resolvable via PATH.)
3. `RunState.ServerDir` null/whitespace.
4. `Directory.CreateDirectory(serverDir)` throws.

### 1.2 SteamCMD invocation (2199-2215)

Exact argument string (2199):

```
+force_install_dir "<serverDir>" +login anonymous +app_update 258550 validate +quit
```

- Executable: `RunState.SteamCmd`.
- `UseShellExecute = false`, `RedirectStandardOutput = true`, `RedirectStandardError = true`, `CreateNoWindow = true`.
- `WorkingDirectory = Path.GetDirectoryName(steamCmd) ?? serverDir`.
- App id `258550` (Rust Dedicated Server), anonymous login, no `-beta` (staging unsupported, per README), `validate` always present.
- `+force_install_dir` is deliberately placed before `+login`.
- No platform-specific branch: identical on Windows and Linux.

### 1.3 Attempt loop (2201-2328)

`maxAttempts = 3`. For `attempt` = 1..3:

1. Log the args at INFO.
2. `Process.Start`. If it returns `null`: ERROR, **return false immediately** (no retry) (2221-2225).
3. Read stdout fully (`ReadToEnd`), then stderr fully (`ReadToEnd`), sequentially (2227-2228).
4. `WaitForExit(10 minutes)` (2230). If it times out: `proc.Kill(true)` (entire process tree), WARN `steamcmd timed out and was killed.` Execution continues to output evaluation; a timeout is not in itself a failure.
5. `outp = (stdOut + "\n" + stdErr).Trim()`.
6. If anything in 2–5 throws: ERROR, `outp = ex.ToString()`, and execution continues to output evaluation (so an exception leads to the heuristics running on the exception text, and then a retry).
7. Log the last 200 non-empty lines of `outp` (split on `\r` and `\n`, `RemoveEmptyEntries`) at INFO as `steamcmd output (tail):`.
8. **Success heuristics** (2255-2261) — case-insensitive substring tests on combined stdout+stderr; any one returns `true`:
   - contains `Success! App '258550'`
   - contains `Success! app '258550'` (redundant: same test, already case-insensitive)
   - contains `installed` AND contains `258550` (anywhere, independently)
9. **The process exit code is never read.**
10. Failure diagnostics, evaluated only when no success string was found:
    - `sawForceInstallWarning`: contains `Please use force_install_dir before logon`. Only effect: WARN `Arguments already correct; will retry.`
    - `sawState0x6`: contains `state is 0x6` (the second clause `Error! App '258550' state is 0x6` is a strict superset string, hence redundant). Because it is a substring test it also matches `0x602`, `0x606`, `0x626`, etc.
    - If `sawState0x6`: remedial action on `<serverDir>/steamapps/appmanifest_258550.acf`:
      - If the file exists: delete `<...>.acf.bak` if present (failure swallowed), then `File.Move(appmanifest, appmanifest + ".bak")`. `IOException` → WARN "likely locked ... Will wait and retry"; other exception → WARN.
      - If absent: INFO "nothing to rename".
      - The `.bak` is never restored or deleted afterwards by this function.
11. If `attempt == maxAttempts`: ERROR `steamcmd failed after retries. Last output:` followed by the same 200-line tail at ERROR; return `false`.
12. Otherwise sleep `3000 * attempt` ms (3 s after attempt 1, 6 s after attempt 2) and retry with identical args.

Outer `catch` (2333-2337): ERROR, return `false`.

### 1.4 Intended time budget

3 attempts × 10 min + 9 s of back-off. In practice the 10-minute timeout is not effective (see defect D1).

---

## 2. GetRemoteOxideRelease() — lines 2042-2154 (context; drives UpdateOxide)

Returns `(tag, url)`; `("no", "")` on any failure.

- `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`
- Header: `User-Agent: FeedMeUpdates/1.0`. No auth token, no `Accept` header. Timeout 20 s. Non-2xx → WARN + `("no","")`.
- `tag` = `tag_name` if string, else `name` if string, else `"no"`.
- Assets: every element of `assets[]` with non-empty `browser_download_url`; `name` is lower-cased.
- Selection, iterating assets in API order:
  - Tracks `firstZip` (first asset whose url or name ends with `.zip`) and `firstNonLinuxZip` (first `.zip` whose name contains none of `linux`, `mac`, `darwin`).
  - **Windows**: name contains `windows` and not `linux`/`mac` → choose and stop. Else name contains `win` and not `linux`/`mac`, and nothing chosen yet → choose (keep iterating).
  - **Non-Windows**: name contains `linux` or `ubuntu` or `x86_64` or `x64` → choose and stop.
  - Fallbacks in order: Windows only → `firstNonLinuxZip`; then `firstZip`; then `assets[0]`.
- Expected real-world assets (unverified against the live API): `Oxide.Rust.zip` (Windows) and `Oxide.Rust-linux.zip` (Linux). With those names Windows resolves through the `firstNonLinuxZip` fallback and Linux through the `linux` match.

In the Scheduled flow the caller (434-462) first calls this function, compares `tag` case-insensitively with the `remote_oxide` CLI arg, and fails with restore (`Fails = "oxide update (different version detected)"`) on mismatch or when the arg is empty/`no`. `UpdateOxide` then calls the function a second time.

---

## 3. UpdateOxide() — lines 2341-2485

Signature: `private static bool UpdateOxide()`. Caller: `RunUpdateOxideWithRestoreOnFail` (895-910); on `false`, `Fails = "oxide update"` and restore-from-backup + exit.

### 3.1 Steps

1. `RunState.ServerDir` empty → ERROR, `false`.
2. `(tag, url) = GetRemoteOxideRelease()`. If `tag == "no"` or `url` empty → ERROR `Could not determine remote Oxide release or download URL.`, `false`.
3. Temp zip path: `Path.Combine(Path.GetTempPath(), "oxide_update_" + Guid("N") + ".zip")`.
4. Download: new `HttpClient` with `Timeout = 5 minutes`, `User-Agent: FeedMeUpdates/1.0`. `GetAsync(url)` (default completion option, i.e. the whole body is buffered in memory before returning; redirects followed by default handler) then `EnsureSuccessStatusCode()`. Failure → ERROR `Download failed`, delete temp zip if present, `false`.
5. Write body to the temp zip (`FileMode.Create`, `FileShare.None`). Failure → ERROR, delete temp zip, `false`.
6. Verification: temp zip must exist and have `Length > 0`. Otherwise ERROR, delete, `false`. **This is the only integrity check** (no hash, no size comparison, no signature).
7. Extract dir: `Path.Combine(Path.GetTempPath(), "oxide_extracted_" + Guid("N"))`, created, then `ZipFile.ExtractToDirectory(tempZip, extractRoot, overwriteFiles: true)`. Failure → ERROR, delete extract dir and zip, `false`.
8. `origin = FindExtractOrigin(extractRoot) ?? extractRoot` (see §7.3).
9. Copy "Managed" (2423-2447):
   - If `<origin>/RustDedicated_Data/Managed` exists → `CopyDirectoryOverwrite` into `<serverDir>/RustDedicated_Data/Managed`.
   - Else if `<origin>/Managed` exists → `CopyDirectoryOverwrite` into `<serverDir>/RustDedicated_Data/Managed`.
   - Else: nothing, no error.
   - Exception → ERROR `Error copying Managed assemblies`, cleanup, `false`.
10. Copy "Oxide" (2449-2470):
    - If `<origin>/Oxide` exists → `CopyDirectoryOverwrite` into `<serverDir>/oxide` (lower-case destination).
    - Else → `CopyDirectoryOverwrite(origin, serverDir)`: the whole archive content is overlaid onto the server root (this re-copies `RustDedicated_Data/Managed` a second time when step 9 used candidate 1).
    - Exception → ERROR `Error copying Oxide files`, cleanup, `false`.
11. Cleanup: delete extract dir (recursive) and temp zip, both swallow-all.
12. INFO `Completed successfully.`, return `true`.

Outer catch: ERROR, best-effort cleanup of both temp paths, `false`.

### 3.2 What is NOT done

- No post-copy verification (no check that `Oxide.Core.dll`/`Oxide.Rust.dll` exist or that their version equals `tag`).
- No removal of stale files from a previous Oxide version; copy is overlay-only.
- No check that the downloaded tag equals the tag validated by the caller.
- No platform-specific handling beyond asset choice (no chmod, nothing for the Linux compiler binary).
- No retry on download failure.

---

## 4. UpdatePlugins() — lines 2492-2738

Signature: `private static bool UpdatePlugins()`. Returns `true` if the pass ran with no error (or was skipped), `false` if any plugin hit an error. The caller (487-499) only logs a WARN on `false`; **plugin errors never trigger a restore and never fail the run**.

### 4.1 Setup

- `RunState.UpdatedPlugins` is reset to a new empty list on entry (2495).
- If `RunState.UpdatePluginsFlag` is not `yes` (case-insensitive) → INFO, return `true`.
- Plugins folder: `<ServerDir>/oxide/plugins`. Missing → INFO, `true`.
- Enumeration: `Directory.GetFiles(pluginsFolder, "*.cs", SearchOption.TopDirectoryOnly)`. None → INFO, `true`. Order is filesystem enumeration order (not sorted).
- One shared `HttpClient`, `Timeout = 30 s`, `User-Agent: FeedMeUpdates/1.0`.
- `requestsPerMinute = 30`.

### 4.2 Per-plugin procedure

For each file `f`:

1. Skip entirely if `f.EndsWith("FeedMeUpdates.cs")` (2529; culture-sensitive, case-sensitive). FMU itself is updated by the self-update pass, never via uMod.
2. `meta = ParseInfoAttribute(f)` (§5.3). `null` → WARN `[Info(...)] attribute not found or unparsable`, skip (not an error).
3. `localTitle`, `localAuthor`, `localVersion` from meta; `baseLocalName = file name without extension`; `className = GetClassNameFromCs(f)` (§5.4). `localAuthor` is only logged, never used for matching.
4. Empty `localVersion` → WARN, skip.
5. `ThrottleIfNeeded(30)` (§5.2).
6. Search request:
   ```
   GET https://umod.org/plugins/search.json?query=<Uri.EscapeDataString(localTitle)>&page=1&sort=title&sortdir=asc
   ```
   via `GetStringAsync` (throws on non-2xx). Exception → ERROR `HTTP search error`, `hadError = true`, next plugin. No retry, no handling of `429`/`Retry-After`.
7. Parse JSON. Requires root property `data` to be a non-empty array, else WARN `No results from uMod search`, skip (not an error). A JSON parse exception is caught by the per-plugin catch (2721) → `hadError = true`.
8. Candidate filter: for each item in `data` having `download_url`: `remoteFile = GetFileNameFromUrl(download_url)`, `baseRemote = file name without extension`. Candidate if `baseRemote` equals `baseLocalName` OR `className`, case-insensitive. Per-item exceptions are swallowed.
9. Zero candidates → WARN `No candidate found`, skip (not an error).
10. More than one candidate → disambiguation: keep those whose `latest_release_version` is a JSON string and `CompareVersionStrings(remote, local) >= 0`. Exactly one left → it is the match (INFO `Disambiguated by version`). Otherwise (0 or ≥2) → WARN `Ambiguous candidates ... skipping`, skip (not an error).
11. `remoteVersion = match.latest_release_version` (must be a JSON string). Empty → WARN, skip.
12. `CompareVersionStrings(remoteVersion, localVersion) <= 0` → INFO up-to-date, skip.
13. `download_url` missing or empty on the match → ERROR, `hadError = true`, skip.
14. `ThrottleIfNeeded(30)` again (the download counts against the same budget).
15. Temp file: `<pluginsFolder>/<Guid "D" format>.temp` (2673-2674), i.e. inside the live plugins folder.
16. `GET <download_url>` with the same client; `EnsureSuccessStatusCode`; stream body to the temp file. Failure → ERROR, delete temp, `hadError = true`, skip.
17. Temp file must exist with `Length > 0`, else ERROR, delete temp, `hadError = true`, skip. No other validation of the content.
18. Replace: `finalPath = <pluginsFolder>/<original local file name>` (local name is preserved, the remote file name is not used). If it exists, `File.Delete(finalPath)`; then `File.Move(tempFilePath, finalPath)`. Failure → ERROR, delete temp if present, `hadError = true`.
19. On success: INFO, and `RunState.UpdatedPlugins.Add(Path.GetFileName(finalPath))` (e.g. `Kits.cs`).

Any other exception for a plugin → ERROR `Unexpected exception checking <file>`, `hadError = true`, continue.

Final: INFO `Completed plugin update pass.`, return `!hadError`. Outer catch → ERROR, `false`.

### 4.3 What is recorded

- `RunState.UpdatedPlugins`: file names (with `.cs`) of plugins actually replaced.
- Success marker (527-547): key `updated_plugins` (JSON array of those names) is present only when `update_plugins == yes` (may be an empty array).
- Failure marker written by `CreateUpdateresultAndStartScript` (3217-3235): `updated_plugins` included only if the flag is exactly `"yes"` (case-sensitive here) and the list is non-empty.
- Restore markers (1771-1779, 1829-1837) never include `updated_plugins`.
- The plugin reads the key with the regex `"updated_plugins"\s*:\s*\[(.*?)\]` (`FeedMeUpdates.cs:1124`).

### 4.4 uMod response fields relied upon

`data[]`, `data[].download_url` (string URL whose last path segment is `<PluginFile>.cs`), `data[].latest_release_version` (string). Nothing else is read.

---

## 5. Misc helpers — lines 2742-2896

### 5.1 ResolveSystemctlPath() (2745-2788)

Used only on the Linux service-start path (3170): result is used as `<systemctl> start <service>` when root, or `sudo -n <systemctl> start <service>` otherwise (30 s timeout each).

1. Run `which systemctl` (`UseShellExecute=false`, stdout+stderr redirected, UTF-8, no window). Read stdout to end, trim, `WaitForExit(2000)`. If non-empty and `File.Exists(output)` → return it. All exceptions swallowed.
2. Else iterate `PATH` (or, if unset, `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`) split on `Path.PathSeparator`; return the first `<dir>/systemctl` that exists.
3. Else return the literal `systemctl`.

### 5.2 ThrottleIfNeeded(int RequestsPerMinute) (2791-2803)

Sliding-window limiter over the static `__PluginRequestTimestamps`:

- Loop: drop timestamps older than `UtcNow - 60 s`; if `Count < RequestsPerMinute` break; else WARN `rate limit reached (N/min). Waiting 5s...` and `Thread.Sleep(5000)`.
- Then append `UtcNow`.
- Called with 30, once before each search and once before each download. Not thread-safe (single-threaded use). It does not react to server responses.

### 5.3 ParseInfoAttribute(string file) → PluginInfo? (2805-2829)

- `record PluginInfo(string Title, string Author, string Version)`.
- Reads the first **200 lines**, joins with `\n`, applies (no `RegexOptions`):
  ```
  \[Info\(\s*"(.*?)"\s*,\s*"(.*?)"\s*,\s*"(.*?)"\s*\)\]
  ```
- First match wins; groups 1/2/3 = title/author/version. No match → `null`. Exception → ERROR `ParseInfoAttribute error for <file>`, `null`.
- Also used by the FMU self-update to read the local FMU version from `oxide/plugins/FeedMeUpdates.cs` (1020-1027).

### 5.4 GetClassNameFromCs(string file) → string (2832-2843)

- Reads the first **400 lines**, joined with `\n`, regex `class\s+([A-Za-z_][A-Za-z0-9_]*)\s*[:\{]`, first match → group 1.
- No match or exception → file name without extension.

### 5.5 GetFileNameFromUrl(string url) → string (2846-2850)

Empty → `""`. Else `Path.GetFileName(new Uri(url).AbsolutePath)`; if `Uri` construction throws, last `/`-separated segment (which may then include a query string).

### 5.6 CompareVersionStrings(string? a, string? b) → int (2853-2866)

- `va = ParseVersion(a)`, `vb = ParseVersion(b)`.
- Both non-null → `va.CompareTo(vb)` (`System.Version` semantics).
- Only `a` parses → `1`; only `b` parses → `-1`.
- Neither parses and both non-null → `string.Compare(a, b, Ordinal)`.
- Otherwise `0`. Any exception → `0`.
- Callers: plugin pass (`remote` vs `local`), FMU self-update (GitHub tag vs local plugin version, 1034).

### 5.7 ParseVersion(string? s) → Version? (2869-2896)

1. Null/empty → `null`. Trim.
2. `Version.TryParse(s)` succeeds → return (accepts 2 to 4 numeric components).
3. Regex `\d+(?:\.\d+){0,3}` first match `ss`:
   - `Version.TryParse(ss)` succeeds → return.
   - Otherwise `parts = ss.Split('.')`; `while (parts.Length < 3) ss += ".0";` then `TryParse(ss)`. (Broken, see D3.)
4. Fallback: split on `\D+`, take the first 3 digit groups, pad with `"0"` to 3, join with `.`, `TryParse`.
5. Else `null`; exceptions swallowed → `null`.

Resulting semantics worth preserving or consciously changing:
- A leading `v` and suffixes like `-beta` are tolerated through the regex step (`v1.2.3` → `1.2.3`, `1.2.3-beta` → `1.2.3`).
- More than 4 components are truncated to the first 4.
- `System.Version` treats missing components as `-1`, so `1.0 < 1.0.0` and `1.0.0 < 1.0.0.0`.

---

## 6. File and directory helpers — lines 2900-3071

### 6.1 IsUnderAny(string path, HashSet<string> roots) → bool (2903-2913)

- Normalises with `Path.GetFullPath(...).TrimEnd(Path.DirectorySeparatorChar)`.
- True if equal to a root, or starts with `root + DirectorySeparatorChar`. Both comparisons are `OrdinalIgnoreCase` **on every OS**.
- Used by `CreateTempBackup` (1656, 1665) with excludes: `<serverDir>/temp_backup`, `<serverDir>/updater.log`, `<serverDir>/FeedMeUpdates.exe`, `<serverDir>/FeedMeUpdates`.

### 6.2 CopyDirectoryOverwrite(string src, string dst) (2916-2938)

- Normalises both roots; creates `dst`.
- Creates every sub-directory of `src` (recursive enumeration) under `dst`.
- For every file (recursive): ensure parent exists, `File.Copy(file, target, overwrite: true)`, log INFO `Copied file: <rel> -> <target>` (one log line per file).
- No exception handling: the first failure propagates to the caller, leaving a partial copy.
- Overlay only: never deletes anything in `dst`. Does not clear read-only attributes on targets.
- Used by `UpdateOxide` only (2432, 2438, 2456, 2461).

### 6.3 FindExtractOrigin(string extractRoot) → string? (2941-2960)

- If `<root>/RustDedicated_Data/Managed` or `<root>/Managed` exists → `root`.
- Else enumerate all sub-directories recursively and return the first `d` for which `<d>/RustDedicated_Data/Managed`, `<d>/Managed` or `<d>/Oxide` exists.
- None → `null`; exception → `null`. Callers fall back to `extractRoot`.
- Callers: `UpdateOxide` (2420) and the FMU self-update (1100).

### 6.4 TmuxSessionExists(string sessionName, int timeoutMs = 2000) → bool (2963-2996)

- Empty name → `false`.
- Runs `tmux has-session -t <sessionName>` (name interpolated unquoted; `UseShellExecute=false`, no redirection, no window).
- Not exited within `timeoutMs` → `Kill()`, `false`. Else `ExitCode == 0`. Exception (e.g. tmux absent) → `false`.
- Caller: `SafeLaunchTmuxSession` (1574-1609), which runs `tmux kill-session -t <session>` (wait 500 ms), then `tmux new-session -d -s <session> "bash -lc 'exec <script>'"`, sleeps 300 ms, and returns `TmuxSessionExists(session)`.

### 6.5 DetectGnomeAndTmux() → bool[2] (2999-3033)

- Non-Linux → `{false, false}`.
- `Exists(name)`: runs `sh -c "command -v <name> >/dev/null 2>&1 && printf 1 || printf 0"`, reads stdout to end, `WaitForExit(500)`, true iff output is `1`. Exception → false.
- Result: `[0] = Exists("gnome-terminal") || Exists("gnome-terminal-server")`, `[1] = Exists("tmux")`.
- Caller: `StartServerScript` (1475-1478). Only binary presence is tested, not usability (no `DISPLAY` check).

### 6.6 CleanTempBackup(string serverDir) (3036-3071)

- Target: `<serverDir>/temp_backup`.
- Absent → INFO, return.
- `Directory.Delete(path, recursive: true)`; exception → WARN. Then re-check existence: INFO `successfully removed` or WARN `still present after delete attempt`. Never throws, returns nothing, single attempt.
- Call sites: success path (553), after a successful restore (1789), inside `HandleRestoreFailure` when `start_script` is non-empty (1851), and inside `CreateUpdateresultAndStartScript` before starting script/service (3267, 3287).

---

## 7. External contracts in this range

### 7.1 Files and directories

| Path | Role |
|---|---|
| `<serverDir>/updater.log` | log, truncated when > 5 MiB at start |
| `<serverDir>/steamapps/appmanifest_258550.acf` | renamed to `.acf.bak` on SteamCMD "state is 0x6" |
| `<serverDir>/steamapps/appmanifest_258550.acf.bak` | leftover of the above; overwritten next time |
| `<serverDir>/RustDedicated_Data/Managed/` | destination of Oxide assemblies |
| `<serverDir>/oxide/` | destination of an `Oxide/` folder if the archive has one |
| `<serverDir>/oxide/plugins/*.cs` | plugins enumerated and replaced |
| `<serverDir>/oxide/plugins/<guid>.temp` | plugin download temp file |
| `<serverDir>/oxide/plugins/FeedMeUpdates.cs` | excluded from the plugin pass |
| `<serverDir>/temp_backup/` | backup dir removed by `CleanTempBackup` |
| `<serverDir>/updateresult.json` | marker; `updated_plugins` array originates here |
| `<tmp>/oxide_update_<guid32>.zip`, `<tmp>/oxide_extracted_<guid32>/` | Oxide download/extract (`Path.GetTempPath()`) |

### 7.2 Processes

| Command line | Where |
|---|---|
| `<SteamCmdPath> +force_install_dir "<serverDir>" +login anonymous +app_update 258550 validate +quit` | 2199 |
| `which systemctl` | 2749 |
| `tmux has-session -t <session>` | 2976 |
| `sh -c "command -v gnome-terminal >/dev/null 2>&1 && printf 1 \|\| printf 0"` (also `gnome-terminal-server`, `tmux`) | 3011 |

### 7.3 Network

| Request | Headers | Timeout |
|---|---|---|
| `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest` | `User-Agent: FeedMeUpdates/1.0` | 20 s |
| `GET <assets[].browser_download_url>` | same | 5 min (whole download) |
| `GET https://umod.org/plugins/search.json?query=<title>&page=1&sort=title&sortdir=asc` | same | 30 s |
| `GET <data[].download_url>` | same | 30 s |

The User-Agent is a hard-coded `FeedMeUpdates/1.0` regardless of the real version.

### 7.4 CLI/RunState inputs consumed

`steamcmd` path, `server_dir`, `update_plugins` (`yes`/`no`), `remote_oxide` (compared by the caller), tmux session name, service name.

---

## 8. Implicit knowledge and workarounds

1. **SteamCMD success is detected from text, not exit code.** The code looks for `Success! App '258550'` (covers "fully installed" and "already up to date"). Likely reason: SteamCMD exit codes are unreliable across platforms/wrapper scripts (unverified). The rewrite should keep the text check and add the exit code as a secondary signal rather than replace it.
2. **`+force_install_dir` must precede `+login`.** Newer SteamCMD prints `Please use force_install_dir before logon!` otherwise. The old code detects the string only to log it.
3. **`state is 0x6` after update job** is treated as a corrupt/stale app manifest: rename `steamapps/appmanifest_258550.acf` to `.bak` so the next attempt rebuilds it, then retry. This is the only real remediation in the retry loop.
4. **Linear back-off 3 s / 6 s, 3 attempts** for transient SteamCMD failures (CDN/login hiccups).
5. **Output is split on both `\r` and `\n`** because SteamCMD emits progress lines with carriage returns; only the last 200 lines are logged to keep the log readable.
6. **Working directory = SteamCMD's own directory**, presumably because SteamCMD expects to find its runtime files relative to itself (unverified).
7. **`validate` is always passed.** It guarantees file integrity but also reverts any file Oxide patched in `RustDedicated_Data/Managed`. Consequence: every server update must be followed by an Oxide install or the server boots without Oxide (and therefore without the FMU plugin). The non-scheduled flows always run both (469-470); the Scheduled flow runs only what `what` says (see D9).
8. **Oxide asset naming**: Windows asset has no OS token in its name, Linux has `-linux`; hence the "first zip not containing linux/mac/darwin" fallback for Windows.
9. **Oxide zip layout**: files live under `RustDedicated_Data/Managed/`; the code also tolerates a wrapping top-level folder (`FindExtractOrigin`), a bare `Managed/` folder, and an `Oxide/` folder. The normal case ends in "overlay the archive root onto the server root".
10. **Scheduled flow pins versions**: the plugin passes the tag/build it saw; the app re-queries and aborts if upstream moved in the meantime, so the plugin's stored "remote" values match what was installed.
11. **uMod title vs file name mismatch**: uMod search works on the human title (`[Info("Admin Radar", ...)]`) while files are named `AdminRadar.cs`. Hence: search by title, then accept only results whose `download_url` file name equals the local file name or the plugin class name.
12. **Class name as second key**: covers plugins whose local file was renamed; Oxide requires file name == class name so normally the two are identical.
13. **Ambiguity is resolved conservatively**: several candidates → keep those not older than local; if still not exactly one, do nothing.
14. **uMod rate limiting**: 30 requests per rolling minute, counting both searches and downloads, polled every 5 s. The real uMod limit is not in the code; 30/min is the empirically chosen safe value (unverified).
15. **Local file name is preserved on replace**, so a case difference or rename on uMod's side cannot create a duplicate plugin file.
16. **Download to a temp file with a non-`.cs` extension inside the plugins folder**: same volume, so the final move is a rename, and Oxide ignores `.temp` files.
17. **Zero-length download guard** for both Oxide and plugins.
18. **FMU's own plugin is excluded** from the uMod pass.
19. **Version strings are messy**: tolerate `v` prefixes and suffixes, fall back to ordinal string comparison; "parseable beats unparseable".
20. **Absolute `systemctl` path**: sudoers `NOPASSWD` rules match on the absolute command path (README section 12 builds the rule from `command -v systemctl`), so `sudo -n` must be given the same absolute path. Fallback PATH list covers environments with an empty `PATH`.
21. **tmux launch is verified** by `has-session` 300 ms after `new-session`, because `tmux new-session -d` returns success even if the command inside dies immediately (unverified motivation).
22. **gnome-terminal detection also checks `gnome-terminal-server`**.
23. **Backup excludes itself, the log and the updater binary** (`IsUnderAny`), since those are in use or would recurse.
24. **`CleanTempBackup` verifies after deleting** and only warns, because recursive delete can partially fail on locked/read-only files (notably on Windows).
25. **Plugin failures are non-fatal by design**: a broken uMod must not block a server/Oxide update.

---

## 9. Defects and risks

Severity reflects impact on an unattended game server.

### High

**D1. SteamCMD timeout is ineffective; possible pipe deadlock (2227-2234).**
`StandardOutput.ReadToEnd()` blocks until SteamCMD closes stdout, i.e. until it exits. Only then is `WaitForExit(10 min)` reached, so the timeout can never fire for a hung SteamCMD: the updater hangs forever with the server down. In addition stdout and stderr are drained sequentially; if SteamCMD writes more than the pipe buffer to stderr while stdout is still open, both processes block (classic redirect deadlock). Rewrite: read both streams asynchronously and enforce a real wall-clock timeout with tree kill.

**D2. Exit code ignored and over-broad success heuristic (2255-2261).**
Success is declared if the output contains `installed` and `258550` anywhere, independently. Any future or localized message combining those tokens on a failed run reports success, the backup is deleted and the server is started on a partial install. I did not find a concrete current SteamCMD failure message that triggers it (unverified); the risk is structural. The second `Success! app` test is dead (duplicate under `OrdinalIgnoreCase`).

**D3. Infinite loop in `ParseVersion` (2881-2882).**
`var parts = ss.Split('.'); while (parts.Length < 3) ss += ".0";` never updates `parts`. Reached whenever `Version.TryParse` fails on a regex match with fewer than 3 components: a single-number version (`"2"`, `"v3"`, `"7-beta"`) or an overflowing component with ≤ 2 parts. The loop appends to `ss` until memory is exhausted; in practice the updater hangs with the server offline. Triggerable by a local plugin `[Info("X","Y","2")]`, by a uMod `latest_release_version`, or by a FMU GitHub tag such as `v2` (path 1034). Step 4 of `ParseVersion` is consequently almost unreachable dead code.

**D4. Plugin identity confusion: any same-named uMod plugin overwrites the local file (2580-2596, 2701-2711).**
Matching uses only file/class name; `localAuthor` is parsed and logged but never compared. A paid/third-party or privately written plugin whose file name coincides with a uMod plugin of higher version number is silently replaced by the uMod one. Locally modified uMod plugins are likewise overwritten. The only copy of the old file is in `temp_backup`, which is deleted at the end of a successful run.

**D5. Downloaded plugin content is not validated (2677-2699).**
Only "HTTP 2xx and length > 0" is checked. An HTML page served with 200 (CDN challenge, maintenance page) replaces a working plugin with garbage, which then fails to compile at boot. No check that the file contains an `[Info]` attribute, the expected class name or the advertised version. `download_url` host/scheme is not validated either.

**D6. Oxide download unverified and install unverified (2368-2403, 2423-2476).**
No checksum/size check against the GitHub asset metadata, and after copying there is no check that Oxide assemblies are present. If the archive contains neither `Managed` candidate, step 9 is skipped silently and step 10 overlays whatever the archive holds onto the server root, returning success. Combined with the permissive asset fallback (`assets[0]`, any `.zip`), a changed release layout yields "success" with no Oxide.

**D7. Backup deleted after a failed restore (1851, uses `CleanTempBackup`).**
`HandleRestoreFailure` calls `CleanTempBackup` before starting the script, destroying the only good copy precisely when the restore did not complete. Outside the primary range but it is a call site of the helper specified here.

### Medium

**D8. TOCTOU between tag validation and download (437-461 vs 2355).**
`UpdateOxide` re-queries GitHub and installs whatever is latest at that moment; the tag validated by the caller is not passed in or re-checked. A release published in between is installed while the plugin records the older tag. Also doubles unauthenticated GitHub API calls (60/hour/IP limit, shared with plugin-side checks).

**D9. Server-only scheduled update leaves the server without Oxide (design; 430-431, 2199).**
With `what=server`, `app_update ... validate` restores vanilla managed assemblies and nothing reinstalls Oxide. The server would boot without Oxide and the FMU plugin would never load to read the marker. The Scheme feature explicitly allows the action `server`. Unverified at runtime; follows from SteamCMD `validate` semantics.

**D10. `state is 0x6` substring also matches other states (2264).**
`0x602`, `0x606`, `0x626` etc. (commonly disk-space/write or network failures, unverified) also trigger the manifest rename, forcing a full re-validation that cannot fix those causes. The `.acf.bak` file is never cleaned up.

**D11. Exceptions while launching SteamCMD are retried pointlessly; null process is not (2221-2225, 2238-2242).**
A non-executable or wrong-architecture binary throws on every attempt and burns all three attempts and 9 s, with the heuristics evaluated on `ex.ToString()`. Inconsistent with the `proc == null` path that returns at once.

**D12. Quoting of `serverDir` in SteamCMD args (2199).**
A Windows path ending in a backslash (`C:\rust\`) produces `"C:\rust\"`, where `\"` is parsed as an escaped quote, corrupting the rest of the command line. A path containing `"` breaks it on any OS. Use `ArgumentList`.

**D13. `ParseInfoAttribute` regex is too strict (2814).**
It requires `)]` immediately after the third string. Not matched, hence plugin silently skipped forever:
- `[Info("T", "A", "1.0.0"), Description("...")]` (combined attribute list);
- legacy `[Info("T", "A", "1.0.0", ResourceId = 123)]`;
- an `[Info]` after line 200;
- version given as a non-literal.
It also takes the first textual match, including one inside a comment, and `(.*?)` mishandles escaped quotes. The same parser gates the FMU self-update (1020-1025).

**D14. Only page 1 of uMod search is examined (2558).**
Results are sorted by title ascending; a plugin with a generic title (many hits) may not be on page 1 and is reported as "No candidate found" indefinitely. Page size is not in the code (unverified).

**D15. No handling of uMod HTTP 429 / `Retry-After` (2562-2571).**
The limiter is purely client-side. Once uMod throttles, every remaining plugin fails fast with an error; nothing waits or retries.

**D16. Non-atomic plugin replace (2704-2709).**
`File.Delete(final)` then `File.Move(temp, final)`. If the move fails (AV lock, permissions) the plugin is gone; since plugin errors never trigger a restore and the backup is deleted on success, the plugin is lost. Use `File.Move(temp, final, overwrite: true)` or `File.Replace`.

**D17. `System.Version` component semantics cause perpetual re-download (2859).**
Local `1.0` versus remote `1.0.0` compares as older (missing component = -1), so the plugin is downloaded and "updated" on every run, and reported in `updated_plugins` every time. Normalise to a fixed component count before comparing.

**D18. `CopyDirectoryOverwrite` is non-atomic and overlay-only (2916-2938).**
A mid-copy failure leaves a mix of old and new Oxide DLLs (mitigated only by the caller's restore, which is itself overlay-only at 1729-1763 and does not remove files the failed update added). Stale assemblies from older Oxide versions are never removed. Read-only destination files make `File.Copy` throw.

**D19. Whole-archive overlay onto server root (2458-2462).**
In the normal layout the Managed folder is copied twice (step 9 then step 10). With a `Managed/`-at-root layout, step 10 additionally creates a stray `<serverDir>/Managed`. Any other file in the archive lands in the server root unfiltered.

**D20. Stale `temp_backup` contents can be restored later (3048-3065 with 1630-1633).**
If recursive delete partially fails, the next run reuses the directory ("files inside will be overwritten") without clearing it; files that no longer exist in the live server remain in the backup and are resurrected by a later restore.

### Low

**D21. Zip-slip.** `ZipFile.ExtractToDirectory` on .NET 8 rejects entries resolving outside the destination (framework behaviour, not verified by test here), so classic traversal is mitigated. Symlink entries and the subsequent unfiltered overlay (D19) are not specifically guarded. A hand-rolled extractor in the rewrite must re-implement the containment check.

**D22. `IsUnderAny` is case-insensitive on Linux (2909-2910).** On a case-sensitive filesystem, `Temp_Backup/` or a file named `feedmeupdates` would be wrongly excluded from the backup. It also excludes a *directory* named `FeedMeUpdates`.

**D23. FMU exclusion test (2529).** `EndsWith("FeedMeUpdates.cs")` is culture-sensitive and case-sensitive, also skips e.g. `MyFeedMeUpdates.cs`, and does not skip `FeedMeUpdatesGUI.cs` (which the self-update handles at 1013/1018), so the GUI plugin is sent through the uMod search.

**D24. `GetClassNameFromCs` takes the first `class X :`/`class X {` in the first 400 lines (2838),** which may be a helper class, a nested type declared before the plugin class, or text in a comment. Effect: only widens/misdirects the secondary match key.

**D25. tmux session name unquoted and non-exact (2976, 1581, 1592).** A name with spaces splits into several arguments. `-t <name>` uses tmux target matching (prefix/pattern), so `rust` can match an existing session `rust2`, giving a false positive in the post-launch check and killing the wrong session at 1581 (tmux behaviour, unverified here). Use `-t "=<name>"`.

**D26. `DetectGnomeAndTmux` reads stdout to end before the 500 ms wait (3019-3020)** — the timeout is ineffective (same pattern as D1, tiny risk). `ResolveSystemctlPath` has the same pattern and redirects stderr without reading it (2762-2763).

**D27. Leftover artefacts.** `<guid>.temp` files remain in `oxide/plugins` if the process dies mid-download; `appmanifest_258550.acf.bak` remains; `/tmp/oxide_*` remain if the process is killed. Nothing sweeps them at startup.

**D28. Resource handling.** `HttpResponseMessage` at 2374 is not disposed; a new `HttpClient` is built per operation; all HTTP is sync-over-async; Oxide zip is fully buffered in memory before being written.

**D29. Logging volume.** One INFO line per copied file (2936, also 1672/1755) produces very large logs per run; together with truncate-at-5-MiB-on-start, the previous run's diagnostics are frequently wiped. On final SteamCMD failure the 200-line tail is logged twice (2251, 2319).

**D30. Duplicated logic.** Tail extraction duplicated (2248-2250 vs 2316-2318); cleanup of `extractRoot`/`tempZip` repeated six times in `UpdateOxide`; download-to-temp + zero-length check duplicated between Oxide, plugins and the FMU self-update (1042 ff.); the `update_plugins == yes` gate is checked by both caller (487) and callee (2501), and with a different (case-sensitive) comparison at 3217.

**D31. Marker inconsistency.** `updated_plugins` is omitted from restore markers even when plugins were already replaced before a later failure; note however that in the current ordering the plugin pass runs after server/Oxide updates, so a restore after plugin replacement can only come from the wipe path (which does not restore). Types also differ across markers (`server_restored` is `"no"`/`"yes"` strings in some, boolean `false` in others: 535, 1776, 1834, 3225).

**D32. `GetRemoteOxideRelease` asset matching oddities (2111-2128).** On Windows `win` also matches `darwin` unless the name contains `mac`; on Linux `x64`/`x86_64` would match a Windows x64 asset listed first. Harmless with today's two assets.

---

## 10. Open questions

1. Is `validate` required on every update, or only as a remediation on retry? It lengthens every update and is what forces the Oxide reinstall (D9).
2. Should `what=server` (scheduled/scheme) implicitly reinstall the currently installed Oxide version, or is "server boots vanilla" acceptable/intended when Oxide has not yet released for the new protocol?
3. Was the `installed` + `258550` success clause added for a specific SteamCMD output variant? If so, which exact line?
4. Should a SteamCMD timeout be a hard failure, or (as now) fall through to output evaluation?
5. Which other `state is 0x...` codes should trigger remediation, and should the `.acf.bak` be restored when all attempts fail?
6. For plugin matching, should author be part of the identity check, and should there be an allow/deny list (to protect paid or customised plugins)?
7. Is `30` requests/minute an observed uMod limit or a guess? What does uMod return when throttled (status, `Retry-After`)?
8. When several uMod candidates remain after version filtering, is "skip" the desired behaviour, or should an exact file-name match be preferred over a class-name match?
9. Should a plugin update failure affect the run result or the marker (currently only a log WARN)?
10. Should the previous plugin file be kept (e.g. `.bak` or a dated backup folder) given `temp_backup` is deleted on success?
11. Is the `Oxide/` folder branch of `UpdateOxide` (copy to `<serverDir>/oxide`) based on a real archive layout, or defensive only?
12. Is the tmux session name expected to be exact, and may it contain spaces?
13. Should log rotation keep the previous file (`updater.log.1`) instead of truncating?
