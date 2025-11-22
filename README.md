![alt text](FeedMeWallpaper2.png)

**FeedMeUpdates**

FeedMeUpdates is a highly configurable automatic update orchestrator for Rust and Oxide servers. It consists of a plugin running on Oxide framework and an executable responsible for performing the actual update, which gets called by the plugin when needed.

**Features**

- Automatic server update
- Automatic Oxide update
- Automatic plugin updates
- Fully configurable update management logic
- Automatic backup and restore in case of failure
- Notification to a Discord channel on start and result of the update
- Configurable to run in background or visible (console)
- Configurable for servers ran via batch/script
- Configurable for servers ran as a service
- Entirely cross-platform (Windows/Linux)

**Requirements**

- Rust server with sufficient privileges to run applications
- Oxide installed
- .NET 8 Framework (_only if using the non-standalone version_)
- Enough disk space to create backup of the server

**Installation**

- Shut down the server
- Create a backup of the server (_delete it if there are no issues at the end of installation_)
- Copy FeedMeUpdates.cs to the oxide plugins folder
- Copy the FeedMeUpdates.json configuration file to the oxide config folder
- Copy FeedMeUpdates.exe (_FeedMeUpdates without extension on Linux_) to the server folder
- Open the previously copied FeedMeUpdates.json configuration file and configure it (_see configuration section_)
- Make sure your service or your starting script is properly configured (_see notes below about either script or service configuration_).
- Start the server
- On first startup, the plugin will force the server to shut down, run the updater, and restart the server.

**Debugging in case of errors**

When the updater is started it writes a log (updater.log) in the server folder; you can check the log to understand where errors may have occurred. Before restarting the server (_whether the update succeeds or fails_), the updater generates a marker file in the server folder (_updateresult.json_). When the server restarts, the plugin automatically moves this marker file into the "markers" subfolder of the server folder, where it is kept for review. When moved to the markers folder, it is also renamed with the update_id of the call made by the plugin to the executable, making it easy to track. The possible update_id values are: "init" in case of the very first plugin execution (_as described in point 8 of the installation section_) which is necessary for correct system setup; "testrun" following an execution of the feedme.testrun command; or a randomly generated 8-digit string at the time of the plugin call to the executable. If you open a marker file with a text editor you'll find: "result", indicating update success or failure; "fail_reason", which indicates the reason for failure if any; and other fields useful for debugging (_e.g., success or failure of the update, temporary backup creation and/or restore in case of update failure, etc_).

**Configuration**

`ServerDirectory` (_e.g. "C:\\rust-server" or on linux "/home/rust/rust-server"_): Enter the path to your Rust server here (_where RustDedicated is located_)

`SteamCmdPath` (_e.g. "C:\\steamcmd\\steamcmd.exe" or on linux "/usr/games/steamcmd"_): Enter the full path to the steamcmd executable here

`UpdaterExecutablePath` (_e.g. "C:\\rust-server\\FeedMeUpdates.exe" or on linux "/home/rust/rust-server/FeedMeUpdates"_): Enter the full path to the FeedMeUpdates executable here

`ServerStartScript` (_e.g. "C:\\rust-server\\start_server.bat" or on linux "/home/rust/rust-server/start_server.sh"_): Enter the full path to the server start script here (note: if you run the server as a service you can ignore this field otherwide please read the note on script configuration)

`RustOnService` (_default is set to false_): Set to true only if you run the server as a service (_important, see service configuration note_)

`ServiceName` (_default is set to ""_): Fill in with the name of the service (_only if running server as a service_)

`CheckIntervalMinutes` (_default is 10_): Indicates how frequently to check for updates (minutes)

`CountdownMinutes` (_default is 5_): Number of minutes for the countdown with chat messages warning of server restart

`UpdatePlugins` (_default is false_): If set to true, the system will also update all installed plugins if more recent versions are found (_only Oxide plugins available on uMod_)

`MarkersSubfolder` (_default is "markers"_): Subfolder name in the server folder to store marker files produced by updates (_if folder does not exist it will be created_).

`OnlyServerProtocolUpdate` (_default is false_): If set to true, the system will update only in case of major updates (_both server and oxide updates must be available and must involve a protocol number change_)

`StartupScan` (_default is true_): If true, the system will immediately search for updates and apply them after initialization; if false, this check is skipped and updates are only looked for during periodic checks.

`ShowUpdaterConsole` (default is false): If true, the system tries to launch the updater executable by opening a new console window (shell), otherwise it is always run in background (_important, read notes on updater exec_ution)

`MaxAttempts` (_default is 0_): Indicates how many update attempts are permitted to the plugin before it disables itself (_0=unlimited_)

`DiscordNotificationsEnabled` (_default is false_): If true, the plugin sends notification of update start and result to the desired Discord server/channel

`DiscordWebhookUrl` (_default is ""_): If DiscordNotificationsEnabled is true, specify the webhook URL for the bot here

`UseScheme` (_default is false_): If true, the system tries to use the update logic expressed by the user in a specified schemefile; in case of loading or reading errors this feature disables itself and the system switches to default logic.

`SchemeFile` (_default is ""_): If UseScheme is true, specify here the full path to the logic schema file

`DailyRestartTime` (_default is ""_): If your server performs daily restarts (_which is recommended!_), specify the restart time here (_in HH:mm format, between 00:00 and 23:59_)

`MinutesBeforeRestart` (_default is 60_): Number of minutes before daily restart to stop periodic update checks (_ignore if your server doesn't do daily restarts_)

**NOTE on script configuration**:
A server start script is provided for both Windows (_.bat_) and linux (_.sh_) and need to be configured with starting parameters if you want to use it. In case you prefer to use your own script, if it contains automatic restarting logic you need to change it to not restart when lock file is present inside server directory (_updating.lock_). If you don't know how to do it then use the ones provided. This is very important because updates will always fail if your server is running while the updater does is job.

**NOTE on service configuration**:

Currently FeedMeUpdates ONLY supports service wrappers that allow management of specific exit codes (_e.g., NSSM in Windows and systemd in Linux_) and ONLY supports services that run RustDedicated directly without scripts.

Below is what's needed for the system to work correctly:

for Windows users:

- Make sure you can control your service with "sc", e.g. by stopping and restarting your service with these two powershell commands:

```
sc stop <service_name>

sc start <service_name>
```

If your service does not stop/start then you may try move your wrapper executable to a stable system path (_e.g. C:\\Tools\\nssm\\nssm.exe_) and update the binPath to the new wrapper path. This usually fix it.

- Have your service autorestart on default but not on exit codes 0, 4294967295, 3221225786. You also need to be sure that stopping the service does not kill child processes and that standard windows failure procedures are disabled for your rust service (_note: your service will still restart on crash or unexpected exits, so you are safe to go_) On NSSM you do it by typing the following powershell commands as admin:

```
nssm set <service_name> AppExit Default Restart

nssm set <service_name> AppExit 0 Exit

nssm set <service_name> AppExit 4294967295 Exit

nssm set <service_name> AppExit 3221225786 Exit

nssm set <service_name> AppStopMethodSkip 6

nssm set <service_name> AppStopMethodConsole 60000

sc.exe failureflag <service_name> 0

reg delete "HKLM\\SYSTEM\\CurrentControlSet\\Services\\<service_name>" /v FailureActions /f
```

For Linux users:

Be sure to have these lines in your service ini:

```
[Service]
KillMode=process
KillSignal=SIGINT
SendSIGKILL=no
Restart=on-failure
SuccessExitStatus=0 9 SIGKILL
NoNewPrivileges=false
```
You also need to add the start command to nopasswd so that updater can restart your service once update is over, here are the commands:
```
# change USERNAME value with the username of the user running the service and SERVICE value with your service name
USERNAME="user"
SERVICE="rust-server.service"
SYSTEMCTL_PATH="$(command -v systemctl)"
# now we add a rule to skip sudo password just for start command
echo "${USERNAME} ALL=(root) NOPASSWD: ${SYSTEMCTL_PATH} start ${SERVICE}" | sudo tee /etc/sudoers.d/${SERVICE%.service}-start >/dev/null
sudo chown root:root /etc/sudoers.d/${SERVICE%.service}-start
sudo chmod 0440 /etc/sudoers.d/${SERVICE%.service}-start
sudo visudo -c -f /etc/sudoers.d/${SERVICE%.service}-start
# now we make sure your ini is properly configured with NoNewPrivileges set to false and then relead the service file
sudo mkdir -p /etc/systemd/system/${SERVICE}.d
printf "[Service]\nNoNewPrivileges=false\n" | sudo tee /etc/systemd/system/${SERVICE}.d/override.conf >/dev/null
sudo systemctl daemon-reload
```

**Note about updater execution**:

- Both on Windows and on Linux if you set RustOnService true the updater will be ran in background, overriding the ShowUpdaterConsole value.
- On Windows, the plugin runs the updater creating a new visible window if ShowUpdaterConsole is true, otherwise in background if false.
- On Linux, if ShowUpdaterConsole is false the updater is run in background, if true the plugin tries to run it in the following ways (_in order of attempt; if both fail it runs in background_):
  1) Opening a new shell window if GNOME-terminal is installed
  2) Creating a new tmux session if tmux is installed (_session name: feedmeupdates_)

**Advanced "Scheme" (Custom Rules)**

By enabling UseScheme and specifying a SchemeFile, you can define lines in the following format:

- events = action
  - events: one or more conditions separated by "+"
  - server → "server changed" event
  - oxide(...) → "oxide changed" event with protocol flags:
    - s = same protocol
    - c = changed protocol
    - u = unknown protocol
    - e = error while fetching protocol
  - oxide without parentheses → considers all: e, s, c, u
- action: server | oxide | both

**Examples**:

- oxide(eucs)+server=both (_If Oxide has changed (any case: e/s/c/u) AND the server has also changed, then update "both"_).
- oxide(su)=oxide (_If Oxide has changed and the protocol is same/unknown (s or u), update only Oxide_).
- server=server (_If only the server has changed, update only the server_).

**Notes**:

- Blank lines or lines starting with "//" are ignored (_comments_).
- If a line is invalid, the entire scheme is marked as "invalid" and UseScheme is disabled; the plugin prints errors and falls back to default logic.

- Rule processing is sequential: the first matching rule determines the action.






