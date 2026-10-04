# Independent review — FeedMeUpdates v2 design set

Reviewed: `docs/v2/DESIGN.md`, `docs/v2/TESTING.md`, `docs/v2/ROADMAP.md` against
`docs/research/*.md`, `docs/v1/DEFECTS.md`, `docs/v1/KNOWLEDGE.md` (all read in full) and a spot check
of `FeedMeUpdates.cs`. Nothing in the repo was modified.

Abbreviations: R-tech = `rust-oxide-carbon-technical.md`, R-host = `hosting-environments.md`,
R-eco = `ecosystem-and-competitors.md`. Severity: **blocker** (design cannot be agreed / phase cannot
start until fixed), **major** (will produce wrong behaviour or an unprovable claim), **minor**.

Items marked *(reviewer knowledge, verify)* rest on my own background knowledge, not on the research
files; treat them as hypotheses.

Counts: 7 blockers, 72 major, 51 minor (130 findings; a few table rows cross-reference a finding
listed elsewhere).

---

## 1. Factual errors and unverified items presented as fact

### F-1 — blocker — TESTING §2 "Readiness gate" / ROADMAP phase 1 gate
> "replay of the recorded 2022–2026 timeline (Steam `release`/`public` promotion times, Oxide publish times, Carbon publish times — from `docs/research/rust-oxide-carbon-technical.md` §7.2)"

R-tech §7.2 contains **only Oxide patch *commit* times** for first Thursdays. It has no Steam
`release` or `public` promotion times, no Carbon publish times, and release *publish* times for just
two Oxide releases (§3.5: 2.0.7676 and 2.0.7573). The only `release`/`public` pair in the whole
research is a single observation of one hotfix (§1.1, 2026-10-02). The fixture this test replays
does not exist, and the Steam side cannot be back-filled (SteamDB must not be scraped, §1.3).
DESIGN §5.2 repeats the claim ("unit tests replay the 2022–2026 timeline in the research document").

Fix: replace with "replay of timelines recorded by the phase-0 recorder (see T-1) plus Oxide
`published_at` values back-filled from the GitHub releases API; synthetic cases for everything
else". Remove "2022–2026" from DESIGN §5.2 rule 2.

### F-2 — major — DESIGN §5.2 "Facts the gate relies on"
> "The new Rust build appears on the Steam `release` branch hours before it is promoted to `public`."

Generalised from one data point (R-tech §1.1: one hotfix, ~6 h). The same research quotes Oxide's
docs: "Don't use `release` branch because it is not always in sync with the `public` branch"
(R-tech §1.6). No [LAB] tag. Rules 1 and 2 are built on it.
Fix: "Observed once (2026-10-02): … **[LAB-3]**. Oxide's docs say `release` is not always in sync
with `public`; the rules below must work when it is not (see D-2, D-3)."

### F-3 — major — DESIGN §5.2
> "On wipe day Oxide and Carbon publish **before** the Rust build becomes public (30–60 minutes)."

R-tech §3.5 verifies *commit* times 30–45 min before and two publish times (13 and 28 min before).
Carbon's "1 hour" is Carbon's own documentation claim. R-tech §7.4 records 2024-05-02 (commit at
19:16 UTC, after the nominal time) and R-eco §2.1 records "5 minutes to 24 hours" and build-server
outages. It is "usually in 2025–2026", not a fact.
Fix: "In 2025–2026 Oxide usually published 15–45 min before … (two publish times verified); Carbon
documents 1 h. Late publication (hours) has happened and must be handled."

### F-4 — major — DESIGN §5.2 "Rules (Carbon)"
> "The gate only checks that the Carbon channel has a build whose protocol date is at or after the Rust build date"

Contradicted by the research's own live data. R-tech §4.4: production protocol on 2026-10-04 was
`2026.10.01.0`. R-tech §1.1: the installed/public Rust build was dated 2026-10-02 (hotfix #3).
The rule as written evaluates **false** on a working server on the day of the research. R-tech
§7.4 says why: on hotfixes "Carbon usually only needs its hook update" — the protocol does not
move. Aux channels use `0.0.1.0-aux01`, which is not a date at all. See D-4 for the consequence.

### F-5 — major — DESIGN §11.2 table, row "TCAdmin family and similar"
> "(Host Havoc, LOW.MS, PingPerfect, Survival Servers, GPortal …) | **No**: updating is a host button or scheduled task"

R-host §B: only LOW.MS is [V] TCAdmin. Host Havoc and PingPerfect are [L]. **Survival Servers** is
"Custom panel [L]" and has a "Rust Update drop-down: Yes, run update/validate on restart" — i.e. a
restart *can* be an update. **GPortal** is "Custom" with "Auto-update behaviour [NF]". The cell
states [L]/[NF] items as fact and one of them wrongly.
Fix: split the row: "TCAdmin (LOW.MS verified; Host Havoc, PingPerfect likely)" and "Other custom
panels (Survival Servers: update-on-restart is an option; GPortal, Apex: unknown)"; replace "No"
with "No on TCAdmin; unknown elsewhere — the restart test decides".

### F-6 — major — DESIGN §11.2, same row, profile B
> "SFTP + RCON: wipes and plugin work"

R-host §B lists **FTP**, not SFTP, for these hosts (GPortal [V] FTP, Survival Servers [V] FTP,
Apex [V] "FTP File Access", Host Havoc/PingPerfect [L] FTP). DESIGN §3.2 has only an `sftp` files
adapter. RCON exposure on these hosts is [NF]. As written, profile B does not exist for this family.
Fix: add an `ftp`/`ftps` files adapter to §3.2 (phase 6) or change the cell to "Profile B only where
the host offers SFTP/FTPS and an open RCON port; otherwise profile C".

### F-7 — major — DESIGN §6.3
> "Steam serves only the current build of a branch (plus `last-month`)."

Not in any research file, no [LAB] tag, and in tension with R-eco §1.3: RSM Web advertises
"one-click updates … and one click back to the previous version" using DepotDownloader, and R-eco
feature #4 (MH) says rollback of the "server build where possible". Older depot manifests are
normally still downloadable by manifest id *(reviewer knowledge, verify)*.
Fix: "Not by SteamCMD `app_update`. Whether a previous manifest of depots 258551/258552/258554 can
be fetched anonymously (DepotDownloader / `download_depot`) is **[LAB-18]**; if yes, rollback of a
non-protocol hotfix becomes an opt-in feature."

### F-8 — major — DESIGN §4.1 (last paragraph) and §6.4 item 4
> "the console adapter is the fallback (`version`, `oxide.version` / `c.version` …)" — "the running build and protocol equal the expected ones"

KNOWLEDGE K-012: "The installed Steam build id cannot be read from inside the game:
`server.Version`/`server.Protocol` are Rust's numbers, not the depot build id." R-tech §8.2 did not
capture `serverinfo` fields. So without file access the Steam build id is not observable, and the
"no more belief files" claim of §4.1 fails for RCON-only setups. No [LAB] tag.
Fix: extend LAB-2: "what do `version`, `serverinfo` and `BuildInfo` return and can any of them be
mapped to the Steam buildid". In §4.1: "Where neither files nor a mappable console value exist, FMU
keeps a recorded build id (last seen at a verified restart) and labels it as a belief in `status`."
In profile C read `steamapps/appmanifest_258550.acf` in-process instead of the console.

### F-9 — minor — DESIGN §5.1
> "v1 fires at 19:00 UTC, which is an hour late for seven months of the year"

v1 does not fire on the clock: it arms `nextUpdateIsForce` in a window before 19:00 UTC and wipes
when an update is detected (K-141, D-023; `FeedMeUpdates.cs` 3960, 4008–4019). The defect is the
sticky, time-window flag, not a clock-triggered wipe.
Fix: "v1 arms a time window around 19:00 UTC and treats any update inside it as the force wipe
(D-023, D-090)".

### F-10 — minor — DESIGN §5.2 "Rules (Carbon)"
> "Carbon updates itself at boot and has its own protocol guard"

R-tech §4.3: the guard is **production only**; "Staging and other beta channels update on every
restart with no protocol gate". The self-updater also needs `curl` on PATH,
`SelfUpdating.Enabled`, and outbound access to api.carbonmod.gg and github.com.
Fix: add "(production channel only)"; `doctor` checks curl, the config flag and reachability, and
FMU installs Carbon itself when any is missing.

### F-11 — minor — DESIGN §7.5
> "proper quoting/escaping" and "preserves … encoding"

K-089: "there is no escape syntax, so `\"` must be removed". K-087: write UTF-8 **without BOM**
because a BOM breaks the first command. "Escaping" does not exist and BOM preservation is wrong.
Fix: "values containing whitespace are quoted; `\"`, CR, LF, TAB are rejected or replaced with a
warning (no escape syntax exists); a BOM is removed; non-UTF-8 files are re-encoded only after a
lossless round-trip check, otherwise left untouched and reported".

### F-12 — minor — DESIGN §7.3
> "changing seed/size alone still produces a fresh map"

R-tech §6.4 heads this "UNVERIFIED but well supported". No tag in the design.
Fix: add **[LAB-17]** to the sentence and add the case to LAB-17's scope.

### F-13 — minor — DESIGN §6.1 step 6
> "known states handled (`0x6`, `0x402`, `0x602` retry …)"

R-tech §1.6: `0x602` is documented as "unknown"; the retry classification comes from the
UNVERIFIED bit-flag reading. `0x606`, `0x212`, `0x626/0x6A6` (documented) are missing from the list.
Fix: tag the sentence [LAB-11] and list the documented states.

### F-14 — minor — DESIGN §9.2
> "Lone.Design, Codefling | Public catalogues"

Codefling's catalogue was not seen live (robots-blocked), rate limits are UNVERIFIED, and R-tech
§5.3 says "Ask Codefling before shipping it". R-eco (b)6 recommends *not* covering stores beyond
uMod + GitHub natively.
Fix: mark Codefling "unverified, needs Codefling's consent"; move both to "Later".

### F-15 — minor — DESIGN §1.1
> "Downloading paid plugins from marketplaces (not permitted by them …)"

Only a Codefling admin statement about third-party automation is verified (R-tech §5.3); Carbon's
admin module does exactly this with the owner's OAuth login. Fix: "not offered: it needs the
owner's marketplace login and Codefling has said it will not allow third-party automation".

### F-16 — minor — DESIGN §13 vs R-eco §3.2
The design follows R-host §D.1 (sandbox removed) and ignores R-eco §3.2, which lists the sandbox
namespaces as blocked and derives feature #23 ("sandbox-clean companion plugin") from it. R-host
shows R-eco's source is the obsolete guide, so the design's reading is probably right, but the two
research files contradict each other and nobody says so.
Fix: one sentence in §13 naming the conflict and which source wins; correct R-eco §3.2.

### F-17 — minor — DESIGN §8.5
> "signed (Ed25519; public key compiled into the agent)" with §8.1 "Dependencies: Minimal: a cron library, an SSH/SFTP library …, System.Text.Json"

The .NET base library has no Ed25519 signature API as far as I know *(reviewer knowledge, verify
for .NET 10)*. Either a crypto dependency is missing from §8.1 or the algorithm should be one the
runtime ships (ECDSA P-256).

---

## 2. Design flaws

### 2.1 The framework-readiness gate (§5.2)

#### D-1 — blocker — rule 2, second clause
> "…or whose notes name B's protocol."

1. The protocol does not change on hotfixes (R-tech §7.3). The Oxide release already installed
   names the current protocol, so for **every same-protocol hotfix this clause is already true**
   and the gate opens with no new Oxide build. That is exactly the case "each Oxide build is tied
   to one exact Rust server build" (R-tech §3.4) forbids.
2. §5.1 states the protocol of B "is unknown" before download, so the clause cannot be evaluated
   before the job in the first place.
3. Rebuild releases can have an empty body (R-tech §3.4).

Fix: delete the clause as a sufficient condition. Keep it only as a veto after download: "if the
release notes name a protocol and it differs from B's, the release is not for B".

#### D-2 — blocker — rule 2, first clause; INV-2
> "an Oxide release exists that was published after B appeared on the `release` branch"

No mechanism identifies which Rust build an Oxide zip targets, so the rule is a time heuristic and
it is unsound on hotfix chains, which are the normal case after a wipe (R-tech §7.4: "often
several" within 24 h).
Scenario (shape taken from 2026-10-02): hotfix B3 lands on `release` at 15:53 and on `public` at
22:10. At 16:30 Oxide publishes a rebuild for B2, the build that is public at that moment. At 22:10
FMU sees B3 on `public`, finds "a release published after B3 appeared on `release`", opens the
gate, installs the B2 Oxide on B3. Health check fails, the game build cannot be rolled back (§6.3),
default `hold-and-poll` keeps a previously playable server down.
Further gaps: (a) if B never appears on `release` (F-2) the clause is undefined; (b) the
`timeupdated` of `release` is overwritten by the next push, so an agent that started late has lost
T(B); (c) INV-2 says "believes is incompatible" — with a heuristic the invariant is unprovable.

Fix (design change):
- Define `T_avail(B)` = earliest time FMU or the source reports B on any branch, persisted in
  state; fall back to `public.timeupdated`.
- Readiness by event class:
  - *Predicted wipe-day update*: ready when an Oxide release has `published_at ≥ T_avail(B)` and
    `release.buildid == public.buildid == B` at decision time.
  - *Any other update*: ready only when an Oxide release has `published_at ≥ T_public(B)`
    (conservative; costs nothing when the old build is still joinable).
- Add **[LAB-19]**: find a positive identifier of the target Rust build inside an Oxide release
  (asset metadata, a constant in the patched `Assembly-CSharp.dll`, the GitHub release `digest`
  history). If one exists it replaces the heuristic.
- Reword INV-2: "FMU never initiates a restart unless its readiness rule holds; the rule is a
  heuristic until LAB-19, and the health check plus `onBroken` bound the damage."

#### D-3 — major — rule 1
> "A framework-only update is allowed only while the `release` branch build equals the installed build"

Scenarios that deadlock it: (a) Facepunch hotfixes `public` directly and `release` stays behind
(Oxide docs say it happens) → `release ≠ installed` indefinitely → no framework update is ever
allowed; (b) after `update-anyway` or `go-vanilla` the server waits for Oxide, and when Oxide
arrives `release` has already moved to the next staged hotfix → blocked, server stays vanilla;
(c) a genuine Oxide fix for the installed build is blocked for the whole `release`→`public` gap
(6 h observed).
Fix: "A framework-only update is allowed when `public.buildid == installed` **and** no build newer
than the installed one is staged on `release` with an Oxide release published after it appeared.
When the server is running without its framework (`go-vanilla`, `onBroken`), the first Oxide
release with `published_at ≥ T_public(installed)` is installed regardless of `release`."

#### D-4 — blocker — Carbon gate
See F-4. With the rule as written a Carbon server never passes the gate after any hotfix or
mid-month mandatory update (Carbon protocol stays at the wipe-day date); it waits `maxWait`, then
`keep-waiting` (default) forever. On 2026-10-22 every Carbon server managed by FMU would stay on the
old build, unjoinable.
Fix: "Carbon: no pre-gate for updates outside the predicted wipe-day window (Carbon patches in
memory; hooks update at boot). On the predicted wipe-day update, wait until the channel entry's
`date` is ≥ `T_avail(B)` **[LAB-3]**, bounded by `maxWait`. The health check is the judge in both
cases. Non-production channels: health check only."

#### D-5 — blocker — installer `on-start`: only game updates are gated
> "In environments where a restart always installs the newest framework … FMU simply does not restart until rule 2 holds."

Rule 2 concerns a pending game update. The dangerous window on wipe day is the opposite one:
Oxide/Carbon for B is published (17:32 UTC) while `public` is still on A. In that window the stock
entrypoint (R-host §A.2: unconditional `releases/latest` for Oxide; unconditional `curl | tar` for
Carbon, which **bypasses Carbon's own protocol guard**) puts a B framework on game A at **any**
restart: FMU's scheduled restart, a scheduled wipe at 18:00:00, a plugin-policy restart.
Fix: add rule 0 for `on-start` installers: "FMU initiates no restart of any kind while the newest
framework release is newer than the one installed and was published after a build that is not yet
on the tracked branch appeared; scheduled restarts and wipes are deferred with a notification and
a maximum deferral." State in §11 that crashes and host-initiated restarts in that window cannot
be prevented, and that the health check must then report "framework ahead of game" rather than a
generic failure.

#### D-6 — major — `update-anyway`, `go-vanilla`, `run-vanilla` are under-specified and conflict with step 7
Step 7: "Always after step 6, because a game update overwrites Oxide's files." With no new Oxide
release, step 7 reinstalls the **old** release, whose patched `Assembly-CSharp.dll` overwrites the
new game's — the "stuck on start" case (R-eco §2.1 item 2) and a deliberate INV-2 violation.
`go-vanilla` after a non-`validate` update leaves a mix of old patched and new vanilla DLLs.
Fix: after step 6 hash the seven patched-file names (R-tech §3.4). If SteamCMD replaced none of
them, the cached Oxide may be re-applied ("content-only hotfix"); otherwise `update-anyway` means
vanilla. Any vanilla start runs `validate` first and moves `Oxide.*.dll` out of `Managed`.

#### D-7 — major — mid-month mandatory updates cannot be recognised before download
§5.1 predicts only from "the calendar (first Thursday)". A mid-month build is therefore handled as
"hotfix: next quiet window or after a configurable delay", while clients already get "Wrong
connection protocol" (R-eco §2.2). On 2026-10-22 the server sits unjoinable until the quiet window.
Fix (pick one, both cheap): (a) profile A/B-with-exec: pre-download B into a staging install or
fetch only the `Managed` depot files, read `Rust.Global.dll`, classify **as fact before the
countdown**; this also removes the two-stage classification in §5.1 and shortens downtime;
(b) everywhere else: treat console lines rejecting clients for protocol mismatch as the signal
"mandatory", plus an optional calendar of announced mandatory dates.

#### D-8 — major — target moves during the job (regresses K-026, D-037, D-083)
SteamCMD installs whatever is current; §6.1 step 6 says "success = manifest `buildid` equals
target". On wipe day a hotfix 20 minutes after the update is normal. The job then fails
("retried, then escalated") with the server down, although the files are fine. Nothing in §5/§6
says the plan is frozen or what happens when it cannot be honoured.
Fix: "The plan records the versions it was made for. If step 6 installs a build newer than the
target, the gate is re-evaluated for the installed build: ready → continue; not ready → apply
`framework.onTimeout` from the stopped state. Detection during a job never changes the plan."

#### D-9 — major — GitHub budget and the fallback cannot feed the rule
The fallback (`releases/latest/download/<asset>` redirect) yields a tag but no `published_at` and
no body, which rule 2 needs. Near wipe time one request a minute already equals the 60/h
anonymous limit; hosting IPs are shared (K-130) and Oxide's own in-game check uses the same API
from the same IP (R-tech §3.1). In profile C FMU controls none of that.
Fix: use `https://github.com/OxideMod/Oxide.Rust/releases.atom` (timestamps, no API quota —
*reviewer knowledge, verify*) as the first fallback; record "first seen at" for tags as the last
resort and treat it as `published_at` only if FMU was polling before `T_avail(B)`; write the
per-host budget as a table (requests/hour per source, normal vs wipe window) in §4.2; in profile C
`doctor` warns that the budget is shared with other tenants.

#### D-10 — major — branches other than `public`
The gate is defined only through `release`→`public` and Oxide's master releases. For a server
tracking `staging`/`aux`: Oxide builds live at an unverified URL (LAB-15, phase 6) with no publish
metadata; there is no earlier branch to observe; Carbon beta channels have no protocol guard. R-host
§A.3 even quotes an egg README saying Oxide does not support staging.
Fix: scope 2.0: "`public` fully supported; `staging`/`aux` supported for vanilla and Carbon with
health check only; Oxide on non-public branches is experimental after LAB-15." Remove the implied
general branch support from §4.2/§6.1.

#### D-11 — major — the `force` preset contradicts §5.1
§5.3 defines `force` as a cron ("first Thursday, 19:00 `Europe/London`") bound to an action; §5.1
says the force wipe is not triggered by a clock. Scenario with the preset as a cron: 18:00:00 UTC
the wipe job runs, no update is public yet (the minute is not guaranteed, R-tech §7.4), the map is
wiped on the old build; at 18:07 the update lands, second restart, second new map. On Pterodactyl
the first restart also installs the new Oxide on the old game (D-5). The same double wipe hits any
admin whose own weekly Thursday schedule coincides with force day.
Fix: "`force` is not a cron. It opens a window at the computed time minus `force.leadTime`; the
wipe profile runs inside the update job that installs a build with a changed save protocol. If no
such build arrives within `force.timeout`, apply `force.onNoUpdate: skip | wipe-anyway` and alert.
A scheduled wipe that falls inside an open force window is merged into it."

#### D-12 — major — "triggered by the real protocol change" is impossible with `on-start` installers
In profile B on Pterodactyl and in profile C the update happens inside the entrypoint; there is no
point between "game files updated" and "server started" at which FMU can read the protocol. Seed
and map must be chosen before the restart, on prediction. If the prediction is wrong and files
were deleted, that is D-023 again.
Fix: "With `on-start` installers the pre-restart part of a predicted force wipe is limited to
reversible changes (seed/size/level URL). File deletion happens after boot, once the new save
protocol is confirmed, and only for orphaned files. If the protocol did not change, the seed is
restored and the admin alerted." Say explicitly in §11.2.

#### D-13 — major — scheduled wipe or restart while the gate is closed
"Scheduled wipe … (which also applies pending updates)" does not say what happens when the pending
update is gated. Proceeding wipes on a build that is about to be replaced (and breaks the server
under `on-start`); waiting silently moves an advertised wipe.
Fix: per-schedule `ifUpdateBlocked: defer(max) | run-without-update` (the latter refused under
`on-start`), with a notification either way.

#### D-14 — minor — polling cadence and source disagreement
"poll modestly" is never quantified; "disagrees with the fallback is ignored for that cycle" will
fire at every promotion while caches differ. Specify: interval by state (idle 5–10 min, predicted
window 60 s), and "two consecutive agreeing reads from one source are enough when the other is
merely older".

### 2.2 Wipes

#### D-15 — major — trash location; "same volume (instant)" — §7.2 with §8.1
The trash is `fmu/trash/<job>/` and `fmu/` is "next to the config". With several servers on
different volumes, a Docker bind mount, or a config in `/etc`, the move crosses file systems
(EXDEV): it is a full copy, or it fails. In profile B the agent's `fmu/` is on another machine.
Fix: "The trash is `<server root>/.fmu/trash/<job>/` on the files adapter's side (panel
`PUT /files/rename`, SFTP rename, `File.Move`). A cross-volume move is refused at preflight unless
`trash.allowCopy` is set."

#### D-16 — major — trash against disk quotas
On panel-hosted servers the trash counts against the server's disk limit. Seven days of `.map`,
`.sav`, `.sav.N` and `sv.files` after a weekly wipe can exceed it and the panel then refuses to
start the server *(reviewer knowledge of Pterodactyl disk limits, verify)*. Preflight checks space
"for snapshot + download" only.
Fix: retention by age **and** size/count; preflight includes trash and the panel limit
(`GET /resources`); on quota hosts the default is "keep last wipe only".

#### D-17 — major — INV-3 promises more than the mechanisms deliver
"Every delete … goes to a trash folder with retention, so a wipe can be undone." Not true for:
profile C (no hold; the active `.sav` is rewritten by `quit`, SQLite files are open and locked on
Windows); the plugin's `OnNewSave` cleanup; pruning of trash and snapshots; a forced wipe (the old
save cannot be loaded by the new build, so "undo" restores nothing usable). `fmu wipe undo` is also
undefined when the server is running or when the new save has the same file name (scheduled wipe,
same seed and protocol).
Fix: reword INV-3: "Deletes performed by a wipe plan are moved to the trash where the files
adapter supports rename; where it does not, the plan says 'permanent' and requires
`allowPermanentDelete`. `wipe undo` requires the server stopped, refuses after a save-protocol
change, and moves colliding new files to a second trash entry."

#### D-18 — major — `OnNewSave` cleanup can destroy data on a non-wipe (§7.4)
`OnNewSave` also fires on a first boot, a restore, an admin's seed change, or a missing/corrupt
save. R-eco §1.2 records the standing fear "would it wipe after every restart?". The design runs
the delete list on every new save, including ones FMU did not plan.
Fix: `onUnexpectedNewSave: ignore | notify (default) | clean`; cleanup without the agent's
"wipe expected" marker goes to trash and is reported. If LAB-6 shows other plugins already loaded
their data, FMU deletes and then reloads the owning plugins, or defers to the next stop.

#### D-19 — minor — identity check when the server is down
"`server.identity` must match what the running server reported" cannot be evaluated for a wipe of
a stopped or crashed server. Fix: persist the last reported identity and command line in state;
refuse if never observed.

#### D-20 — minor — `blueprints: "when-forced"` has no detection mechanism
A Facepunch blueprint wipe is a bump of a separate number (R-tech §6.2, UNVERIFIED). Define it via
LAB-17: "the game created a new `player.blueprints.<N>.db`; FMU only removes the orphan."

#### D-21 — minor — K-091/D-059 not carried over
§7.3 never states that `server.levelurl` and `seed/worldsize/level` are mutually exclusive, nor
that an existing level URL must not be removed when no replacement is configured. Add both; add
K-107 range validation for seed and size.

### 2.3 Jobs, journal, rollback

#### D-22 — major — Stop before Hold races every auto-restarting supervisor (§6.1 steps 3–4)
Step 3 sends `quit`; step 4 then asks the supervisor to hold. systemd `Restart=always`, a wrapper
loop (5 s, K-109), NSSM default `AppExit Restart`, and Wings (an RCON `quit` "is handled as a
crash", R-host §A.4, so Wings starts the server and the entrypoint updates it) all restart the
server between the two steps. FMU then modifies files of a starting server — D-005 again. The
opposite order has its own problem: `systemctl stop` is a signal, and signals do not save.
Fix: replace steps 3–4 with a per-supervisor sequence table in §3.2, e.g. systemd: `server.save` →
`systemctl stop` (unit generated/checked for `KillSignal`, `TimeoutStopSec`), Pterodactyl:
`server.save` via command → `POST /power stop` (never RCON `quit`), NSSM: `nssm stop`, wrapper
loop: create the hold file first, then `quit`, `managed`: set the hold flag, then `quit`.
The table is where K-041…K-046, K-056…K-059 and K-109 must land; today the design drops them.

#### D-23 — major — downloads happen while the server is down
The Oxide asset (step 7) and plugin updates (step 8) are fetched after Stop. A GitHub outage or
rate limit at that moment is downtime, on the day it is most likely. The snapshot of "DLLs FMU is
about to replace" also needs the archive listing before step 5.
Fix: Preflight downloads and verifies every artefact; steps 7–8 only install from `fmu/cache`.

#### D-24 — major — no failure memory (regresses D-020/D-081, K-119)
"A failed game update is retried, then escalated." Nothing stops the next detection cycle from
planning the same job again, forever, each time with a countdown and a restart. Profile C can loop
at the crash-window period.
Fix: "A (server, target build, framework tag) that failed N times (default 2) is parked until an
input changes or the admin runs `fmu retry`; the parked state is shown in `status` and alerted once."

#### D-25 — major — INV-1 cannot be guaranteed by "a single top-level handler"
`kill -9`, OOM kill and power loss run no handler. If nothing restarts the agent (run by hand in
tmux; remote home PC asleep) the server stays down and nobody is told. "Critical alert delivered"
is also not in FMU's power when Discord or the network is down — which correlates with failures.
Fix: reword INV-1: "…or deliberately held down with a critical alert **attempted on every
configured channel and recorded durably; undelivered alerts are retried and repeated at next
start**." Add: an optional dead-man's-switch URL pinged on a schedule (the only mechanism that
covers a dead agent), `fmu service install` as the only supported way to run `fmu run`
unattended, and a `doctor` failure when the daemon is not supervised.

#### D-26 — major — "all steps are idempotent" needs frozen plan values
Random seed, map-pool pick with no-repeat memory, the `every-2nd` blueprint counter and the
post-wipe command timeline are not idempotent unless the resolved values are written to the
journal at plan time and counters are advanced exactly once at commit. State this in §6.2.
Journal durability also needs fsync of file and directory; disk-full must not prevent the journal
write (pre-allocate).

#### D-27 — minor — preflight requires RCON
"RCON reachable" as a preflight condition blocks a job for a server that is already down (the
watchdog case). Make it conditional on the server running.

#### D-28 — minor — lock scope in profile B
The lock lives in the agent's local state. Two agents (home PC and VPS) pointed at the same rented
server exclude nothing. Put an owner-stamped lock file on the server side through the files adapter.

#### D-29 — minor — fixed health deadline and watchdog kills
A fixed 15 min deadline will declare slow map generation, a slow SteamCMD inside an entrypoint or
a 150-plugin compile "unhealthy". The watchdog's "RCON silent" restart can kill a server during a
long save. Use progress-based deadlines (log activity, phase markers) and never restart on RCON
silence alone before startup completed.

### 2.4 Supervisors

#### D-30 — blocker — `managed`: agent restart, crash, self-update and OS shutdown are not designed
`managed` is "the recommended supervisor" (§3.2, §15.3) and FMU "owns the RustDedicated child".
Not specified:
- **systemd**: stopping or restarting the `fmu` unit (self-update, config change, package upgrade)
  kills the cgroup: RustDedicated gets SIGTERM then SIGKILL, which does not save (R-tech §8.3).
  This is K-041 one level up; the design claims the opposite ("removes the whole class of 'who
  restarts whom' problems").
- **Windows service**: children are not killed when the service stops unless a Job object is
  used. Without one the game is orphaned and the new agent must re-adopt it; with one, an agent
  crash kills the game unsaved. The child runs in session 0 with no console, so the only clean stop
  is RCON; a wrong RCON password means every stop is a kill.
- **Pipes**: if the agent holds the child's stdout/stderr, an agent death breaks them.
- **OS shutdown/reboot**: 90 s default on systemd, ~20 s on Windows, both shorter than save + quit
  on a large map (LAB-4 measures that but nothing uses the result).
- **One agent for many servers** (§8.3): one agent failure affects all of them.
- **Invalid config at boot**: FMU owns the launch, so a JSON typo keeps the server down after a
  reboot.

Fix: add a §3.3 "`managed` lifecycle": child started detached (own session/process group, no
inherited pipes, `-logfile`); state file with pid + start time + identity; on start the agent
re-adopts a live child; the generated unit uses `KillMode=process`, `ExecStop=fmu stop --graceful`,
`TimeoutStopSec` from LAB-4; Windows registers for pre-shutdown; self-update restarts only the
agent; last-known-good launch arguments are kept in state and used when the config does not
parse; RCON must pass `doctor` before `managed` can be enabled. Until this exists and is tested
(T-6), `managed` cannot be the default.

#### D-31 — major — `managed` launch environment per framework is missing
Carbon on Linux needs `carbon/tools/environment.sh` (DOORSTOP variables, `LD_PRELOAD`,
`LD_LIBRARY_PATH`, R-tech §4.1); Oxide needs `LD_LIBRARY_PATH` (§3.7). Children FMU spawns
(SteamCMD) must **not** inherit them (R-tech §9.3). Add to §3.2/§6.1.

#### D-32 — major — the `script` supervisor inherits everything §2 calls unworkable
"`script` (admin's start script, detached)" needs v1's detaching knowledge (K-066…K-077: tmux
`has-session` verification, `-t =name`, gnome-terminal presence ≠ usability, login shell) and the
lock protocol with wrapper loops (K-109). None of it is in the design, and "hold-down" for a loop
is undefined. "Existing setups keep working through the other supervisors" is unsupported.
Fix: either drop wrapper loops from 2.0 (offer `managed` running the admin's own command line) or
specify: the hold file convention the loop must check, how the session is created and verified,
and that `.bat` targets receive no arguments (see D-39).

#### D-33 — major — Windows service adapter: privileges and "sc"
`sc stop/start` need administrator rights or a modified service DACL; §13's least-privilege
recipes are Linux-only. RustDedicated is not a service binary, so "Windows service (sc)" without a
wrapper (NSSM, WinSW) does not exist; TESTING §5.2 lists it as a separate matrix value. K-057
(NSSM exit codes) and K-058 (recovery actions) are not mentioned.
Fix: define the adapter as "NSSM/WinSW-wrapped service"; add the DACL recipe (`sc sdset`) or a
statement that the agent runs elevated; carry K-056…K-058 into `doctor` checks.

#### D-34 — major — several servers sharing one install directory or one SteamCMD
"One job per server" is the only lock. Two identities running from one install directory must both
stop before step 6. Two SteamCMD runs from one SteamCMD directory conflict. "SteamCMD downloads can
be shared when servers use the same branch" has no mechanism. D-006 (finding the right
`RustDedicated` among several) is not addressed for `script`/`host` supervisors.
Fix: locks per install directory and per SteamCMD directory; a job on a shared install stops all
its servers; processes are identified by pid from the supervisor or by `+server.identity`/port on
the command line; drop "shared downloads" or define it as "update a staging install once, then
sync".

#### D-35 — minor — panel hold can be broken from outside
While FMU holds a Pterodactyl server stopped, a host schedule or the customer pressing Start
launches it mid-wipe. Detect via `GET /resources` during the job and abort/roll forward.

### 2.5 Agent ↔ plugin protocol (§10.1)

#### D-36 — major — JSON as a console argument
"the agent calls `fmu.<command> <json>`". Rust's console parser splits on spaces, treats `"` as
quoting and has no escape syntax (K-089); v1 already had to base64 everything (K-152/K-153). Raw
JSON will not arrive intact, and Oxide patches the RCON assembly (R-tech §8.1).
Fix: "arguments are one base64url token; replies are one line `FMU1 <id> <base64url>`; `fmu.hello`
negotiates a protocol version; maximum message size is established by **[LAB-20]** on vanilla,
Oxide, Carbon and through the Pterodactyl wrapper."

#### D-37 — major — "works identically in local and remote profiles" is false
Pterodactyl `POST /command` returns no output (R-host §A.6); answers require the console websocket
(short-lived token) or a reachable WebRCON port. WebRCON carries the password in the URL path over
plaintext `ws://` (R-tech §8.1); profile B across the internet exposes full server control. §13
does not mention it.
Fix: profile B console order: panel websocket (wss) → WebRCON only via loopback/tunnel or with an
explicit `allowPlaintextRcon`; `doctor` states the exposure. Capabilities `read-output`/`events`
decide whether the plugin's health report is available at all.

#### D-38 — major — agent-only commands are reachable from the in-game console
`fmu.*` console commands that take a payload (cleanup lists, timelines) can be typed by any in-game
admin unless restricted. That is a path from a game permission to file deletion (INV-3, INV-9).
Fix: commands of the agent protocol are accepted only from RCON/server console (no player
connection); file operations are re-validated plugin-side against the configured roots.

### 2.6 Plugin bundling (§10.3)

#### D-39 — major — INV-6 does not hold for batch files and console commands
Starting a `.bat`/`.cmd` goes through `cmd.exe`, where argument-list escaping is not safe for
`& % ^` *(reviewer knowledge, verify)*. Values from config or network (map-vote result, hostname
template) are also interpolated into console commands and `server.cfg`.
Fix: INV-6: "…batch files are started without arguments; values placed into console commands or
cfg lines pass one sanitiser (no quotes, control characters or newlines)".

#### D-40 — major — "the same `Fmu.Core` code already covered by L1" is not the code that runs
- Compiled by a different compiler (Oxide.Compiler / Carbon's Roslyn 4.14) against the game's
  `mscorlib`, run on Unity Mono: culture, `TimeZoneInfo`, regex and date parsing differ. L1 on
  .NET 10 proves nothing about that runtime.
- `System.Text.Json` (the agent's JSON, §8.1) does not exist in the game runtime; Core's parsers
  (TESTING §2 puts the JSON parsers in `Fmu.Core`) must use something else there, so the parser
  path is not shared.
- The cron library must be inlined as source into the single file.
- "namespaces flattened into nested classes" rewrites the source: extension methods cannot live in
  nested classes, so the bundler either forbids C# features or changes semantics. The shipped text
  is not the tested text.

Fix: (1) Core has zero package dependencies; JSON through a small reader owned by Core and used
on both sides; (2) keep namespaces if LAB-8 confirms both compilers accept extra namespaces in a
plugin file, otherwise enforce a "bundle-safe subset" with an analyzer; (3) add test level L3b:
the L1 vectors executed inside a real server through `fmu.selftest`, on Oxide and Carbon, both
OSes, plus a CI run of the Core tests on Mono.

#### D-41 — major — time zone data in three runtimes
The force preset and every schedule need `Europe/London` and other IANA zones: .NET on Linux needs
`tzdata` (absent in slim containers), .NET on Windows needs ICU (absent before Server 2019), and
Unity Mono on Windows does not resolve IANA ids *(reviewer knowledge, verify)*. Not in the design,
not in LAB-8.
Fix: Core takes a zone provider; the agent embeds tz data; the plugin tries the system and falls
back to a built-in rule set for the zones FMU's presets use; `doctor` and the plugin report an
unresolvable zone at setup (INV-10). Add to LAB-8.

#### D-42 — major — profile C plugin updates are unauthenticated code installation
Both frameworks disable TLS certificate validation process-wide (R-tech §0 item 3, §9.3). uMod
publishes no hashes. INV-8 limits "TLS validation is on" to the agent and says nothing about
profile C, where a network attacker can deliver a plugin that runs as the server user.
Fix: state the degradation under INV-8 and in `doctor`; the plugin uses its own HTTP handler with
its own certificate validation if LAB-8 shows that is possible; otherwise `install` is not offered
in profile C by default (`notify`/`stage` only).

#### D-43 — minor — what is `Process` for in the plugin?
LAB-9 tests process spawning in a container but no design feature uses it. If nothing does, remove
`System.Diagnostics` from the plugin (better odds of listing, smaller attack surface) and delete
LAB-9; otherwise name the feature.

### 2.7 Rented hosting (§11)

#### D-44 — major — the restart test cannot answer "does a restart update?"
It "records … whether the game or framework version changed". If nothing is pending when the admin
runs it, versions do not change either way.
Fix: use side evidence (mtime of `Oxide.Rust.dll`/`carbon/managed`, SteamCMD log and manifest
timestamps after the restart); otherwise store "unknown" and re-evaluate automatically at the
first restart with a pending update. `doctor` prints "unknown" as such.

#### D-45 — major — profile C "prepare the wipe" on Windows/TCAdmin hosts
LAB-10 covers a Linux container only. TCAdmin hosts are "often on Windows" (R-host §B.2), where the
open `.sav` and SQLite files cannot be moved or deleted by a plugin, and `quit` writes a save
anyway. The TCAdmin row promises "prepare the wipe".
Fix: change the cell to "clean orphans and plugin data after the host's wipe (`OnNewSave`); wipe
preparation only if **[LAB-21]** (in-process file operations on Windows) allows it". See T-4.

#### D-46 — major — profile C on the stock Pterodactyl egg may not be able to run a scheduled wipe at all
Seed/size/level URL are pinned on the command line (R-host §A.1); whether `server.cfg` can override
them is LAB-5 (primary sources conflict); deleting the active save before `quit` is LAB-10. If both
come back negative, profile C on the most common panel cannot wipe on a custom schedule. §3.1
should say so, and §11 should describe the obvious middle option, which the research already
identifies (R-host Tier 2): plugin **plus** client API key (startup variable + power action from
inside), with the key-exposure warning. Decide whether that is supported.

#### D-47 — major — detection depends on one volunteer API in profiles B and C
§4.2: "never depend on it alone". The fallback is SteamCMD, which is absent on a remote agent
machine, does not exist for linux-arm64 (a listed target) *(reviewer knowledge)*, and is not
available to a plugin. R-eco §1.2: Update Notice (43.6K downloads) died because "it depended on a
single third-party API".
Fix: the agent embeds an anonymous PICS client (SteamKit2; R-tech §1.4) as the second source; the
source URL is configurable so a self-hosted api.steamcmd.net mirror can be used; profile C states
the single dependency in `doctor`.

#### D-48 — minor — snapshots in profile B
"Stored under `fmu/snapshots/<job>`" means downloading plugins, configs, data (and optionally
saves) over a panel API or SFTP on every job. Specify server-side snapshots (panel `compress`
into `.fmu/snapshots`) with the quota guard of D-16.

#### D-49 — minor — capability degradation is decided at job time
"drops the step and records the degradation in the plan" happens when the job is planned, i.e.
possibly on wipe day. INV-10 needs `doctor` to dry-plan every configured schedule and fail on a
dropped step unless it is acknowledged in config (`acceptDegraded`).

---

## 3. Omissions

### 3a. v1 knowledge and defects not addressed or regressed

| ID | Sev. | What is missing | Fix |
|---|---|---|---|
| O-1 | major | K-041…K-046, K-056…K-059, K-109 (service-manager and wrapper knowledge) — see D-22, D-30, D-32, D-33 | per-supervisor table in §3.2 |
| O-2 | major | K-026 / D-037 / D-083 (pin versions, frozen decision) — see D-8 | plan freezing rule |
| O-3 | major | D-020 / D-081 / K-119 (failure counter) — see D-24 | parking rule |
| O-4 | major | D-021 / D-098 (past or missed schedule fires at once / never) — no misfire policy. An agent that was down at the wipe time, or a laptop that slept, must not wipe hours late at peak | §5.3: "an occurrence missed by more than `misfireGrace` (default 10 min) is skipped and alerted; `fmu schedule run-missed` runs it deliberately" |
| O-5 | major | D-022 / D-078 / D-079 (changing or breaking config at runtime) — see O-12 | config lifecycle section |
| O-6 | minor | D-007 (fail-open status probes): the three-state rule exists for sources (§4.2) but not for the supervisor `status` capability | "status is running / stopped / unknown; unknown never authorises file changes" |
| O-7 | minor | D-006 (which `RustDedicated`) — see D-34 | |
| O-8 | minor | K-144/K-145 (external daily restart): hosts' own schedulers still exist in profiles B/C | `blackout` windows per server |
| O-9 | minor | K-147 (no countdown with nobody online) | "countdown is skipped when 0 players" |
| O-10 | minor | K-020 (invisible characters in tags), K-129 (GitHub needs a User-Agent), K-137/D-132 (no mentions in Discord posts), K-052 (execute bits after extraction), K-063 (read-only attribute on Windows), K-084 (verify deletions) | one line each in §4.2, §8.4, §6.1 |
| O-11 | minor | D-131 / D-124: the plugin hot-reloads when the agent replaces it; countdown and timeline state must survive a reload | persist plugin state; reload test |

### 3b. Must-have features from R-eco "(a)"

| ID | Sev. | Feature | Gap | Fix |
|---|---|---|---|---|
| O-20 | major | #9 Backups with retention and a documented restore command | Only job snapshots exist; no command to list or restore them; no "keep last N pre-wipe saves"; R-eco (b)12 (Rust's built-in backup rotation) ignored | `fmu snapshots list / restore <job> [--only plugins,cfg,saves]`; `snapshots.keep { last, preWipe, maxSize }` |
| O-21 | major | #4 Rollback "server build where possible" | Declared impossible without evidence (F-7) | LAB-18 |
| O-22 | minor | #1 policy "start with plugins disabled" | Only vanilla is offered | add `start-without-plugins` if the framework itself loads |
| O-23 | minor | #20 Discord embed with map preview and connect info | Not in §8.4 | add to the wipe result notification |
| O-24 | minor | #23 sandbox-clean companion | One plugin file carries profile-C file/network code even in companion mode | the bundler emits a second "lite" flavour for marketplace listing |

Features #2, #3, #5, #6, #7, #8, #16 are covered (with the flaws above).

### 3c. Operational concerns

| ID | Sev. | Concern | Fix |
|---|---|---|---|
| O-30 | major | **Config schema versioning and lifecycle**: no `schemaVersion`, no migration between 2.x versions, no rule for unknown keys, no reload semantics, secrets file mentioned once (§11.3) and never defined; R-eco §1.3 shows breaking config migrations are a known source of anger | new §8.6: `schemaVersion`; `fmu config migrate` with backup; unknown keys are errors; reload on signal with validate-then-swap; an invalid file never replaces the running config; FMU never writes the config after `init` (counters and memories live in state); secrets in a separate file or environment variables |
| O-31 | major | **First run**: how `init` finds the server, the RCON password, SteamCMD; whether FMU installs SteamCMD; adopting a running server without restarting it; "admins install nothing" ignores SteamCMD's 32-bit libraries, `curl` for Carbon, tz data | describe the `init` dialogue and each prerequisite check; add `fmu install steamcmd`; state what needs root once |
| O-32 | major | **Linux user model**: agent must run as the server's user (files it writes otherwise become unreadable; a root-run agent leaves root-owned files); SteamCMD must not run as root; `systemctl --user` + linger as the privilege-free option; `sudo` rule now needs `stop` as well as `start` (K-044 rules only allow `start`) | `doctor` fails when uid ≠ owner of the server directory or uid = 0; document the user-unit recipe and the new sudoers lines |
| O-33 | major | **Agent inside the game container** (FMU as entrypoint): the natural Docker deployment is not described; needs PID-1 duties and a graceful stop within Docker's stop timeout | add as a supported form of `managed`, or exclude it explicitly |
| O-34 | major | **Windows specifics**: sharing violations from antivirus and Oxide's file watcher during rename (D-056, D-060) need bounded retry; session-0 service has no console; unsigned single-file executables trigger Defender/SmartScreen; pre-shutdown time | retry policy in the files adapter; Authenticode or a documented exclusion; see D-30 |
| O-35 | minor | **Remote agent availability**: nothing alerts when the profile-B machine is off on wipe day | dead-man's switch (D-25) |
| O-36 | minor | **Competing automation not listed in §12**: WindowsGSM's 30-minute auto-update, LinuxGSM cron `update`/`monitor`, `RUST_UPDATE_CHECKING`, host wipe tasks | `doctor` detects and warns |
| O-37 | minor | **Network**: RCON host must be explicit (`127.0.0.1`, or `rcon.ip`) — `localhost` may resolve to `::1`; proxy settings for the HTTP layer and for SteamCMD differ | config keys + `doctor --network` |
| O-38 | minor | **Clock**: schedules depend on the local clock; no check of clock skew against HTTP `Date` headers | warn when skew > 60 s |
| O-39 | minor | **Signing key custody**: "built by CI only" puts the signing key in the same GitHub account whose compromise D-017 is about | offline key or build attestations; state the threat model |
| O-40 | minor | **didstopia image** pulls Oxide from `umod.org/.../download/develop` (R-host §C.2), a source the research shows stale | recipe: disable its Oxide-on-boot and let FMU install |
| O-41 | minor | **Profile C self-update of the plugin** is undefined ("the agent updates the plugin file") | "notify only" |

---

## 4. Testing gaps

| ID | Sev. | Gap | Fix |
|---|---|---|---|
| T-1 | blocker | The gate's test data does not exist (F-1) and the calendar is being lost: observe mode arrives in phase 2, which cannot be finished before 2026-10-22 or 2026-11-05 (today is 2026-10-04 and phase 0 has not started) | Phase 0 deliverable: a standalone recorder (cron job or scheduled workflow) that stores raw responses of api.steamcmd.net, the Oxide releases feed and api.carbonmod.gg every minute in wipe windows and every 10 minutes otherwise. Independent of FMU code. Running before 2026-10-22 |
| T-2 | major | No test executes Core on the game runtime (D-40) | L3b `fmu.selftest` in L4 + Mono run in CI |
| T-3 | major | L3 compiles with CI's Roslyn, not with Oxide.Compiler (C# 14, net48) or Carbon's Roslyn 4.14 | state that L3 is a smoke test; the authoritative compile is L4 on both frameworks for every release |
| T-4 | major | Rented hosting is tested only on a local Pterodactyl panel; every claim about TCAdmin and custom panels in §11.2 is untested and TCAdmin cannot be reproduced locally | rent one month on one Pterodactyl-based host, one TCAdmin Windows host and one custom-panel host; run the probe, restart test and LAB-10/LAB-21 there; publish the results as the "what works where" table |
| T-5 | major | `fake-rustserver` does not model the console argument parser, so L2 cannot catch D-36 | record real parser behaviour in LAB-20 and implement it in the fake |
| T-6 | major | No tests for the `managed` lifecycle: agent killed/restarted/self-updated with the game running, OS shutdown, invalid config at boot, re-adoption | L2 suite + L4 scenarios on both OSes; gate for making `managed` the default |
| T-7 | major | Invariants with no suite, although §1.2 says "every one maps to a test suite": INV-8 (signature/hash verification, tampered self-update, interrupted swap), INV-9, INV-10 (`doctor` output against the capability matrix) | add the three suites to TESTING §3 |
| T-8 | major | No Carbon timeline or Carbon gate cases; no cases for D-1/D-2/D-3 (hotfix chains, `release` out of sync, rebuild tags with empty notes, target moving mid-job) | add to the L1 gate suite |
| T-9 | major | Missing lab validations: LAB-18 (old manifests), LAB-19 (Oxide build identifier), LAB-20 (RCON payload round-trip and size), LAB-21 (in-process file operations on Windows), time-zone resolution in Mono (add to LAB-8), `releases.atom` and conditional-request behaviour of GitHub, Carbon `.info` assets (R-tech §10 item 9), same buildid on `release` and `public` for the same build | add to TESTING §5.4 |
| T-10 | minor | No misfire, clock-jump or sleep/resume tests for schedules | L1 cases |
| T-11 | minor | No upgrade tests: journal and state written by version N read by N+1; config migration | L2 suite with stored fixtures per released version |
| T-12 | minor | No quota/disk tests for trash and snapshots; no journal write under disk-full | L2 with a quota'd temp volume |
| T-13 | minor | No soak test of the daemon (weeks: memory, log rotation, reconnects) | one lab instance left running; report at each phase gate |
| T-14 | minor | No watchdog false-positive tests (long save, map generation) | fake server that goes RCON-silent while alive |
| T-15 | minor | Matrix lists "batch loop" and "Windows service (sc)" which are not supervisors in DESIGN §3.2 (see D-33) | align the lists |
| T-16 | minor | LAB-3 can never be "confirmed"; it can only fail to be falsified. "right on at least two forced wipes and two mid-month updates" is a sample of four | word it as such; keep the conservative policy as the permanent default, with the validated rule as an opt-in |
| T-17 | minor | `--now` "lab only" in a shipped binary | compile-time flag excluded from release builds |
| T-18 | minor | LAB-12 simulates the save-protocol change only, not timing; it also needs the Oxide release matching the `last-month` build | say so; record which Oxide tag was used |

Unrealistic parts of the lab plan: one Ubuntu machine hosting bare servers, Docker, LinuxGSM and a
Pterodactyl panel + Wings, with three frameworks each, depends on RAM that is "measured in phase 0";
the plan should state a fallback (cells run sequentially, one server at a time). Each simulated
force wipe downloads the server twice per cell.

---

## 5. Roadmap

| ID | Sev. | Finding | Fix |
|---|---|---|---|
| P-1 | major | **Phase 2 is too large**: four supervisors on two OSes, RCON client, job runner, journal, snapshot, SteamCMD driver, Oxide install, Carbon verify, health check, rollback, notification, observe mode | split: 2a `managed` + systemd on Linux, vanilla + Oxide, happy path and journal; 2b Windows (`managed`, NSSM); 2c fault injection, rollback, Carbon; 2d `script` (if kept) |
| P-2 | major | **Gates that cannot be evaluated**: "Whole lab matrix green" (phase 7) and "update and failed-update scenarios on both OSes × three frameworks" (phase 2) refer to cells that are never enumerated — TESTING §5.2 says "the roadmap lists which cells gate which phase" and the roadmap does not | add a table: phase → exact cells (OS, supervisor, framework, profile, scenario set) |
| P-3 | major | **Phase 1 gate depends on things that come later**: gate L1 needs timelines that do not exist (F-1); `doctor` "capabilities" needs adapters from phase 2; "reads all sources" includes plugin sources from phase 5 | phase 1 = installed state + game/framework sources + schedules; gate suite uses synthetic timelines + recorder data so far; `doctor` limited to config and sources |
| P-4 | major | **Phase 4 depends on phases 3 and 5** (`OnNewSave` cleanup uses the wipe planner; profile C "plugin updates" is plugin management), yet the notes say it "can move before phase 3" and that 5 and 6 are independent | split phase 4: 4a bundler + companion (after 2), 4b plugin-only (after 3 and 5); correct the note |
| P-5 | major | **LAB items scheduled after the phase that needs them**: LAB-14 (Wings crash detection with a plugin `quit`) is at phase 6 but phase 4 tests plugin-only on the local panel; LAB-15 at phase 6 although branch handling is decided in phase 1 | move LAB-14 to phase 4; decide D-10 in phase 0 |
| P-6 | major | **No release until phase 7**, yet "each phase ends with something usable"; signing and the release pipeline are last, so nobody outside the lab runs v2 before 2.0, and phase 7's gate needs "one real forced wipe" plus LAB-3's four events — at least two more months after everything else | build the release pipeline in phase 1; publish tagged previews from phase 1 (read-only `status`/`check` is safe to ship) |
| P-7 | major | **Multi-server is phase 7** but the lab has three servers per machine from phase 0 and the agent is "one process handles many servers"; locks and SteamCMD sharing (D-34) must be right in phase 2 | multi-server core in phase 2; staggering and canary later |
| P-8 | minor | Phase 0 gate "CI green on an empty solution" proves nothing; "design reviewed and agreed" has no criterion | gate: every blocker in this review closed; recorder running; lab report |
| P-9 | minor | `fmu migrate` and "a v1 install migrated in the lab" are a release gate for a tool with no known users (R-eco §4: 1 star, no public feedback) | replace by a documentation page mapping v1 keys to v2; drop the gate |
| P-10 | minor | Effort: one maintainer plus AI sessions against 9 supervisors, 4 files adapters, 3 console adapters, 4 hosting families, a bundler, a signed updater and a plugin manager | adopt the cut in §6 below |

---

## 6. Overreach and recommended simplifications

| ID | Sev. | Claim | Why it promises too much | Suggested wording / cut |
|---|---|---|---|---|
| X-1 | major | §1 "on any way of running a server" | Profile C cannot install the game or framework, may not be able to wipe (D-46), has unauthenticated downloads (D-42) and a single detection source (D-47) | "on self-managed machines and on Pterodactyl/Pelican hosts with an API key: full automation. Elsewhere: detection, gating of FMU's own restarts, notification and post-wipe work; the exact list is printed by `doctor`." |
| X-2 | major | §3.1 profile C "Detection, readiness gate, … plugin updates, post-wipe cleanup" | Several items are pending LAB-5, LAB-6, LAB-8, LAB-10 and the gate is heuristic | list each as "planned, subject to LAB-n"; ship profile C as "notify + post-wipe" first |
| X-3 | major | §11.2 "Full solution with a client API key" | R-host §A.6: many "Pterodactyl-based" hosts hide the API page or run forks [L]/[NF]; R-host §A.6 also notes keys can be IP-restricted | "Full solution where the host exposes client API keys (to be checked per host)" |
| X-4 | major | INV-1, INV-2, INV-3 as absolutes | See D-25, D-2, D-17 | reword as given there |
| X-5 | major | §3.2 `managed` "removes the whole class of 'who restarts whom' problems" | It moves them to the agent's own lifecycle (D-30) | delete the sentence; keep "recommended" conditional on T-6 |
| X-6 | minor | §8.1 "Self-contained means admins install nothing" | SteamCMD + 32-bit libs, curl, tz data | "nothing for FMU itself" |
| X-7 | minor | §7.4 "shipped presets for common plugins" | A maintained list of other authors' data files that FMU deletes is a liability | ship as documented examples that are never applied automatically |
| X-8 | minor | §5.4 conditional rules "with the same expressive power" as the v1 scheme | The v1 scheme is defective (D-080) and v2's event model differs | drop; the per-event policies cover it |

Recommended scope for 2.0 (everything else becomes 2.x):

1. Profile A on `managed`, systemd and NSSM; vanilla, Oxide, Carbon; `public` branch.
2. Gate with the conservative rules of D-2/D-4, observe mode, notifications with a dead-man's switch.
3. Wipe engine with server-side trash, dry-run, undo, force window (D-11).
4. Companion plugin (countdown, `/nextwipe`, abort, health report). No profile C yet.
5. 2.1: Pterodactyl/Pelican profile B. 2.2: profile C, after the rented-host tests (T-4).
   2.3: plugin management (uMod + GitHub + inbox only).
6. Cut from the plan until someone asks: `script` supervisor with wrapper loops, Docker image of
   the agent, linux-arm64, LinuxGSM adapter, Codefling/Lone.Design sources, conditional rules,
   `fmu migrate`, canary, Nitrado, RustMaps, map-vote integration.

Simplifications that also remove risk: pre-download and classify by fact before the countdown
(D-7, D-23); one trash location per server (D-15); no source rewriting in the bundler (D-40); no
`Process` in the plugin (D-43).

---

## 7. What to settle before phase 0 can close

1. Rewrite §5.2 (D-1, D-2, D-3, D-4, D-5) and INV-2.
2. Specify the `managed` lifecycle (D-30) or stop recommending it.
3. Start the timeline recorder (T-1) before 2026-10-22.
4. Replace steps 3–4 of §6.1 with the per-supervisor table (D-22).
5. Decide the 2.0 scope (§6 above) and enumerate lab cells per phase (P-2).
