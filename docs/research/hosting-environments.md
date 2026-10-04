# R1 — How Rust servers are hosted, updated, restarted and wiped (research for FeedMeUpdates 2.0)

Research date: 2026-10-04. Every claim carries a source URL. Tags:

- **[V]** VERIFIED — read in a primary source during this research (source code on GitHub, official docs, the host's own knowledge base).
- **[V-code]** VERIFIED BY CODE READING — a behaviour I derived by reading the primary source code, but did not run. Treat as "very likely, test before relying on it".
- **[L]** LIKELY / UNVERIFIED — secondary source, inference, or general knowledge. Do not design on it without testing.
- **[NF]** NOT FOUND — I looked and could not find a primary source.

Source dates are given where the source exposes one. "Fetched 2026-10" means the file was read from the default branch today and has no visible date of its own.

---

## A. Pterodactyl and Pelican

### A.1 The stock Rust egg (Pterodactyl panel)

Source: `https://raw.githubusercontent.com/pterodactyl/panel/1.0-develop/database/Seeders/eggs/rust/egg-rust.json` (egg `exported_at` 2023-03-25; fetched 2026-10).

- **[V]** Docker image: `ghcr.io/pterodactyl/games:rust`.
- **[V]** Startup command:
  ```
  ./RustDedicated -batchmode +server.port {{SERVER_PORT}} +server.queryport {{QUERY_PORT}} +server.identity "rust" +rcon.port {{RCON_PORT}} +rcon.web true +server.hostname "{{HOSTNAME}}" +server.level "{{LEVEL}}" +server.description "{{DESCRIPTION}}" +server.url "{{SERVER_URL}}" +server.headerimage "{{SERVER_IMG}}" +server.logoimage "{{SERVER_LOGO}}" +server.maxplayers {{MAX_PLAYERS}} +rcon.password "{{RCON_PASS}}" +server.saveinterval {{SAVEINTERVAL}} +app.port {{APP_PORT}} $( [ -z ${MAP_URL} ] && printf %s "+server.worldsize \"{{WORLD_SIZE}}\" +server.seed \"{{WORLD_SEED}}\"" || printf %s "+server.levelurl {{MAP_URL}}" ) {{ADDITIONAL_ARGS}}
  ```
  Consequences: `server.identity` is hard-coded to `rust` (save dir is `/home/container/server/rust/`); seed, world size and level URL are **command-line arguments built from egg variables**, so they override anything in `server.cfg`.
- **[V]** Stop command is `quit`; "started" is detected by the console line `Server startup complete`.
- **[V]** Variables (all user-viewable; all user-editable except the ports): `HOSTNAME`, `FRAMEWORK` (`vanilla|oxide|carbon`, default `vanilla`), `LEVEL`, `DESCRIPTION`, `SERVER_URL`, `WORLD_SIZE`, `WORLD_SEED`, `MAX_PLAYERS`, `SERVER_IMG`, `SERVER_LOGO`, `RCON_PASS`, `SAVEINTERVAL`, `ADDITIONAL_ARGS`, `MAP_URL`; not user-editable: `QUERY_PORT`, `RCON_PORT`, `APP_PORT`.
- **[V]** There is **no `AUTO_UPDATE` variable in the stock egg**. The entrypoint treats an unset `AUTO_UPDATE` as "update" (see A.2), so a stock install always updates. A panel maintainer's documented workaround for people who do not want that is to add an `AUTO_UPDATE` variable to the egg and set it to `0` — which only a panel admin can do, not a renting customer. Source: `https://github.com/pterodactyl/panel/issues/4767`.
- **[V]** Install script downloads SteamCMD into `/mnt/server/steamcmd` (= `/home/container/steamcmd` at runtime) and runs `app_update 258550 validate`. So **steamcmd is present inside the server's own volume** at `/home/container/steamcmd/steamcmd.sh`.

### A.2 Entrypoint and wrapper (the "yolk")

Sources: `https://raw.githubusercontent.com/pterodactyl/yolks/master/games/rust/entrypoint.sh`, `.../wrapper.js`, `.../Dockerfile` (fetched 2026-10).

- **[V]** On **every container start** the entrypoint does, in order:
  1. `if [ -z ${AUTO_UPDATE} ] || [ "${AUTO_UPDATE}" == "1" ]` → `./steamcmd/steamcmd.sh +force_install_dir /home/container +login anonymous +app_update 258550 +quit` (no `validate`).
  2. `FRAMEWORK=carbon` → `curl .../CarbonCommunity/Carbon.Core/releases/download/production_build/Carbon.Linux.Release.tar.gz | tar zx`, then sets `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY=.../carbon/managed/Carbon.Preloader.dll` and prefixes `LD_PRELOAD=$(pwd)/libdoorstop.so`.
  3. `FRAMEWORK=oxide` (or legacy `OXIDE=1`) → `curl .../OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip`, `unzip -o -q`.
  4. `node /wrapper.js "<startup>"`.
- **[V]** So with the stock egg, **a restart is an update**: Rust, then Oxide or Carbon, are refreshed unconditionally to "latest" before the game launches. There is no version matching between the Rust build and the framework build. The Oxide download is **not** gated by `AUTO_UPDATE`.
- **[V]** The image contains `curl`, `unzip`, `tar`, Node.js and the `ws` module; it runs as user `container` with `HOME=/home/container`.
- **[V]** `wrapper.js` starts the game with `child_process.exec`, relays stdout until RCON is up, then connects to WebRCON at `ws://localhost:RCON_PORT/RCON_PASS` and from then on forwards panel console input as RCON commands and prints RCON output (also appended to `latest.log`). Before RCON connects, the only console input it honours is `quit` (anything else prints `Unable to run "<cmd>" due to RCON not being connected yet.`).
- **[V-code]** What happens when RustDedicated exits by itself: the `exit` handler only sets a flag (the `process.exit(code)` line is commented out). The wrapper process ends when the RCON websocket closes (`ws.on("close")` → `process.exit()`), i.e. with exit code 0. **If the game dies before RCON ever connected, nothing calls `process.exit`**: the wrapper keeps printing `Waiting for RCON to come up...` every 5 s and the container stays "starting" forever. An update tool that quits the server very early in boot can therefore hang the instance.

### A.3 Pelican variants

Sources: `https://raw.githubusercontent.com/pelican-eggs/yolks/master/games/rust/entrypoint.sh`, `.../Dockerfile`; `https://raw.githubusercontent.com/pelican-eggs/games-steamcmd/main/rust/rust_autowipe/README.md`, `.../egg-rust-autowipe.yaml`, `.../egg-pterodactyl-rust-autowipe.json` (egg exported 2026-05-10); `.../rust/rust_staging/README.md`.

- **[V]** The pelican-eggs yolk has the same logic, plus: `SRCDS_BETAID` / `SRCDS_BETAPASS` for branches, a `FRAMEWORK=oxide-staging` option (`https://downloads.oxidemod.com/artifacts/Oxide.Rust/staging/Oxide.Rust-linux.zip`), Carbon from `CarbonCommunity/Carbon` instead of `Carbon.Core`, Node 22. `wrapper.js` is byte-identical apart from a trailing newline.
- **[V]** The community "Rust Autowipe" egg moves hostname/level/worldsize/seed/levelurl/tags **out of the command line into `server/rust/cfg/server.cfg`** through the panel's config-file parser, and adds `REGEN_SERVER` (0/1) and `REMOVE_FILES` (space-separated globs; default includes `player.*.db`, `proceduralmap.*.map`, `*.sav` and some `oxide/data` files). The wipe happens **in the install script**, i.e. you trigger it with "Reinstall server". A seed of `0` makes the install script write a random 5-digit seed to `seed.txt`.
- **[V]** The "Rust Staging" egg's README states Oxide does not support the staging branch.
- **[NF]** I could not locate the Rust egg shipped inside `pelican-dev/panel` itself (the paths I tried returned 404), so I cannot say whether Pelican's bundled egg differs from the Pterodactyl one.

### A.4 Crash detection and auto-restart (Wings)

Sources: `https://raw.githubusercontent.com/pterodactyl/wings/develop/server/crash.go`, `.../server/server.go`, `.../server/listeners.go`; docs `https://pterodactyl.io/wings/1.0/configuration.html` and `https://pelican.dev/docs/wings/optional-config`; Pelican `https://raw.githubusercontent.com/pelican-dev/wings/main/server/crash.go` (same logic plus activity logging).

- **[V]** A transition from *starting* or *running* to *offline* that was not initiated through the panel is treated as a crash and runs the crash handler.
- **[V]** Wings marks the server as "stopping" only when the configured stop command (`quit`) is **sent through the panel console/API**. A `quit` issued from inside the game (a plugin calling the console command, or a direct RCON client) is not seen by Wings, so the exit is handled as a crash.
- **[V]** Crash handler rules:
  - per-server `CrashDetectionEnabled` false → `Aborting automatic restart, crash detection is disabled for this instance.`
  - exit code 0 and not OOM and node setting `detect_clean_exit_as_crash` false → nothing happens, server stays offline.
  - last crash less than `timeout` seconds ago (default **60**) → `Aborting automatic restart, last crash occurred less than 60 seconds ago.` and the server stays offline.
  - otherwise → power action `start` (which re-runs the entrypoint, hence the updates).
- **[V]** Defaults: `detect_clean_exit_as_crash: true`, `timeout: 60`. Both are node-level settings in the Wings `config.yml`; a renting customer cannot see or change them.
- **Design consequence [V-code]:** on a default Pterodactyl/Pelican node, a plugin that simply quits the server gets an automatic restart *with* a game + framework update. It fails if (a) the host disabled crash detection or clean-exit-as-crash, (b) the previous unrequested exit was under 60 s ago, (c) the quit happens before RCON is connected (wrapper hang, A.2).

### A.5 Container limits

Source: `https://raw.githubusercontent.com/pterodactyl/wings/develop/environment/docker/container.go` (fetched 2026-10); `https://pterodactyl.io/community/config/eggs/creating_a_custom_image.html`.

- **[V]** `ReadonlyRootfs: true`; `SecurityOpt: no-new-privileges`; capabilities dropped: `setpcap, mknod, audit_write, net_raw, dac_override, fowner, fsetid, net_bind_service, sys_chroot, setfcap`.
- **[V]** Writable locations: the server volume mounted at `/home/container`, and `/tmp` as tmpfs mounted `rw,exec,nosuid`, default size 100 MB.
- **[V]** Container runs as the configured non-root uid:gid; PID limit default 512 (`container_pid_limit`); memory/CPU/disk limits come from the panel.
- **[V]** Nothing in that configuration blocks `exec` of files stored under `/home/container` (the game binary itself and `steamcmd.sh` are executed from there).
- **[L]** Therefore a plugin can start a child process with `System.Diagnostics.Process` (a Linux binary dropped in `/home/container`, or `/bin/bash`, `curl`, `unzip`, `node`, which are in the image). Not tested by me. Caveats: the child lives in the same container and **dies when the container stops**, so a "companion that outlives the server and restarts it" — the FMU v1 model — cannot work here. Anything started must finish before the game exits, or the work must be left to the entrypoint.
- **[L]** Running `steamcmd` from a plugin while the game is running would rewrite files in use; the entrypoint already does this safely on the next start.
- **[NF]** Whether commercial hosts add seccomp/AppArmor profiles or egress firewalls on top of this. Unknown per host.

### A.6 Client API

Sources: `https://raw.githubusercontent.com/pterodactyl/panel/1.0-develop/routes/api-client.php`, `.../config/http.php`, `.../app/Models/ApiKey.php`, `.../app/Models/Task.php`, `.../app/Http/Requests/Api/Client/Servers/Schedules/StoreTaskRequest.php`; Pelican `https://raw.githubusercontent.com/pelican-dev/panel/main/routes/api-client.php`.

- **[V]** Routes under `/api/client/servers/{server}`:
  - `POST /power` (start, stop, restart, kill), `POST /command`, `GET /resources`, websocket credentials.
  - Files: `GET /files/list`, `/files/contents`, `/files/download`, `/files/upload`; `PUT /files/rename`; `POST /files/copy`, `/files/write`, `/files/compress`, `/files/decompress`, `/files/delete`, `/files/create-folder`, `/files/chmod`, plus a remote "pull" endpoint.
  - Schedules: list/create/update/delete, `POST /schedules/{id}/execute`, tasks; task actions are `command`, `power`, `backup` only.
  - `GET /startup`, **`PUT /startup/variable`** (changes any user-editable egg variable, e.g. `WORLD_SEED`, `WORLD_SIZE`, `MAP_URL`, `FRAMEWORK`).
  - `POST /settings/reinstall`, `POST /settings/rename`, `PUT /settings/docker-image`; backups; databases; subusers.
- **[V]** Authentication is a client API key created by the user in their account page; a key can carry an `allowed_ips` list. Default rate limit: 256 requests per minute (`APP_API_CLIENT_RATELIMIT`).
- **[V]** Pelican exposes the same `/command`, `/power`, `/schedules`, `/startup`, `/settings/reinstall` routes.
- **[V]** An existing open-source tool, `clearshot-xs/pterodactyl-rust-auto-wipe`, does scheduled and force wipes from an **external machine** using only a client API key: it deletes files by glob under `/home/container`, picks a seed or map URL, broadcasts a countdown, restarts, and on force-wipe Thursday polls umod.org every 2 minutes from 13:00 US Eastern for the new Oxide build. Source: `https://github.com/clearshot-xs/pterodactyl-rust-auto-wipe` (README fetched 2026-10).
- **[L]** Calling the API **from inside the container** (a plugin doing HTTPS to the panel URL): nothing in the panel distinguishes the caller's location, and containers have outbound network with DNS 1.1.1.1/1.0.0.1 by default (`config_docker.go`). It should work unless the key has `allowed_ips` set or the host firewalls the panel from its nodes. Not tested. Security note: the key is account-wide (all of the user's servers), so storing it in a plugin config is a real exposure.
- **[L]** Many commercial "Pterodactyl-based" hosts run forks or hide the account API page; availability of client API keys must be checked per host. **[NF]** for each individual host below.

### A.7 Known complaint

- **[V]** GitHub issue "Manual server + Oxide updating for Rust" (pterodactyl/panel #4767): "Currently, updating on server startup is the only way to go about it"; the requester's point is that Rust and Oxide releases are not aligned so unconditional update-on-start can break plugins or startup. Closed as duplicate with the `AUTO_UPDATE=0` suggestion; no update button was built. `https://github.com/pterodactyl/panel/issues/4767`

---

## B. Commercial hosts

General caveat: no host I found documents whether plugins may spawn processes, and almost none documents an API. Those cells are **[NF]** unless stated.

| Host | Panel | Rust / framework update | Wipe tooling | Files | Startup params | API | Source (date) |
|---|---|---|---|---|---|---|---|
| **Shockbyte** | Own panel (`panel.shockbyte.com`) [V]; underlying tech [NF] | [V] "Automatic updates are enabled by default… take effect after a server restart"; toggle **Steam Updates** in "Rust Server Settings". Article says Oxide/Carbon are updated by the same restart but describes no separate mechanism | [V] **Tasks** tab: drag a wipe task (map, blueprint, or both) into Scheduled Tasks, with date/time and repeat every X hours/days/weeks | Web file manager [V] | [NF] | [NF] | `https://shockbyte.com/help/knowledgebase/articles/how-to-update-your-rust-server-and-oxide-or-carbon` (2024-10-27); `https://shockbyte.com/billing/knowledgebase/851/How-to-Configure-Automatic-Wipes-on-your-Rust-Server.html` (2024-10-27) |
| **Pine Hosting** | Custom "Pine Panel v2" [V per their blog] | [V] Startup tab toggle **Automatic Framework Update**; restart "fetch[es] all new files automatically" | [V] Schedules → View Templates → a force-wipe template; editable **Rust Wipe** task chooses what is wiped; blog says it can also update startup values such as a random seed | Web file manager [V]; SFTP [NF] | Startup tab exists [V] | [NF] | `https://pinehosting.com/clientarea/knowledgebase/172/How-To-Update-OxideorRust-On-Your-Rust-Server.html`; `https://pinehosting.com/clientarea/knowledgebase/231/How-To-Configure-Automatic-Wipes-On-Your-Rust-Server.html`; `https://pinehosting.com/blog/the-pine-panel-v2-0-whats-changed/` (2024-02-26) |
| **Host Havoc** | "Game Control Panel" (`gamepanel.hosthavoc.com`); TCAdmin [L — matches the TCAdmin Rust config below, not stated by the host] | [V] uMod installed from "Non-Workshop Mods"; "uMod updates are applied automatically the next time you run a server update". Server update is an explicit action, not every restart | [V] Scheduled task **Wipe on Rust Wipe Day**, set to run Daily; it "checks whether today is the first Thursday of the month each time it runs" | FTP + file manager [L] | [NF] | [NF] | `https://hosthavoc.com/billing/knowledgebase/17/How-to-installorupdate-uModorOxide-on-your-Rust-server.html` (updated 2026-06-10); `https://hosthavoc.com/wiki/game-servers/rust/schedule-wipe` |
| **LOW.MS** | [V] "our TCAdmin-based panel" | [V] Manual: "Updates come through Steam Update in the panel; after a forced wipe day you'll want to run that". [V] "Facepunch overwrites Oxide files every time the server is updated… you have to reinstall Oxide" | Scheduled Tasks exist [V]; a dedicated wipe task is not documented [NF] | File Manager, Configuration Files [V] | [V] **Commandline Manager** for launch arguments | [NF] | `https://low.ms/knowledgebase/rust-getting-started`; `https://low.ms/knowledgebase/rust-troubleshooting-guide` (2026-04-30) |
| **PingPerfect** | Game panel with Mod Manager / Commandline Manager / Scheduled Tasks (TCAdmin vocabulary) [L] | [V] "go to the mod manager button. Click Install uMod (oxide)"; auto-update [NF] | [NF] | FTP [L] | [V] Commandline Manager | [NF] | `https://pingperfect.com/index.php/knowledgebase/63/Rust` |
| **GG Host** (small, for comparison) | TCAdmin-style [L] | [V] "Update server" option on the wipe task | [V] Same **Wipe on Rust Wipe Day** task: "does nothing on an ordinary day, and on the first Thursday of the month it stops the server, runs the update and the wipe" | — | — | [NF] | `https://www.gghost.games/knowledgebase/85/Rust-wipe-schedule-what-force-wipe-day-does-to-your-server-and-how-to-handle-it.html` |
| **Survival Servers** | Custom panel [L] | [V] "By default, the server will only update if it is absolutely required"; a **Rust Update** drop-down offers "Yes, run update/validate" on restart. [V] Oxide chosen from a Mod drop-down; after a Rust update it must be re-applied by switching to "No Mod", Save & Restart, back to Oxide, Save & Restart | No scheduler for wipes documented; manual deletion over FTP [V] | FTP [V] | "Advanced Launch Params" [V] | [NF] | `https://survivalservers.com/wiki/index.php?title=Rust_Wiping_Data`; `https://survivalservers.com/wiki/index.php?title=Install_Oxide_Plugin`; `https://www.survivalservers.com/wiki/How_to:_Control_Panel` |
| **RocketNode** | "RocketNode Panel"; the Startup variable names (Level, World Size, World Seed, Save Interval, Additional Arguments) and "Reinstall Server" match the Pterodactyl Rust egg [L] | [V] "toggle a single button to have Oxide installed and auto-updated" | Scheduled wipes [NF]; "Reinstall Server" [V] | Web file manager [V]; SFTP [L] | [V] editable Startup variables | [NF] | `https://rocketnode.com/blog/how-to-make-a-rust-server/` (2023-05-07) |
| **Apex Hosting** | Own panel | [V] Customizations → **Enable uMod Support**; auto-update [NF] | [NF] | [V] "FTP File Access" | [NF] | [NF] | `https://apexminecrafthosting.com/add-plugins-rust/` |
| **GPortal** | Custom | [V] Basic Settings: switch to "Oxide Mods", restart. Auto-update behaviour [NF] | [V] PC: manual, stop server and delete `player.blueprints.*.db` over FTP in `/Release/server/my_server_identity/`. The restart/wipe scheduler I found documented is for **Console Edition** only | [V] FTP | Basic Settings form; free command line [NF] | [NF] | `https://www.g-portal.com/wiki/en/how-do-i-install-oxide-umod-and-plugins-for-my-rust-server/`; `https://www.g-portal.com/wiki/en/how-do-i-wipe-the-players-blueprints-on-my-rust-server/`; `https://www.g-portal.com/wiki/en/how-do-i-create-a-restart-wipe-schedule-for-my-rust-console-edition-server/` |
| **Nitrado** | Custom web interface | [NF] — Nitrado's guide pages are JavaScript-rendered and returned no content to my fetches | [NF] | FTP + file browser [L] | [NF] | **[V] yes — NitrAPI**: `gameservers/restart`, `gameservers/stop`, `gameservers/command`, `gameservers/file_server/{list,upload,download,delete,move,copy,mkdir,...}`, `gameservers/games/install` | `https://github.com/nitrado/NitrAPI-PHP/blob/master/lib/Nitrapi/Services/Gameservers/Gameserver.php`; `.../FileServer/FileServer.php` |
| **berrybyte** (small, for comparison) | Own panel, behaves like the Pterodactyl egg | [V] "Starting the server checks the dedicated build before Rust launches"; "The runtime refreshes that framework at startup too"; Game Settings → Modding Framework (Vanilla/Oxide/Carbon) | none documented | — | — | [NF] | `https://berrybyte.net/wiki/games/rust/wipe-and-update` |

### B.1 The TCAdmin Rust config that several hosts appear to use

- **[V]** A community TCAdmin game config, "Rust – Automatic wipe/update, Oxide, Plugin Installer" (author Dennis, submitted 2020-09-29), provides: "Automatic wipe every first Thursday of the month (update + wipe)" through a scheduled task named "Wipe on Rust Wipe Day" configured daily; "If uMod is installed, uMod will also be updated automatically when a Steam Update has run"; plugin download from uMod; seed/world-size variables; seed generation. Source: `https://community.tcadmin.com/topic/11464-rust-automatic-wipeupdate-oxide-plugin-installer/`
- **[L]** The identical task name at Host Havoc and GG Host suggests they use this config or a derivative.
- **[V]** TCAdmin-family hosts therefore differ from the Pterodactyl family in one important way: **a restart is not an update**. The update is an explicit "Steam Update" action or part of a scheduled task.

### B.2 What I could not establish for any host

- **[NF]** Whether plugins may spawn processes. No host documents it. On Pterodactyl-type containers it is technically possible (A.5); on TCAdmin hosts the game runs under a per-service OS user, often on Windows, and I found no statement either way.
- **[NF]** A customer-facing API at Shockbyte, Pine, Host Havoc, LOW.MS, PingPerfect, Survival Servers, RocketNode, Apex, GPortal.
- **[NF]** Whether `server.seed` / `server.worldsize` / `server.levelurl` are forced on the command line at each host. It is verified only for the stock Pterodactyl egg (A.1) and, by inference from variable names, RocketNode. This matters: where they are forced on the command line, a plugin cannot change the map for the next wipe by editing `server.cfg`.

---

## C. Self-hosted managers

### C.1 LinuxGSM (`rustserver`)

Sources: `https://docs.linuxgsm.com/game-servers/rust`; `https://raw.githubusercontent.com/GameServerManagers/LinuxGSM/master/lgsm/config-default/config-lgsm/rustserver/_default.cfg`; `.../lgsm/modules/command_wipe.sh`; `.../lgsm/modules/mods_list.sh`; `.../lgsm/modules/command_monitor.sh` (all fetched 2026-10).

- **[V]** Commands: `update`, `force-update`, `start`, `stop`, `restart`, `monitor`, `mods-install`, `mods-update`, `map-wipe`, `full-wipe`.
- **[V]** `map-wipe` deletes `*.map` and `*.sav*` in the identity dir; `full-wipe` additionally deletes `*.db` **except `player.tokens.db`** (Rust+ pairing is preserved).
- **[V]** If `seed=""` in the config, a wipe generates a new random seed (`shuf -i 1-2147483647`) stored in `<selfname>-seed.txt`.
- **[V]** Start parameters are one config string including `+server.seed`, `+server.salt`, `+server.worldsize`, `+server.level`, `customlevelurl`, `+server.identity "${selfname}"`.
- **[V]** `updateonstart="off"` by default.
- **[V]** Mods: `rustoxide` (asset from `api.github.com/repos/OxideMod/Oxide.Rust/releases/latest`) and `rustcarbon` (`CarbonCommunity/Carbon.Core` `production_build`).
- **[V]** The server runs in a tmux session; `monitor` (intended for cron) checks the session and queries the server, and restarts it when down. The docs give cron examples for scheduled wipes, e.g. `0 0 * * 0 /home/rustserver/rustserver map-wipe`.
- **[L]** A plugin-initiated `quit` leaves LinuxGSM believing the server should be running, so the next `monitor` run restarts it — without updating unless `updateonstart` is on.

### C.2 Docker — `didstopia/rust-server`

Sources: `https://raw.githubusercontent.com/Didstopia/rust-server/master/README.md`, `.../start_rust.sh`, `.../Dockerfile` (fetched 2026-10).

- **[V]** "This image will install/update on startup." Data lives in `/steamcmd/rust`.
- **[V]** Env vars: `RUST_SERVER_STARTUP_ARGUMENTS`, `RUST_SERVER_IDENTITY` (default `docker`), `RUST_SERVER_SEED` (12345), `RUST_SERVER_WORLDSIZE` (3500), `RUST_SERVER_LEVELURL` (when set, seed and size are ignored), `RUST_SERVER_NAME`, `RUST_SERVER_MAXPLAYERS`, `RUST_SERVER_DESCRIPTION`, `RUST_SERVER_URL`, `RUST_SERVER_BANNER_URL`, `RUST_SERVER_SAVE_INTERVAL`, `RUST_RCON_WEB`, `RUST_RCON_PORT`, `RUST_RCON_PASSWORD`, `RUST_APP_PORT`, `RUST_BRANCH`, `RUST_UPDATE_CHECKING` (0; 1 = "fully automatic update checking, notifying players and restarting to install updates"), `RUST_UPDATE_BRANCH`, `RUST_START_MODE` (0 update then start, 1 update only, 2 start only), `RUST_OXIDE_ENABLED` (0), `RUST_OXIDE_UPDATE_ON_BOOT` (1), `RUST_RCON_SECURE_WEBSOCKET`, `RUST_HEARTBEAT`.
- **[V]** Oxide is pulled from `https://umod.org/games/rust/download/develop` on boot. No Carbon variable exists in this image.
- **[V]** Seed, world size and level URL are passed as command-line arguments built from env vars — same "locked from inside" property as the Pterodactyl egg.
- **[V]** No wipe feature. An `rcon` helper exists (`docker exec <c> rcon say Hello`).
- **[L]** Restart after the process exits depends on the Docker restart policy chosen by the admin (`--restart unless-stopped` etc.); the image does not set one.

### C.3 AMP (CubeCoders)

- **[V]** In a 2025-03-29 support thread a user reports Carbon not loading when AMP starts the server; staff point to a community generic-module template (`Greelan/AMPTemplates:dev`) that "has an option to install Carbon". Source: `https://discourse.cubecoders.com/t/amp-rust-carbon/26110`
- **[V]** AMP has a scheduler with triggers/tasks used for Rust restarts (bug report, AMP 2.3.2.10, 2022). Source: `https://github.com/CubeCoders/AMP/issues/622`
- **[NF]** The current Rust template's exact update stages, whether Oxide/Carbon are refreshed on each update, any wipe task, and AMP API specifics. The product page returned no content and I could not find the Rust files in `CubeCoders/AMPTemplates` by guessing names. **Treat AMP details as unverified.**
- **[L]** AMP exposes a web API and runs the game as a child of the instance; whether an unrequested exit auto-restarts depends on instance settings.

### C.4 WindowsGSM

Sources: `https://raw.githubusercontent.com/WindowsGSM/WindowsGSM/master/README.md`, `.../WindowsGSM/GameServer/RUST.cs`.

- **[V]** Features: "Auto Restart — auto restart your server if it crashes", "Auto Update — Checks for updates every 30 minutes", "Restart Crontab", "Start on Login".
- **[V]** The Rust module launches with `-nographics -batchmode -silent-crashes` plus hostname/ip/port/queryport/level/maxplayers and a free-form parameter string; updates through SteamCMD app 258550 comparing local and remote build.
- **[V]** No Oxide/Carbon handling in the built-in Rust module; community plugins add it (e.g. `https://github.com/Joe90384/WindowsGSM.RustOxideWithRustEdit`). No wipe feature.

### C.5 TCAdmin — see B.1.

### C.6 systemd, NSSM / Windows services, tmux/screen, batch loops

- **[L]** No primary source specific to Rust; this is general platform behaviour and is what FMU v1 already targets. Summary: `Restart=always|on-failure` (systemd) and NSSM's exit actions restart the process when it exits, optionally via `ExecStartPre` for an update step; a `:loop` batch file or `while true` shell loop conventionally runs SteamCMD, then the framework download, then the server, then loops. In all of them the admin owns the script, so seed/size/level are whatever the script passes, and an external process is free to stop, update, wipe and start.
- **[V]** Typical community scripts are exactly this pattern (SteamCMD + wget + 7-Zip in a Windows batch): `https://oxidemod.org/threads/automating-updates-for-rust-oxide-server.17804/` (2016–2017).

### C.7 Facepunch's own wipe timer

- **[V]** `wipetimer.wipeTimezone` (default Europe/London), `wipetimer.wipeDayofWeek` (4 = Thursday), `wipetimer.wipeHourofDay` (19), `wipetimer.wipeUnixTimestampOverride`, `wipetimer.wipecronoverride` (Cronos syntax, e.g. `0 14 1-7,15-21 * 4`). Source: `https://wiki.facepunch.com/rust/server-wipe-timer`
- **[V]** The page documents configuring *when* the wipe is announced; it does not say the server wipes itself. **[L]** These convars drive the displayed countdown only. FMU should set them to match its own schedule rather than rely on them.

---

## D. Plugin sandbox and marketplace rules

### D.1 Oxide

- **[V]** The sandbox is gone. uMod announcement "Oxide Development and Community Update": the sandbox would be removed on "April 6th (next force wipe)" [2023] because it "has become obsolete" and was "more of a limitation for developers than it is a safeguard for server owners"; plugin guidelines would now permit "plugins using reflection and harmony". Source: `https://umod.org/community/announcements/47115-oxide-development-and-community-update`
- **[V]** OxideMod feedback item "Removing sandbox completely" is marked **Completed**. Source: `https://oxidemod.nolt.io/4`
- **[V]** Current `Oxide.CSharp` source (`src/CompiledAssembly.cs`, `src/CSharpExtension.cs`, branches `develop` and `master`, fetched 2026-10) contains no occurrence of "sandbox" or "blacklist"; the only assembly patching left is the `DirectCallMethod` optimisation. Sources: `https://raw.githubusercontent.com/OxideMod/Oxide.CSharp/develop/src/CompiledAssembly.cs`, `.../src/CSharpExtension.cs`
- **[V]** Codefling's "Disable Sandbox" page: "Oxide has (finally) removed the sandbox, so this plugin is no longer necessary." Source: `https://codefling.com/plugins/disable-sandbox`
- **[V]** The old uMod guide (create `oxide.disable-sandbox` in `RustDedicated_Data/Managed`) is still online but was last updated about five years ago and is obsolete. Source: `https://umod.org/guides/oxide/disabling-plugin-sandboxing`
- **Conclusion [V + L]:** an Oxide `.cs` plugin can today use `System.IO`, `System.Net`, `System.Diagnostics.Process` and reflection with no opt-in file. The remaining limits are the OS/container ones (A.5), not the framework. (The "can" for `Process` specifically is an inference from the absence of any filter in the compiler source; I did not run it.)

### D.2 Carbon

- **[V]** Carbon's configuration documentation lists `SelfUpdating`, `Watchers`, `Compiler` (`UnloadOnFailure`, `ConditionalCompilationSymbols`), `Publicizer` — and no sandbox, namespace allow/deny list or similar. Source: `https://carbonmod.gg/owners/configuring-carbon`
- **[V]** Carbon self-updates at boot: `"SelfUpdating": { "Enabled": true, "HookUpdates": true, "RedirectUri": null }`; before any Rust assembly loads it downloads the version list, and if outdated fetches the build from GitHub into `carbon/managed`; hook updates follow. "Wipe updates: released 1 hour before Rust server updates"; on production the update applies once the Rust build itself has been updated. Source: `https://carbonmod.gg/owners/auto-updates`
- **[L]** Carbon has no plugin sandbox (absence of evidence in the docs; not confirmed from Carbon's compiler source).
- **Design consequence [V]:** on Carbon, the framework half of "update" already happens by itself on restart on every kind of hosting. Only the Rust build needs an external updater.

### D.3 uMod submission rules

- **[V]** The approval guide still contains: "Plugin must not be using any Reflection, as this will be restricted and blocked." It was last updated about six years ago and contains no explicit rule on `System.IO`, `System.Diagnostics`, `Process`, `System.Net`, companion executables or obfuscation. Source: `https://umod.org/guides/development/approval-guide`
- **[V]** That rule is contradicted by the 2023 announcement saying reflection and Harmony plugins are now permitted (D.1).
- **[NF]** A current, explicit uMod rule about starting external processes or shipping a native companion binary. The FAQ page returned no body text to my fetch. **Ask the uMod moderators before assuming a plugin that launches an executable would be approved.**
- **[L]** FMU v1's need for a bundled executable is the kind of thing a manual reviewer is likely to question; an in-process, HTTP-only mode would be much easier to get listed.

### D.4 Codefling upload guidelines

Source: `https://codefling.com/upload-guidelines/` (fetched 2026-10; no revision date shown).

- **[V]** "Submissions must not contain malware, backdoors, credential theft, hidden commands, or hardcoded access for the author or anyone else."
- **[V]** "Do not obfuscate or conceal functionality to prevent review." On request, creators must "provide Codefling with the source code needed to review your submission, including custom bundled components."
- **[V]** "External communication is allowed only when needed for a feature described in the listing… Document which services the submission contacts, why it contacts them, and what data it sends."
- **[V]** Prohibited: "Analytics, usage tracking, hidden network activity, silent update checks, and remote kill switches."
- **[V]** "Plugins must not depend on files from another website that require a login or purchase to download or use."
- **[V]** Nothing names `System.IO`, reflection, `Process` or DLLs/executables specifically; the approach is disclosure-based.
- **Consequences for FMU [L]:** every endpoint FMU contacts (Steam, GitHub/Oxide, Carbon, a panel API) must be listed in the description; update checks must be a documented, user-visible feature, not silent; a bundled native executable must come with source. FMU being open source helps here. Codefling also has a "Tools" category (`https://codefling.com/tools/myrustserver` exists there), which is the natural home for a companion program.

---

## E. Pain points reported by admins

1. **Three layers on three schedules.** "A modded server has three [versions], and they update on three different schedules controlled by three different groups of people." "Force wipe lands and the server does not come back. The game build updated, the modding framework has not caught up, and four plugins are throwing compile errors into a log nobody is reading." [V, secondary guide, 2026-08-20] `https://guildorder.com/games/rust/guides/plugin-stack-governance-and-wipe-day-updates`
2. **Oxide lags the Rust build.** "Oxide (uMod) needs a matching build after every monthly Rust update, and that build usually appears within hours of the update rather than at the same moment." [V, host KB] `https://www.gghost.games/knowledgebase/85/...` — "umod needs updating (sometimes even a couple of times) which could happen 5 minutes after update but also 24 hours after" [V, forum] `https://umod.org/community/rust/15682-automating-wipe-process-with-updates`
3. **Unconditional update-on-start is a liability.** Pterodactyl issue #4767: the only way to update is to restart, and a restart at the wrong moment yields new Rust + old Oxide. [V] `https://github.com/pterodactyl/panel/issues/4767`
4. **A game update overwrites Oxide.** "Facepunch overwrites Oxide files every time the server is updated… you have to reinstall Oxide or your plugins will refuse to load." [V, 2026-04-30] `https://low.ms/knowledgebase/rust-troubleshooting-guide` — Survival Servers documents a four-step toggle-off/toggle-on ritual for this. [V] `https://survivalservers.com/wiki/index.php?title=Install_Oxide_Plugin`
5. **Half-finished updates.** "Forced wipe day in particular likes to leave servers in a half-updated state." [V] same LOW.MS article.
6. **Plugins break after the patch.** "RPC Error in DoPlace… almost always means a plugin is out of date relative to the current Rust build." [V] same LOW.MS article. "Plugins themselves sometimes need updates from their authors after a big patch." [V] GG Host article.
7. **Being awake for it.** Force wipe is the first Thursday at about 19:00 London time; admins in other time zones and admins with jobs cannot attend: "i got a full time job and a kid now… players will leave due to high down time" [V, 2017] `https://oxidemod.org/threads/automating-updates-for-rust-oxide-server.17804/`. The umod thread notes that "no one has packaged up a solution" combining wait-for-Oxide updates with wipe scheduling. [V] umod thread above.
8. **Running vanilla while waiting corrupts plugin state.** "some plugins may rely on the fact that they've been installed since the server was wiped" [V, 2016] same oxidemod thread. Implication: do not let players in on a new map until the framework and critical plugins load.
9. **Host wipe schedulers are date-based, not release-based.** The TCAdmin task fires on the calendar (first Thursday, at whatever hour the daily task runs) [V, B.1]; it does not wait for the Rust build or for Oxide. The only tool I found that waits for the Oxide release is the external `pterodactyl-rust-auto-wipe` [V, A.6].
10. **Seed/map still manual.** With LinuxGSM "manual seed and wipe date changes remain necessary" in typical one-liners [V, umod thread]; on panels the seed is a startup variable the admin must edit [V, A.1].

Not found: I searched for recent (2025–2026) Reddit r/playrustadmins threads on these topics and the search engine returned none, so the forum evidence above is older (2016–2020) while the 2026 evidence comes from host knowledge bases and guides.

---

## Implications for FMU 2.0

### Capability tiers that exist in practice

**Tier 1 — Full shell and process control.** Bare metal/VPS with systemd, NSSM, tmux/screen, batch loops, LinuxGSM, WindowsGSM, self-run Docker.
- A companion process can outlive the game, run SteamCMD, replace framework files, delete saves, edit launch arguments and start the server again.
- Everything is automatable: wait-for-framework gating, staged update, wipe with new seed/map, rollback.
- Integration points worth supporting rather than fighting: LinuxGSM (`./rustserver update|mods-update|map-wipe|full-wipe`, respect its lock files so `monitor` does not race FMU), Docker restart policies (inside a container the companion dies with it — treat self-run Docker as Tier 2 unless FMU runs on the host), WindowsGSM (its 30-minute auto-update will race FMU; tell the admin to disable one of them).

**Tier 2 — Container with self-updating entrypoint, plus a panel API.** Pterodactyl, Pelican and hosts built on them; berrybyte-style and probably RocketNode; self-run `didstopia/rust-server`.
- No process survives the game. A restart *is* the update (Rust + Oxide/Carbon to latest, unconditionally).
- Seed/size/level URL are command-line arguments from panel variables; `server.cfg` edits are overridden by them.
- What a plugin alone can do: detect a new Rust build and a matching framework build over HTTP, announce, save, then `quit` and rely on crash detection to restart-with-update. It can delete save files with `System.IO` before quitting (the identity dir is under `/home/container/server/rust/`), which gives a map wipe on next boot. Risks: crash detection disabled or `detect_clean_exit_as_crash: false` (server stays down), the 60 s rule, the wrapper hang if the game exits before RCON connects.
- What a plugin plus a client API key can do (from inside or from any external machine): proper `restart` power action (not dependent on crash detection), `PUT /startup/variable` to change `WORLD_SEED` / `WORLD_SIZE` / `MAP_URL` / `FRAMEWORK`, file delete for wipes, schedules, reinstall. This is the complete solution for this tier and has prior art.
- What nobody can do here: prevent the entrypoint from installing the newest Oxide on restart. So the **gate must be on the restart itself** — do not restart until the framework build that matches the new Rust build has been published. On force-wipe day that means the server stays on the old build (unjoinable by updated clients) until then; FMU cannot improve on that, only avoid making it worse.
- Carbon simplifies this tier: it self-updates at boot and publishes before the Rust update.

**Tier 3 — Managed panel where restart is not an update (TCAdmin family and custom panels), FTP/file-manager access, no documented API.** Host Havoc, LOW.MS, PingPerfect, Survival Servers, GPortal, Apex, and Shockbyte/Pine in so far as their toggles are off.
- Updates run only through the host's button, toggle or scheduled task. A plugin cannot trigger a Steam update. Whether it can spawn `steamcmd` is unknown and should be assumed forbidden.
- What a plugin can realistically do: (a) detect and notify — new Rust build, framework build available, plugins outdated — by Discord webhook and in-game; (b) prepare a wipe before a restart it does not control: delete map/save/blueprint/plugin-data files on a schedule or when it detects the build changed, so that the host's own scheduled restart/update produces a clean wipe; (c) run post-wipe actions on first boot of a new save (`OnNewSave`); (d) restart the process with `quit`/`restart` **only if** the admin confirms the host restarts it automatically — and that restart will not update anything on TCAdmin-style hosts.
- Seed/map: only if the host leaves them in `server.cfg`. Where the panel forces them on the command line, FMU can only tell the admin what to set, or the admin uses the host's own wipe task (Shockbyte Tasks, Pine "Rust Wipe" task, TCAdmin "Wipe on Rust Wipe Day").
- Best fit: **FMU cooperates with the host's scheduler** (documented recipes per host) and adds what those schedulers lack — release awareness, plugin-data cleanup, verification after boot, notifications.

**Tier 3b — Managed host with a real API.** Nitrado (NitrAPI: restart, stop, command, file server). Could be driven like Tier 2 from an external machine or from the plugin, but I could not verify how Nitrado updates Rust/Oxide, so this needs hands-on testing before promising anything.

### Design points that follow

1. **Make the companion executable optional.** It is only usable in Tier 1. The plugin must be able to do detection, gating, wipe-file deletion and notification by itself, with managed code only.
2. **Add an "external controller" mode**: the same logic running on any machine (or as a small service), driving the server through an adapter — Pterodactyl/Pelican client API first, NitrAPI second, SSH/local shell third. This covers Tier 2 fully and is the only way to get a controlled restart where crash detection is off.
3. **Probe, do not assume.** At first run detect: OS; `/home/container` + `/entrypoint.sh`/`/wrapper.js` (Pterodactyl-family); writable/executable dirs; whether `Process.Start` works; whether seed/size/levelurl appear on the process command line (then `server.cfg` cannot override them); framework (Oxide vs Carbon). Then tell the admin which tier was detected and what will and will not be automated.
4. **Offer a one-time "restart test"** the admin runs deliberately: the plugin quits and records whether the server came back and whether the build/framework changed. That single test answers the two questions no documentation answers per host: "does my host restart me?" and "does a restart update?".
5. **Gate on framework availability, not on the calendar.** Every source in section E points at the same failure: restarting into a new Rust build before Oxide exists. Poll the Oxide.Rust GitHub release (and Carbon's version list) and compare against the Rust build before allowing the restart; make the "start vanilla if the framework is late" behaviour an explicit opt-in because of pain point 8.
6. **Never quit before the server has fully started** on Pterodactyl-family containers (wrapper hang), and keep at least 60 s between two self-initiated exits.
7. **Wipe file sets:** follow LinuxGSM's proven split — map wipe `*.map` + `*.sav*`; full wipe adds `*.db` except `player.tokens.db` — plus a user-defined list of plugin data files (the autowipe egg's `REMOVE_FILES` and the external auto-wiper's per-wipe-type lists are good models).
8. **Distribution:** no sandbox stands in the way technically. Document every network endpoint, keep update checks visible and configurable, ship source for any binary (Codefling). Get an explicit answer from uMod about process spawning before planning a uMod listing; a managed-only plugin mode is the safe submission.

### Open questions to resolve by testing or by asking

- Does `Process.Start` actually work inside a stock Pterodactyl Rust container, and on each commercial host?
- Which commercial "Pterodactyl-like" hosts expose client API keys to customers?
- Nitrado and GPortal: is Rust/Oxide updated on restart? (No readable primary source found.)
- AMP's current Rust template: update stages, framework handling, API.
- uMod's present-day position on plugins that launch executables.
- Per host: are seed/size/level URL forced on the command line?
