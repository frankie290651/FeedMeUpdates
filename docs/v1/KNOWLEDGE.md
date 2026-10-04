# FeedMeUpdates v1 — knowledge the v2 rewrite must not lose

A checklist of the practical, mostly undocumented knowledge embedded in FeedMeUpdates v1.6.5: the quirks of SteamCMD, Oxide, uMod, service managers, terminals, Rust's files and the game's Mono runtime that the v1 code works around. It is consolidated from the eight specifications in `docs/v1/spec/` and checked against the source where they differed.

How to read it:

- Each item is a **rule** v2 must keep, **why** it exists (the real-world quirk), **where** it lives in v1, and a **v1 flag** when v1's own implementation of the rule is wrong or doubtful — keep the intent, not the code. Flags point to `DEFECTS.md` IDs.
- Locations: `P:` = `FeedMeUpdates.cs`, `A:` = `Application/Program.cs`, `S:` = `Scripts/`, `README` = `README.md` section.
- "(external)" marks knowledge about third-party behaviour that the specs state but that was not tested here; re-check before relying on it.
- The appendix lists v1's complete external contracts (config, datafile, files, CLI, marker, commands).

## 1. SteamCMD

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-001 | Rust Dedicated Server is Steam app `258550`; use `+login anonymous`. Only the `public` branch is supported. | The app is downloadable anonymously; no credentials are ever needed. Staging is explicitly out of scope. | P:1698; A:1966, 2199; README line 46 | — |
| K-002 | Update command: `+force_install_dir "<dir>" +login anonymous +app_update 258550 validate +quit`, with `+force_install_dir` **before** `+login`. | Newer SteamCMD prints "Please use force_install_dir before logon!" and may ignore the directory otherwise. | A:2199, 2263 | Directory quoting breaks on a trailing backslash (D-025). |
| K-003 | Decide SteamCMD success from its output text (`Success! App '258550'`, which covers "fully installed" and "already up to date"), with the exit code as a secondary signal. | SteamCMD exit codes are unreliable across platforms and wrapper scripts (external). | A:2255-2261 | v1 ignores the exit code entirely and also accepts `installed` + `258550` anywhere (D-049). |
| K-004 | On `state is 0x6`, move `steamapps/appmanifest_258550.acf` aside (`.acf.bak`) and retry. | A stale or corrupt app manifest blocks the update; SteamCMD rebuilds it on the next run. | A:2264-2302 | Substring also matches `0x602`/`0x606`…; the `.bak` is never restored or removed (D-052). |
| K-005 | Retry transient SteamCMD failures: 3 attempts, waits of 3 s and 6 s. | CDN and login hiccups are common. | A:2201-2328 | Launch exceptions are retried pointlessly (D-122). |
| K-006 | Split SteamCMD output on both `\r` and `\n`; log only the last 200 lines. | Progress lines use carriage returns; the full output floods the log. | A:2248-2250 | Tail is logged twice on final failure. |
| K-007 | Merge stdout and stderr when reading SteamCMD. | It writes useful text to either stream depending on platform. | A:1899-1902, 2227-2236 | The plugin reads stdout only and never drains stderr (D-010). |
| K-008 | Run the update with the working directory set to SteamCMD's own folder. | SteamCMD expects its runtime files next to itself (external). | A:2214 | The query calls (A:1966, P:1698) do not set it. |
| K-009 | `validate` restores vanilla files in `RustDedicated_Data/Managed`, so every server update must be followed by an Oxide install. | Otherwise the server boots without Oxide, and therefore without FMU, and the result marker is never read. | A:2199, 469-470 | The Scheduled flow with `what=server` does not reinstall Oxide (D-051). |
| K-010 | Remote build id comes from `+login anonymous +app_info_print 258550 +quit`; parse `"branches"` → `"public"` → `"buildid" "<5–12 digits>"` with brace balancing. Never take the first `buildid`. | The output is VDF mixed with banner lines and contains a `buildid` for every branch. | A:1954-2039; P:1685-1760 | Two hand-synchronised copies; brace matcher ignores quotes (D-110). |
| K-011 | Refresh SteamCMD's app-info cache before printing (`+app_info_update 1`, or delete `appcache/appinfo.vdf`). | SteamCMD can answer `app_info_print` from a stale local cache (external). | not in v1 | v1 never refreshes (D-047). |
| K-012 | The installed Steam build id cannot be read from inside the game: `server.Version`/`server.Protocol` are Rust's numbers, not the depot build id. Something outside the game must establish it. | This is the whole reason for `LocalSteamBuildID`, the first-boot "init" update and `steambid.temp`. | P:983-1019, 3515-3521; A:466-478 | v1 keeps a *belief* that can go stale (D-082, D-036); v2 could read `steamapps/appmanifest_258550.acf`. |
| K-013 | Compare build ids (and Oxide versions) for inequality, not ordering. | A rollback on Steam's side must also trigger an update. | P:1452-1486, 1263 | — |
| K-014 | SteamCMD must be given as a full path to an existing file. | The app checks `File.Exists`; PATH lookup is not done. | A:2166-2197, 818-825 | Not checked for executability. |
| K-015 | Run every external process with asynchronous reads of both pipes, a real wall-clock timeout and a process-tree kill. | SteamCMD can stall on network or a self-update prompt; a full stderr pipe deadlocks a sequential reader. | pattern exists in A:1912-1947 | That function has no callers; every runner actually used is wrong (D-008, D-009). |

## 2. Oxide releases and compatibility

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-016 | Latest Oxide = `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`; use `tag_name` (fallback `name`), `assets[].name`, `assets[].browser_download_url`. | This is the release feed both programs agree on; the tag string is the cross-process contract. | A:2042-2154; P:1763-1797 | Plugin extracts with a regex, not a JSON parser. |
| K-017 | Oxide asset names: Windows `Oxide.Rust.zip` has **no OS token**, Linux is `Oxide.Rust-linux.zip`. Windows must be "first zip that is not linux/mac/darwin". | Matching on `win`/`windows` alone finds nothing (external, current naming). | A:2095-2141 | Heuristics are loose (`win` in `darwin`, `assets[0]` fallback) (D-109). |
| K-018 | Oxide zip layout: files under `RustDedicated_Data/Managed/`; tolerate a wrapping top-level folder, a bare `Managed/`, and an `Oxide/` folder. | Layout has varied; the normal case is "overlay the archive root onto the server root". | A:2423-2470, 2941-2960 | Whole-archive overlay, double copy, no post-install check (D-058, D-029). |
| K-019 | Tags may or may not have a leading `v`: strip it for comparison and try both forms for lookups. | Oxide's tag naming is not consistent. | P:1786, 1888-1890 | — |
| K-020 | Tags can contain invisible characters (BOM, zero-width, control); clean before building URLs or comparing. | Observed in practice (the hex-dump debug code exists for this). | P:1855-1867, 2140-2145 | — |
| K-021 | Local Oxide version = `ProductVersion` (else `FileVersion`) of `RustDedicated_Data/Managed/Oxide.Rust.dll`; compare only the first `X.Y.Z` of both sides. | DLL versions carry `+hash`/`-suffix`/a 4th component; tags carry `v`. | P:1804-1848 | Three other probed paths are not real; failure returns `"unknown"`, which loops (D-085). |
| K-022 | The Oxide feed gates everything: if it cannot be fetched, do not update anything in that cycle. | Updating Rust without a matching Oxide leaves the server without Oxide and without FMU. | P:1252-1255, 1426-1429, 3585 | — |
| K-023 | Oxide changed + protocol changed + Rust unchanged ⇒ wait. Oxide changed + same protocol ⇒ Oxide only. Both changed ⇒ both. Rust changed alone with `OnlyServerProtocolUpdate` ⇒ wait for Oxide. | Around force wipe Oxide and Rust builds are published at different moments; installing one without the other breaks the server. | P:1411-1593 (table in spec P2 §2.10) | "Unknown" permits an update and network failures land in "unknown" (D-088); the cache is not keyed by tag (D-084). |
| K-024 | The Rust protocol an Oxide release targets is in the **commit message** of the release's commit, not in the release JSON: `GET …/commits/{tag}`, else `…/releases/tags/{tag}` → `target_commitish` → `…/commits/{commitish}`. Compare with `server.Protocol` (`N.N.N`). | GitHub's release object has no protocol field. | P:1870-2050 | Message extraction is fragile (D-087); up to 6 blocking requests (D-039). |
| K-025 | Oxide commit messages often omit the protocol (typical: "Patch for <Month> Rust update"); a keyword score ≥ 6 is treated as "protocol changed". | The only signal available on forced-wipe patches. | P:3439-3505 | A plain "Rust update" scores 6; month regex has no word boundaries (D-087). |
| K-026 | Pin versions across the hand-off: the app must install exactly the build/tag the plugin decided on, and refuse if upstream moved. | The plugin's compatibility decision and its `RemoteSteamBuildID` bookkeeping are only valid for those exact versions. | A:402-462 | Checked after the backup, causing a full restore (D-037); a failed query looks like a mismatch (D-038); Oxide is re-fetched unchecked (D-050). |
| K-027 | Download to a temp file, reject zero length, extract to a temp directory, then copy. | Truncated downloads happen. | A:2365-2420 | No hash/size check; no containment check of its own (D-029, D-114). |
| K-028 | Local values (protocol, Oxide version) need reading only once per load. | A new Oxide or Rust build always requires a restart. | P:853-875 | — |

## 3. uMod plugin updates

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-029 | Search `https://umod.org/plugins/search.json?query=<title>&page=1&sort=title&sortdir=asc` by the `[Info]` **title**, then accept only results whose `download_url` file name equals the local file name or the plugin class name. | uMod searches on the human title ("Admin Radar") while files are named `AdminRadar.cs`. | A:2558-2596 | Author is never compared (D-027); only page 1 (D-054). |
| K-030 | Use the class name as a second key. | Covers locally renamed files; Oxide requires file name = class name. | A:2832-2843 | First `class X` in 400 lines may be a helper (D-115). |
| K-031 | Resolve ambiguity conservatively: several candidates → keep those not older than local; if not exactly one, do nothing. | A wrong match overwrites a working plugin. | A:2598-2625 | — |
| K-032 | Fields relied on: `data[]`, `data[].download_url`, `data[].latest_release_version`. | Nothing else in the response is used. | A:2573-2660 | — |
| K-033 | Rate-limit uMod: 30 requests per rolling minute, counting searches **and** downloads. | uMod throttles; 30/min is the empirically safe value (unverified). | A:2791-2803 | No reaction to HTTP 429 / `Retry-After` (D-055). |
| K-034 | Keep the local file name when replacing a plugin. | A case difference or rename on uMod's side must not create a duplicate plugin file. | A:2701 | — |
| K-035 | Download to a temp file with a non-`.cs` extension inside `oxide/plugins`. | Same volume so the final step is a rename; Oxide ignores `.temp` files. | A:2673-2674 | Delete-then-move is not atomic (D-056); leftovers (D-116); content is not validated (D-028). |
| K-036 | Exclude FMU's own plugin from the uMod pass. | It is updated by the self-update, not via uMod. | A:2529 | `EndsWith`; the GUI plugin is not excluded (D-115). |
| K-037 | Plugin update failures never fail the run and never trigger a restore. | A broken uMod must not block a server/Oxide update. | A:487-499 | The replaced plugins' only copy is deleted with `temp_backup`. |
| K-038 | Plugin metadata = first `[Info("Title","Author","Version")]` in the first 200 lines. | Standard Oxide attribute; also used to read FMU's own version. | A:2805-2829 | Too strict for combined attributes and `ResourceId` (D-053). |
| K-039 | Version strings are messy: tolerate a leading `v` and suffixes, compare numerically, "parseable beats unparseable", fall back to ordinal. | uMod and GitHub versions are free text. | A:2853-2896 | Infinite loop on single-number versions (D-011); `1.0 < 1.0.0` (D-057). |
| K-040 | Update plugins while the server is down, after server and Oxide, and report the replaced file names (`updated_plugins`). | Oxide compiles plugins at boot; admins want to know what changed. | A:487-499, 527-547 | — |

## 4. Process and service control on Linux

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-041 | systemd unit must have `KillMode=process`, `SendSIGKILL=no`, `KillSignal=SIGINT`. | The updater is a child of RustDedicated and lives in the service's cgroup; the default `control-group` mode kills it when the service stops. | README §12 (line 387) | Not checked by code; v2 should detect or avoid the dependency. |
| K-042 | Unit must have `NoNewPrivileges=false`. | Otherwise `sudo` cannot elevate from inside the unit to start the service. | README §12 | — |
| K-043 | Unit must have `Restart=on-failure` with `SuccessExitStatus=0 9 SIGKILL`. | A clean `quit` must not trigger an automatic restart while the updater is working. | README §12 | With this setting nothing restarts the server if the updater fails (D-003). |
| K-044 | Start the service with exactly `sudo -n <absolute systemctl> start <service>` (plain `<systemctl> start` when root). | sudoers `NOPASSWD` rules match the literal command; README builds the rule from `command -v systemctl` and exactly `start <service>`. Any extra flag or `restart` breaks existing installs. | A:3142-3191, 2745-2788; README lines 395-406 | Resolved path may differ from the rule's (D-117); name unquoted (D-043); result ignored (D-015). |
| K-045 | Always `sudo -n`. | The updater has no TTY; a password prompt would hang forever. | A:3185 | — |
| K-046 | `start`, not `restart`. | The server has already quit by itself; only `start` is whitelisted. | A:3078-3192 | — |
| K-047 | Detect root from `/proc/self/status` (`Uid:` line, effective uid), falling back to `USER`. | Avoids P/Invoke; the fallback covers containers without `/proc`. | A:3142-3168 | — |
| K-048 | Resolve `systemctl` via `which`, then PATH, with a default PATH list when PATH is empty. | Service environments often have a minimal or empty PATH. | A:2745-2788 | — |
| K-049 | Service state: `systemctl is-active --quiet <name>` needs no sudo. | Read-only query. | A:1329-1344 | Non-zero for `deactivating`; errors read as "stopped" (D-007). |
| K-050 | Service mode only works when the service runs RustDedicated directly. | The app waits on the service state, not on the process. | README; A:980-987 | — |
| K-051 | The process to wait for is named `RustDedicated` (no extension) on both OSes. | `GetProcessesByName` takes the name without `.exe`. | A:975, 1361-1372 | Machine-wide, no timeout (D-006). |
| K-052 | `chmod +x` the start script before launching it; `chmod +x` the updater after promotion. | Files copied from Windows, zips or backups lose the execute bit. | A:1460-1473; P:2780-2823, 2985-2991 | — |
| K-053 | Inside the game's Mono sandbox there is no POSIX permission API: shell out to `/bin/sh -c` for `test -x`, `readlink -f`, `chmod`. | Needed to validate the updater binary. | P:2780-2823, 3797-3849 | Path sits inside double quotes in the script (D-043). |
| K-054 | Validate the updater binary at plugin load (exists, not a directory, non-empty, executable, symlink resolved). | A broken binary should be reported at boot, not when the server is about to quit. | P:3634-3697 | Result never refreshed and the server quits anyway (D-079, D-001). |
| K-055 | Accept either `RustDedicated` or `RustDedicated.exe` on any OS when checking the server directory. | Avoids an OS switch (possibly Wine/Proton setups). | A:808-816 | Intent unconfirmed. |

## 5. Process and service control on Windows

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-056 | `sc query "<name>"`: parse `STATE : <n>`; 4 RUNNING, 2 START_PENDING and 3 STOP_PENDING all mean "still running". | RustDedicated keeps files locked and is still saving while stopping. | A:1303-1328 | Fallback on the word `RUNNING` treats pending as stopped; service-not-found reads as stopped (D-007). |
| K-057 | NSSM: `AppExit Default Restart`, `AppExit 0 Exit`, `AppExit 4294967295 Exit`, `AppExit 3221225786 Exit`, `AppStopMethodSkip 6`, `AppStopMethodConsole 60000`. | The service must go to STOPPED when RustDedicated quits for an update, or the wait never ends; those are the exit codes Rust produces. | README §12 (lines 349-352) | — |
| K-058 | Disable Windows service recovery: `sc failureflag <svc> 0` and delete `FailureActions`. | The SCM must not restart the server behind the updater's back. | README §12 (line 359) | — |
| K-059 | Start with `sc start "<name>"` (15 s); exit 0 means "request accepted". | The updater inherits the token of the server process that launched it; no elevation handling exists. | A:3129-3134 | "Already running" is non-zero; result ignored (D-015). |
| K-060 | `cmd.exe /c start "Title" "<path>"`: the dummy title is required. | `start` treats the first quoted argument as the window title; without it the path becomes the title and an empty console opens. | A:1403-1432; P:2204-2207 | — |
| K-061 | `.ps1` scripts: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"`. | Default policy blocks unsigned scripts; `-NoProfile` avoids profile side effects and delay. | A:1403, 1427 | — |
| K-062 | A running executable cannot be deleted or overwritten. | Drives the two-stage self-update, the backup exclusion and the restore exemption for the updater binary. | A:1636-1650, 1741; P:2826-3012 | — |
| K-063 | Clear file attributes (`SetAttributes(Normal)`) before deleting. | Read-only files cannot be deleted on Windows. | A:1745 | Not done in `CopyDirectoryOverwrite`. |
| K-064 | Command line limits: 32767 characters for `CreateProcess` (v1 budget 32500), 8191 through `cmd.exe`. | Server descriptions can be very long. | P:3735-3756 | Guard ignores the `cmd.exe` limit (D-095). |
| K-065 | A quoted value must not end in a backslash. | `\"` is an escaped quote for the Windows argv parser. | P:3769; A:2199 | v1 emits it (D-025). |

## 6. Detaching processes and terminals

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-066 | The updater is launched **before** `quit` (the plugin dies with the server) and must outlive the server; the app then waits for the server to stop. | Only the plugin knows when to update; only an external process can replace the server's files. | P:2121-2292, 3401-3420; A:968-999 | Launch success is never confirmed before `quit` (D-001). |
| K-067 | The restarted server must outlive the updater. | The updater exits right after launching the start script. | A:1375-1610 | Start is never verified (D-015). Consider `setsid`. |
| K-068 | Linux hidden mode: `/bin/bash -c "nohup '<path>' >/dev/null 2>&1 &"`. | Double-fork effect plus SIGHUP immunity; works on every box. | A:1530-1546; P:2271-2282 | Output discarded (D-113); bash's exit says nothing about the child. |
| K-069 | tmux: `kill-session` first, then `new-session -d -s <name> "bash -lc 'exec <script>'"`, then verify with `has-session`. | A leftover session of the same name makes `new-session` fail; `new-session -d` succeeds even if the command dies at once. | A:1574-1609, 2963-2996 | 300 ms race (D-042); prefix matching, use `-t =name` (D-044). |
| K-070 | gnome-terminal: `gnome-terminal -- bash -lc "exec '<script>'"`; detect either `gnome-terminal` or `gnome-terminal-server`. | The window is owned by gnome-terminal-server, so it survives the updater. | A:1548-1572, 2999-3033; P:2230-2241 | Needs a display; presence ≠ usability (D-016). |
| K-071 | Use a login shell (`bash -lc`) for visible launches. | The user's profile provides PATH, `LD_LIBRARY_PATH`, `DOTNET_ROOT` (framework-dependent build), as when starting by hand. | A:1555, 1592; P:2235-2261 | The nohup variant uses plain `-c` (D-045). |
| K-072 | Use `exec` inside the terminal/session. | The script replaces bash, so the session's lifetime equals the server's and signals reach it directly. | A:1555, 1592 | Session vanishes with crash output (D-113). |
| K-073 | Every "show console" method can be absent; always end in nohup. Order in code: named tmux session → gnome-terminal → tmux `rust-server` → nohup. | Headless hosts. | A:1480-1523 | README states a different order (D-125). |
| K-074 | Detect tools with `sh -c "command -v <name> >/dev/null 2>&1 && printf 1 \|\| printf 0"`, Linux only. | Uses the PATH of the server process. | A:2999-3033; P:3328-3362 | Duplicated in both programs. |
| K-075 | Updater console on Linux uses the fixed tmux session `feedmeupdate`; if it exists the command is typed in with `send-keys … C-m`. Windows console title is `FeedMeUpdates`. | Lets an operator keep one window for watching updates (`tmux attach -t feedmeupdate`). | P:2244-2261 | Types into whatever runs there (D-044). |
| K-076 | Service mode forces the hidden launch. | A service has no console to show. | A:248-249 | — |
| K-077 | Windows hidden mode starts the exe directly with `CreateNoWindow`; visible mode uses `cmd /c start`. | `start` creates an independent console. | P:2200-2222; A:1391-1458 | — |

## 7. File locks and backup/restore

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-078 | Never touch server files until the server has fully stopped. | Saves are still being written and binaries are locked during shutdown. | A:968-999 | No settle delay, fail-open probes (D-007). |
| K-079 | Exclude the log file from the backup. | It is held open for writing; copying it on Windows hits a sharing violation and one failed file aborts the whole backup. | A:1636-1650 | — |
| K-080 | Exclude the updater binary from the backup and never delete it on restore. | A running exe cannot be deleted or overwritten on Windows; one failed file turns the restore into "restore failed". | A:1636-1650, 1741 | Only matches the default name in the server root (D-111). |
| K-081 | On restore, delete the target before copying. | Read-only/hidden targets cannot be overwritten; on Linux unlinking avoids "text file busy". | A:1741-1755 | — |
| K-082 | Take a backup before modifying anything and restore it when an update step fails; record `backup_cycle`/`server_restored` in the marker. | A half-updated server does not boot. | A:393-400, 877-910, 1693-1820 | Full tree (D-040), overlay (D-013), deleted after a failed restore (D-012), stale reuse (D-041). |
| K-083 | What needs protecting is what cannot be re-downloaded: `server/<identity>/` (map, saves, player DBs), `oxide/` (plugins, config, data). | Game binaries can be re-fetched with `validate`. | A:1617-1690 (copies everything) | v2 should narrow the backup. |
| K-084 | After deleting a directory tree, check that it is really gone. | Recursive delete partially fails on locked or read-only files, notably on Windows. | A:3036-3071 | Only warns. |
| K-085 | Tell the admin to plan disk space for the backup. | It is a full copy. | README lines 118, 505 | No free-space check in code. |
| K-086 | Write the result marker **before** starting the server. | The plugin reads it at boot. | A:527-573 | A marker write failure still starts the server (D-107). |

## 8. server.cfg editing

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-087 | Write `server.cfg` as UTF-8 **without BOM**. | A BOM becomes part of the first command name and breaks that line. | A:3872-3879 | Non-UTF-8 input is corrupted (D-066). |
| K-088 | Format: one console command per line, `key<whitespace>value`; `#` and `;` start comments; preserve each line's original line ending. | It is an ordinary Rust console script. | A:3701-3856 | Inline comments and indentation are lost (D-120). |
| K-089 | Quote a value in double quotes when it contains whitespace or is empty; there is no escape syntax, so `"` must be removed. | Rust's console parser has no escaping. | A:3723-3733, 3882-3888 | Description also loses `'`; bare hostname keeps `"` (D-120). |
| K-090 | Replace CR, LF and TAB in values with a space. | A value must stay on one line. | A:3859-3869 | Also collapses double spaces. |
| K-091 | `server.levelurl` and `server.level`/`server.seed`/`server.worldsize` are mutually exclusive: with a map URL remove the three, otherwise remove the URL. | Contradictory settings give the wrong map. | A:3500-3640 | URL is removed even when nothing replaces it (D-059). |
| K-092 | An empty NextWipe value means "leave the existing key untouched". | Admins may manage seed/size elsewhere. | A:3588-3640 | — |
| K-093 | Remove the persisted wipe-timer override from `server.cfg` during the wipe. | The plugin persists it with `server.writecfg` for custom wipes; the stale value must not survive. | A:3442-3456; P:3933-3935 | Key name probably does not match (D-065). |
| K-094 | Rust executes the file top to bottom (last duplicate wins) and command names are case-insensitive. | An editor must update all occurrences and match case-insensitively. | — | v1 updates the first occurrence only, case-sensitively (D-063, D-064). |
| K-095 | Replace the file via a temp file in the same directory. | Never leave a half-written cfg. | A:3872-3879 | Delete-then-move, up to 7 rewrites (D-062). |
| K-096 | Keys v1 edits: `server.hostname`, `server.description`, `server.level`, `server.seed`, `server.worldsize`, `server.levelurl`, `wipeUnixTimestampOverride`. | — | A:3442-3642 | Start scripts often pass `+server.*` on the command line, which may shadow cfg edits (open question). |
| K-097 | Persisting a convar from the plugin: run `server.writecfg` then `server.readcfg`. | Used for the custom-wipe timer override. | P:3933-3935 | Need for `readcfg` unconfirmed. |

## 9. Wipe file anatomy

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-098 | Save data lives in `<ServerDir>/server/<identity>/`; the default identity is `my_server_identity`. | RustDedicated's default when `+server.identity` is not passed. | A:600, 709, 3316 | Identity not validated (D-118). |
| K-099 | Map wipe = top-level files matching `.map`, `.sav`, `.dat`. | Names embed save/protocol numbers and have companions (`*.sav.1`, `*.db-wal`, `*.db-journal`), so match by pattern, not exact name. | A:3425-3441 | Matched on the **full path** (D-014); `.dat` is very broad. |
| K-100 | Blueprints = files matching `.blueprints`; kept by default. | Blueprint wipes are optional and rare. | A:3365-3384; P:65 | App default is "delete" when the flag is missing (D-068). |
| K-101 | Rust+ pairing tokens = files matching `.tokens`; separate from player data, kept by default. | Players should not have to re-pair the companion app after every wipe. | A:3385-3404 | Flag never reset after a wipe (D-086). |
| K-102 | Player data = `.deaths`, `.identities`, `.states`, `relationship`, `.files`. | `.files` is grouped with player data, so a map-only wipe keeps it. | A:3405-3424 | Grouping intent unconfirmed. |
| K-103 | Plugin datafiles to delete are exact file names in `oxide/data`. | Many plugins need their data reset at wipe. | A:3334-3364 | Case-sensitive, no sub-folders (D-119). |
| K-104 | Order: plugin data, blueprints, tokens, player data, map, then `server.cfg`. | — | A:3334-3642 | No per-file error handling (D-060). |
| K-105 | Report soft problems as alerts, not failures: `wipe_info` = `[alert one][alert two]`. | A missing optional file should not fail the wipe. | A:3308-3646; P:680-689 | A `]` in an alert breaks the format (D-067). |
| K-106 | `wiped: "yes"` in a success marker makes the plugin clear `CustomWipeDay/Time` and the NextWipe settings. | One-shot wipe settings must not apply twice. | P:1141-1203 | "yes" even when nothing was deleted (D-061). |
| K-107 | Validate wipe parameters before launch: map size integer 1000–6000, seed decimal 0–4294967295. | The app writes whatever it receives. | P:804-847, 3113-3133 | Bypassed by `feedme.set` (D-079). |
| K-108 | A custom wipe runs with no update and no backup. | Speed; nothing but saves is touched. | A:325-391 | At least `server.cfg` should be copied first. |

## 10. Lock and marker protocol

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-109 | `updating.lock` is a three-party protocol, not just a mutex: the wrapper script loops `run server; sleep 5; exit if updating.lock exists`. | Admins run the server in a restart loop; the lock is the signal for the loop to step aside. | S:startserver.sh, startserver.bat; A:297-322 | Created too late (D-005); sample script has a syntax error (D-099). |
| K-110 | The app creates the lock and never deletes it; the plugin deletes it at boot, **after** processing the marker. | The lock must persist until the new wrapper is running. | A:322; P:788-789, 1221-1232 | The plugin logs "may have been interrupted" on every normal run (D-106). |
| K-111 | An app start that finds the lock must not update. | Prevents two updaters on one server. | A:312-319 | It also deletes the other run's backup and starts the server (D-034). |
| K-112 | `updateresult.json` is the only result channel from app to plugin; the app's exit code is always 0 and never read. | The plugin is not running while the app works. | A:351-361, 527-547, 1767-1837, 3195-3253 | Six shapes, mixed types (D-107); regex reader (D-067); crash on missing key (D-019). |
| K-113 | On boot the plugin: reads the marker, notifies Discord, promotes `RemoteSteamBuildID` on success, resets wipe settings on `wiped=yes`, then archives the marker as `markers/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json`. | Gives a history of runs and keeps the marker from being processed twice. | P:1103-1181 | Archive name unsanitised, folder never pruned (D-108). |
| K-114 | `steambid.temp` is a one-shot hand-over of the installed build id from app to plugin (Init flow); the plugin stores it and deletes the file. | The app does not know about Oxide's datafile. | A:476-478; P:983-1019 | May contain `no` (D-036). |
| K-115 | The plugin stores the target build as `RemoteSteamBuildID` at detection time and promotes it only on a success marker. | The belief must only change when the update really happened. | P:1654-1657, 1141-1166 | Promoted by any success marker (D-082). |
| K-116 | `update_id` selects the flow: `init`, `testrun`, `wipe`, or 8 decimal digits (scheduled). Anything else: the app does nothing. | Double-clicking the binary must be harmless. | A:251-270; P:2132 | — |
| K-117 | Deleting `oxide/data/FeedMeUpdatesData.json` is the de-facto reset: the next boot runs the init flow. | Self-healing of a wrong build-id belief. | P:3515-3521 | Also triggered by a corrupt datafile (D-126). |
| K-118 | Marker and lock file names are fixed (`updateresult.json`, `updating.lock`). | The app hard-codes them. | A:297, 359, 1769, 3199; P:70-71 | Configurable in the plugin, which breaks the handshake (D-125). |
| K-119 | A failure counter (`TryNumber`, `MaxAttempts`) should stop repeated failing updates. | A failing update otherwise loops with a full backup/restore each time. | P:934-980, 850-851 | Does not work as intended (D-020, D-081). |

## 11. Self-update

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-120 | Two-stage update of the app: the app stages `FeedMeUpdates(.exe).new` + `FMU_CHECKSUMS.txt` in the server dir; the plugin promotes it at next boot. | A running executable cannot overwrite itself. | A:1006-1170; P:2826-3012 | Promotion can lose the binary (D-024); skew (D-030). |
| K-121 | The plugin `.cs` is replaced while the server is down so Oxide compiles the new one at boot. | Oxide loads plugins from source at start. | A:1105-1116 | No verification, non-atomic (D-032); happens before the lock and the backup (D-005, D-017). |
| K-122 | The system has one version: the plugin's `[Info(…, "x.y.z")]`; the app has none of its own. | "Update needed" = release tag > plugin version. | A:1020-1039; P:18 | v2 needs per-component versions. |
| K-123 | FMU release = `GET https://api.github.com/repos/frankie290651/FeedMeUpdates/releases/latest`; four zip assets told apart only by the substrings `win` and `standalone`. | Windows/Linux × self-contained/framework-dependent. | A:1187-1289 | Exact names never checked (D-109); framework-dependent zips only partly staged (D-031). |
| K-124 | `FMU_CHECKSUMS.txt` = one bare SHA-256 (64 hex, or base64 of 32 bytes; spaces, `-`, `:` tolerated); compare in constant time; on any failure delete both staged files. | Detects corrupted downloads. | P:2880-3091 | Integrity only (D-017); over-strict format (D-096). |
| K-125 | Block updater launches while the binary is being promoted (`SystemUpdating`). | Launching a half-moved binary fails. | P:2826-2877, 2063, 3524 | Flag set late (D-092). |
| K-126 | The release zip may carry `FeedMeUpdatesGUI.cs`; the GUI plugin reads config through `GetGuiConfigSnapshot()` and writes through `feedme.set`. | Companion GUI plugin (not in this repo). | A:1112-1116; P:2678-2741 | Snapshot exposes the webhook URL (D-075). |

## 12. HTTP inside the game's Mono runtime

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-127 | Never block the main thread waiting for Oxide `webrequest` or `timer` callbacks. | They are delivered on the server main thread, so the wait can only time out (external). | P:196-224, 258-273 | v1 does exactly this in StartupScan and testrun (D-069). |
| K-128 | Keep two HTTP stacks, or one proven one: Oxide `webrequest` and `System.Net.WebClient` each fail on some hosts. | WebClient under Unity/Mono can fail on TLS/certificates; webrequest is callback-bound. `feedme.status` prints `backend=` for diagnosis. | P:157-381 | Detection chooses nothing (D-070); global flag races (D-072). |
| K-129 | GitHub's API rejects requests without a `User-Agent`. | 403 otherwise. | P:145 (`FeedMeUpdates`); A:2049 (`FeedMeUpdates/1.0`) | The webrequest path sends no custom headers (D-071). |
| K-130 | GitHub allows 60 anonymous requests/hour per IP, and hosting IPs are shared. Budget calls; log 403 as a possible rate limit. | Plugin and app draw on the same allowance. | P:1781 | Not budgeted (D-039). |
| K-131 | Use a short, configurable timeout (`HttpTimeoutMs`, default 3000 ms). | Some calls run where a long wait freezes the game. | P:199, 751 | Requests are abandoned, not cancelled (D-077). |
| K-132 | `timer.Once(0f, …)` is the way to marshal back to the main thread before touching Oxide APIs or plugin state. | Oxide/Unity APIs are main-thread only. | P:176, 265, 607, 1648 | Applied inconsistently (D-124). |
| K-133 | Avoid a JSON library dependency in the plugin: v1 uses regexes and a hand-written array parser. | Newtonsoft types/compile issues in the Oxide sandbox (presumed). | P:1206-1218, 3136-3229, 4137-4163 | Regex extraction does not unescape JSON (D-067). |
| K-134 | Run detection off the main thread (`Task.Run`) with a re-entrancy guard. | SteamCMD takes tens of seconds. | P:1609-1678 | Guard sticks forever if SteamCMD hangs (D-010). |

## 13. Discord

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-135 | Webhook call: `POST <url>` with `Content-Type: application/json`, body `{"content":"…"}`; success is `204 No Content`. | Discord's webhook contract. | P:588-624, 337-379 | Reported as 200 (D-123). |
| K-136 | The start message is sent by the plugin just before `quit`; the result message is sent by the plugin at the **next boot** from the marker. The app has no Discord code. | The plugin is not running during the update. | P:627-650, 653-731, 1133 | Start message is premature and lossy (D-073); result message crashes on restore markers (D-019). |
| K-137 | `content` is limited to 2000 characters and must be valid JSON (escape control characters); restrict mentions. | Discord answers 400 otherwise. | not in v1 | D-076, D-132. |
| K-138 | The webhook URL is a secret. | Anyone with it can post. | P:52 | Logged and echoed (D-075). |
| K-139 | Message texts (appendix A.8) are user-visible and may be recognised by admins. | — | P:627-731 | Stability as a contract is an open question. |

## 14. Time and scheduling

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-140 | Official force wipe = first Thursday of the month, 19:00 UTC: day = `1 + ((4 − dayOfWeek(1st) + 7) mod 7)`; if already past, use next month. | Facepunch's monthly schedule. | P:3944-3989 | No DST handling; may be 18:00 UTC in summer (D-090). |
| K-141 | Do not wipe at a fixed time: inside the window (`BeforeForceWipeRange`, default 15 min before) arm a flag, and wipe when the forced build is actually detected and installed (`-isforce 1`). | Wiping before the new build is installed is pointless. | P:3992-4057, 3721; A:501-525 | Flag is sticky and catches unrelated updates (D-023). |
| K-142 | Set the in-game wipe timer (`wipeUnixTimestampOverride <unix seconds>`) at every boot to the next force wipe, or to the custom wipe time. | Players see the next wipe in game. | P:3862-3941 | Convar name (D-065). |
| K-143 | Custom wipe date/time: day-first (`dd/MM/yyyy`, `-` accepted), `H:mm`, in the server's **local** time zone. | Author's locale; documented that way. | P:3892-3906, 4060-4093 | Two parsers disagree (D-097); past dates wipe at once (D-021); DST gap (D-098). |
| K-144 | During the custom-wipe countdown, stop update detection and the daily-restart guard. | A detected update must not start a competing updater. | P:4038-4053 | Other triggers are not guarded (D-089). |
| K-145 | Daily-restart guard: stop checking `MinutesBeforeRestart` minutes before `DailyRestartTime` (local `HH:mm`). | An external scheduler restarts the server daily; an update overlapping it would be killed mid-way. | P:892-905, 3232-3289 | Exact-equality test (D-094). |
| K-146 | Countdown: announce at start, then every minute; minimum 1 minute; different text for force wipe. | Players need warning. | P:2056-2118 | Decision is not frozen and cannot be cancelled (D-083). |
| K-147 | Startup scan and the init flow launch without a countdown. | Nobody is connected right after boot. | P:3513-3602 | Wrong on a plugin hot-reload (D-131). |
| K-148 | `quit` is issued 0.5 s after launching the updater; Rust's `quit` saves by itself. | No explicit `server.save` is needed. | P:3401-3420 | — |
| K-149 | Detection interval ≥ 1 minute (default 10), first detection 2 s after start; with the default interval the GitHub budget holds. | Rate limit. | P:1596-1606 | — |
| K-150 | Recompute remaining time from the clock on each tick. | Timers drift and hitch. | — | v1 decrements counters (D-091). |

## 15. CLI and argument encoding

| ID | Rule | Why | Where in v1 | v1 flag |
|---|---|---|---|---|
| K-151 | Whitelist every argument value: `^[A-Za-z0-9 _\.\-\/:\@\+\=,\\]*$`. | One rule that is safe for cmd.exe, CreateProcess and POSIX shells; the command line passes through up to three quoting layers. | P:3788-3794 | Rejects common paths such as `C:\Program Files (x86)\…` (D-001); the exe path is not checked (D-043). |
| K-152 | Free text (server name, description, map URL) travels as `b64:` + standard base64 of UTF-8; empty is `b64:`. | Spaces, quotes, `&`, `?`, `%`, non-ASCII. | P:3612-3631; A:274-286 | Malformed base64 crashes the app (D-033). |
| K-153 | The plugin-datafile list travels as unpadded base64url of a compact JSON string array (no prefix); the app also accepts raw JSON. | Brackets and quotes are not in the whitelist. | P:4096-4163; A:3649-3698 | Two different base64 schemes. |
| K-154 | Quoting: Windows `-key "value"`, Linux `-key 'value'` with `'` → `'\''`. | Works with the whitelist. | P:3769-3775, 3852-3855 | Relies on Mono's argument splitting (unverified); trailing backslash (D-025). Use an argument-list API. |
| K-155 | `-update_id` must be the first argument; keys are case-insensitive; `-key value` and `-key=value` both work; a value starting with `-` needs the `=` form; last occurrence wins. | The app reads the first flag positionally. | A:148-271, 913-965 | D-100. |
| K-156 | `no` means "do not update this component"; empty means "unknown". | Sentinel for `-remote_build`/`-remote_oxide`. | P:2184-2185; A:262-266 | A failed query also yields `no` (D-038). |
| K-157 | When the command line is too long, drop the server description first. | The wipe matters more than the description. | P:3735-3756 | D-095. |
| K-158 | All 25 arguments are always sent, wipe-related ones included. | Simplicity; the app decides by flow. | P:3706-3733 | `-servicetype` is never read (D-101). |
| K-159 | `-showserverconsole` is the inverse of `RunServerScriptHidden`. | Config speaks of hiding, the app of showing. | P:3719; A:248 | — |

---

# Appendix A — complete external contracts of v1

## A.1 Plugin config (`oxide/config/FeedMeUpdates.json`)

Key names are the JSON property names (case-sensitive). No versioning or migration.

| Key | Type | Default | Notes |
|---|---|---|---|
| ServerDirectory | string | `""` | Root with RustDedicated; base for marker, lock, `steambid.temp`, `markers/`, staged binary |
| SteamCmdPath | string | `""` | Full path to steamcmd |
| UpdaterExecutablePath | string | `""` | May be relative to ServerDirectory |
| ShowUpdaterConsole | bool | `false` | |
| ServerStartScript | string | `""` | If empty and not a service, Init writes `C:\rust-server\start_server.bat` (Windows) or `/home/rust/rust-server/start_server.sh` and saves |
| RunServerScriptHidden | bool | `false` | Sent inverted as `-showserverconsole` |
| ServerTmuxSession | string | `""` | |
| RustOnService | bool | `false` | |
| ServiceName | string | `""` | |
| ServiceType | string | `""` | Passed to the app, never used there |
| HttpTimeoutMs | int | `3000` | `<= 0` → 3000 |
| StartupScan | bool | `false` | |
| MaxAttempts | int | `0` | 0 = unlimited |
| CheckIntervalMinutes | int | `10` | Clamped to ≥ 1 |
| CountdownMinutes | int | `5` | Clamped to ≥ 1 for update countdown only |
| UpdatePlugins | bool | `false` | |
| OnlyServerProtocolUpdate | bool | `false` | |
| UseScheme | bool | `false` | |
| SchemeFile | string | `""` | Used verbatim |
| DailyRestartTime | string | `""` | `HH:mm` or `H:mm`, local |
| MinutesBeforeRestart | int | `60` | Guard active only if > 0 |
| DiscordNotificationsEnabled | bool | `false` | |
| DiscordWebhookUrl | string | `""` | Secret |
| BeforeForceWipeRange | int | `15` | `<= 0` → 15 |
| CustomWipeDay | string | `""` | Day-first date, local |
| CustomWipeTime | string | `""` | `H:mm`, local |
| ServerIdentity | string | `""` | App defaults empty to `my_server_identity` |
| NextWipeServerName | string | `""` | |
| NextWipeServerDescription | string | `""` | |
| NextWipeMapUrl | string | `""` | |
| NextWipeLevel | string | `""` | |
| NextWipeSeed | string | `""` | Decimal uint |
| NextWipeRandomSeed | bool | `false` | |
| NextWipeMapsize | string | `""` | Integer 1000–6000 |
| NextWipeKeepBps | bool | `true` | |
| NextWipeResetRustPlus | bool | `false` | Not reset after a wipe |
| NextWipeDeletePlayerData | bool | `false` | |
| NextWipeDeletePluginDatafiles | string | `""` | JSON array of file names, as a string |
| UpdaterMarkerFileName | string | `"updateresult.json"` | Must stay default (hard-coded in app) |
| UpdaterLockFileName | string | `"updating.lock"` | Must stay default (hard-coded in app) |
| MarkersSubfolder | string | `"markers"` | |

The shipped example configs (`Config/Linux`, `Config/Windows`) differ from these defaults: `ShowUpdaterConsole=true`, `UpdatePlugins=true`, `MinutesBeforeRestart=30`, `BeforeForceWipeRange=30`, `ServerIdentity="my_server_identity"`, `NextWipeLevel="Procedural Map"`, `NextWipeRandomSeed=true`, `NextWipeKeepBps=false`, `NextWipeDeletePlayerData=true`.

## A.2 Plugin datafile (`oxide/data/FeedMeUpdatesData.json`)

Flat `Dictionary<string,string>`.

| Key | Meaning |
|---|---|
| `TryNumber` | Failure counter as a string; written after each launch; `0` on first run |
| `LocalSteamBuildID` | Steam build id the plugin believes is installed; empty ⇒ init flow at next boot |
| `RemoteSteamBuildID` | Build id an in-flight server update targets; promoted to `LocalSteamBuildID` on a success marker, then removed |

## A.3 Files and directories (relative to the server directory unless noted)

| Path | Created by | Consumed / removed by | Role |
|---|---|---|---|
| `updater.log` | app | — | Log; truncated when > 5 MiB at start; line `[dd/MM/yy HH:mm:ss] [LEVEL] message`, local time |
| `updating.lock` | app (after server stop) | wrapper script reads; plugin deletes at boot | Lock and wrapper signal; empty file |
| `updateresult.json` | app | plugin reads at boot and moves | Result marker (A.5) |
| `markers/marker_<update_id>_<dd_MM_yy_HH-mm-ss>.json` | plugin | never pruned | Marker archive |
| `steambid.temp` | app (Init flow) | plugin reads and deletes | Build id hand-over, bare text |
| `temp_backup/` | app | app deletes | Full-tree backup |
| `FeedMeUpdates.exe.new` (Windows) / `FeedMeUpdates.new` (other) | app | plugin promotes or deletes | Staged app binary |
| `FMU_CHECKSUMS.txt` | app | plugin deletes | Single SHA-256 of the staged binary |
| `oxide/plugins/FeedMeUpdates.cs`, `oxide/plugins/FeedMeUpdatesGUI.cs` | app (self-update) | Oxide | Plugin sources replaced in place |
| `oxide/plugins/<guid>.temp` | app | app | uMod download temp |
| `oxide/plugins/*.cs` | app (uMod pass) | Oxide | Replaced plugins |
| `oxide/config/FeedMeUpdates.json` | plugin | plugin | Config (A.1) |
| `oxide/data/FeedMeUpdatesData.json` | plugin | plugin | Datafile (A.2) |
| `oxide/data/<file>` | — | app deletes at wipe | Plugin datafiles listed for deletion |
| `steamapps/appmanifest_258550.acf` → `.acf.bak` | app | — | Renamed on SteamCMD `state is 0x6` |
| `RustDedicated_Data/Managed/` | app | — | Oxide assemblies destination |
| `server/<identity>/` | — | app deletes at wipe | Save, map and player files |
| `server/<identity>/cfg/server.cfg` (+ transient `server.cfg.tmp_<32 hex>`) | app edits | Rust | Wipe settings |
| `<tmp>/oxide_update_<guid>.zip`, `<tmp>/oxide_extracted_<guid>/`, `<tmp>/FMU_update_<guid>.zip`, `<tmp>/FMU_extracted_<guid>/` | app | app | Temp downloads (system temp dir) |
| scheme file at `SchemeFile` | admin | plugin | Rule file (A.7) |

Backup exclusions: `temp_backup`, `updater.log`, `FeedMeUpdates.exe`, `FeedMeUpdates` (root-level, exact names).

## A.4 App command line

Invocation: `<exe> -update_id <id> -key value …`. `-update_id` must be first. Keys case-insensitive; `-key value` or `-key=value`; last occurrence wins; unknown keys ignored. Booleans: `true`/`false`/`1`/`0`, anything else false. Exit code is always 0. The plugin always sends all 25, in this order.

| # | Argument | Values | App default if absent |
|---|---|---|---|
| 1 | `-update_id` | `init`, `testrun`, `wipe` (case-insensitive) or 8 digits | required |
| 2 | `-what` | `both`, `server`, `oxide`; `none` for wipe | `""` (testrun → `both`) |
| 3 | `-update_plugins` | `yes` / `no` | `""` = validation failure (not validated for wipe) |
| 4 | `-server_dir` | path | current directory |
| 5 | `-steamcmd` | full path | `""` |
| 6 | `-start_script` | path | `""` |
| 7 | `-remote_build` | build id, `no`, or empty | `""` (scheduled → `no`) |
| 8 | `-remote_oxide` | tag without `v`, `no`, or empty | `""` (scheduled → `no`) |
| 9 | `-isservice` | `1` / `0` | false |
| 10 | `-servicetype` | text | `""`; never read |
| 11 | `-servicename` | text | `""` |
| 12 | `-showserverconsole` | `1` / `0` (inverse of `RunServerScriptHidden`) | false; forced false in service mode |
| 13 | `-servertmux` | session name | `""` |
| 14 | `-isforce` | `1` / `0` | false; acted on only in the scheduled flow |
| 15 | `-nextwipelevel` | text | `""` |
| 16 | `-nextwipeurl` | `b64:` + base64 | `""` |
| 17 | `-nextwipeseed` | decimal | `""` |
| 18 | `-nextwipemapsize` | decimal | `""` |
| 19 | `-nextwipekeepbps` | `1` / `0` | false (= delete blueprints) |
| 20 | `-nextwiperesetrustplus` | `1` / `0` | false |
| 21 | `-nextwipedelplayerdata` | `1` / `0` | false |
| 22 | `-nextwipedelpluginsdata` | unpadded base64url of a JSON string array, or raw JSON, or empty | `""` |
| 23 | `-serveridentity` | text | `my_server_identity` |
| 24 | `-servername` | `b64:` + base64 | `""` |
| 25 | `-serverdescription` | `b64:` + base64 | `""` |

Flows: `init` (backup, server + Oxide update, write `steambid.temp`; requires `what=both`, remote args `no`), `testrun` (same without `steambid.temp`; requires `what=both`), 8 digits (scheduled: backup, version guards, selected updates, plugins, optional force wipe), `wipe` (wipe only, no backup). Every flow first waits for the server to stop and runs the self-update.

## A.5 `updateresult.json` shapes

Indented JSON written by the app. `–` = key absent.

| Writer | result | fail_reason | updated_plugins | backup_cycle | server_restored | timestamp | update_id | wiped | wipe_info |
|---|---|---|---|---|---|---|---|---|---|
| Update success (A:529-541) | `"success"` | `null` | array, only if `update_plugins=yes` | `true` | `"no"` (string) | UTC round-trip `o` | `init` / `testrun` / 8 digits | `"no"` / `"yes"` | `""` or alerts |
| Custom wipe success (A:351-357) | `"success"` | `null` | – | – | – | UTC `o` | `"wipe"` | `"yes"` | alerts |
| Early failure, wipe (A:3202) | `"failed"` | string | – | – | – | `yyyy-MM-ddTHH:mm:ssZ` | as passed | `"no"` | `""` |
| Early failure, other (A:3219, 3238) | `"failed"` | string | array, only if flag is `yes` and list non-empty | `false` (bool) | `false` (bool) | `…Z` | as passed | `"no"` | `""` |
| Restored (A:1771) | `"failed"` | `"Failed " + what` | – | `true` | `"yes"` (string) | `…Z` | as passed | – | – |
| Restore failed (A:1829) | `"failed"` | `"Failed restore after failed " + what` | – | `true` | `false` (bool) | `…Z` | as passed | – | – |

`what` ∈ `server update`, `oxide update`, `server update (different version detected)`, `oxide update (different version detected)`. Early-failure reasons: `Blocked by updating.lock file`, `Failed backup creation`, `<Flow> validation failed: …`, or an exception message. `wipe_info` = concatenated `[alert]` items.

Plugin reads (regex, not a JSON parser): `result`, `update_id` (alias `updateId`), `fail_reason` (alias `error`), `wiped` (alias `wipe_result`), `wipe_info` (alias `wipeinfo`), `updated_plugins`. It ignores `backup_cycle`, `server_restored`, `timestamp`.

## A.6 Console commands, permission, hooks

| Item | Behaviour |
|---|---|
| Permission `feedme.run` | Required in game; console, `IsServer` and `IsAdmin` are always allowed |
| `feedme.status` | Prints `ON/OFF`, pending flags, `LocalSteamBuildID`, `backend`, `httpTimeoutMs`, `lastOxideStatus`, `netFail`, `scheme` |
| `feedme.status on` / `enable` | Starts periodic checks (not persisted) |
| `feedme.status off` / `disable` | Stops periodic checks (not persisted; does not stop a countdown or the wipe timer) |
| `feedme.version` | Plugin version, Rust version, build id, protocol, Oxide version |
| `feedme.testrun` | Immediate server + Oxide update (`update_id=testrun`), no countdown, no confirmation |
| `feedme.set <option> <value>` | Sets any of the 41 config keys (lower-case key; aliases `protocolonly`, `randomseed`, `keepbps`, `resetrustplus`, `deleteplayerdata`) and saves the config. Not in the README. Bools accept `true/false/1/0/yes/no/y/n/on/off/enable(d)/disable(d)` |
| Server commands issued by the plugin | `quit`, `wipeUnixTimestampOverride <unix seconds>`, `server.writecfg`, `server.readcfg` |
| Oxide hooks | `Init`, `Unload`, `LoadDefaultConfig`, `OnServerInitialized`, `OnServerSave` (empty) |
| Inter-plugin API | `GetGuiConfigSnapshot()` → dictionary of the 41 config keys (for `FeedMeUpdatesGUI`) |
| Fixed names | tmux sessions `feedmeupdate` (updater) and `rust-server` (server default); window titles `FeedMeUpdates`, `RustServer` |

## A.7 Scheme file

Plain text, one rule per line: `events=action`. Blank lines and lines starting with `//` are skipped. `action` = `server`, `oxide` or `both` (case-insensitive). `events` = tokens joined by `+`: `server`, and/or `oxide` or `oxide(<flags>)` with lowercase flags `s` (same protocol), `c` (changed), `u` (unknown), `e` (error); bare `oxide` = all four. First matching rule wins; one invalid line disables the scheme and falls back to default logic. Read only at plugin load.

## A.8 Network endpoints and user-visible messages

| Endpoint | Used by | Notes |
|---|---|---|
| `GET https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest` | plugin, app | `User-Agent` `FeedMeUpdates` (plugin WebClient) / `FeedMeUpdates/1.0` (app), 20 s in app |
| `GET …/OxideMod/Oxide.Rust/commits/{ref}`, `…/releases/tags/{tag}` | plugin | Protocol lookup |
| `GET https://api.github.com/repos/frankie290651/FeedMeUpdates/releases/latest` | app | Self-update, 20 s; download 5 min |
| `GET https://umod.org/plugins/search.json?query=<title>&page=1&sort=title&sortdir=asc` and `download_url` | app | 30 s each, 30 requests/min |
| `GET https://api.github.com/` | plugin | Backend probe at load |
| `POST <DiscordWebhookUrl>` | plugin | `{"content":"…"}` |

Discord texts: `[FeedMeUpdates] Starting update: <Server & Oxide / Server / Oxide>[ (ForceWipe)]` + `Remote Oxide version: <tag>` + `Protocol NUMBER CHANGE` or `No protocol change`; `[FeedMeUpdates] Starting custom wipe.`; `[FeedMeUpdates] Update finished: <RESULT> (ID: <id>)` / `[FeedMeUpdates] wipe finished: …` followed by `Update executed SUCCESSFULLY.` / `Update FAILED.` (or `Wipe …`), `Updated plugins: a, b`, `Failure reason: …`, `Server wiped.` / `Server not wiped`, `Wipe ALERTS reported:` + ` - <alert>` lines.

Chat broadcasts: `Notice: update detected. Server will restart to apply updates in {n} minute(s).`; `ATTENTION: server will restart for update in {n} minute(s).`; `ATTENTION: server will restart for monthly force wipe in {n} minute(s).`; `ATTENTION: server will restart for wipe in {n} minute(s).`; `ATTENTION: server is wiping.` Admin-only: `Updater launch aborted: invalid updater executable detected at init.`; `Updater executable not configured; cannot start updater.`; `Updater executable not found; check UpdaterExecutablePath.`; `Updater launch aborted: invalid arguments.`; `Error launching updater. Check permissions and executable path.`
