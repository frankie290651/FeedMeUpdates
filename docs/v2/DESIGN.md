# FeedMeUpdates 2.0 — Design

Status: **draft, revision 3** (2026-10-04). Nothing here is implemented yet.
It has been through two independent review rounds, both kept in `docs/v2/reviews/`: the first
review's blocker and major findings and the second round's findings are incorporated; the first
review's minor findings remain the backlog for the implementation phases.

Audience: the maintainer, and whoever (human or AI session) implements v2.

Related documents:

- `docs/v1/DEFECTS.md` — consolidated defect register of v1 (D-nnn).
- `docs/v1/KNOWLEDGE.md` — practical knowledge in v1 that v2 must keep (K-nnn).
- `docs/v1/spec/*.md` — line-level behaviour specification of v1.
- `docs/research/*.md` — hosting environments, Rust/Oxide/Carbon technical facts, ecosystem.
- `docs/v2/ROADMAP.md` — phases and gates.
- `docs/v2/TESTING.md` — test strategy and lab plan.

Statements tagged **[LAB-n]** are assumptions that must be confirmed on a real server before the
code that depends on them is considered done. They are listed in `TESTING.md` §5.4.

---

## 1. Goal

One tool that keeps a Rust server updated and wiped on schedule **without the admin being there**,
and that an admin can trust with their files.

Scope by environment, stated honestly:

- **Self-managed machines** (Windows, Linux; services, scripts, containers the admin runs):
  everything in this document.
- **Panel-hosted servers where the host exposes an API or file access** (Pterodactyl/Pelican with
  client API keys first): nearly everything, driven from outside.
- **Rented servers with plugin upload only**: detection, the restart gate, notifications and
  post-wipe work; more where lab tests show the host allows it. FMU cannot install the game or the
  framework there, and says so.

v2 must:

1. Update the Rust server, the modding framework (Oxide, Carbon, or none) and plugins.
2. Run wipes: the monthly forced wipe and custom schedules, with map rotation and plugin-data cleanup.
3. Avoid restarting a server into a combination that cannot work (new Rust build without a matching framework).
4. Verify the server actually came back, and undo what can be undone when it did not.
5. Tell the admin what happened, every time, including when it could not act.

Compatibility with v1 (config, CLI, files, self-update path) is **not** a goal. `fmu migrate`
converts a v1 config once.

### 1.1 Non-goals

- RCON administration, bans, player tools.
- A web panel (a read-only status endpoint may come later).
- Map voting UI (integrate with existing plugins, §12).
- Downloading paid plugins from marketplaces (they sit behind per-user logins, and Codefling's staff
  have said they will not allow third-party automation of it; §9.2).

### 1.2 Invariants

"Perfect" is not testable. These are, and each has a named test suite in `TESTING.md`.

| ID | Invariant |
|---|---|
| INV-1 | **No silent downtime.** Every job ends with the server running and verified, or deliberately held down with a critical alert *attempted on every configured channel and recorded durably*; undelivered alerts are retried and repeated at next start. A job interrupted by a crash of the agent is resumed at the next agent start, which is why the daemon must itself be supervised (`fmu service install`; `doctor` fails otherwise). An optional dead-man's-switch URL covers a dead agent or machine. |
| INV-2 | **No restart against the gate.** FMU never initiates a restart unless its readiness rule (§5.2) holds for the combination that restart will produce. The rule is evidence-based where a build fingerprint is available and a time heuristic otherwise; the health check and the `onBroken` policy bound the damage when it is wrong. |
| INV-3 | **Bounded deletes.** A wipe plan touches only files matched by name inside the configured identity and data roots. Where the files adapter supports rename, deleted files are moved to a trash folder and can be restored within retention; where it does not, the plan marks the delete "permanent" and requires `allowPermanentDelete`. Every plan is printable with `--dry-run` before it runs. |
| INV-4 | **No unbounded wait.** Every external call (process, HTTP, RCON, service manager) has a timeout and an escalation. Waiting for a third party (`keep-waiting`, `hold-and-poll`) is unbounded only by explicit policy; it re-alerts at a fixed interval and shows in `status`. |
| INV-5 | **Resumable.** The plan (including every random or counted choice) is frozen in a journal before the first change; an interrupted job is resumed or rolled back from it. |
| INV-6 | **No shell strings.** Processes are started with argument arrays; batch files are started without arguments; any value placed into a console command or a cfg line passes one sanitiser (no quotes, control characters or newlines). |
| INV-7 | **No secrets in logs or notifications.** |
| INV-8 | **Verified artefacts.** In the agent, TLS validation is on, archives are structure-checked, and FMU's own updates are signature-checked. Inside the game process TLS validation is disabled by the frameworks themselves, so the plugin does not install code by default in plugin-only mode (§10.2). |
| INV-9 | **Dangerous settings are not reachable from the game.** Executable paths and credentials can only be changed in the config file; agent-protocol commands are refused from player connections. |
| INV-10 | **Explicit degradation.** When a capability is missing, `fmu init`, `fmu doctor` and the plan say what will not be done, instead of failing on wipe day. |

---

## 2. Why v1's shape cannot reach the goal

v1 puts the decision logic in an Oxide plugin and the file work in an executable that the plugin
launches half a second before quitting the server (details in `docs/v1/`):

- The brain lives inside the process it is about to kill: it cannot observe the update, cannot
  retry, and reports only on the next boot — if there is one.
- The executable must outlive its parent, which needs per-platform detaching and service-manager
  settings, and cannot work in a container where every child dies with the server.
- The logic sits in a single-file plugin with no tests.
- It assumes Oxide, the `public` branch, SteamCMD as the only build source and a forced wipe
  at a fixed 19:00 UTC (the real rule follows UK local time). All four are wrong or incomplete in 2026 (`docs/research/rust-oxide-carbon-technical.md` §0).

v2 inverts the relationship: **an external agent decides and acts; the plugin is in-game UX**, and
acts alone only where no agent can run.

---

## 3. Architecture

```
                        ┌──────────────────────────────────────────────┐
   sources (HTTP,       │                 FMU Agent (fmu)              │
   Steam PICS)     ───► │  Sources ─► Decision engine ─► Job runner    │
                        │  (builds,    (policy, gate,    (frozen plan, │
                        │   releases)   schedules)        journal)     │
                        │        ┌───────────┬───────────┐             │
                        │   Supervisor     Files      Console          │
                        │    adapter      adapter     adapter          │
                        └────────┼───────────┼───────────┼─────────────┘
                                 ▼           ▼           ▼
                        ┌──────────────────────────────────────────────┐
                        │   Rust server (vanilla, Oxide or Carbon)     │
                        │   optional FMU plugin: countdown UI,         │
                        │   /nextwipe, abort, health report, hooks     │
                        └──────────────────────────────────────────────┘
```

| Deliverable | What it is | Needed when |
|---|---|---|
| **`fmu` agent** | Self-contained executable (win-x64, linux-x64, linux-arm64) and a Docker image. Daemon (`fmu run`) or one-shot commands. | Wherever a program can run: on the server machine, or on another always-on machine for hosted servers. |
| **FMU plugin** | One `.cs` file that compiles on Oxide and Carbon. | Optional with the agent. Required in plugin-only mode. |
| **`Fmu.Core`** | Decision logic with no I/O and no package dependencies. Used by the agent; a subset is bundled into the plugin (§10.3). | Never shipped alone. |

### 3.1 Deployment profiles

| Profile | Where FMU runs | Scope |
|---|---|---|
| **A — Local** | Agent on the server machine | Everything in this document |
| **B — Remote** | Agent on another always-on machine | What the host's API and file access allow: gated restart, wipes, seed/map changes, plugin updates, verification. Requires the host to expose a client API key or file access plus a console channel. |
| **C — Plugin-only** | No agent | Ships as **notify + restart gate + post-wipe work**. Further items (wipe preparation, plugin installs) are enabled per host only after the lab items they depend on (LAB-5, 6, 8, 9, 10, 21) confirm them. |
| **C+ — Plugin with panel key** | No agent; the plugin calls the panel's client API | Covers the gap on Pterodactyl-family hosts (startup variables, real power actions). Supported only with an explicit warning: the key is account-wide and sits in a plugin config. Decision pending (§15). |

### 3.2 Capabilities instead of platform checks

Each server has three adapters; each advertises capabilities; the planner builds the job from what
is available and records every degradation in the plan (INV-10).

| Adapter | Implementations | Capabilities |
|---|---|---|
| **Supervisor** | `managed`, `systemd`, `winservice` (a wrapper-run service: NSSM or WinSW — RustDedicated is not a service binary), `script`, `docker`, `linuxgsm`, `pterodactyl`/`pelican`, `host` | `hold`, `stop`, `start`, `status`, `restart-implies-update` |
| **Files** | `local`, `pterodactyl`, `sftp`, `ftp(s)`, `inprocess` (plugin) | `read`, `write`, `rename`, `delete`, `list`, `exec`, `server-side-archive` |
| **Console** | `webrcon`, `panel-websocket`, `inprocess` | `command`, `read-output`, `events` |

Installers for the game and the framework are one of: `fmu` (FMU runs SteamCMD / installs the
framework), `on-start` (the host's entrypoint does it at every start), `host-action` (a button or
scheduled task only the host can run), `self` (Carbon at boot).

**Stopping is supervisor-specific.** A generic "quit, then ask the supervisor to hold" races every
auto-restarting supervisor, and a generic "stop the unit" is a signal that does not save. Each
supervisor implements one hold-and-stop sequence; this is where v1's service knowledge (K-041…K-059,
K-066…K-077, K-109) lands:

| Supervisor | Hold and stop | Start | Notes |
|---|---|---|---|
| `managed` | set hold flag → `server.save` → `quit` → wait → escalate | agent launches the child | §3.3 |
| `systemd` | `server.save` → `quit` with a unit that does not restart on clean exit (`Restart=on-failure`, `SuccessExitStatus` as generated/checked by `doctor`) → verify inactive; fallback `systemctl stop` (a signal: does not save, **[LAB-4]**) | `systemctl start` | Privilege options: `systemctl --user` + linger (none needed), or a sudoers rule for `start` **and** `stop` of one unit, absolute `systemctl` path (the sudoers rule matches on it), always `sudo -n` (no TTY); a unit that uses sudo needs `NoNewPrivileges=false` |
| `winservice` | `server.save` → `quit` with the wrapper set not to restart on clean exit (NSSM `AppExit` codes, K-057) → verify stopped; fallback wrapper stop **[LAB-16]** | `nssm start` / `sc start` | Needs an elevated agent or a service DACL granting start/stop to the agent's account; Windows recovery actions disabled (K-058). State from `sc query`: pending states count as running; "service not found" is unknown, never "stopped". NSSM is lab-tested; WinSW is not in 2.0. |
| `script` | create the hold file the loop checks → `server.save` → `quit` → verify the process is gone | remove the hold file (the loop restarts the server). FMU launches the script itself only when no loop is running: tmux `new-session -d` running a login shell that execs the script, verified with `has-session -t =<name>` and by the process appearing; otherwise `setsid nohup`. No terminal emulators. | Supported only for loops that honour the hold file (templates shipped); otherwise FMU offers `managed` running the admin's own command line. `.bat` targets get no arguments (INV-6). |
| `pterodactyl` / `pelican` | `server.save` via console → `POST /power stop` (never an RCON `quit`: Wings treats that as a crash and restarts) → poll state | `POST /power start` | During a hold, `GET /resources` is polled; if someone starts the server from outside, the job aborts or rolls forward |
| `docker` | `server.save` → `docker stop -t <measured time>` when the restart policy is `always`/`unless-stopped` (a signal: does not save by itself, **[LAB-4]**, hence the save first); `quit` only with policy `no`/`on-failure` | `docker start` | |
| `linuxgsm` | `server.save` over RCON → `./rustserver stop` (which also stops its `monitor` from restarting the server) | `./rustserver start` | LinuxGSM's own stop does not save |
| `host` | no hold. `server.save` → `quit`, only after the restart test (§11.1) | the host | Only restart-shaped jobs are possible |

Processes are identified by pid from the supervisor, or by `+server.identity`/port on the command
line — never by image name (v1 waits on any `RustDedicated` on the machine).

### 3.3 The `managed` supervisor lifecycle

`managed` (the agent launches RustDedicated itself) removes wrapper scripts and service-manager
quirks, but moves the problem to the agent's own lifecycle. It is designed as follows and becomes the
recommended default **only after** its lifecycle suite (TESTING §3, suite 7) **and the lab lifecycle
scenarios** pass on both OSes.

- The game is started **detached**: own session/process group (Linux) or outside the agent's job
  object (Windows), no inherited pipes, logging to `-logfile`. An agent restart, crash or self-update
  never touches the game.
- State file per server: pid, process start time, identity, launch arguments. On start the agent
  **re-adopts** a live game instead of launching a second one.
- The agent's own unit/service is generated by `fmu service install`: `KillMode=process`,
  `ExecStop=fmu stop --graceful --if-system-stopping`, `TimeoutStopSec` derived from the measured
  save+quit time (LAB-4). The games are stopped only when the OS is shutting down (systemd reports
  the system as stopping; on Windows, the pre-shutdown notification) or when the admin runs
  `fmu service stop --with-games`. A plain stop or restart of the agent leaves the games running, to
  be re-adopted.
- OS shutdown therefore saves; a config that fails to parse at boot starts the games with the
  **last-known-good launch arguments** kept in state and raises an alert.
- Launch environment per framework: Carbon on Linux needs its doorstop variables and `LD_PRELOAD`,
  Oxide needs `LD_LIBRARY_PATH`; children FMU spawns (SteamCMD) get a clean environment.
- `managed` cannot be enabled until RCON passes `doctor` (without RCON every stop is a kill).
- Crash handling: restart with backoff; a crash loop parks the server and alerts.
- Running the agent as a **container entrypoint** (PID 1 duties, graceful stop within Docker's stop
  timeout) is not part of 2.0; it is listed under "Later" in the roadmap.

---

## 4. Sources and state

### 4.1 What is installed

| Fact | Source |
|---|---|
| Rust build id and branch | `steamapps/appmanifest_258550.acf` **[LAB-1]** |
| Rust protocol (`network.save.report`) | `Rust.Global.dll` metadata, or the running server over the console **[LAB-2]** |
| Framework and version | `carbon/managed/Carbon.Preloader.dll` or `RustDedicated_Data/Managed/Oxide.Rust.dll`; both present = broken install, reported |
| Plugins and versions | `[Info(...)]` parsed from each `.cs` with a tolerant parser |
| Identity and save files | listing of `server/<identity>/`; the last identity and command line reported by the running server are persisted and required for a wipe |

Where no file is readable (some profile C hosts) the console gives protocol and framework version
but **not** the Steam build id. There FMU keeps a recorded value: the build id last confirmed by a
file read or by an update FMU itself verified; where a restart is known to update, the newest build
on the tracked branch at the last healthy boot. It is labelled "belief" in `status` and `doctor`,
and the health check skips the build comparison when only a belief exists.

### 4.2 What is available

| Fact | Sources, in order | Notes |
|---|---|---|
| Rust builds per branch | (1) `api.steamcmd.net/v1/info/258550` (URL configurable for a self-hosted mirror); (2) anonymous Steam PICS query from the agent (SteamKit2) **[LAB-22]**; (3) local SteamCMD `app_info_print` after clearing the `appinfo.vdf` cache | (1) is volunteer-run with no SLA. Profile C has only (1) and says so in its status. FMU persists `T_public(B)`: the source's `timeupdated` for the tracked branch when it first shows build B (FMU's own observation time only when the source gives none). |
| Game file fingerprint | Hashes of the assemblies Oxide patches: recorded from disk right after each game update (before the framework is installed), and for a not-yet-installed build from the depot manifest via PICS **[LAB-19, LAB-22]** | Lets the gate recognise a content-only hotfix (§5.2). An adopted server has no recorded fingerprint until its first FMU update. |
| Oxide releases | GitHub API (`published_at`, body, assets); `releases.atom` as the quota-free fallback **[LAB-23]**; "first seen" time as last resort — used as `published_at` only if FMU was already polling that source before `T_avail(B)`, otherwise the release is treated as older than B | `umod.org/games/rust/latest.json` is stale and unused. Optional GitHub token. |
| Carbon releases | `api.carbonmod.gg/releases`; GitHub fallback | |
| Plugin versions | §9 | |

One HTTP layer for all fetchers: timeouts, bounded retries, 429/`Retry-After`, ETag caching, proxy
support, and a **request budget per host** published in the docs (normal vs wipe window) because
hosting IPs share anonymous rate limits (in profile C the GitHub quota is also shared with Oxide's own
version check and with other tenants on the same IP; `doctor` says so). Polling cadence by state:
idle 5–10 min; inside a predicted update window (§5.2) 60 s. Conditional requests are used wherever
the source supports them. A source that goes backwards is ignored and logged. `fmu doctor --network` prints
every endpoint FMU will contact.

---

## 5. Decision engine

Pure functions in `Fmu.Core`: `(installed, available, schedules, policy, history, now) → decision`.

### 5.1 Events

| Event | Recognised by | Default |
|---|---|---|
| **Game update** | New build on the tracked branch | Apply promptly once the gate passes (clients auto-update, so an old build may already be unjoinable) |
| — with save-protocol change (forced wipe) | **Fact** when the protocol of the new build can be read before the job (pre-fetch of `Rust.Global.dll`, profile A/B with exec, **[LAB-22]**); otherwise after the game files are updated; **predicted** from the forced-wipe window. If LAB-22 fails: inside a predicted window → forced wipe; otherwise unknown → treated as mandatory | The forced-wipe profile runs inside the same job |
| — mandatory, no wipe (network protocol change) | Same fact; elsewhere, the server console reporting clients rejected for a protocol mismatch **[LAB-2]** | Short countdown, ignores quiet windows |
| — hotfix, no protocol change | Same fact | May wait for a quiet window, bounded by `maxDeferral`. When the kind is unknown it is treated as mandatory. |
| **Framework update only** | New framework release for the installed build | At next restart window (configurable) |
| **Plugin updates** | §9 | Per-plugin policy |
| **Scheduled wipe / restart** | Cron | See §5.3 |

Detection during a running job never changes that job's plan. A `(server, target build, framework
tag)` that failed N times (default 2) is **parked** until an input changes or the admin runs
`fmu retry`; it is shown in `status` and alerted once (v1 can loop forever, D-020/D-081).

### 5.2 The framework-readiness gate

Facts (`docs/research/rust-oxide-carbon-technical.md` §3, §4, §7):

- Each Oxide build ships patched copies of specific game assemblies, so it fits one Rust build.
  Hypothesis: it also fits a later build in which those assemblies did not change **[LAB-19]**;
  until confirmed, rule 2(a) and the re-apply in rule 4 are off by default.
- In 2025–2026 Oxide usually published 15–45 minutes **before** the Rust build went public on
  forced-wipe days (two publish times verified: 13 and 28 minutes); Carbon documents one hour.
  Late publication, by hours, has happened. So "a newer framework exists" does not mean "install it now".
- The new build has been observed on the Steam `release` branch hours before `public`, but Oxide's
  own docs say `release` is not always in sync. It is a hint, never a requirement **[LAB-3]**.
- Hotfix chains in the 24 hours after a wipe are normal.

Definitions: `B_inst` installed build; `B` newest build on the tracked branch; `T_public(B)` first
report of B on that branch; `T_avail(B)` earliest report of B on any branch (falls back to `T_public`).
The *forced-wipe window* opens `force.leadTime` before the computed forced-wipe time (§5.3) and
closes when a build with a new save protocol is installed or `force.timeout` passes.
A *predicted update window* is the forced-wipe window, plus admin-configured dates
(`updates.announced: ["2026-10-22"]`, for mandatory updates Facepunch announces), plus any time a
build newer than `B` is staged on `release`.

**Rule 0 — never put a newer framework on an older game.** While the newest framework release was
published after a build that is not yet on the tracked branch appeared — or, lacking that evidence,
while the forced-wipe window is open and the game update has not arrived — FMU performs no
framework-only install and, under `on-start` installers, **initiates no restart of any kind**
(scheduled restarts and wipes are deferred with a notification, up to `maxDeferral`; an occurrence
still blocked after that is **skipped and alerted**, never run into the blocked combination). Crashes and
host-initiated restarts in that window cannot be prevented; the health check then reports
"framework ahead of game" specifically.

**Rule 1 — framework-only update.** Allowed when the tracked branch's build equals `B_inst` and
rule 0 does not apply.
When the server is running without its framework (after `go-vanilla` or `onBroken`), the first
framework release with `published_at ≥ T_public(B_inst)` is installed regardless of other branches.

**Rule 2 — game update to B (Oxide).** Ready when one of:

- **(a) fingerprint:** the patched-assembly fingerprint of B equals that of the build the
  installed/cached Oxide release was applied to → that release fits B (content-only hotfix)
  **[LAB-19]**. When the fingerprint differs, is not available, or no fingerprint was recorded for
  the installed build (adopted server, first run), (b)/(c) decide;
- **(b) outside a predicted update window:** an Oxide release with `published_at ≥ T_public(B)`;
- **(c) inside a predicted update window:** an Oxide release with `published_at ≥ T_avail(B)`, and
  `release.buildid == public.buildid == B` at decision time (no newer build staged).

If LAB-19 finds a positive identifier of the target build inside an Oxide release, it replaces (b) and (c).

Release notes naming a protocol are used only as a **veto** after download (a release whose notes
name a different protocol than B's is not for B) — never as a sufficient condition, because the
protocol does not change on hotfixes.

**Rule 3 — the target can move.** The plan records the versions it was made for. If SteamCMD
installs a build newer than the target, the gate is re-evaluated for the installed build from the
stopped state: ready → continue; not ready → with the server held down, wait at most
`framework.maxWaitStopped` (default 15 min) with a critical alert, downloading the framework into
the cache when it appears (the only download allowed while stopped), then
`framework.onTimeoutStopped: go-vanilla (default) | stay-down`.

**Rule 4 — when nothing fits.** Wait up to `framework.maxWait` (default 3 h in the forced-wipe
window, 45 min otherwise), alerting at each step, then `framework.onTimeout`:
`keep-waiting` (default) / `update-anyway` / `go-vanilla`. After a game update, the cached Oxide
release is re-applied **only** if the fingerprint check says the patched assemblies did not change;
otherwise `update-anyway` means starting vanilla. Any vanilla start runs `validate` first and moves
`Oxide.*.dll` out of `Managed`, so no mix of old patched and new vanilla assemblies is ever started.

**Rule 5 — the health check is the judge** (§6.4). If the framework does not load on the new
build: `framework.onBroken` = `hold-and-poll` (default) / `run-vanilla` / `leave-running`.

**Carbon.** Carbon patches in memory and updates itself and its hooks at boot, with its own protocol
guard on the production channel. No pre-gate outside the forced-wipe window. Inside it, wait until
the channel entry's `date ≥ T_avail(B)` (assuming that field is the publish time, **[LAB-3]**),
bounded by `maxWait`. The health check judges
both cases. Rule 0 applies under `on-start` installers, because the stock entrypoint downloads
Carbon unconditionally and bypasses Carbon's guard. FMU does first install and channel switches.

**Branches.** 2.0 supports `public` fully. `staging`/`aux` are supported for vanilla and Carbon with
the health check only; Oxide on non-public branches is experimental until **[LAB-15]**.

### 5.3 Schedules

- Cron with an IANA time zone; any number of named schedules, each bound to `wipe:<profile>`,
  `restart`, `update` or `commands`.
- Time zones: Core takes a zone provider. The agent embeds tz data; the plugin tries the system and
  falls back to built-in rules for the zones FMU's presets use; an unresolvable zone is reported at
  setup (**[LAB-8]**).
- **The forced wipe is not a cron.** `force` opens a window at the computed time (first Thursday,
  19:00 `Europe/London` = 18:00 UTC in summer, 19:00 UTC in winter) minus `leadTime`. Its wipe
  profile runs **inside the update job that installs a build with a changed save protocol**. If no
  such build arrives within `force.timeout`: `force.onNoUpdate: skip | wipe-anyway`, with an alert.
  A scheduled wipe that falls inside an open forced-wipe window is merged into it (no double wipe).
- Per schedule: `ifUpdateBlocked: defer | run-without-update` (deferral bounded by `maxDeferral`, then skip and alert) (the latter refused under
  `on-start` installers); `misfireGrace` — an occurrence missed by more than the grace period (agent
  down, laptop asleep) is skipped and alerted, never run late at peak time.
- Quiet windows and a minimum-players rule for optional work.
- `fmu schedule next` prints upcoming occurrences.

### 5.4 Conditional rules

v1's scheme file becomes a small rule list in the config; `fmu migrate` converts it.

---

## 6. Jobs

One job per server; additional locks per **install directory** and per **SteamCMD directory**, so
two identities sharing an install stop together and two SteamCMD runs never overlap. In profile B
the lock is an owner-stamped file on the server side, so two agents exclude each other.

### 6.1 Standard update / wipe job

| # | Step | Notes |
|---|---|---|
| 1 | **Plan and freeze** | Resolve every choice (target versions, seed or map pick, blueprint counter decision, command timeline) and write the plan to the journal. |
| 2 | **Preflight** | Disk space for snapshot, download **and trash** (and the panel's disk limit where there is one); paths inside allowed roots; credentials; console reachable if the server is running; gate satisfied. **All artefacts are downloaded and verified now** into the cache (framework archive, plugin updates), and the archive listing drives the snapshot. Failure here = nothing touched. |
| 3 | **Announce** | Countdown via the plugin UI or `say`; abortable; the start notification is sent and awaited. |
| 4 | **Hold and stop** | Supervisor-specific (§3.2). Signals only as the last escalation **[LAB-4]**. |
| 5 | **Snapshot** | Targeted: framework files about to be replaced, plugins + configs + data, `cfg/`, optionally the saves. Server-side (`.fmu/snapshots/<job>`, via the panel's archive call in profile B). Never a copy of the whole server folder. |
| 6 | **Update game** | SteamCMD, `-beta <branch>` always explicit, clean environment, streamed output with timeout, known error states handled **[LAB-11]**, success = manifest build ≥ target and fully installed. The hashes of the assemblies Oxide patches are then recorded in state as the fingerprint of that build. Then rule 3. |
| 7 | **Install framework** | From the cache only, per rule 2/4. Carbon: nothing, except first install or channel switch. |
| 8 | **Update plugins** | From the cache (§9). |
| 9 | **Wipe** | If planned, and — for a forced wipe — if the save protocol actually changed (§7). |
| 10 | **Start** | Supervisor. |
| 11 | **Verify** | §6.4. |
| 12 | **Commit or roll back** | Commit advances counters exactly once, prunes snapshots/trash, sends the result. |

Under `on-start` installers steps 6–7 belong to the host's entrypoint. The pre-restart part of a
predicted forced wipe is then limited to **reversible** changes (seed, size, level URL); orphaned
files are removed after boot once the new save protocol is confirmed; if the protocol did not change,
the seed is restored and the admin alerted.

### 6.2 Journal

Step records are written with temp-file + rename + fsync (file and directory); the journal file is
pre-allocated so a full disk cannot prevent the write. On start the agent resumes an unfinished job
from the first unfinished step, using only frozen plan values, or rolls it back.

### 6.3 Rollback

| Thing | Rollback |
|---|---|
| Framework build | Previous release kept in the cache; meaningful only if the game build did not change |
| Plugins, configs, data, cfg edits | From the snapshot, per item |
| Wiped files | From the trash, within retention; refused after a save-protocol change (the new build cannot load them) and requires the server stopped |
| Game build | **Not in 2.0.** Whether older manifests can be fetched (as some tools advertise) is **[LAB-18]**. After a protocol change the old build is useless anyway. A failed game update is retried, then parked with an alert — never "restored" by copying the whole install back, as v1 attempts. |

`fmu snapshots list | restore | prune` exposes the snapshots as the backup feature, with retention
by age and count ("keep last N pre-wipe").

### 6.4 Health check and watchdog

Healthy = process running; console answers; startup complete; build and protocol as expected;
framework loaded with the expected version; plugin report (failures reported, **critical plugins**
fail the job); optionally the query port answers.

Deadlines are **progress-based** (log activity, phase markers), because map generation and large
plugin sets take long. The watchdog never restarts on console silence alone before startup
completed, and tolerates long saves.

---

## 7. Wipe engine

### 7.1 Profiles

```jsonc
"wipeProfiles": {
  "monthly": {
    "map": true,
    "blueprints": "every-2nd",      // true | false | "every-Nth" | ["2026-12"] | "when-forced"
    "playerData": false,
    "rustPlus": false,
    "pluginData": "profile:default",
    "next": { "map": "pool:main", "hostname": "MyServer | Wiped {wipe:dd/MM}" },
    "commands": [ { "after": "0s", "run": "o.reload Kits" }, { "after": "10m", "run": "say Welcome" } ]
  }
}
```

`"when-forced"` means: the game created a new blueprint database on its own; FMU only removes the
orphan (**[LAB-17]** establishes how that is recognised).

### 7.2 File rules

- Patterns match **file names** inside `server/<identity>/` and the framework data folder. Globs,
  never hard-coded protocol numbers; SQLite sidecars go with their database.
- The identity folder must look like one, and must be the identity last reported by the running
  server; never observed = refuse.
- **Trash** is `<server root>/.fmu/trash/<job>/`, reached through the files adapter (local move,
  panel rename, SFTP rename) so it is always on the server's volume. A cross-volume move is refused
  unless `trash.allowCopy`. Retention by age **and** size/count; on quota-limited hosts the default
  is "keep the last wipe only".
- `fmu wipe undo <job>` requires the server stopped, refuses after a save-protocol change, and moves
  colliding new files into a second trash entry.
- `fmu wipe --dry-run` prints the files, sizes, config changes and commands.

### 7.3 Next map

Sources: fixed seed, random seed, seed pool, level-URL pool, a map-vote plugin's choice, later
RustMaps (with timeout and fallback to the next source). No-repeat memory. Seed and size are
range-checked. `server.levelurl` and `seed`/`worldsize` are mutually exclusive, and an existing
level URL is never removed unless a replacement is configured (v1 does, D-059).

The value is written **where the startup parameters actually live**, found by the probe (§11.1):
`server.cfg`, the `managed` launch arguments, an args/env file for the admin's script or unit, a
panel startup variable, or a Docker env file — in exactly one place, with a warning when the same
setting is found elsewhere **[LAB-5]**.

### 7.4 Plugin data

Keep/delete globs per plugin, presets for common plugins, different lists per profile. Deleted while
the server is down when possible. Otherwise by the plugin on `OnNewSave` **[LAB-6]**, and only when
FMU expected that wipe: `onUnexpectedNewSave: ignore | notify (default) | clean`, because a new save
also appears on first boot, after a restore or a seed change, and "will it wipe on every restart?"
is a standing fear among admins. Plugin-side cleanup moves files to `.fmu/trash` like every other
delete. If LAB-6 shows the owning plugins have already loaded their data, FMU either reloads those
plugins after the cleanup or defers it to the next stop; LAB-6 decides which.

### 7.5 Surroundings

- `server.cfg` editor that preserves everything it does not change, edits the last occurrence of a
  key, quotes correctly and replaces atomically (with bounded retry on Windows sharing violations).
- Hostname/description templates.
- Syncs Facepunch's wipe timer convars from the schedule **[LAB-7]**; exposes the next wipe through
  the hook existing wipe-panel plugins consume.
- Detects Carbon's AutoWipe module and common wipe/restart plugins and warns about double actions.

---

## 8. Agent

### 8.1 Technology

| Choice | Decision |
|---|---|
| Runtime | .NET 10 (LTS), self-contained single-file. (.NET 8, used by v1, leaves support in November 2026.) |
| Dependencies | Cron parsing and tz data in Core as source; in the agent only: SteamKit2 (PICS), an SSH/SFTP/FTP library, and the platform's Ed25519 implementation or a small vetted library for release signatures |
| Config | One JSON file (comments allowed) + JSON Schema; secrets in a separate file or environment variables |
| Processes | Argument arrays; async stream reading; timeout → terminate tree; clean environment for children |
| Logging | Structured + human; rotation; redaction |
| State | `<config dir>/fmu/` for agent state, cache and logs; `<server root>/.fmu/` for snapshots, trash and the server-side lock |

### 8.2 Config lifecycle

`schemaVersion` in the file. The agent **never writes the config at runtime**: counters, no-repeat
memories and learned facts live in state. An older schema is migrated in memory and reported; the
file is rewritten only by `fmu config migrate`, which keeps a backup. Unknown keys are errors (typos must not be silently ignored); `fmu config validate`; reload on
`fmu reload` only, never mid-job; a config that fails validation leaves the previous one in effect.

### 8.3 Commands

```
fmu init | doctor [--network] | config validate|migrate | reload | steamcmd install
fmu run | service install|uninstall|stop [--with-games]
fmu status | check | history | schedule next
fmu update [--dry-run] | restart | stop [--graceful] | start | abort | retry
fmu wipe <profile> [--dry-run] | wipe undo <job>
fmu snapshots list|restore|prune
fmu plugins status|update|rollback <name>
fmu migrate <v1-config> | self-update
```

Meaningful exit codes; `--json` everywhere.

### 8.4 First run

`fmu init` finds the install (running process, common paths, or asks), reads RCON settings from the
command line/cfg, locates or offers to download SteamCMD, lists prerequisites that are missing
(SteamCMD's 32-bit libraries on Linux, `curl` for Carbon's self-updater), checks that it runs as the
server's user (never root; files it writes must be owned by that user), proposes a profile and
supervisor, and **adopts a running server without restarting it**. On Windows it explains the
unsigned-executable warning until releases are code-signed (§15).

### 8.5 Multi-server

`defaults` + `servers[]`. Locks per install and per SteamCMD directory from the first version that
acts (phase 2). Staggering, `maxConcurrent` and canary rollout in phase 8. One daemon handles many
servers; with `managed`, games are detached, so an agent failure does not take them down.

### 8.6 Notifications

Sent by the agent at the time of the event. Discord webhook and generic webhook; severity levels;
durable outbox with retry (INV-1); optional dead-man's-switch ping.

### 8.7 FMU self-update

Off by default and explicit. Signed release assets (Ed25519, key compiled in); atomic swap keeping
the previous binary; restarts only the agent, never the games.

---

## 9. Plugin management

### 9.1 Policies

Per plugin: `ignore`, `notify`, `stage` (apply at the next restart/wipe window), `install` (apply now).

### 9.2 Sources

| Source | Check | Download |
|---|---|---|
| uMod | `plugins/<slug>.json`, search to resolve slugs; serial, cached, honours 429 **[LAB-13]** | Yes |
| GitHub | Declared per plugin | Yes |
| Lone.Design, Codefling | Lone.Design's public `plugins.json` (verified); Codefling's catalogue as used by Carbon's admin module (not read directly, to be confirmed in phase 7) | No — notify only |
| **Inbox folder** | — | The admin drops a file; FMU installs it at the configured moment with backup. This is how paid plugins are automated. |

Matching uses title, author and file name together. Downloads are validated (C# plugin with an
`[Info]`, version not lower, not an HTML page).

### 9.3 Safety

Previous source + config + data kept per plugin; atomic replace; compile result awaited; automatic
per-plugin rollback on failure; optional quarantine of plugins broken by a game update; audit history.

---

## 10. The plugin

### 10.1 With the agent

Countdown UI, `/nextwipe`, abort (permission-gated), health report, `OnNewSave` work, status commands.

Protocol over the console adapter:

- Arguments are **one base64url token**; replies are one line `FMU1 <id> <base64url>`; `fmu.hello`
  negotiates the version. Raw JSON does not survive Rust's console parser (v1 already had to encode,
  K-152/K-153). Maximum message size and round-trip behaviour on vanilla, Oxide, Carbon and through
  the Pterodactyl wrapper: **[LAB-20]**.
- Agent-protocol commands are accepted **only from RCON or the server console**, never from a
  player connection; file operations are re-validated plugin-side against the configured roots (INV-9).
- Console channel in profile B: the panel's console websocket (wss) first. WebRCON carries the
  password in the URL over plaintext, so across the internet it is used only through a tunnel or with
  an explicit `allowPlaintextRcon`; `doctor` states the exposure. Where the channel has no
  `read-output` (panel `POST /command` returns nothing), the plugin's health report is unavailable
  and the plan says so.

Without the plugin the agent uses `say` and the game's own facilities.

### 10.2 Without the agent (profile C)

- Sources over HTTP on worker threads; the single build source is stated in status.
- The restart gate (rules 0–2 with time heuristics only).
- Restart by `server.save` + `quit`, only after the restart test, never before startup completed,
  never twice within the host's crash-loop window, and parked after repeated failures.
- `OnNewSave` cleanup and command timeline.
- Notifications.
- **Plugin installs are off by default**: both frameworks disable TLS certificate validation for
  the whole process, so the plugin would be installing code over an unauthenticated channel. Default
  is `notify`/`stage`; `install` is offered only if **[LAB-8]** shows the plugin can use its own
  validating HTTP handler.
- Wipe preparation per host only where LAB-5, 6, 8, 9, 10 and 21 allow.
- The plugin does not spawn processes unless a lab-validated feature needs it (**[LAB-9]**); if
  none does, `System.Diagnostics` is not referenced at all.

### 10.3 One file, tested code

```
src/Fmu.Core/     decision engine, schedules, parsers, wipe planner — no package dependencies,
                  its own small JSON reader, tz rules and cron parser as source
src/Fmu.Plugin/   Oxide/Carbon glue
tools/bundle      deterministic bundler → dist/FeedMeUpdates.cs
```

The bundled file is compiled by a different compiler and runs on Unity's Mono, so tests on .NET do
not prove it. Therefore:

- Core is restricted to a **bundle-safe subset** enforced by an analyzer; namespaces are kept if
  both plugin compilers accept them **[LAB-8]**, otherwise the subset excludes what flattening breaks.
- **L3b in-game self-test**: `fmu.selftest` runs the Core test vectors inside a real server, on
  Oxide and Carbon, on both OSes; CI also runs the Core tests on Mono.
- The CI compile against Oxide/Carbon references is a smoke test; the authoritative compile is the
  lab's, on both frameworks, for every release.

---

## 11. Rented and managed hosting

Sources and their confidence levels: `docs/research/hosting-environments.md`. Everything below about
commercial hosts is **documentation-based until tested on a real rented server** (TESTING §5.5).

### 11.1 Probe and restart test

- **Probe** at setup: OS; container markers; writable directories; whether seed/size/level URL are
  on the process command line; framework; panel API reachable; time-zone resolution.
- **Restart test**, run deliberately by the admin: FMU quits the server and records whether it came
  back and how long it took. Whether a restart *updates* is inferred from side evidence (framework
  file timestamps, Steam manifest and log timestamps); if nothing was pending the answer is stored as
  **unknown** and re-evaluated at the first restart with a pending update.

### 11.2 What is expected per family

| Family | Restart = update? | Profile B | Profile C |
|---|---|---|---|
| **Pterodactyl / Pelican** (stock Rust egg) | Yes with the stock egg's default (verified in the entrypoint source); a host can turn it off, so the restart test decides. Pelican is covered by the fake panel and documentation only until a Pelican lab cell exists. | Power actions, files, startup variables, console: the complete flow, **where the host exposes client API keys** (many branded hosts hide them or restrict by IP) | Restart gate and `quit` (Wings restarts it under default crash-detection settings). A custom-schedule wipe needs a way to change the map: if the egg pins seed/size on the command line and LAB-5/LAB-10 come back negative, profile C **cannot** do scheduled wipes there; C+ or profile B can. |
| **Update-on-restart custom panels** (Shockbyte, Pine; `didstopia` Docker) | Per host toggle (documented by the hosts) | File access + console where offered | Restart gate; coexist with the host's own wipe scheduler |
| **TCAdmin-based hosts** (LOW.MS verified; Host Havoc, PingPerfect likely) | No: updating is a button or scheduled task | Only where the host offers FTP/FTPS **and** a reachable RCON port (not documented for these hosts): plugin work; wipes only as restart-shaped jobs, or with the server stopped by the admin in the panel. Otherwise profile C. Cannot run the Steam update. | Notify ("new build out, framework ready — press Update now"); clean orphans and plugin data after the host's wipe; verify afterwards. Wipe preparation only if **[LAB-21]** allows it on Windows. |
| **Survival Servers** | Optional update on restart (documented) | FTP | As the row its setting matches |
| **GPortal, Nitrado, Apex and others** | Unknown | Nitrado has a real API (adapter planned after Pterodactyl) | Notify + post-wipe until tested |

On a host where only the host can press "update", no tool can update the game. FMU's value there is
knowing when to press it, doing the work around it, and checking the result.

### 11.3 Container cautions

Never quit before startup completed (the stock wrapper hangs if the game exits before RCON is up);
keep the crash-loop window between self-initiated exits **[LAB-14]**; a client API key is
account-wide and belongs in the agent's secret file.

---

## 12. Integrate, do not reinvent

Carbon self-update (verify only); Carbon AutoWipe and wipe plugins (detect, one owner of the
schedule); Facepunch wipe-timer convars (write them); the wipe-info hook (expose through it);
map-vote plugins (accept their choice); LinuxGSM (adapter); restart plugins (detect, warn); Rust's
own save backups (keep, add config/plugin snapshots around them).

---

## 13. Security and distribution

- Archive extraction is traversal-checked; the files adapter refuses paths outside the server root;
  secret files are permission-checked.
- Least privilege: a dedicated non-root user on Linux; `systemctl --user` or a one-unit sudoers rule;
  on Windows an elevated service or a service DACL for one service. Bounded retry around Windows
  sharing violations (antivirus, the frameworks' file watchers).
- Remote console exposure is stated by `doctor` (§10.1).
- Marketplace rules: every endpoint documented; no analytics; self-update opt-in; source public.
  Oxide removed its plugin sandbox in 2023 (confirmed in its compiler source), but uMod's security
  guide still lists blocked namespaces, so its listing policy on file/process APIs must be asked
  before planning a uMod listing.
- Releases are built by CI only, with checksums and signatures. MIT licence.

---

## 14. Repository layout (target)

```
src/      Fmu.Core, Fmu.Agent, Fmu.Adapters.*, Fmu.Plugin
tests/    Fmu.Core.Tests, Fmu.Agent.Tests, fakes/, fixtures/
tools/    bundle/, lab/, recorder/
docs/
legacy/   v1 sources, for reference until 2.0 ships
```

---

## 15. Open decisions for the maintainer

1. **Start the timeline recorder now.** It stores the raw Steam, Oxide and Carbon responses. Primary:
   a cron job or systemd timer on the lab machine (every minute in predicted windows, every five
   minutes otherwise). Backup: a scheduled GitHub Actions workflow using the repository token — it
   cannot poll every minute and only runs from the default branch, so its workflow file would be the
   single v2 file committed to `main`. The gate has no real test data until this runs; the next
   events are the 2026-10-22 mandatory update and the 2026-11-05 forced wipe.
2. **Rent test servers** for a month: one Pterodactyl-based host, one TCAdmin Windows host, one
   custom-panel host. Without them every rented-hosting claim stays documentation-based.
3. **Plugin with panel key (C+)**: support it with the warning, or not at all.
4. **`managed`** as the recommended supervisor once its lifecycle tests pass.
5. Executable name (`fmu`), config format (JSON with comments).
6. Code signing for Windows releases (cost) vs documenting the warning.
7. Where to publish: GitHub always; Codefling Tools; uMod only after asking its moderators.
8. Release order (ROADMAP): previews from phase 2; whether hosted-server support comes before plugin management.
