# FeedMeUpdates

> Automatic, configurable update and wipe orchestrator for Rust + Oxide servers. Detects server/Oxide/plugin updates, automates monthly and custom wipes, performs safe backups, restarts cleanly, and can auto-update itself (plugin + app). Sends optional Discord notifications.

![Screenshot of FeedMeUpdates](Imgs/FeedMeWallpaper.png)

[![Latest Release](https://img.shields.io/github/v/release/frankie290651/FeedMeUpdates)](https://github.com/frankie290651/FeedMeUpdates/releases)
[![License](https://img.shields.io/github/license/frankie290651/FeedMeUpdates)](LICENSE)
[![Issues](https://img.shields.io/github/issues/frankie290651/FeedMeUpdates)](https://github.com/frankie290651/FeedMeUpdates/issues)

---

## Table of Contents
1. Overview  
2. What’s New  
3. Features  
4. Architecture  
5. Requirements  
6. Quick Start  
7. Installation  
8. Configuration  
   - Example JSON  
   - Parameter Reference  
9. Update Flow  
10. Wipe Automation (Force + Custom)  
11. Self‑Update (FMU plugin + app)  
12. Service / Script Integration  
13. Discord Notifications  
14. Commands & Permissions  
15. Error Handling, Markers & Logs  
16. Advanced: Scheme Rules  
17. Security & Permissions  
18. FAQ  
19. License

---

## 1. Overview

FeedMeUpdates (FMU) is composed of:
- An Oxide plugin (`FeedMeUpdates.cs`) running inside your Rust server, responsible for detection, countdowns, wipe scheduling and invoking the external updater.
- A companion executable (`FeedMeUpdates.exe` on Windows or `FeedMeUpdates` on Linux) that performs the heavy lifting: backup/restore, Rust/Oxide/plugin updates, wipe operations, marker and log generation, and server restart.

It focuses on safety (backup + automatic restore on failure), automation (updates, wipes, plugin refresh), and clear visibility (in‑game countdowns, Discord webhooks, marker files and logs).

Note: Staging branch is not supported.

---

## 2. What’s New

Major additions and changes in this release:

- Monthly Force Wipe automation
  - Automatically detects the official force wipe window (first Thursday of each month at 19:00 UTC).
  - Safely prepares and triggers a force wipe with a configurable pre‑wipe window.
- Custom Wipe automation (between force wipes)
  - Schedule a one‑off custom wipe at your chosen local date/time.
  - Granular wipe operations (keep BPs, delete player data, map, plugin datafiles, update server.cfg keys).
- Self‑update capability
  - FMU can auto‑update both the Oxide plugin and the updater app by checking the latest GitHub release and staging files safely.
  - Includes robust checksum verification and platform‑aware promotion of the new binary.

Plus many resiliency improvements:
- Safer HTTP requests with fallback backends and timeouts.
- Better process/service control on Windows and Linux (tmux/gnome‑terminal support).
- Clearer marker files and Discord notifications, including wipe details and updated plugin lists.

---

## 3. Features

- Rust server auto‑update via SteamCMD (with retries and heuristics)
- Oxide auto‑update (GitHub releases)
- Optional uMod plugin updates (version checks per plugin)
- Monthly force wipe automation
- Custom wipe scheduling and execution
- Full backup before updates + automatic restore on failure
- Granular wipe controls (BPs, player data, map files, plugin datafiles)
- Server.cfg editing (e.g., hostname, description, level/seed/worldsize/levelurl)
- In‑game countdown announcements
- Discord webhook notifications (start + result, including wipe notes)
- Script‑based or service‑based server restarts
- Cross‑platform (Windows/Linux), tmux and gnome‑terminal support
- Robust network handling with timeouts and backends fallback
- Rule engine (“Scheme”) for conditional update policies
- Self‑update: plugin and app auto‑update

---

## 4. Architecture (High‑Level)

```
[Oxide Plugin]
  ├── Detects changes (server / Oxide / plugins, wipe windows)
  ├── Schedules countdowns and suppresses checks near daily restart
  ├── Invokes Updater (with encoded, safe args)
  └── Reads markers, rotates them, sends Discord notifications

[Updater Executable]
  ├── Waits for server/service to stop
  ├── Self‑update pass (plugin + app) from GitHub release
  ├── Creates backup (temp_backup/)
  ├── Applies Rust and/or Oxide updates
  ├── Updates plugins (optional)
  ├── Executes WipeCycle if requested (force/custom)
  ├── Writes updateresult.json + updater.log
  └── Restarts server (script/service)
```

---

## 5. Requirements

- Rust Dedicated Server folder (with permissions to read/write/exec)
- Oxide installed
- SteamCMD installed and reachable
- Disk space for a full temporary backup of your server folder
- .NET 8 Runtime (only if you use the framework‑dependent app build; standalone builds don’t require it)

---

## 6. Quick Start

```bash
# 1) Stop the server
# 2) Back up the server folder
# 3) Place FeedMeUpdates.cs into oxide/plugins
# 4) Place FeedMeUpdates(.exe) in the server root
# 5) Create/Edit oxide/config/FeedMeUpdates.json with your paths
# 6) Start the server
#    - On first boot, FMU will stop, update, and restart the server cleanly
```

---

## 7. Installation

1) Stop the server cleanly.  
2) Make a manual backup (you can remove it later when you trust FMU’s automatic backup/restore).  
3) Copy `FeedMeUpdates.cs` (plugin) and `FeedMeUpdates(.exe)` (app) into your server directory as described above.  
4) Configure `FeedMeUpdates.json` with absolute paths and options.  
5) Ensure your “start script” or “service” integration is correct (see section 12).  
6) Start the server. The plugin may immediately run a scan and/or initial update depending on config.

---

## 8. Configuration

### 8.1 Example `FeedMeUpdates.json`

```json
{
  "ServerDirectory": "/home/rust/rust-server",
  "SteamCmdPath": "/usr/games/steamcmd",
  "UpdaterExecutablePath": "/home/rust/rust-server/FeedMeUpdates",
  "ShowUpdaterConsole": false,

  "ServerStartScript": "/home/rust/rust-server/start_server.sh",
  "RunServerScriptHidden": true,
  "ServerTmuxSession": "",

  "RustOnService": false,
  "ServiceName": "",
  "ServiceType": "",

  "HttpTimeoutMs": 3000,
  "StartupScan": true,
  "MaxAttempts": 0,
  "CheckIntervalMinutes": 10,
  "CountdownMinutes": 5,
  "UpdatePlugins": false,
  "OnlyServerProtocolUpdate": false,
  "UseScheme": false,
  "SchemeFile": "",

  "DailyRestartTime": "",
  "MinutesBeforeRestart": 60,

  "DiscordNotificationsEnabled": false,
  "DiscordWebhookUrl": "",

  "BeforeForceWipeRange": 15,
  "CustomWipeDay": "",
  "CustomWipeTime": "",
  "ServerIdentity": "",
  "NextWipeServerName": "",
  "NextWipeServerDescription": "",
  "NextWipeMapUrl": "",
  "NextWipeLevel": "",
  "NextWipeSeed": "",
  "NextWipeRandomSeed": false,
  "NextWipeMapsize": "",
  "NextWipeKeepBps": true,
  "NextWipeDeletePlayerData": false,
  "NextWipeDeletePluginDatafiles": "",

  "UpdaterMarkerFileName": "updateresult.json",
  "UpdaterLockFileName": "updating.lock",
  "MarkersSubfolder": "markers"
}
```

### 8.2 Parameter Reference (high‑level)

- Paths
  - ServerDirectory: Root where `RustDedicated(.exe)` resides.
  - SteamCmdPath: Full path to `steamcmd`.
  - UpdaterExecutablePath: Full path to FMU app (executable).

- Startup / UI
  - ServerStartScript: Script to start the server when not using a service.
  - RunServerScriptHidden: Hide script console (Linux falls back to nohup, gnome‑terminal, tmux).
  - ServerTmuxSession: tmux session name when showing console on Linux.
  - RustOnService: If true, FMU controls a service instead of a script.
  - ServiceName / ServiceType: Service identifier and optional type hint.

- Detection / Scheduling
  - HttpTimeoutMs: HTTP timeout for remote checks.
  - StartupScan: Check immediately on plugin start.
  - CheckIntervalMinutes: Poll interval for updates.
  - CountdownMinutes: In‑game countdown before restarting to update.
  - UpdatePlugins: Update uMod plugins during update pass.
  - OnlyServerProtocolUpdate: If true, updates only when the server changes and Oxide protocol changes.
  - UseScheme + SchemeFile: Enable and provide custom rule file (see section 16).

- Daily restart guard
  - DailyRestartTime: If set (HH:mm), FMU disables scanning `MinutesBeforeRestart` minutes prior to that time to avoid races with your own restart scheduler.

- Discord
  - DiscordNotificationsEnabled + DiscordWebhookUrl: Send concise update/wipe start and result messages.

- Wipe automation
  - BeforeForceWipeRange: Minutes before official force wipe (first Thu 19:00 UTC) to begin the wipe cycle.
  - CustomWipeDay / CustomWipeTime: Schedule a custom wipe once (local time).
  - ServerIdentity: Identity folder under `server/` used to locate data and cfg.
  - NextWipeServerName / NextWipeServerDescription: Optional values to write into server.cfg at wipe.
  - NextWipeMapUrl: If set, FMU removes `server.level`, `server.worldsize`, `server.seed` and writes `server.levelurl`.
  - NextWipeLevel / NextWipeSeed / NextWipeMapsize: Map parameters (validated; size must be 1000–6000).
  - NextWipeRandomSeed: If true, FMU generates a uint seed and sets it at wipe.
  - NextWipeKeepBps: Keep blueprints if true; otherwise delete BP DB.
  - NextWipeDeletePlayerData: Delete player data files.
  - NextWipeDeletePluginDatafiles: JSON array (as string) of plugin .json/.data files to delete from `oxide/data` at wipe.

- Markers and locks
  - UpdaterMarkerFileName: FMU app writes updater result here.
  - UpdaterLockFileName: Prevent re‑entry; FMU plugin clears it on startup if found.
  - MarkersSubfolder: Folder where plugin archives markers on next boot.

Notes:
- Long values for `NextWipeServerName`, `NextWipeServerDescription` and `NextWipeMapUrl` are safely base64‑encoded on the command line by the plugin. The app decodes them automatically.
- After a successful wipe, the plugin resets all “NextWipe*” configuration values.

---

## 9. Update Flow

1) Plugin detects changes (Rust build via SteamCMD, Oxide release via GitHub) or upcoming wipe windows.  
2) If an update/wipe is required:
   - In‑game countdown messages are broadcast (force/custom wipe messages are explicit).
   - The plugin invokes the FMU app with safe, encoded arguments.
3) The app:
   - Waits for the Rust process/service to stop.
   - Runs the self‑update pass (see section 11).
   - Creates a full backup (`temp_backup/`).
   - Applies Rust/Oxide updates and optionally updates uMod plugins.
   - If requested, executes the WipeCycle.
   - Writes `updateresult.json` and logs to `updater.log`.
   - Restarts the server (script or service).
4) On next boot, the plugin reads the marker, rotates it to `markers/`, promotes remote build to local (if successful), sends Discord result notification, and resumes.

---

## 10. Wipe Automation (Force + Custom)

### Monthly Force Wipe
- FMU computes the official force wipe date/time: first Thursday of each month at 19:00 UTC.
- When the remaining time is within `BeforeForceWipeRange` minutes (default 15), the plugin prepares and triggers a force wipe cycle.
- Countdown messages clearly mention force wipe.

### Custom Wipe
- Set `CustomWipeDay` (e.g., `05/12/2025`) and `CustomWipeTime` (e.g., `23:30`) in local time to schedule a one‑off wipe between force wipes.
- The plugin will:
  - Announce the wipe and pause regular update checks.
  - At `CountdownMinutes` remaining, broadcast in‑game warnings every minute.
  - Invoke the FMU app with update_id `wipe`.

### WipeCycle operations (inside the app)
- Optional plugin datafile deletions from `oxide/data` (exact filenames).
- Blueprints deletion (unless `NextWipeKeepBps=true`).
- Player data deletion if enabled.
- Map cleanup: removes `.map`, `.sav`, `.dat`.
- server.cfg updates:
  - If `NextWipeMapUrl` set: removes `server.level`, `server.worldsize`, `server.seed` and writes `server.levelurl`.
  - Else: writes `server.level`, `server.seed`, `server.worldsize` as provided.
  - Optionally writes `server.hostname` and `server.description`.

Wipe results, alerts, and any anomalies are included in:
- `updateresult.json` (wiped=yes/no, wipe_info warnings)
- Discord result notification

After a successful wipe, all “NextWipe*” fields are reset by the plugin.

---

## 11. Self‑Update (FMU plugin + app)

FMU includes a safe, two‑stage self‑update:

- The app checks the latest GitHub release for this repository:
  - Compares versions against the local plugin (`[Info(...)]` metadata).
  - Downloads the matching asset for your OS and runtime (Windows/Linux, standalone/framework‑dependent).
  - Stages:
    - Replaces the plugin file `oxide/plugins/FeedMeUpdates.cs`.
    - Drops a new updater binary as `FeedMeUpdates(.exe).new`.
    - Writes `FMU_CHECKSUMS.txt` with the SHA‑256 checksum.
- On the next server boot, the plugin:
  - Detects `FeedMeUpdates(.exe).new` + checksum file.
  - Computes and verifies SHA‑256 (constant‑time compare).
  - Promotes the `.new` file to the live `UpdaterExecutablePath` (and `chmod +x` on Linux).
  - Cleans up temporary files and resumes normal operation.

All actions are logged to `updater.log`. If anything fails during promotion, the plugin logs details and disables itself to avoid unsafe states.

---

## 12. Service / Script Integration

FMU can start your server either via:
- Script (Windows `.bat`/PowerShell or Linux shell script)
- Service (Windows SC/NSSM or Linux systemd)

Recommendations:

- Windows (NSSM)
  - Ensure service start/stop works via `sc stop <name>` / `sc start <name>`.
  - Configure NSSM to not aggressively restart on non‑zero exit codes while FMU is handling updates.

- Linux (systemd)
  - Recommended service settings:
    ```
    [Service]
    KillMode=process
    KillSignal=SIGINT
    SendSIGKILL=no
    Restart=on-failure
    SuccessExitStatus=0 9 SIGKILL
    NoNewPrivileges=false
    ```
  - If running FMU as a non‑root user, configure sudoers to allow `systemctl start <service>` without a password for that user.
  - On Linux, if `ShowUpdaterConsole=true`, FMU attempts gnome‑terminal or tmux. Otherwise, it falls back to nohup.

---

## 13. Discord Notifications

Set `DiscordNotificationsEnabled=true` and provide `DiscordWebhookUrl` to receive:
- Start notification: kind of update (server/oxide/both) and whether it’s a force wipe.
- Result notification: success/failure, updated plugin list (if any), wipe result, and any wipe alerts.

Messages are simple text posts via webhook.

---

## 14. Commands & Permissions

- Permission: `feedme.run` (required for in‑game use; console is always allowed)
- Commands:
  - `feedme.status [on|off]`  
    - Toggle periodic update checks or print current status (backend, last HTTP status, scheme on/off).
  - `feedme.version`  
    - Print plugin version + local Rust version/build/protocol + Oxide version.
  - `feedme.testrun`  
    - Immediately triggers a full update attempt (server+oxide). Intended for testing on non‑production copies.

---

## 15. Error Handling, Markers & Logs

- `updater.log` (server root): detailed logs of each update/wipe run (rotated by size).
- `updateresult.json`: summary marker written by the app:
  - `result`: `success` or `failed`
  - `fail_reason` (if failure) and `updated_plugins` (if any)
  - `backup_cycle` and `server_restored` flags (when restore happened)
  - `update_id`: `init`, `testrun`, `wipe` or an 8‑digit ID for scheduled runs
  - `wiped`: `yes` / `no`, plus `wipe_info` alerts if applicable
- On next boot, the plugin:
  - Reads/prints the marker.
  - Sends the Discord result notification.
  - Moves the marker into `<ServerDirectory>/<MarkersSubfolder>/`.
  - Promotes `RemoteSteamBuildID` → `LocalSteamBuildID` after successful update.

If a lock file (`updating.lock`) is present at boot, the plugin warns and removes it.

---

## 16. Advanced: Scheme Rules

Customize when to update server and/or Oxide based on events:

Format per line:
```
events=action
```

Events (combine with `+`):
- `server` → server changed
- `oxide(...)` → oxide changed with protocol flags:
  - `s` same protocol
  - `c` changed protocol
  - `u` unknown protocol
  - `e` error while fetching protocol
- `oxide` (without parentheses) → any of e/s/c/u

Actions: `server | oxide | both`

Examples:
```
oxide(eucs)+server=both
oxide(su)=oxide
server=server
```

FMU validates the file strictly; invalid lines disable `UseScheme` and revert behavior to the default logic.

---

## 17. Security & Permissions

- Ensure FMU app has execute permissions (`chmod +x` on Linux).
- Restrict who can edit `FeedMeUpdates.json` (contains webhook URL and paths).
- Give the FMU user only necessary permissions (e.g., systemd start for a single service via sudoers).
- Ensure SteamCMD directories are owned by the service user and writable.
- Plan for disk space: backups mirror your server folder content to `temp_backup/` during updates.

---

## 18. FAQ

- Can I update without a restart?  
  No. Safe replacement of binaries and Oxide assemblies requires a stop/start.

- What happens if an update fails?  
  FMU restores from `temp_backup/`, writes a failure marker, and attempts to start your server back.

- Can I skip plugin updates?  
  Yes, set `UpdatePlugins=false`.

- Can I change server name/description just for the next wipe?  
  Yes: fill `NextWipeServerName` / `NextWipeServerDescription`. FMU writes them into `server.cfg` during the wipe.

- How do I use a custom map URL at wipe?  
  Set `NextWipeMapUrl`. FMU will remove `server.level/worldsize/seed` and write `server.levelurl`.

- How do I schedule a custom wipe?  
  Set `CustomWipeDay` + `CustomWipeTime`. FMU will automatically run a wipe at that local date/time.

---

## 19. License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file.

---
