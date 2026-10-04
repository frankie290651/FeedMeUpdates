# R3 - Ecosystem map for FeedMeUpdates v2.0

Research date: 2026-10-04. Scope: tools that overlap with FMU (updates + wipes for Rust dedicated servers), what admins complain about, distribution/trust norms, and public footprint of FMU itself.

## How to read this report

- **VERIFIED** = I fetched the page and the claim is stated there. Source URL follows each claim.
- **UNVERIFIED** = inference, a secondary/marketing source, or a number I could not cross-check.
- **Caveat on numbers:** pages were read through an automated summarizer. Feature descriptions are reliable; **download/sales counters and "last updated" dates on Codefling pages are approximate** and a few look implausible (flagged inline). Re-check by hand before quoting any of them publicly.
- **Caveat on age:** most first-hand admin complaints I could reach are forum threads from 2016-2021. The pain points are corroborated by 2026 hosting guides, but the guides are vendor content, not admin voices.
- **Not reachable:** Reddit (r/playrustadmin) returned nothing through search and could not be fetched; Steam discussion threads returned only page chrome; Discord content is not indexable. Nothing in this report comes from those three sources.

---

## 1. Existing tools

### 1.1 Summary table

| Tool | Type | Platform | Price | Updates server | Updates framework | Updates plugins | Wipes | Restarts server itself |
|---|---|---|---|---|---|---|---|---|
| Smooth Restarter (uMod) | Plugin | any | Free, MIT | No | Detects Oxide release, triggers shutdown | No | No | **No** (shutdown only) |
| Timed Execute (uMod) | Plugin | any | Free, unlicensed | No | No | No | No | No |
| Update Notice (uMod) | Plugin | any | Free, MIT | notify only | notify only | No | No | No - **and currently dead** |
| Plugin Update Notifications (uMod) | Plugin | any | Free, MIT | No | No | notify (uMod only) | No | No |
| Automatic Plugin Updater (uMod) | Plugin | any | Free, GPLv3 | No | No | **installs** (uMod only) | No | No |
| Update Checker (Codefling, tofurahie) | Plugin | any | unclear | No | No | notify, 13 marketplaces | No | No |
| Update Checker Bot (Codefling) | Self-hosted Discord bot | Win/Linux | ~$15 | No | No | distributes plugin files via RCON | No | No |
| Wipe Data Cleaner / Auto Commands (uMod) | Plugin | any | Free | No | No | No | post-wipe data cleanup / commands | No |
| Wipe Info API / Wipe Schedule / Wipe Timer (uMod) | Plugin | any | Free | No | No | No | display/countdown only | No |
| Auto Wipe (Codefling, Tangerine) | Plugin | Oxide + Carbon | $19.99 | No | No | No | Yes, cron | **No** (needs external restarter) |
| Automated Maps (Codefling, Tangerine) | Plugin | Oxide + Carbon | $19.99 | No | No | No | map pool + Discord vote + RustMaps | No |
| Map Voter and Auto wipe script (Codefling, kaysharp) | Plugin + script | Win, Ubuntu, Pterodactyl, OGP | $30 | Yes ("auto-updates") | UNVERIFIED | No | Yes, incl. forced wipe, voting, RustMaps | via script |
| Carbon self-update | Framework built-in | any | Free | No | Yes (itself, on boot) | No | - | No |
| Carbon AutoWipe module | Framework module | Carbon only | Free | No | - | No | Yes, cron, map pool, placeholders | UNVERIFIED |
| LinuxGSM `rustserver` | Shell manager | Linux | Free | Yes | Yes (`mods-update`) | No | `map-wipe` / `full-wipe` + cron | Yes |
| rust-wipe-manager (GitHub) | Bash on top of LinuxGSM | Ubuntu/Debian | Free, MIT | Yes | Yes + rollback | No | Force wipe with Steam polling | Yes + watchdog |
| Didstopia/rust-server | Docker image | Linux/Docker | Free | Yes (build-id polling) | Oxide on boot | No | No | Yes |
| Pterodactyl "Rust Autowipe" egg | Panel egg | Pterodactyl | Free | on boot | on boot | No | only via **reinstall** | panel |
| AIO.Egg (SturdyStubs) | Panel egg | Pterodactyl | Free | on boot | Carbon/Oxide/Vanilla switch | No | No | panel |
| gamemann/Rust-Auto-Wipe | Go daemon | Pterodactyl API | Free, MIT | No | No | No | Yes, cron, multi-server | via panel API |
| clearshot-xs/pterodactyl-rust-auto-wipe | Node daemon | Pterodactyl API | Free, MIT | No | watches Oxide update for force wipe | No | Yes, cron, multi-server | via panel API |
| Pterodactyl Auto Wipe (Lone.Design) | Python service | Pterodactyl | $10 | No | No | No | Yes, multi-server | via panel API |
| Rust Wipe Bot (Codefling) | Bot | Pterodactyl/Pelican | $9.99 | No | No | No | Yes | via panel API |
| SERVER AUTO WIPER (Codefling) | Discord bot | Windows VPS | $30 | No | No | No | Yes, multi-server, voting | yes |
| MyRustServer | Desktop GUI | **Windows only** | EUR 9.99/mo - 29.99 lifetime | Yes | Yes (Oxide + Carbon) | Yes | Yes (wipe manager) | Yes |
| RSM Web (rustservermanager.com) | Web panel | Win + Linux | EUR 20 one-time | Yes, one-click rollback | Yes | browse/install | Yes, incl. forced wipe | Yes |
| RSMfmx | Desktop GUI | Windows | Free, open source | installer | installer | manager | No | auto-restart |
| RustAdmin | RCON desktop/web | Win / web | Free desktop; EUR 10-15/mo online | No | version check | plugin update check | "on wipe" action only | No |
| BattleMetrics RCON | SaaS RCON | any | $10/mo | No | No | No | No (scheduler = RCON commands) | No |
| RustPanel.gg | SaaS, WebRCON + SFTP | any host | Free / $9.99 / $19.99 mo | No | No | install from uMod | "Wipe Day Autopilot", dry-run | UNVERIFIED |
| AMP (CubeCoders) | Panel | Win/Linux | paid licence | Yes | UNVERIFIED | No | scheduled wipe task | Yes |
| Hosting-panel wipe schedulers | Host feature | host-locked | bundled | Yes | varies | No | Yes | Yes |

**Structural observation (my synthesis, grounded in the rows above):** nothing in the free/open tier covers server update + framework update + plugin update + wipe + restart + health check in one tool, cross-platform, without requiring a panel. The closest are MyRustServer (Windows-only, closed, paid) and RSM Web (closed, paid, single-server). FMU already occupies an unusual slot: open source, both OSes, panel-independent.

### 1.2 uMod plugins

**Smooth Restarter** (2CHEVSKII) - VERIFIED: daily scheduled shutdowns, countdown UI, API, and "automatic Oxide updates: server restarts when new Oxide.Rust versions are released"; 58.8K downloads, 616 watchers, MIT. Explicit limitation: "This plugin does not re-launch your server after shutdown, because it is not possible." https://umod.org/plugins/smooth-restarter
- Complaint, VERIFIED: thread "Restart not triggering after Oxide update" - several users report the Oxide-update detection silently not firing; one resorted to timer-based restarts; no fix documented. https://umod.org/community/smooth-restarter/28136-restart-not-triggering-after-oxide-update (approx. 5 years old)

**Timed Execute** (misticos) - VERIFIED: runs commands on in-game time, real time, once-chains, repeat; 58.3K downloads, 492 watchers; **no licence** ("original author reserves all rights... may be unavailable in the future"). https://umod.org/plugins/timed-execute
- Used as a poor man's scheduler; threads show config confusion (plugins reloading every 5 minutes, TimerOnce trouble). https://umod.org/community/timed-execute/27965-plugins-are-being-reloaded-every-5-minutes

**Update Notice** (Psystec) - VERIFIED: notified chat/GUI/Discord for Server, Client, Staging, Oxide, Carbon, DevBlog updates; 43.6K downloads, MIT. Page banner: **"CURRENTLY NOT FUNCTIONING. THE API HOST LEFT AND THIS PLUGIN WILL NOT WORK ANYMORE!"** https://umod.org/plugins/update-notice
- Lesson: it depended on a single third-party API. 43K installs' worth of demand is currently unserved on uMod.

**Plugin Update Notifications** (Whispers88) - VERIFIED: checks uMod only, console + Discord, 12h default interval, 15.7K downloads, MIT. https://umod.org/plugins/plugin-update-notifications

**Automatic Plugin Updater** (birthdates, now Mr_Padingy) - VERIFIED: actually installs updates from uMod; 4h default interval, filter by semver level, blacklist, Discord; 20.4K downloads, GPLv3; subject to uMod rate limits. https://umod.org/plugins/automatic-plugin-updater
- The 2.0.0 GitHub rewrite adds four policies (Ignore / Notify / **Stage** / Install), staging outside the live plugins folder, source + config backups, rollback, HTTP 429 handling, JSON audit history, downgrade blocking. https://github.com/mr-padingy/AutomaticPluginUpdater - VERIFIED. This is the best public design reference for safe plugin updating.

**Wipe Data Cleaner** (2CHEVSKII) - VERIFIED: deletes configured `oxide/data` files when a new save is created, wildcards, `/wipe` command; 37.7K downloads, MIT; JSON datafiles under oxide/data only. https://umod.org/plugins/wipe-data-cleaner
- Recurring fear, VERIFIED: "would it wipe after every restart?" https://umod.org/index.php/community/wipe-data-cleaner/32859-plugin-would-wipe-after-server-restarts

**Auto Commands** (MrBlue) - exists and is recommended by uMod staff for run-commands-on-wipe; I did not fetch its page (UNVERIFIED details). https://umod.org/plugins/auto-commands ; staff recommendation VERIFIED at https://umod.org/community/plugin-requests/28856-timed-commands-after-wipe-rust

**Wipe Info API** (MJSU) - VERIFIED: computes next wipe for 4-/5-week cycles, hook `OnWipeCalculated(nextWipe, daysTillWipe, isForcedWipeDay)`; 35.4K downloads, MIT; used by Magic Wipe Panel and required by Map Voter. https://umod.org/plugins/wipe-info-api
**Wipe Schedule** (k1lly0u) - VERIFIED: `/nextwipe`, join/broadcast announcements; 41.9K downloads, MIT. https://umod.org/plugins/wipe-schedule
**Wipe Timer**, **Realtime Wipe Info** - exist (not fetched). https://umod.org/plugins/wipe-timer , https://umod.org/plugins/realtime-wipe-info

**Backup Extended** (VisEntities) - VERIFIED: wraps Rust's built-in backup rotation, up to 8 versions, 14.2K downloads, unlicensed. https://umod.org/plugins/backup-extended

### 1.3 Codefling / Lone.Design / others (paid)

**Auto Wipe** (Tangerine) - VERIFIED features: cron schedules; wipes map/BP/data; deletes files **at server startup before other systems load**; backups; Discord webhook; file replacement from ZIP; convars for seed/worldsize; $19.99; Oxide + Carbon; v1.1.6 (June 2025). Explicit weakness: "restart command only shuts down the server", external restart software required. Counters reported as 22.6k downloads / 338 purchases / 7 reviews (approximate). https://codefling.com/plugins/auto-wipe
**Automated Maps** (Tangerine) - VERIFIED: map pool, Discord vote polls, RustMaps API pool population, cron; $19.99 (bundle with Auto Wipe $35). https://codefling.com/plugins/automated-maps
- So the plugin-only route to "scheduled wipe with rotating maps" costs about $35 and still needs a restarter and does not update anything.

**Map Voter and Auto wipe script** (kaysharp) - VERIFIED: in-game + Discord voting, procedural and RustMaps generation with filters, scheduled wipes including forced wipe, countdowns, "server automation including auto-updates", multi-server by identity; $30; requires Image Library, WipeInfoApi, Discord Extension; 85 versions, updated roughly weekly (v1.8.2). Reported ~86k downloads (approximate). A reviewer warning "BACK UP YOUR CONFIG FILE BEFORE UPDATING TO 1.7.1" indicates breaking config migrations. https://codefling.com/plugins/map-voter-and-auto-wipe-script
- Failure mode, VERIFIED: when RustMaps API went down / added rate limits (April 2023) the server looped trying to pick a map and the admin fell back to manual wiping. https://codefling.com/files/support/6555-new-rustmaps-api/
- This is the most feature-rich direct competitor on the wipe side. UNVERIFIED how deep its update handling goes (framework? plugins?).

**SERVER AUTO WIPER** (Default01) - VERIFIED: Discord bot on a Windows VPS, cron, map voting, title/description updates, multi-server, BP option; $30; v1.0.5 from 2023, not updated in ~3 years, **currently unavailable for purchase**. https://codefling.com/tools/server-auto-wiper
**Auto-Wipe Script** (Neko's Collection) - VERIFIED: Windows batch + exe + seed list via Task Scheduler; v1.0.0; documentation itself tells users to do a "dry-run" first. https://codefling.com/tools/auto-wipe-script
**Rust Wipe Bot** (Limmek) - VERIFIED: Pterodactyl/Pelican API, YAML configs, pre/post commands, wildcard file removal; $9.99; v1.0.0 July 2024, **no longer available for purchase**. https://codefling.com/tools/rust-wipe-bot
**Pterodactyl Auto Wipe** (lencorp, Lone.Design) - VERIFIED: Python service, unlimited servers, per-plugin file deletion, Discord embeds; $10; last update Feb 2024; no random seed support; no reviews, unanswered questions. https://lone.design/product/pterodactyl-auto-wipe/
- Pattern: three of the paid wipe tools are abandoned or delisted. Abandonment is a real, visible risk in this niche and an argument for open source.

**Update Checker** (tofurahie) - VERIFIED features: auto-discovers installed plugins and matches them across 13 marketplaces (uMod, Codefling, Lone.Design, Chaos, RustWorkshop, GitHub, ModPulse, ...), single self-editing Discord message, in-game browser, orphaned config detection; v4.6.4, very frequent updates. Reported "1.3 million downloads" is implausible as stated - UNVERIFIED. Price not shown. https://codefling.com/plugins/update-checker
**Update Checker Bot** (Magic Services et al.) - VERIFIED: self-hosted (Bun) Discord bot; admins drop plugin files in a thread and it routes them to the servers that need them via RCON; 11+ sources; ~$15; v2.1.3 Nov 2025. https://codefling.com/discord-bots/update-checker-bot
- Key insight: **paid plugins cannot be auto-downloaded** (they sit behind marketplace logins). The ecosystem's answer is "notify, then the admin supplies the file". An "inbox folder" pattern fits this.

**MyRustServer** (LosGranada) - VERIFIED: Windows-only GUI; installer for server/Oxide/Carbon; auto-update; auto-restart with crash recovery; wipe manager (date, time, type); plugin manager with update checks across several repositories; backups; Discord bot + webhooks; file verification. Pricing EUR 9.99 (1 month) / 19.99 (3 months) / 29.99 lifetime, tied to device counts. https://myrustserver.com/ and https://codefling.com/tools/myrustserver . Reported "3.1 million downloads / 171,600 sales" is implausible - UNVERIFIED; 68 reviews is plausible and makes it the most-reviewed tool here.
**RSM Web** - VERIFIED: Windows + Linux panel, DepotDownloader install, "one-click updates with release notes, and one click back to the previous version", wipe scheduler incl. Facepunch forced wipe with backup/update options, Oxide/Carbon switch, plugin browser; **single server per install**; EUR 20 one-time. https://rustservermanager.com/
**RSMfmx** (Quantum) - VERIFIED: formerly paid, now free and open source Windows GUI; last release Dec 2025. https://codefling.com/tools/rust-server-manager-fmx
**RustAdmin** - VERIFIED: RCON client with scheduler (once / weekdays / periodic / on event), plugin update checks, Oxide version check, and an "On server wipe" action category added Dec 2025; free desktop, EUR 10-15/month online. It reacts to wipes, it does not perform them. https://www.rustadmin.com/
**BattleMetrics RCON** - VERIFIED: triggers + scheduler operate on RCON actions (messages, kicks, bans, commands); $10/month. Cannot touch files, so it cannot update or wipe by itself. https://www.battlemetrics.com/rcon/rust
**RustPanel.gg** - VERIFIED (vendor page): off-box SaaS over WebRCON + host SFTP; "Wipe Day Autopilot" with 24h-to-5min countdowns in game and Discord, save/shutdown at T-0, map/BP deletion per policy, **dry-run testing and an abort button**; free 1 server, $9.99/mo 3 servers, $19.99/mo unlimited. https://rustpanel.gg/ . No independent reviews found - maturity UNVERIFIED.

### 1.4 Carbon built-ins

- **Self-update** - VERIFIED: on boot Carbon downloads its version list, patches itself from GitHub, then updates hooks before plugins load; controlled by `SelfUpdating` in `carbon/config.json`. The wipe-day production build is published "1 hour before the actual Rust server update drops". https://carbonmod.gg/owners/auto-updates
- **AutoWipe module** - VERIFIED: optional, off by default; cron-scheduled wipe entries (full or map); map pool with random pick and "temporary" maps; post-wipe commands and file/folder deletion; placeholders such as `[WIPE_DAY]`, `[WIPE_MONTH]` in hostname/description; `/nextwipe`; 30-second poll with a 1-hour cooldown. https://carbonmod.gg/owners/modules/optional-modules/autowipe-module
- Implication: on Carbon, the framework-update problem is largely solved and a basic wipe scheduler is free. FMU v2 must (a) support Carbon and (b) add value above this baseline: server (SteamCMD) updates, backups/rollback, health checks, BP cadence, external supervision. UNVERIFIED whether the module can restart the process or relies on the host supervisor.
- Timing claims, UNVERIFIED (hosting blogs): Carbon "typically patched within hours", Oxide "can take longer", "the first servers online grab the players". https://xgamingserver.com/blog/rust-carbon-vs-oxide/ . No verified market-share data exists: "We do not have verified install counts". https://www.vykix.com/blog/carbon-vs-oxide-the-2026-showdown/

### 1.5 Facepunch built-ins (often overlooked)

- **Server wipe timer** - VERIFIED: `wipetimer.wipeTimezone`, `wipeDayofWeek`, `wipeHourofDay`, `wipeUnixTimestampOverride`, `wipecronoverride` (Cronos syntax, e.g. `0 19 * * 4#1`), and `PrintWipe` to list the next 10 occurrences. Defaults for monthly / weekly / biweekly schedules. https://wiki.facepunch.com/rust/server-wipe-timer
- Implication: a wipe tool should **write these convars** so the in-game timer agrees with the real schedule. None of the tools I reviewed advertises doing so (absence of evidence, UNVERIFIED).

### 1.6 Linux managers, Docker, panels

- **LinuxGSM** - VERIFIED: `map-wipe`, `full-wipe`, `mods-install`, `mods-update`, cron examples, random seed when `seed=""`. No force-wipe awareness, no framework-readiness gate, no plugin updates. https://docs.linuxgsm.com/game-servers/rust . Community one-liner: `stop && wipeall && update-lgsm && update && mods-update && start`. https://umod.org/community/rust/15682-automating-wipe-process-with-updates
- **rust-wipe-manager** (wobujidao) - VERIFIED: daily restarts with warnings, graceful stop with fallback, first-Thursday full wipe that **polls Steam every 2 minutes for up to 2 hours and waits for the update before wiping**, aborts with a critical alert on timeout, watchdog for "process alive, but RCON silent", **Oxide rollback if plugins fail to load**, Telegram alerts at three verbosity levels. Single server, Telegram only, 1 star. https://github.com/wobujidao/rust-wipe-manager . Tiny adoption but the most complete operational design I found; worth reading in full.
- **Didstopia/rust-server** - VERIFIED: 271 stars, 103 forks, 18 open issues; `RUST_UPDATE_CHECKING`, `RUST_UPDATE_BRANCH`, `RUST_OXIDE_ENABLED`, `RUST_OXIDE_UPDATE_ON_BOOT`, `RUST_START_MODE`. Update check = SteamCMD build id for app 258550 compared with a stored `build.id`, then a restart helper. No wipe logic. https://github.com/Didstopia/rust-server , https://github.com/Didstopia/rust-server/blob/master/update_check.sh . Last commit date and Carbon support UNVERIFIED.
- **Pterodactyl "Rust Autowipe" egg** - VERIFIED: framework variable (vanilla/carbon/oxide), `MAP_URL`, `WORLD_SEED`, `REMOVE_FILES` list (defaults include Kits, Teleportation, ServerRewards data); **wipe happens by reinstalling the server** - no scheduling, no dedicated BP option. https://eggs.pterodactyl.io/egg/games-rust-autowipe/
- **AIO.Egg** - VERIFIED: switch between Carbon builds, Oxide, Vanilla; auto-update on start; no wipes. https://github.com/SturdyStubs/AIO.Egg
- **gamemann/Rust-Auto-Wipe** - VERIFIED: Go, Pterodactyl API, cron per server, world rotation (map/size/seed), hostname date templates, selective deletion, pre/post webhooks, env overrides; 29 stars, MIT. https://github.com/gamemann/Rust-Auto-Wipe
- **clearshot-xs/pterodactyl-rust-auto-wipe** - VERIFIED: Node, cron with timezone, multi-server, force-wipe detection by watching Oxide updates on the first Thursday, glob deletion, seed lists / map URLs, Docker; 6 stars, MIT. https://github.com/clearshot-xs/pterodactyl-rust-auto-wipe
- **AMP** - VERIFIED complaints: scheduled wipe-and-restart throwing `InvalidOperationException`, closed unresolved (June 2023). https://discourse.cubecoders.com/t/rust-amp-server-schedule-wipe-not-working/4395 ; related: https://github.com/CubeCoders/AMP/issues/622 , https://discourse.cubecoders.com/t/odd-rust-server-behaviour-auto-map-wipe-not-working/4532 (titles only, not fetched).
- **Host wipe schedulers** - VERIFIED example (Loafhosts, June 2026): cadences weekly / bi-weekly / monthly / first Thursday / custom, timezone, map vs blueprint vs full, seed rotation, RCON announcements, wipe history, dedicated permission because it is destructive. https://loafhosts.com/guides/rust-wipe-scheduler . This is the feature baseline that rented-server admins already expect.
- **TCAdmin** community config "Automatic wipe/update, Oxide, Plugin Installer" exists (not fetched). https://community.tcadmin.com/topic/11464-rust-automatic-wipeupdate-oxide-plugin-installer/

### 1.7 Discord bots and feeds

- **Rust Force Wipe bot** - VERIFIED: countdown to the monthly force wipe, about 1,020 servers. It is a calendar, not an update detector. https://top.gg/bot/1029962998611181618
- I did **not** find a maintained public bot that announces Rust server/client/Oxide/Carbon releases from live feeds; the uMod plugin that did this is dead (1.2). Private community Discords very likely do this (UNVERIFIED, not indexable).
- Update detection sources seen in the wild: SteamCMD build id (Didstopia), GitHub `/releases/latest` for Oxide.Rust (https://umod.org/community/rust/36517-release-rust-oxide-automated-update-script-for-linux-users), Carbon's own version list.

---

## 2. What admins complain about and want

### 2.1 Force-wipe day

1. **Framework lag.** VERIFIED: uMod admin Wulf - Oxide timing "depends on what change, if it was pre-patched for, if someone is around, and how fast the build server is"; they "generally try to get it done within 30 mins to 1 hour". https://umod.org/community/rust/3181-how-long-does-an-oxide-update-take-after-a-rust-update . Another thread summarises the range as "5 minutes to 24 hours". https://umod.org/community/rust/15682-automating-wipe-process-with-updates
2. **Blind updating breaks servers.** VERIFIED: "if it updates rust and forceinstalls old Oxide, it will stuck on start"; Wulf advises against "blindly updating". https://oxidemod.org/threads/automating-updates-for-rust-oxide-server.17804/ (2016-2017)
3. **Running vanilla in the gap is dangerous** (plugin data corruption while plugins are absent) - same thread, VERIFIED as a stated pain point. The counter-advice (go vanilla temporarily) appears in https://umod.org/community/rust/3181-how-long-does-an-oxide-update-take-after-a-rust-update . Admins disagree, so this must be a **policy option**, not a hard-coded behaviour.
4. **Server looks online but is unplayable.** VERIFIED: owner found the server showing "online" while pending updates made it unjoinable. https://oxidemod.org/threads/forced-wipes-and-what-to-do-as-owner.26942/ (2017). Protocol mismatch threads: https://umod.org/community/rust/38591-protocol-mismatch-after-recent-rust-update , https://oxidemod.org/threads/wrong-connection-protocol-server-update-required.16365/
5. **Build-server outages upstream.** VERIFIED: a mismatch was caused by uMod's build server failing to publish. Same uMod thread as above. A tool must tolerate "no compatible framework build yet" for hours and say so clearly.
6. **Plugins broken by the patch.** VERIFIED in 2026 guides: "plugins themselves sometimes need updates from their authors after a big patch" (https://www.gghost.games/knowledgebase/85/Rust-wipe-schedule-what-force-wipe-day-does-to-your-server-and-how-to-handle-it.html); "disable problematic extensions rather than allowing them to destabilize the server" (https://hostari.com/blog/rust-september-october-2026-update-force-wipe/); "a running server missing three plugins keeps its population" (https://guildorder.com/games/rust/guides/plugin-stack-governance-and-wipe-day-updates, Aug 2026).
7. **Framework files get overwritten by the game update** and must be reinstalled. https://xgamingserver.com/blog/rust-built-different-update-server-admins/ (June 2026)
8. **Recommended manual procedure** (guildorder): rehearse against the `staging` branch with the real plugin stack and note compile errors; keep a plugin "register" (source, version, config, dependencies) as the last-known-good to restore from; on wipe day update game, confirm framework matches, boot with logs open. VERIFIED at the guildorder URL above. This is a ready-made spec for a "pre-flight" feature.
9. **Post-restart verification** is prescribed by hosts: check "the map is new, players can connect, and (if you run Oxide) that plugins loaded" (gghost URL above).

### 2.2 Mid-month updates

- VERIFIED: clients auto-update, then get "Wrong connection protocol: server update required" until the server is patched (threads in 2.1 item 4). Needs fast detection, short countdown, and the same framework gate.
- I found no admin discussion distinguishing optional/hotfix patches from protocol-changing ones. UNVERIFIED whether admins want different policies per patch type; plausible, not evidenced.

### 2.3 Wipe schedules

- VERIFIED: common cadences are weekly Thursday, bi-weekly Thursday, monthly forced only, and "2x or 3x" servers wiping twice a week. https://survivalconfigs.com/reference/rust/wipe-schedule
- VERIFIED: BP policy is per-server and must be communicated; blueprints do not reset automatically with the monthly wipe. https://hostari.com/blog/rust-september-october-2026-update-force-wipe/ . One source claims a quarterly BP cycle (Mar/Jun/Sep/Dec) - UNVERIFIED, single secondary source, and it conflicts with another blog describing a June 2026 wipe as clearing blueprints.
- VERIFIED: cron is the lingua franca (Carbon AutoWipe, Tangerine Auto Wipe, gamemann, clearshot, Facepunch `wipecronoverride`).
- VERIFIED pitfall: wrong `server.identity` makes automation wipe the wrong (or no) save directory. survivalconfigs URL above.
- "BP wipe every N wipes" - I found no tool advertising a counter-based rule. Not found as an explicit admin request either. UNVERIFIED demand; cheap to implement.

### 2.4 Map rotation

- VERIFIED: map pools, Discord and in-game voting, RustMaps generation with filters are what the paid plugins sell (1.3). RustMaps public API v4 requires an API key and exposes generation limits. https://github.com/HandyS11/RustMapsApi (third-party .NET client, MIT); CLI: https://github.com/maintc/rustmaps-cli . Tier limits UNVERIFIED (official docs not fetched).
- VERIFIED failure mode: RustMaps outage/rate limit stalled a wipe (1.3). Any integration needs a fallback (next pool entry or random seed) and a timeout.
- VERIFIED gap: Lone.Design's Pterodactyl Auto Wipe has no random seed support and users ask for it. https://lone.design/product/pterodactyl-auto-wipe/

### 2.5 Announcements

- VERIFIED: hostname/description placeholders (Carbon `[WIPE_DAY]`/`[WIPE_MONTH]`; gamemann date templates), `/nextwipe`, join/broadcast notices (Wipe Schedule), countdowns 24h to 5min in game and Discord (RustPanel), Discord embeds (Lone.Design tool). "Ambiguous wipe announcements lose returning players". https://hostingbuff.com/guides/rust-server-wipe-guide.html

### 2.6 Post-wipe tasks and plugin data

- VERIFIED: long-standing demand for clearing plugin data at wipe; the sandbox blocked a naive plugin ("you can't delete files with the System.IO block"). https://umod.org/community/plugin-requests/8545-clear-data-files-on-wipe-rust
- VERIFIED: run commands at wipe, including delayed ones ("grant permissions 3200 seconds post-wipe"); solved by combining two plugins. https://umod.org/community/plugin-requests/28856-timed-commands-after-wipe-rust
- VERIFIED: keep some data while wiping the rest (thread title "Wipe all Oxide data except for custom usergroups?"; body not fetched). https://umod.org/community/rust/18394-wipe-all-oxide-data-except-for-custom-usergroups
- VERIFIED: typical default delete lists include Kits, Teleportation, ServerRewards data (Pterodactyl egg, 1.6).

### 2.7 Backups, rollback, health, notifications

- VERIFIED (host guide, March 2026): back up before plugin installs, config changes, wipes, framework updates and every major game update; do not rely on a single rotating backup; test restores. https://pinehosting.com/blog/rust-server-backups-explained-protecting-player-progress-and-custom-maps/
- VERIFIED: "which files do I back up?" is a recurring question. https://umod.org/community/rust/2833-server-backup-which-files , https://umod.org/community/rust/3797-making-a-backup-which-files-are-needed (titles; bodies not fetched)
- VERIFIED: rollback is a selling point for RSM Web ("one click back to the previous version"), rust-wipe-manager (Oxide rollback) and AutomaticPluginUpdater 2.0.
- VERIFIED: hung-but-alive detection and critical alerts on timeout exist only in rust-wipe-manager among the tools reviewed.
- VERIFIED: silent startup hangs with no log are a classic support case. https://umod.org/community/rust/30448-server-not-starting-after-update

### 2.8 Multi-server

- VERIFIED: the uMod automation thread was opened by someone running several servers who wanted weekly/monthly processes automated; replies conclude no packaged solution existed. https://umod.org/community/rust/15682-automating-wipe-process-with-updates (about 6 years old)
- VERIFIED: every Pterodactyl wipe daemon is multi-server; the desktop/panel tools are not (RSM Web: one server per install; MyRustServer: device-limited). Staggered restarts across a network: not found in any tool (absence, UNVERIFIED).

---

## 3. Distribution and trust

### 3.1 Where admins get tools

uMod (free, open source), Codefling (free + paid), Lone.Design (paid), ChaosCode (search result title now reads "RustHQ - Rust Marketplace & Community", rebrand UNVERIFIED), GitHub. Lists: https://lone.design/where-to-download-rust-plugins/ , https://cybrancee.com/learn/knowledge-base/trusted-rust-plugin-websites-for-your-server/ (states that unverified sources bring "security risks, performance issues, and compatibility problems").

### 3.2 uMod rules

- VERIFIED sandbox namespaces blocked: `System.Diagnostics`, `System.Threading`, `System.Runtime.InteropServices`, `System.IO` (few exceptions), `System.Net` (few exceptions), `System.Reflection`, `System.Security`. "Extensions are not sandboxed" and uMod "rarely accepts extensions". https://umod.org/guides/server-management/security
- VERIFIED approval guide: no Reflection; a permissible open-source licence is required; permissions, config, Lang API, documentation; two passing reviews. It does not mention external processes explicitly. https://umod.org/guides/development/approval-guide
- **Consequence for FMU (inference, UNVERIFIED but strongly implied):** the current `FeedMeUpdates.cs` has `using System.Diagnostics;` and `using System.IO;` and calls `Process.Start` in many places (checked in the local source). In that form it would not pass uMod review. A uMod-listable plugin would have to be a thin, sandbox-clean companion (countdown UI, hooks, RCON-visible commands, web requests through Oxide's WebRequests) that only signals an external agent.
- Context, VERIFIED: uMod's admin said "The uMod project is probably never gonna happen but Oxide is still actively being developed" and that a replacement website is planned. https://umod.org/community/general-support/56555-oxide-umod-and-carbon (about a year old). Official plugin auto-update was promised around 2019 and never shipped. https://umod.org/community/general-support/9498-automatic-updates-for-plugins

### 3.3 Codefling rules

VERIFIED at https://codefling.com/upload-guidelines/ :
- External communication only when needed for described features; must document which services are contacted and what is sent. **Prohibited: analytics, usage tracking, hidden network activity, silent update checks, remote kill switches.**
- No external licence keys / activation servers / purchase verification.
- No obfuscation that prevents review; source on request.
- "Vibe coded" submissions rejected except free, transformative content.
- Changelogs must flag breaking changes and new dependencies.
VERIFIED at https://codefling.com/creator/ : non-exclusive but price parity; six months of support for paid content.
- Codefling has a Tools category that accepts executables and bots (MyRustServer, RSMfmx, wipe bots), and RSMfmx carries a curator "verified for safety" note. So FMU's exe + plugin shape fits Codefling Tools as-is.
- **Consequence for FMU:** the self-update feature must be documented and user-controllable (ideally opt-in) to stay clear of the "silent update checks" rule. Every endpoint FMU contacts (Steam, GitHub, uMod, Discord webhook) should be listed in the description.

### 3.4 Lone.Design

VERIFIED: 10% commission; products reviewed; vendors must fix reported defects within 15 days and respond within 48 hours or risk removal. https://lone.design/terms-of-service-restrictions/

### 3.5 Pricing norms

Wipe plugins/bots $10-$30 one-time; managers EUR 20-30 one-time or about EUR 10/month; SaaS panels $10-$20/month; the uMod layer is free under MIT/GPL. (All from the sources in section 1.)

### 3.6 What builds trust (evidence-backed)

- Open source and a real licence: uMod requires it; two popular uMod plugins carry "may be unavailable in the future" warnings because they lack one (Timed Execute, Backup Extended).
- Not being abandoned: three paid wipe tools are stale or delisted (1.3); Update Notice died with its API.
- Dry-run and abort: RustPanel markets them; the Codefling batch script tells users to dry-run.
- Staging, backups, rollback, audit trail: AutomaticPluginUpdater 2.0, RSM Web, rust-wipe-manager.
- Clear answer to "will this wipe on every restart?" (Wipe Data Cleaner thread).
- Destructive actions behind a dedicated permission (Loafhosts).
- Discord support from the author is highlighted on nearly every Codefling listing.
- Checksums / signed releases: FMU already ships SHA files and a GPG-verified release commit; I found no competitor advertising this. Differentiator worth stating loudly.

---

## 4. Public footprint of FeedMeUpdates

- VERIFIED: GitHub only. 1 star, 0 forks, 0 open issues, 67 commits, MIT, C# plugin + .NET 8 companion app, Windows + Linux. https://github.com/frankie290651/FeedMeUpdates , https://github.com/frankie290651/FeedMeUpdates/issues
- VERIFIED: latest release tag 1.6.5, dated "January 3" (year not shown, presumably 2026 - UNVERIFIED), 6 assets, verified signature. https://github.com/frankie290651/FeedMeUpdates/releases
- VERIFIED: web searches for the name return only the GitHub repo. **No uMod, Codefling, Lone.Design or forum thread found**; Reddit could not be checked. There is effectively no public feedback yet.
- GitHub API was not accessible from this session, so release download counts and closed-issue history are UNVERIFIED.
- Repo description says "for Rust servers running Oxide" - no Carbon support advertised.

---

## (a) Prioritized features for v2

Tags: **MH** must-have, **DIFF** differentiator, **NICE** nice-to-have.

| # | Feature | Tag | Justification (evidence) |
|---|---|---|---|
| 1 | **Framework-readiness gate**: on a Rust update, wait for a matching Oxide/Carbon build, with configurable policy (wait up to N hours / start vanilla / start with plugins disabled) and a critical alert on timeout | MH | The single most repeated pain point (2.1 items 1-5); only rust-wipe-manager polls-and-waits |
| 2 | **Carbon support** (detect framework, respect Carbon self-update, coexist with its AutoWipe module) | MH | Carbon is promoted by hosts as faster on wipe day; FMU is Oxide-only today (1.4, 4) |
| 3 | **Post-restart health check**: process up, RCON responsive, server listed/joinable, framework loaded, plugin compile errors parsed and reported | MH | "Online but unplayable" and silent hangs (2.1.4, 2.7); hosts prescribe exactly this checklist |
| 4 | **Automatic rollback** to last-known-good (server build where possible, framework, plugins, configs) when the health check fails | MH | Selling point of RSM Web, rust-wipe-manager, AutomaticPluginUpdater; FMU already restores from `temp_backup` on update failure - extend to health failure |
| 5 | **Cron-based wipe schedules** with timezone, multiple named schedules, wipe type per schedule (map / BP / full), plus a "force wipe" preset | MH | Cron is the standard everywhere (2.3); FMU currently has one custom date/time |
| 6 | **Dry-run / preview mode** that prints exactly which files would be deleted, which commands run, next N wipe dates; plus an abort command during countdown | MH | Trust: RustPanel sells it, Codefling script recommends it, wrong `server.identity` is a known footgun |
| 7 | **Per-plugin data wipe profiles**: keep/delete lists with globs, shipped presets for common plugins, different lists per wipe type | MH | Oldest recurring request (2.6); every wipe tool has some version of it |
| 8 | **Failure notifications with severity levels** (Discord first; generic webhook) incl. "update not available", "framework not ready", "server did not come back" | MH | Only rust-wipe-manager does levels, and only on Telegram |
| 9 | **Backups with retention policy** (keep N, keep last pre-wipe, size/disk guard) and a documented restore command | MH | Host guidance: never a single rotating backup, back up before every destructive step (2.7) |
| 10 | **Staggered multi-server orchestration**: one config for many servers, offsets between restarts, shared SteamCMD download, per-server overrides | DIFF | Multi-server demand is real (2.8); no reviewed tool staggers; panel daemons need Pterodactyl |
| 11 | **Staging pre-flight**: before wipe day, compile the live plugin stack against the staging branch and report which plugins will break | DIFF | Recommended manual practice (guildorder); nobody automates it |
| 12 | **Plugin quarantine on boot**: auto-unload/disable plugins that fail to compile after an update and report them, instead of failing the whole update | DIFF | "A running server missing three plugins keeps its population"; hosts advise disabling broken plugins |
| 13 | **Plugin update policies** per plugin: ignore / notify / stage / install, with source+config backup and rollback | DIFF | Mirrors AutomaticPluginUpdater 2.0; Wulf warned against blind installs |
| 14 | **Plugin "inbox" folder** for paid plugins: drop a file, FMU installs it at the next window with backup | DIFF | Paid plugins cannot be auto-downloaded; Update Checker Bot exists solely for this |
| 15 | **Sync Facepunch `wipetimer.*` convars** and the hostname/description placeholders from the same schedule | DIFF | Native timer exists (1.5); no tool advertises syncing it; placeholders are baseline (2.5) |
| 16 | **Map pool / rotation**: seed lists, levelurl lists, random seed, no-repeat, with fallback when a source fails | MH | Baseline in every wipe product; Lone.Design tool criticised for lacking random seed |
| 17 | **RustMaps integration** (generate/pick with filters) behind an optional API key, with timeout and fallback | NICE | Sold by Map Voter and Automated Maps; outage stalled wipes in 2023 |
| 18 | **BP wipe rules**: every N wipes, specific months, or follow force wipe only | DIFF | BP policy varies per server (2.3); counter rule not found elsewhere (demand itself UNVERIFIED) |
| 19 | **Post-wipe command timeline** with delays (immediately, +10 min, +1 h) | NICE | Explicit request solved today by two plugins combined (2.6) |
| 20 | **Wipe announcements**: in-game countdown UI, `/nextwipe`, Discord embed with map preview and connect info | MH | Baseline across competitors (2.5) |
| 21 | **Update announcer**: Discord/in-game notice when server, client, staging, Oxide or Carbon builds change, using first-party sources only (Steam, GitHub, Carbon) | DIFF | Update Notice (43.6K downloads) is dead because it relied on one third-party API |
| 22 | **Audit log / history** of every update and wipe (what changed, versions before/after, duration, result) | NICE | Loafhosts "wipe history"; AutomaticPluginUpdater JSON audit; supports trust |
| 23 | **Sandbox-clean companion plugin + standalone agent**, so the plugin is listable on uMod and the agent also works with no plugin (pure RCON) | DIFF | uMod sandbox blocks Process/IO (3.2); also unlocks vanilla and Carbon servers |
| 24 | **Panel/container awareness**: Pterodactyl/Pelican (API power actions), Docker, systemd, NSSM - detect who supervises the process and restart through it | DIFF | Large share of tools are Pterodactyl-only; "plugin can't relaunch the server" is the stated weakness of Smooth Restarter and Tangerine Auto Wipe |
| 25 | **Map voting** (Discord / in-game) | NICE | Popular, but two mature paid plugins own it; better integrated than rebuilt (see b) |

Suggested order of work: 1-9 make v2 safe and complete on the core promise; 10-15 are where FMU can be clearly better than anything free; the rest follows demand.

## (b) Things to integrate with rather than reinvent

1. **Carbon self-update** - let Carbon patch itself on boot; FMU only verifies the resulting version and gates on it. https://carbonmod.gg/owners/auto-updates
2. **Carbon AutoWipe module** - detect it and either defer to it or disable it explicitly to avoid double wipes. https://carbonmod.gg/owners/modules/optional-modules/autowipe-module
3. **Facepunch wipe timer convars** - write them, do not build a parallel in-world timer. https://wiki.facepunch.com/rust/server-wipe-timer
4. **Wipe Info API hook** (`OnWipeCalculated`) - expose FMU's schedule through the same hook shape so Magic Wipe Panel-style UIs and other plugins keep working. https://umod.org/plugins/wipe-info-api
5. **Smooth Restarter** - coexist: detect it, and offer its API/commands for countdown UI where admins already use it. https://umod.org/plugins/smooth-restarter
6. **Update Checker (tofurahie)** for multi-marketplace version discovery - FMU should not try to scrape 13 stores; consume its results or only cover uMod + GitHub natively. https://codefling.com/plugins/update-checker
7. **Map Voter / Automated Maps** - accept "next map" from them (they write seed/levelurl) and let FMU do the stop/update/wipe/start. Automated Maps already "detects when files were deleted and does its work". https://codefling.com/plugins/automated-maps
8. **RustMaps API** via an existing client. https://github.com/HandyS11/RustMapsApi (.NET, MIT), https://github.com/maintc/rustmaps-cli
9. **Pterodactyl/Pelican API, LinuxGSM commands, systemd, NSSM** for power control and (on LinuxGSM) for `update` / `mods-update`. https://docs.linuxgsm.com/game-servers/rust
10. **SteamCMD build-id check** as in Didstopia's script, and GitHub `releases/latest` for Oxide. https://github.com/Didstopia/rust-server/blob/master/update_check.sh
11. **BattleMetrics / RustAdmin** - do not build RCON admin, bans or player tooling; just emit webhooks and RCON-visible messages they can trigger on. https://www.battlemetrics.com/rcon/rust , https://www.rustadmin.com/
12. **Rust's built-in backup rotation** (what Backup Extended wraps) for save files; FMU adds config/plugin/data snapshots around it. https://umod.org/plugins/backup-extended

## Open questions I could not answer

- Real admin sentiment on Reddit/Discord in 2025-2026 (unreachable).
- Whether Map Voter's "auto-updates" include the framework and plugins.
- RustMaps API tier limits and pricing.
- Actual Oxide vs Carbon share, and actual Oxide wipe-day latency in 2026 (only vendor blogs).
- Codefling counters for MyRustServer and Update Checker.
- FMU release download counts.
