# FeedMeUpdates 2.0 — Roadmap

Status: draft, revision 3 (2026-10-04). Companion to `DESIGN.md` and `TESTING.md`.

Each phase ends with something usable and a gate that names exactly what must be green. Work happens
on feature branches off an integration branch (`v2`, to be created from `v2-design` once the design
is agreed), merged by pull request. `main` keeps the v1 sources until 2.0 ships; the only v2 addition
there may be the recorder's workflow file. Preview builds
are published from phase 2a onwards so that real admins can run v2 before the final release.

## Lab cells

A cell is `OS / supervisor / framework`. Gates below refer to these names only.

| Cell | OS | Supervisor | Framework |
|---|---|---|---|
| U-man-ox | Ubuntu | `managed` | Oxide |
| U-man-ca | Ubuntu | `managed` | Carbon |
| U-man-va | Ubuntu | `managed` | vanilla |
| U-sysd-ox | Ubuntu | systemd | Oxide |
| U-loop-ox | Ubuntu | script loop (tmux) with hold file | Oxide |
| W-man-ox | Windows | `managed` | Oxide |
| W-man-ca | Windows | `managed` | Carbon |
| W-svc-ox | Windows | NSSM service | Oxide |
| W-loop-ox | Windows | batch loop with hold file | Oxide |
| U-ptero-ox | Ubuntu | local Pterodactyl panel, stock egg | Oxide |
| U-ptero-ca | Ubuntu | local Pterodactyl panel, stock egg | Carbon |
| U-dock-ox | Ubuntu | Docker (`didstopia/rust-server`) | Oxide |
| U-lgsm-ox | Ubuntu | LinuxGSM | Oxide |

## Phases

| Phase | Outcome | Gate |
|---|---|---|
| **0 — Foundations** | Review blockers closed in the design; **timeline recorder running** (before 2026-10-22); repo restructured (`legacy/`, `src/`, `tests/`, `tools/`); CI on Linux and Windows; lab reachable over SSH; cells U-man-ox, U-man-ca, U-man-va, W-man-ox, W-man-ca installed by hand (no FMU yet); RAM/disk per instance measured | Recorder has stored at least one day of data; lab report written; decisions 1–5 of DESIGN §15 answered |
| **1 — Know the truth** (read-only) | `Fmu.Core` + `fmu status / check / schedule next / config validate`: installed state, game and framework sources (not plugin sources), event classification, schedules with time zones. **Touches nothing.** Release pipeline (build, checksums, signature) set up now and used for every preview | L1: parsers, schedules, classification, gate rules on synthetic timelines; LAB-1, LAB-2, LAB-19, LAB-22, LAB-23; `fmu status` correct on the five phase-0 cells |
| **2a — Linux pipeline** | Job runner with frozen plan and journal; `managed` + systemd on Linux; WebRCON client; SteamCMD driver; snapshot; health check; Oxide install; install/SteamCMD locks; Discord notification with outbox; `doctor` | L2 suites 1–5, 6a, 7, 8 (archive part), 10 — failed-update scenarios end in resume or hold-with-alert here, rollback cases are added in 2b; lab: update and failed-update scenarios on U-man-ox, U-man-va, U-sysd-ox, plus `managed` lifecycle scenarios on U-man-ox (agent `kill -9`, unit restart, re-adoption, reboot, invalid config at boot); LAB-4, LAB-11, LAB-12. **Preview 1.** |
| **2b — Carbon, rollback, observe** | Carbon verify path; rollback; parking of failed targets; observe mode | L2 rollback and parking cases; lab: U-man-ca; rollback scenarios on U-man-ox; observe-mode instances running for LAB-3; LAB-24 |
| **2c — Windows** | `managed` and NSSM service on Windows; Windows file-lock handling; service install | L2 on Windows; lab: W-man-ox, W-man-ca, W-svc-ox, plus `managed` lifecycle scenarios on W-man-ox (service stop, pre-shutdown, re-adoption); LAB-16. **Preview 2.** |
| **2d — Script loops** | `script` supervisor with the hold-file convention and shipped loop templates | Lab: U-loop-ox, W-loop-ox |
| **3 — Wipe engine** | Profiles, schedules in jobs (forced-wipe window, misfire, `ifUpdateBlocked`), trash + undo, dry-run, next-map sources, cfg editor, hostname templates, wipe-timer sync, `snapshots` commands | L1 wipe planner + cfg editor; L2 suite 11; lab: simulated forced wipe (`last-month` → `public`), custom wipes and undo on U-man-ox, U-man-ca, W-man-ox, W-svc-ox; LAB-5, LAB-7, LAB-17. **Preview 3.** |
| **4a — Bundler and companion plugin** | Bundler + analyzer; companion mode: countdown UI, `/nextwipe`, abort, health report, console protocol | L3 in CI; L2 suite 9; **L3b** on U-man-ox, U-man-ca, W-man-ox, W-man-ca; LAB-8, LAB-20 (vanilla, Oxide, Carbon) |
| **4b — Post-wipe work in the plugin** | `OnNewSave` cleanup and command timeline, `onUnexpectedNewSave` | Lab on the same four cells; LAB-6 |
| **5 — Panels and remote** | Profile B: Pterodactyl/Pelican adapter (power, files, startup variables, console websocket); SFTP/FTP + console adapter with the `host` supervisor, probe and restart test; agent Docker image; rule 0 under `on-start` installers | L2 against the fake panel; lab: full update and wipe jobs on U-ptero-ox and U-ptero-ca driven from the Windows machine; a restart-shaped wipe job over SFTP against U-man-ox with supervisor `host`; LAB-14; LAB-20 through the Pterodactyl wrapper. **Preview 4.** |
| **6 — Plugin-only mode** | Profile C: restart gate, notifications, post-wipe work; further items only where LAB-5, 6, 8, 9, 10 and 21 allow; C+ implemented or dropped, per the decision taken in phase 0 | Lab: U-ptero-ox and U-ptero-ca with no agent; LAB-9, LAB-10; LAB-21 on W-man-ox with the agent stopped (and on the rented TCAdmin Windows host if rented); **one month on real rented servers** (TESTING §5.5) if the maintainer rents them, otherwise profile C ships labelled "tested on a local panel only" |
| **7 — Plugin management** | Policies, uMod and GitHub sources, inbox folder, staging, per-plugin rollback, quarantine, audit history | L1/L2 matching and validation; lab: update, broken update, rollback on U-man-ox, W-man-ox; LAB-13 |
| **8 — Release 2.0** | Docker and LinuxGSM supervisors; multi-server staggering and canary (L2 suite 6b, suite 8 self-update part); signed self-update; `fmu migrate`; documentation per environment, including what is and is not automated on each hosting family and every endpoint contacted | All thirteen cells green on the release candidate; INV suites green; gate rules validated on recorded live events (TESTING §6) or shipped with the conservative defaults and labelled so; a v1 install migrated in the lab; the release candidate has run through one real forced wipe |
| **Later** | Agent as container entrypoint; Pelican lab cell; Nitrado adapter; RustMaps; staging pre-flight; game-build rollback if LAB-18 allows; non-public Oxide branches (LAB-15); read-only status endpoint | — |

## Notes on ordering

- Phase 1 is read-only on purpose: useful on day one, zero risk, and it is where facts v1 guessed at
  get established.
- The recorder is the one thing with a deadline: real update days cannot be re-run.
- Hosted-server support (phases 5–6) comes before plugin management (phase 7) because it was the
  maintainer's stated priority; the two can be swapped.
- Phase 6 depends on phases 3 and 4b (wipe planner, `OnNewSave` work). Phase 7 is independent of 5–6.
- v1 stays untouched on `main` until phase 8.

## Working agreement for implementation sessions

1. One slice per branch and pull request, small enough to review.
2. Tests first for logic; a lab scenario for anything that touches a real server.
3. Nothing is claimed as working unless a named test or lab report shows it.
4. A lab finding that contradicts a document updates that document in the same pull request.
5. Destructive lab actions stay inside the lab server directories.
6. The minor findings in `docs/v2/reviews/` are checked off as the phase that owns them is implemented.
