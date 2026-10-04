# FeedMeUpdates 2.0 — Test strategy and lab plan

Status: draft, revision 3 (2026-10-04). Companion to `DESIGN.md`.

The rule for v2: **a behaviour is done when a test proves it**, at the cheapest level that can prove
it. Logic is proven by unit tests; orchestration by component tests against fakes; anything that
depends on how Rust, Steam, Oxide, Carbon, an OS or a panel really behaves is proven in the lab on
real servers, and the observation is then frozen into a fixture so the cheaper levels keep covering it.

## 1. Levels

| Level | Runs where | What it covers | Speed |
|---|---|---|---|
| **L1 Unit** | CI (Linux + Windows) and dev machine | `Fmu.Core`: decision engine, readiness gate, schedules, parsers, wipe planner, cfg editor, version comparison, config validation | seconds |
| **L2 Component** | CI (Linux + Windows) and dev machine | Agent orchestration against fakes: full jobs, journal recovery, rollback, adapters | a few minutes |
| **L3 Plugin build (smoke)** | CI | Bundled `FeedMeUpdates.cs` compiles against Oxide and Carbon references with CI's compiler; Core tests also run on Mono | a minute |
| **L3b In-game self-test** | Lab | `fmu.selftest` runs the Core test vectors inside a real server, compiled by Oxide's and by Carbon's own compilers, on both OSes. This is the authoritative check of the plugin's logic. | minutes |
| **L4 Lab end-to-end** | The maintainer's Windows and Ubuntu machines, driven over SSH | Real servers: every supervisor, framework and profile in the matrix below | minutes to an hour per scenario |
| **L5 Live observation** | Lab, on real wipe days and real mid-month updates | The things nobody can simulate: Facepunch's actual timing and the frameworks' actual release order | calendar-bound |

## 2. L1 — unit tests

- Fake clock everywhere; no test reads the real time.
- **Schedules**: force-wipe preset for every month 2022–2030 against a hand-checked table
  (18:00 UTC in BST months, 19:00 UTC in GMT months, including the first Thursdays that fall between
  the UK and US clock changes); DST gaps and overlaps in several zones; cron edge cases.
- **Readiness gate (INV-2)**: there is **no recorded history** of Steam branch times or Carbon publish
  times to replay (the research holds only Oxide commit times). Test data comes from two places:
  (1) the **timeline recorder** (`tools/recorder`, phase 0) that stores raw Steam, Oxide and Carbon
  responses every few minutes and every minute inside update windows — each real event becomes a
  fixture; (2) synthetic timelines for every rule in DESIGN §5.2: framework published before the
  game (rule 0), framework late by hours, framework never published, content-only hotfix with no
  framework release (fingerprint equal), hotfix chains where a rebuild for the previous build is
  published while the next build is staged, `release` branch out of sync or never updated, rebuild
  tags with empty notes, target moving mid-job (rule 3), source outage, source going backwards,
  Carbon on forced-wipe day and on a mid-month update, a parked target.
- **Decision table**: every combination of {game changed, protocol changed, framework changed,
  framework ready, schedule due, policy} → expected decision. v1's 11-row table and scheme rules are
  imported as cases so `fmu migrate` can be shown to preserve intent.
- **Parsers** with real-world fixtures: `appmanifest_258550.acf`, `app_info_print` output,
  api.steamcmd.net JSON, GitHub release JSON, Carbon release JSON, uMod plugin/search JSON,
  `[Info(...)]` attribute variants (including the forms v1's regex misses), version strings
  (including single-number versions that hang v1).
- **SteamCMD transcript classifier**: recorded outputs for success, already-up-to-date, `0x6`,
  `0x202`, `0x402`, `0x602`, `0x606`, timeouts and truncated output → expected classification and action.
- **Wipe planner (INV-3)**: given a directory listing and a profile → exact move list. Hostile cases from
  the v1 defect register: server path containing `.sav`/`.data`, empty identity, identity with path
  separators, symlinks, plugin-data names with `..`.
- **`server.cfg` editor**: property-based round-trip (untouched lines are preserved; a BOM is
  removed), duplicates, comments, mixed line endings, quoting of values with whitespace, rejection
  of quotes and line breaks in values.
- **Redaction (INV-7)**: no secret from a config fixture ever appears in any log line or notification produced by any test (checked globally by the test harness).

## 3. L2 — component tests with fakes

Fakes live in `tests/fakes` and are real programs/servers, so the agent is exercised through its
real process, HTTP and socket code:

| Fake | Behaviour it can script |
|---|---|
| `fake-steamcmd` | Prints a chosen transcript, takes a chosen time, exits with a chosen code, optionally hangs or dies mid-way; on "success" mutates the fake server tree and manifest |
| `fake-rustserver` | A small process that looks like RustDedicated to the supervisors, including the console argument parser as recorded in LAB-20: opens a WebRCON port, answers `serverinfo`/`version`/`quit`/`server.save`, can refuse to quit, quit without saving, crash on start, start slowly, report plugin compile failures |
| Fake HTTP origin | Serves Steam/GitHub/Carbon/uMod fixtures; can return 429 with `Retry-After`, 5xx, slow responses, truncated bodies, wrong content (HTML instead of zip) |
| Fake panel | Pterodactyl client API subset: power, command, files, startup variables; includes the crash-detection rules |
| Fake server tree | Generator for a realistic install: Managed folder, Oxide or Carbon layout, identity folder with correctly named files, plugins with data and configs |

Scenario suites:

1. **Happy paths** per profile and supervisor: update, update + wipe, wipe only, restart, plugin update.
2. **Fault injection at every journal step**: the agent is killed after each step boundary and
   restarted; assert INV-1 and INV-5 (server ends up running and verified, or held down with a
   critical alert; no lock left; journal closed). This suite is generated from the step list, so a
   new step is automatically covered.
3. **Every external failure**: SteamCMD failure classes, download corruption, disk full, permission
   denied, RCON refused, service manager errors, server that never becomes healthy → expected
   rollback or escalation, and expected notification.
4. **Timeouts**: every fake can hang; assert INV-4 (the job always ends within its budget).
5. **Safety**: zip-slip archives, paths escaping the server root, arguments containing quotes,
   spaces, `;`, `$()` and non-ASCII (INV-6); dry-run produces zero writes (verified by comparing
   tree hashes before and after).
6. **Multi-server**: (6a, phase 2a) locks per install directory and per SteamCMD directory, two
   identities on one install; (6b, phase 8) staggering, canary stop.
7. **`managed` lifecycle**: agent killed and restarted while the game runs (and, from phase 8, self-updated)
   (re-adoption, no second launch); OS shutdown path; config that fails to parse at boot
   (last-known-good launch); crash loop parking. Gate for making `managed` the default.
8. **INV-8**: tampered archive, tampered self-update, wrong signature, interrupted binary swap.
9. **INV-9**: agent-protocol commands sent from a player connection are refused; no command changes
   executable paths or credentials.
10. **INV-10**: `doctor` output and plan degradations checked against a capability matrix for each
    adapter combination.
11. **Schedules in jobs**: misfire grace, forced-wipe window merging, `ifUpdateBlocked`, `onNoUpdate`.

## 4. L3 and L3b — the plugin

- The bundler is deterministic: same inputs → byte-identical `FeedMeUpdates.cs` (checked in CI).
- An analyzer keeps `Fmu.Core` inside the bundle-safe subset (no package dependencies, no APIs
  missing from the game's runtime).
- **L3 (CI, smoke):** the bundled file is compiled with Oxide's and with Carbon's symbols and
  references (fetched in CI, cached, never committed), and the Core unit tests are run on Mono. CI's
  compiler is not Oxide's or Carbon's, so this catches gross errors only.
- **L3b (lab, authoritative):** the bundled plugin is loaded on real Oxide and Carbon servers on
  both OSes and `fmu.selftest` executes the Core test vectors in-process: schedules and time zones,
  parsers, gate rules, version comparison. Required for every release.

## 5. L4 — the lab

### 5.1 Machines

Provided by the maintainer and reached over SSH from the development session:

| Machine | Role |
|---|---|
| **Ubuntu** | Linux servers; Docker; a local Pterodactyl panel + Wings to reproduce rented hosting; LinuxGSM instance |
| **Windows** | Windows servers; NSSM-wrapped service; batch loop with hold file |

Requirements to confirm before starting (measured, not assumed, during phase 0): RAM and disk per
Rust instance with a minimal map, how many instances can run at once on each machine, SSH with key
auth, an account allowed to manage services (sudo on Ubuntu; administrator on Windows), OpenSSH
Server enabled on Windows.

Lab servers always use a small world size and a non-default port range, are never listed publicly
(`server.tags`/query settings as needed), and have no players.

### 5.2 Matrix

Each cell is a scripted scenario set in `tools/lab`, runnable unattended, that leaves a report.

| Dimension | Values |
|---|---|
| OS | Ubuntu, Windows |
| Supervisor | `managed`, systemd, NSSM service (WinSW is not lab-tested in 2.0), script loop with hold file (tmux / batch), Docker, LinuxGSM, Pterodactyl (local panel) |
| Framework | vanilla, Oxide, Carbon |
| Profile | A local, B remote (agent on the other machine), C plugin-only |

Not every combination is meaningful. `ROADMAP.md` enumerates the cells that gate each phase; a gate
refers only to those named cells.

### 5.3 How real updates are simulated

- **Game update with a save-protocol change (a real forced wipe, on demand):** install the
  `last-month` Steam branch, then let FMU update to `public`. **[LAB-12]** confirms this reproduces
  a force-wipe transition (different save protocol, new map generated).
- **Framework update:** install an older Oxide release, or switch Carbon channel/version, then run FMU.
- **Framework not ready:** point the Oxide source at the fake HTTP origin that withholds the release,
  then releases it after N minutes.
- **Plugin updates:** install old versions of a few free uMod plugins; add a plugin that fails to compile.
- **Time:** the agent's clock is injectable (`--now`, lab only) for schedule scenarios.
- **Failures:** kill the agent mid-job (`kill -9` / `taskkill /F`), fill the disk with a quota'd
  volume, revoke permissions, block the network with a firewall rule, make the server hang at boot.

### 5.4 Validations of assumptions

These are facts the design relies on that could not be confirmed from documentation. Each gets a
small lab script, its result is written back into `docs/research/` and frozen as a fixture.

| ID | Question | Needed by phase |
|---|---|---|
| LAB-1 | `appmanifest_258550.acf`: are `buildid`, `StateFlags` and the branch key reliable on both OSes, including right after a failed update? | 1 |
| LAB-2 | Can the protocol (`network.save.report`) be read from `Rust.Global.dll` without loading it, and does it match `version`/`serverinfo` over RCON?; can any console value be mapped to the Steam build id; what the server console prints when a client with a newer protocol is rejected | 1 |
| LAB-3 | Readiness rules against reality: publish order of Steam `release`/`public`, Oxide and Carbon on real forced-wipe, mandatory and hotfix days (recorder + L5; first events 2026-10-22 and 2026-11-05); whether Oxide publishes before `public` on mid-month mandatory updates; whether a build has the same id on `release` and `public`; format and meaning of the Carbon channel entry's `date` | 0 onwards |
| LAB-4 | Does the server save on SIGINT, SIGTERM (Linux), Ctrl+C and console close (Windows)? How long do `server.save` and `quit` take on a populated save? | 2a |
| LAB-5 | Precedence between command-line `+server.seed/worldsize/levelurl`, `server.cfg` and `serverauto.cfg` | 3 |
| LAB-6 | When does `OnNewSave` fire relative to other plugins loading their data, on Oxide and on Carbon? Can FMU's cleanup run first? | 4b |
| LAB-7 | Exact names and effects of the `wipetimer.*` convars on a current build (`find wipe`); is there any native auto-wipe? | 3 |
| LAB-8 | What the plugin can use inside the game's Mono runtime on both frameworks: C# level and extra namespaces accepted by both compilers, `System.IO.Compression`, HTTP on worker threads with its own TLS validation, SHA-256, IANA time-zone resolution (Windows and Linux) | 4a |
| LAB-9 | Can a plugin spawn a process inside a stock Pterodactyl Rust container? Does anything survive the game exiting? | 6 |
| LAB-10 | Plugin-only wipe preparation in a container: what can be deleted or changed before `quit` so that the next boot is a clean wipe? | 6 |
| LAB-11 | SteamCMD exit codes and final output lines on success and on each failure class, Windows and Linux | 2a |
| LAB-12 | `last-month` → `public` as a simulated force wipe | 2a |
| LAB-13 | uMod's current rate-limit headers and limits on `plugins/<slug>.json` and `search.json` | 7 |
| LAB-14 | Wings crash detection with a plugin-initiated `quit`: restart delay, the 60-second rule, the wrapper hang when quitting before RCON is up | 5 |
| LAB-15 | Public download URL of Oxide's non-`public` branch builds (staging/release) | later |
| LAB-16 | Windows service stop semantics: `sc stop` vs NSSM console signal vs process kill; which let the server save | 2c |
| LAB-17 | Exact file names in `server/<identity>/` on the current build, for procedural and custom maps; which carry the save protocol number; how a Facepunch blueprint wipe shows up | 3 |
| LAB-18 | Can an older game build be fetched (old depot manifests)? Decides whether game rollback is ever possible | later |
| LAB-19 | A positive identifier of the Rust build an Oxide release targets: depot-manifest hashes of the patched assemblies, release asset metadata, or a constant in the patched DLL | 1 |
| LAB-20 | Agent↔plugin payloads through the console: maximum size and round-trip integrity on vanilla, Oxide, Carbon and through the Pterodactyl wrapper; the console argument parser's exact rules | 4a (vanilla, Oxide, Carbon); 5 (Pterodactyl wrapper) |
| LAB-21 | What a plugin can move/delete in-process on **Windows** (open `.sav`, SQLite files) | 6 |
| LAB-22 | Anonymous Steam PICS from the agent (SteamKit2): branch build ids, depot manifests, fetching single files such as `Rust.Global.dll` before the update | 1 |
| LAB-24 | Contents of Carbon's `.info` release assets (a hash or version usable for verification) | 2b |
| LAB-23 | `releases.atom` for Oxide: timestamps available, no API quota; whether conditional requests (ETag, 304) count against the API quota | 1 |

### 5.5 Real rented servers

A local Pterodactyl panel proves how the stock egg and Wings behave, not how a commercial host is
configured, and TCAdmin-style hosting cannot be reproduced locally at all. Before any claim about a
hosting family is published as tested, FMU is run for a month on one rented server of each kind:
a Pterodactyl-based host, a TCAdmin Windows host, a custom-panel host. Until then DESIGN §11 is
labelled documentation-based. On each rented server: the probe, the restart test, LAB-10 and LAB-21,
and (where FTP and RCON are offered) one profile B wipe job; the results are published as the
"what works where" table. (Renting is the maintainer's decision, DESIGN §15.)

## 6. L5 — live observation

- **Recorder** (phase 0, before 2026-10-22): raw responses of api.steamcmd.net, the Oxide release
  list and api.carbonmod.gg, every five minutes and every minute inside predicted windows, stored
  with timestamps. It needs no FMU code; it runs as a timer on the lab machine, with a GitHub
  Actions workflow as a coarser backup (DESIGN §15).
- **Observe mode** (from phase 2b): a lab instance per framework where FMU decides and logs but
  never acts, next to one that acts.
- After each real event the timeline and FMU's decisions become L1 fixtures. The readiness gate is
  labelled "validated" only after it has been right on at least two forced wipes and two mid-month
  updates; until then the default policies stay conservative.

## 7. CI and release

- GitHub Actions on `ubuntu-latest` and `windows-latest`: build, L1, L2, L3 on every push and PR.
- Release workflow: self-contained builds for each platform, checksums, signature, bundled plugin,
  Docker image; nothing is released from a developer machine.
- A release candidate is promoted only after the lab matrix cells required for its phase are green
  and the report is attached to the release.

## 8. What tests cannot promise

- Facepunch can change file names, branch names, timing or protocol handling in any update. The
  parsers fail closed (unknown → do nothing, alert), and L5 keeps the fixtures current.
- Commercial hosts differ and change. The probe and restart test (DESIGN §11.1) exist because no
  amount of testing on a local Pterodactyl panel proves how a specific host is configured.
