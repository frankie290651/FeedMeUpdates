# R2 — Technical research for FeedMeUpdates v2.0

Research date: **2026-10-04**. All "live" observations below were taken on that day.

## How to read this report

- **VERIFIED** = read in this session from a primary source: source code cloned from GitHub (commit dates given), official docs/wiki, or a live API response.
- **UNVERIFIED** = third-party article, forum post, or my own background knowledge that I could not confirm against a primary source in this session. Treat as a hypothesis to test.
- Limits of this session: `api.steampowered.com`, `api.steamcmd.net` and `umod.org` were not reachable with curl (egress proxy), so they were read through a fetch tool that summarises pages. Values such as JSON field names from those endpoints were relayed by that tool, not seen raw. I could not download the Rust server itself, so nothing here was checked against the decompiled `Assembly-CSharp.dll` except the small decompiled snippets embedded in Oxide's `docs.json`.

Primary sources cloned and read:

| Repo | HEAD date | Used for |
|---|---|---|
| `OxideMod/Oxide.Rust` (master/develop, 900 commits of history) | 2026-10-03 | version logic, release workflow, commit timestamps of every monthly patch |
| `OxideMod/Oxide.CSharp` | 2026-03-04 | compiler download, language version, preprocessor symbols |
| `OxideMod/Oxide.Compiler` | 2026-02-26 | Roslyn version, target framework |
| `OxideMod/Oxide.Core` | 2026-03-04 | `WebRequests` library |
| `OxideMod/Oxide.Docs` | 2026-10-02 | install docs, cfg precedence, `docs.json` (decompiled hook sites) |
| `OxideMod/Actions` | 2026-08-06 | version numbering, prerelease publishing |
| `CarbonCommunity/Carbon` (main) | 2026-10-03 | self-updater, layout, commands, compiler, vendors |
| `CarbonCommunity/Carbon.Documentation` | 2026-09-15 | install, auto-update, conditionals, modules |
| `Facepunch/webrcon` (gh-pages) | 2021-02-20 | WebRCON wire format |
| `Nikedemos/Lone.DesignUpdateChecker` | 2022-10-20 | Lone.Design API URL |

---

## 0. Verdict on the four v1 assumptions

| v1 assumption | Verdict | Evidence |
|---|---|---|
| "Force wipe is always the first Thursday at 19:00 UTC" | **Wrong for about seven months a year.** The day (first Thursday) holds; the time is 19:00 **London local time**: 19:00 UTC in winter (GMT), **18:00 UTC in summer (BST)**. | Facepunch wiki wipe-timer page: defaults are "First Thursday at 19:00 (London time)", timezone default `Europe/London`. Oxide patch commits land ~17:15–17:30 UTC Apr–Oct and ~18:10–18:45 UTC Nov–Mar, every year 2022–2026 (table in §7). |
| "The Rust build id must be fetched via SteamCMD" | **Wrong.** `https://api.steamcmd.net/v1/info/258550` returns every branch's `buildid` and `timeupdated` with no auth. PICS libraries (SteamKit2 etc.) are a second option. SteamCMD should be the fallback, not the only path. | Live response 2026-10-04 (§1). |
| "Oxide is the only framework" | **Wrong.** Carbon is a full alternative with its own layout, release channels, self-updater and `c.` commands. Oxide and Carbon cannot coexist in one install. Rust's own server-browser tag list has separate Oxide and Carbon tags. | Carbon source and docs (§4); Facepunch wiki tags page (§7). |
| "Staging branch is unsupported" | **Wrong.** Carbon publishes a dedicated staging build (`rustbeta_staging_build`, updated 2026-10-03) plus release/aux builds. Oxide has `staging`, `release`, `aux01`, `aux02` git branches and a prerelease workflow that publishes non-master builds. | Carbon release list and `api.carbonmod.gg/releases`; Oxide.Rust `git ls-remote` and `prerelease.yml` (§2–§4). |

Other findings that should change the v2 design:

1. **Oxide and Carbon production builds are published *before* the Rust update goes public on wipe day** (Oxide ~30–45 min before, Carbon states 1 hour before). An updater that installs "latest framework" as soon as it appears will put a new-protocol framework on an old-protocol server. Gate framework install on the Rust build actually installed.
2. **`https://umod.org/games/rust/latest.json` is stale**: it returned 2.0.7598 (2026-08-18) while GitHub's newest tag is 2.0.7801 (2026-10-03). Use GitHub for Oxide versions.
3. **Both Oxide's and Carbon's `webrequest` libraries disable TLS certificate validation** process-wide (`ServerCertificateValidationCallback => true`). Anything a plugin downloads over HTTPS inside the server is not authenticated by TLS; verify hashes.
4. **The save protocol number has increased on every first-Thursday update from 2022 to 2026** (232 → 289). That is the mechanism of the forced map wipe.
5. **Mandatory non-wipe updates happen mid-month** (network protocol bump only), e.g. the Halloween update announced for 2026-10-22. The updater must handle "update without wipe" as a first-class case.
6. **Steam branch list is not fixed.** On 2026-10-04 the server app had `public`, `release`, `staging`, `aux02`, `last-month`, `debug`. No `aux01` or `aux03` existed that day. Discover branches at runtime.

---

## 1. Detecting a new Rust dedicated-server build

App id **258550** (server). Depots: 258551 (Windows), 258552 (Linux), 258554 (common content). Client app is 252490.

### 1.1 api.steamcmd.net — VERIFIED (live, via fetch tool)

`GET https://api.steamcmd.net/v1/info/258550` (optional `?pretty=1`). Response top level: `data`, `status`. Under `data.258550`: `_change_number` (39437714 at time of reading), `_sha`, `common`, `extended`, `depots`. `depots.branches.<name>` carries `buildid`, `timeupdated` (unix seconds) and optionally `description`.

Branches observed 2026-10-04:

| Branch | buildid | timeupdated (UTC) | description |
|---|---|---|---|
| `public` | 25681086 | 2026-10-02 22:10 | — |
| `release` | 25681086 | 2026-10-02 15:53 | "Release, before public" |
| `staging` | 25681182 | 2026-10-02 15:58 | — |
| `aux02` | 24494353 | 2026-07-31 14:02 | "Up and coming" |
| `last-month` | 25454815 | 2026-09-22 10:51 | "last-month" |
| `debug` | 17041002 | 2025-01-17 15:05 | "debug" |

Notes:
- `release` and `public` had the same buildid, with `release` updated about six hours earlier. That matches the description "Release, before public": the build sits on `release` first and is then promoted.
- `depots` also has a `privatebranches` key. No `pwdrequired` field was reported on any branch.
- Service facts from https://www.steamcmd.net/ (VERIFIED): no authentication, "currently made available for free and without specific restrictions", no documented rate limit, updates "generally available within seconds", run by one person (Jona Koudijs), open source and self-hostable (github.com/steamcmd), explicitly non-official. Second endpoint: `GET /v1/version`.
- Reliability judgement: good primary signal, but it is a single volunteer-run service with no SLA. Poll modestly (once a minute at most near wipe time, far less otherwise) and keep a fallback.

### 1.2 Steam Web API `ISteamApps/UpToDateCheck` — UNVERIFIED for 258550

- Endpoint: `https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/?appid=<id>&version=<n>`, no key needed. Response fields reported by a third-party write-up: `success`, `up_to_date`, `version_is_listable`, `required_version`, `message` (https://github.com/m-this/tf2-archipelago/pull/181).
- The same write-up found it works for a Source *game* app id (440) but fails for the Source *server tool* app id with "Couldn't get app info for the app specified". The `version` it compares is the game's own patch version, not the Steam buildid.
- I could not call it for 258550 or 252490 (robots.txt blocked the fetch tool; curl blocked by the proxy). Assume it is **not usable** for Rust until you test it yourself.

### 1.3 SteamDB — UNVERIFIED (background knowledge)

- https://steamdb.info/app/258550/depots/ shows the same branch data. Oxide's own docs point owners there to track server updates (VERIFIED: Oxide.Docs `install-oxide.md`).
- SteamDB has no public API and its FAQ asks people not to scrape it. Do not build on it.

### 1.4 Steam PICS libraries — UNVERIFIED (background knowledge)

- SteamKit2 (.NET), `steam-user` (Node), `steam` (Python) can log in anonymously and call PICS product-info for app 258550, returning the same `depots.branches` data that SteamCMD and api.steamcmd.net expose. They can also subscribe to change numbers, which is how api.steamcmd.net itself stays current.
- This is the most robust non-SteamCMD option but it is an external process, not something to run inside a Mono plugin.

### 1.5 Installed build id without Steam — UNVERIFIED (background knowledge)

- `<install_dir>/steamapps/appmanifest_258550.acf` holds `buildid`, `StateFlags` (4 = fully installed) and the branch key (`UserConfig`/`MountedConfig` → `BetaKey`). Reading it is cheap and avoids starting SteamCMD.

### 1.6 SteamCMD commands

VERIFIED against the Facepunch wiki (https://wiki.facepunch.com/rust/Creating-a-server), the Valve wiki (https://developer.valvesoftware.com/wiki/SteamCMD), Oxide docs and Carbon docs:

```
# install / update (Valve: set install dir before login)
steamcmd +force_install_dir <dir> +login anonymous +app_update 258550 validate +quit
# explicit branch
steamcmd +force_install_dir <dir> +login anonymous +app_update 258550 -beta public validate +quit
steamcmd +force_install_dir <dir> +login anonymous +app_update 258550 -beta staging validate +quit
```

- Syntax: `app_update <app_id> [-beta <name>] [-betapassword <pw>] [validate]`.
- Facepunch wiki: "Once you go up in buildID you'll need to specify the `public` branch to go back to the default build." So always pass `-beta public` explicitly rather than omitting `-beta`.
- Oxide docs: "Don't use `release` branch because it is not always in sync with the `public` branch."
- `validate` overwrites modified files. This matters for Oxide, which replaces game DLLs: a `validate` reverts them to vanilla, so Oxide must be reinstalled afterwards. Carbon docs use exactly this as the way to remove Oxide.
- Cross-platform download: `+@sSteamCmdForcePlatformType windows|linux|macos`.
- Linux: run `./steamcmd.sh`, not as root, and 32-bit libraries are needed on 64-bit systems. Scripts: `+runscript file.txt`.

Remote build id through SteamCMD (VERIFIED as LinuxGSM's method, commit 458fec5, May 2020):

```
steamcmd +login anonymous +app_info_update 1 +app_info_print 258550 +quit
```

- Output is VDF text; read `depots → branches → <branch> → buildid`.
- **Stale cache quirk (VERIFIED):** LinuxGSM had to delete every `appinfo.vdf` under `$HOME` before running this, because SteamCMD otherwise returned the cached, outdated build id. `+app_info_update 1` alone was not enough.

Known error states (VERIFIED from LinuxGSM docs, https://docs.linuxgsm.com/steamcmd/errors):

| State | Documented meaning |
|---|---|
| `0x202` | not enough disk space |
| `0x212` | not enough disk space |
| `0x402` | connection issue with Steam; wait and retry |
| `0x602` | unknown |
| `0x606` | cannot write to disk (permissions, symlinks) |
| `0x626` / `0x6A6` | missing / corrupt update files; revalidate |
| `0x6` | no connection to content servers |
| `0x2`, `0x206` | unknown |

Message form: `Error! App '258550' state is 0x202 after update job.`

UNVERIFIED (background knowledge, test before relying on it):
- Success strings are `Success! App '258550' fully installed.` and `Success! App '258550' already up to date.`
- SteamCMD's exit code is not a reliable success signal (0 is sometimes returned after a failed update, and non-zero codes such as 7 or 8 appear after runs that worked). Parse the output and compare the manifest buildid afterwards.
- The hex states are bit flags of Steam's app state (0x2 update required, 0x4 fully installed, 0x200 update paused, 0x400 update started), which is why 0x402 and 0x602 are usually transient and worth one retry.
- Carbon docs tip (VERIFIED): "If steamcmd fails to validate, delete the `steamapps` directory from the root of the server install and try again."

---

## 2. Rust release branches in 2026 and framework mapping

### 2.1 Steam branches — VERIFIED (live list in §1.1)

- `public`: the live game. `release`: the same build a few hours before it is promoted to public. `staging`: next month's work. `auxNN`: feature branches that come and go (only `aux02` existed on 2026-10-04). `last-month`: previous month's build. `debug`: old (January 2025).
- The Facepunch wiki still gives `-beta staging` and `-beta aux01` as its examples, so aux branches are expected to appear again.

### 2.2 Carbon mapping — VERIFIED (source `SelfUpdater.cs`, live `https://api.carbonmod.gg/releases`)

| Carbon channel | GitHub release tag | Version line | Target Rust branch | Last build seen |
|---|---|---|---|---|
| Production | `production_build` | 2.0.x (2.0.262) | `public` | 2026-10-02 |
| Edge | `edge_build` | 2.1.x (2.1.1285) | `public`, latest Carbon commits | 2026-10-03 |
| Rust staging | `rustbeta_staging_build` | 2.3.x (2.3.1477) | `staging` | 2026-10-03 |
| Rust release | `rustbeta_release_build` | 2.4.x (2.4.341) | `release` | 2026-10-03 |
| Rust aux01 | `rustbeta_aux01_build` | 2.5.x | `aux01` | 2026-09-15 |
| Rust aux02 | `rustbeta_aux02_build` | 2.6.x | `aux02` | 2026-05-20 |
| Rust aux03 | `rustbeta_aux03_build` | 2.7.x | `aux03` | 2026-07-20 |
| Rust aux04 | `rustbeta_aux04_build` | 2.8.x | `aux04` | 2026-05-13 (GitHub only; absent from the API list) |
| Experimental | `experimental_build` | 2.9.x | `public` | 2026-08-22 |
| Profiler | `profiler_build` | 2.8.x | — | 2026-05-28 |

- Git tags `preview_build`, `qa_build` and `productionprev_build` exist in the repo but have no entry in the API list and no enum value in the self-updater. Treat "preview" as retired.
- Carbon also feeds plugins compile symbols for the branch (`STAGING`, `AUX01`, `AUX02`) per its docs.

### 2.3 Oxide mapping

- VERIFIED: GitHub releases (master) target `public` (`<SteamBranch>public</SteamBranch>` in `Oxide.Rust.csproj`).
- VERIFIED: the repo has `staging`, `release`, `aux01`, `aux02`, `develop` branches. `prerelease.yml` builds every non-master push with the version suffixed `-<branch>` and syncs the artifacts to a Cloudflare R2 bucket under `artifacts/Oxide.Rust/<branch>/`.
- UNVERIFIED (inferred): the public URL is probably `https://downloads.oxidemod.com/artifacts/Oxide.Rust/<branch>/Oxide.Rust[-linux].zip`, by analogy with the compiler URL hard-coded in Oxide.CSharp (`https://downloads.oxidemod.com/artifacts/Oxide.Compiler/{branch}/`). The base URL is a repository variable I could not read. Confirm before using.
- The AppVeyor staging links quoted on the uMod forum (2024, non-staff poster) are likely dead now that builds run on GitHub Actions.

---

## 3. Oxide for Rust

### 3.1 Where releases live — VERIFIED

- GitHub: https://github.com/OxideMod/Oxide.Rust/releases. Official docs (docs.oxidemod.com, `install-oxide.md`) send owners there.
- Assets per release: **`Oxide.Rust.zip`** (Windows) and **`Oxide.Rust-linux.zip`** (Linux), about 13 MB each. Direct URL pattern: `https://github.com/OxideMod/Oxide.Rust/releases/download/<tag>/<asset>`.
- Tag = version, e.g. `2.0.7801`. Newest tags on 2026-10-04: 2.0.7783 and 2.0.7788 (Oct 1 update), 2.0.7801 (Oct 2 hotfix #3).
- The umod.org Rust page links to the same GitHub asset (it showed 2.0.7801).
- **`https://umod.org/games/rust/latest.json` returned `version: 2.0.7598`, created 2026-08-18.** It is behind by six weeks. Its fields (`version`, `download_url: https://umod.org/games/rust/download?tag=public`, `snapshot_url: .../download/develop`) still exist, but do not use it as the version source.
- Oxide's own in-game check (`RustExtension.GetLatestVersion`) calls `https://api.github.com/repos/OxideMod/Oxide.Rust/releases` and takes `[0].tag_name`. Unauthenticated GitHub API calls are limited per IP (60/hour, UNVERIFIED in this session), so an updater should prefer `git ls-remote --tags` or the `releases/latest/download/<asset>` redirect.

### 3.2 Version numbering — VERIFIED

- `2.0.<N>`: prefix `2.0` plus a repository-wide counter that the build action increments on every build (`OxideMod/Actions` `action.yml`, `action-pack/increment`). N is not a Rust build number and is not contiguous between releases.
- Several tags often point at the same commit (2.0.7676, 7683, 7704, 7711 are all the Sept 3 commit). Those are rebuilds against newer game DLLs with no Oxide source change.
- Non-master builds get a suffix: `2.0.<N>-<branch>`.

### 3.3 Reading the installed version — VERIFIED

- `RustExtension.AssemblyVersion` = `Major.Minor.Build` of the `Oxide.Rust.dll` assembly name. File location: `RustDedicated_Data/Managed/Oxide.Rust.dll`.
- Console command `oxide.version` (alias `o.version`). From the server console it prints:
  ```
  Oxide.Rust Version: 2.0.NNNN
  Oxide.Rust Branch: master
  ```
  For a player it prints a localised line with Oxide version, game name, `BuildInfo.Current.Build.Number` and `Rust.Protocol.printable`.
- Covalence: `server.Version` = Rust build number, `server.Protocol` = `Rust.Protocol.printable`.

### 3.4 Relation to the Rust build — VERIFIED

- The zip contains **patched copies of game assemblies**. The csproj lists the patched files: `Assembly-CSharp.dll`, `Facepunch.Console.dll`, `Facepunch.Network.dll`, `Facepunch.Rcon.dll`, `Facepunch.Sqlite.dll`, `Facepunch.UnityEngine.dll`, `Rust.Clans.Local.dll`. So each Oxide build is tied to one exact Rust server build, and any Rust hotfix that changes those DLLs needs a new Oxide build.
- Release notes carry the protocol: "Patch for October 1st Rust update (2634.289.1)". The three numbers are `network.save.report` (Carbon reads the same three fields from `Rust.Global.dll` → `Rust.Protocol`). Some rebuild releases have an empty body, so the protocol string is not guaranteed to be present.
- Oxide docs: "For every server update by Facepunch, there is an Oxide update. Don't mismatch version of Oxide and version of servers."

### 3.5 Timing on force-wipe day — VERIFIED from commit and release timestamps

- Oxide's "Patch for <date> Rust update" commit lands 30–45 minutes **before** the public update (full table in §7.2).
- Release publish times I could read: 2.0.7676 at 2026-09-03 17:32:41Z; 2.0.7573 on 2026-08-06 at about 17:47. Both precede the 18:00 UTC public update.
- So in 2025–2026 the usual "wait for Oxide" delay is zero or negative. The risk has inverted: the new Oxide can be visible while the server is still on last month's build.
- Hotfix days follow: Oct 1 update → hotfix patch releases on Oct 2/3; September had hotfixes #4–#6 on the 11th, 17th and 22nd.

### 3.6 Zip contents — VERIFIED from the build targets

- Layout: `RustDedicated_Data/Managed/` containing `Oxide.*.dll` (Oxide.Core, Oxide.CSharp, Oxide.MySql, Oxide.SQLite, Oxide.Unity, Oxide.References, Oxide.Rust), the patched game DLLs listed above, dependency DLLs, and `x64/` and `x86/` subfolders with native libraries (the Linux build includes `libMonoPosixHelper.so`).
- Extract over the server root with overwrite. The server must be stopped (docs: uploading while running "could potentially corrupt your server data").
- I did not open an actual zip; the list comes from `Game.targets` and the csproj.

### 3.7 Linux specifics — VERIFIED

- Use the `-linux` zip. The example start script exports `LD_LIBRARY_PATH=$LD_LIBRARY_PATH:$(pwd)`.
- On Unix, Oxide.CSharp rewrites `Oxide.References.dll.config` to map `MonoPosixHelper` to `x64/libMonoPosixHelper.so`.
- **The plugin compiler is a separate executable downloaded at runtime** from `https://downloads.oxidemod.com/artifacts/Oxide.Compiler/<branch>/` (`linux-x64.Compiler` or `win-x64.AOT.Compiler.exe`) into the server root as `Oxide.Compiler[.exe]`. Oxide chmods it itself and logs "Compiler may not be set as executable; chmod +x or 0744/0755 required" if it cannot start. A server with no outbound HTTPS, or a read-only root, will have Oxide load but compile no plugins.
- No doorstop or `LD_PRELOAD` is needed (that is Carbon).

### 3.8 uMod "new platform" / agent — UNVERIFIED

- I found no 2025–2026 announcement of a released uMod agent. The newest search hits were years-old umod.org announcements.
- What is verifiable: active Oxide development is under the OxideMod GitHub org with docs at docs.oxidemod.com (VitePress, "Copyright © 2023-present OxideMod"), downloads at downloads.oxidemod.com, and commits through 2026-10-03. umod.org still hosts the plugin catalogue and forum. Design v2 for Oxide 2.0.x as it exists.

---

## 4. Carbon (carbonmod.gg)

### 4.1 Install layout — VERIFIED (build script `tools/build/runners/build.cs`, `Defines.cs`, docs)

Archive extracted over the server root:

| Path | Platform | Purpose |
|---|---|---|
| `carbon/managed/` | both | core assemblies (`Carbon.Preloader.dll`, `Carbon.dll`, …), with `lib/`, `modules/`, `hooks/` |
| `carbon/native/` | both | `CarbonNative.dll` / `libCarbonNative.so` |
| `winhttp.dll` | Windows | Unity Doorstop, renamed from `doorstop.dll` |
| `doorstop_config.ini` | Windows | `target_assembly = carbon/managed/Carbon.Preloader.dll` |
| `libdoorstop.so` | Linux | Unity Doorstop |
| `carbon.sh` | Linux | wrapper: sources the environment script, runs `RustDedicated "$@"` |
| `carbon/tools/environment.sh` | Linux | sets `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, `LD_PRELOAD=<root>/libdoorstop.so`, `LD_LIBRARY_PATH`, `TERM=xterm` |

Created at runtime under `carbon/`: `plugins/` (with `backups/`, `debug/`, `cszip_dev/`), `configs/` (note the plural; Oxide uses `config/`), `data/`, `lang/`, `logs/`, `extensions/`, `modules/`, `temp/`, `compiler/`, `profiles/`, `developer/`, plus `config.json`, `config.auto.json`, `config.profiler.json`, and `carbon/.protocol`.

- Linux launch order from the docs: update with SteamCMD, `source carbon/tools/environment.sh`, then start `RustDedicated`.
- Carbon does not modify `RustDedicated_Data/Managed`: "your Managed folder will always be untouched from Facepunch's shipped configuration". It publicises and patches assemblies in memory.
- **Oxide and Carbon cannot coexist.** Migration requires a `validate` first to revert Oxide's patched DLLs. On first boot Carbon auto-migrates Oxide's `config`, `data`, `lang`, users/groups and moves `Oxide.Ext.*.dll` into `carbon/extensions`.

### 4.2 Releases and assets — VERIFIED (GitHub API via fetch tool)

- https://github.com/CarbonCommunity/Carbon/releases. One rolling release per channel tag (tags in §2.2). Title format: "Production Build — v2.0.262".
- Asset names: `Carbon.<Windows|Linux>.<Release|Debug|Minimal>.<zip|tar.gz>` plus a matching `.info` file each.
  - Production: Release and Minimal.
  - Edge, staging, aux: Debug and Minimal.
  - Rust release channel: Debug, Minimal and Release.
- "Minimal" strips CarbonAuto, the admin panel and modules; it is built with release optimisations.
- URL pattern: `https://github.com/CarbonCommunity/Carbon/releases/download/<tag>/<asset>`.
- I did not read what the `.info` files contain.

### 4.3 Self-updater on boot — VERIFIED (`Carbon.Preloader/src/Utility/SelfUpdater.cs`)

1. Before any Rust assembly loads, the preloader downloads `https://api.carbonmod.gg/releases` (JSON array of `{name, version, date, protocol, prerelease}`) to `carbon/temp/versions.json`.
2. It finds the entry whose `name` equals its own compiled-in tag and compares `version` with the Preloader assembly version (`Major.Minor.Build`). Equal → "up to date", stop.
3. **Production only:** it reads `Rust.Protocol` (`network.save.report`) from `RustDedicated_Data/Managed/Rust.Global.dll` and compares with `carbon/.protocol` (line 1 Rust protocol, line 2 Carbon protocol). If the pending Carbon build has a different Carbon protocol and the Rust protocol has not changed since last boot, it logs "Skipped self-updating since the pending Carbon update has changed its protocol. Update the Rust server to self-update!" and does nothing. A higher protocol *revision* forces the update regardless.
4. Otherwise it downloads `http://github.com/CarbonCommunity/Carbon/releases/download/<tag>/Carbon.<OS>.<Target>.<ext>` by spawning the external **`curl`** binary, and extracts only entries under `carbon/managed` and `carbon/native`.
5. Hook definitions update separately after that, before extensions and plugins load.

Consequences for an external tool:
- Staging and other beta channels update on every restart with no protocol gate. Production waits for the Rust server to be updated first. So for Carbon the correct wipe-day sequence is: update Rust, then boot; Carbon fixes itself.
- The self-updater never touches `winhttp.dll`, `libdoorstop.so`, `doorstop_config.ini`, `carbon.sh` or `carbon/tools/`. First install and channel switches of those files remain an external job.
- It needs `curl` on PATH and outbound access to api.carbonmod.gg and github.com.
- Config (`carbon/config.json`): `"SelfUpdating": { "Enabled": true, "HookUpdates": true, "RedirectUri": null }`. `RedirectUri` overrides the download URL.
- `c.changeversion <channel> <Release|Debug|Minimal>` writes `carbon/temp/versionoverride.txt` with a release URL; the next boot switches channel. `c.changeversion reset` cancels. The command forces `SelfUpdating.Enabled = true`.
- Docs schedule: production updates about twice a month plus hotfixes; the wipe-day production build is released "**1 hour** before the actual Rust server update drops, using the `release` branch to build against".

### 4.4 Reading installed and latest version — VERIFIED

- Latest: `https://api.carbonmod.gg/releases`, pick the entry for your tag. Production on 2026-10-04: `2.0.262`, protocol `2026.10.01.0`. The protocol is date-shaped; aux channels use `0.0.1.0-aux01`.
- Installed: assembly version of `carbon/managed/Carbon.Preloader.dll`, or console `c.version`:
  `Carbon [Minimal] <version>/<platform>/<protocol> [<git branch>] [<git tag>] on Rust <build number>/<network.save.report> (<build date>) <scm change id>`.
- Also `c.build`, `c.protocol`, `c.commit`.

### 4.5 Oxide plugin compatibility — VERIFIED (docs and source)

- Carbon ships Oxide-compatible namespaces and types (`RustPlugin`, `Plugin`, `webrequest`, `Interface.Oxide`). `CarbonPlugin : RustPlugin` adds Carbon-only features. Docs claim "over 95%" of Oxide plugins work unchanged.
- Oxide `o.*`/`oxide.*` console commands do not exist in Carbon; equivalents are `c.*`. `c.assignalias` can map old names.
- Oxide extensions (`Oxide.Ext.*.dll`) go in `carbon/extensions/`.

### 4.6 Commands relevant to an updater — VERIFIED (source)

`c.version`, `c.build`, `c.protocol`, `c.commit`, `c.plugins [-j]`, `c.reload <name|*>`, `c.load`, `c.unload`, `c.plugininfo`, `c.reloadconfig`, `c.uninstallplugin` (moves the file to `plugins/backups`), `c.installplugin`, `c.extensions`, `c.modules`, `c.setmodule`, `c.reloadmodule`, `c.changeversion`, `c.loadconfig`, `c.saveconfig`, `c.conditionals`, `c.hooks`, `c.whymodded`, `c.delete`, `c.deleteext`, `c.shutdown` (unloads Carbon, test only).

Hooks: Carbon implements the Oxide hook names. Carbon-only: `public override async ValueTask OnAsyncServerShutdown()` on `CarbonPlugin`, which makes a direct shutdown or a queued restart wait for the plugin.

### 4.7 Plugins, extensions, modules — VERIFIED (docs)

| Kind | Form | Location | Loading |
|---|---|---|---|
| Plugin | `.cs` source, or `.cszip` (zip of partial-class files) | `carbon/plugins/` | compiled by in-process Roslyn, hot-reloaded on file change |
| Extension | compiled DLL with a class implementing `API.Assembly.ICarbonExtension` (`Awake`, `OnLoaded`, `OnUnloaded`) | `carbon/extensions/` | at boot; restart to update |
| Module | built into Carbon or DLL in `carbon/modules`; enabled per config | `carbon/modules/` (configs) | `c.setmodule`, `c.reloadmodule` |

Optional built-in **AutoWipe module** (off by default): cron-scheduled full or map wipes, map URL pool, post-wipe commands and file deletes, checks every 30 s with a 1-hour cooldown, console commands `autowipe.wipes|add|delete|maps|addmap|deletemap|wipechat`, hostname placeholders `[WIPE_DAY]` etc. It sets `server.levelurl`, `server.worldsize` and `server.seed` in memory. This overlaps with FeedMeUpdates' wipe feature on Carbon servers.

### 4.8 Detecting Carbon vs Oxide from a plugin

- Compile-time, VERIFIED: Carbon always defines `CARBON` and `RUST` (`Community.Config.cs`), plus `WIN` or `UNIX`, `MINIMAL` on minimal builds, branch symbols, and protocol symbols of the form `RUST_ABV_2511_243_1` / `RUST_BLW_...`. Oxide defines `OXIDE` and `OXIDEMOD`, plus `OXIDE_PUBLICIZED` under a condition, plus extension- and config-supplied symbols (`CompilerService.cs`).
  ```csharp
  #if CARBON
      // Carbon
  #else
      // Oxide
  #endif
  ```
- Runtime type check, UNVERIFIED but consistent with the source layout: `Type.GetType("Carbon.Community, Carbon.Common") != null`. `Community` is in namespace `Carbon` in the Carbon.Common project; I did not confirm the compiled assembly name.
- From outside the process: existence of `carbon/managed/Carbon.Preloader.dll` vs `RustDedicated_Data/Managed/Oxide.Rust.dll`. If both are present the install is broken.
- Rust's server browser has distinct compressed tags for Modded (`z`), Oxide (`o`) and Carbon (`y`) (Facepunch wiki tags page).

---

## 5. Plugin update sources

### 5.1 Plugin metadata — VERIFIED (Oxide.Docs `attributes.md`, Carbon docs)

```csharp
[Info("Plugin name", "Developer/Maintainer", "1.0.0")]
[Description("A short description of the plugin")]
class MyPlugin : RustPlugin   // or CarbonPlugin / CovalencePlugin
```
- File name must match the class name. Namespace `Oxide.Plugins` (Carbon also accepts `Carbon.Plugins`). Oxide still recognises `uMod.Plugins`.
- Oxide magic comments: `// Requires: <Plugin>` and `// Reference: <Assembly>`.
- An external updater can read the version with a regex on the `[Info(...)]` line without compiling.

### 5.2 uMod — VERIFIED live (via fetch tool) unless marked

- Single plugin: `https://umod.org/plugins/<slug>.json`. Fields seen for `vanish`: `name`, `title`, `slug`, `description`, `author`, `author_id`, `downloads`, `watchers`, `license`, `url`, **`download_url`** (`https://umod.org/plugins/Vanish.cs`), **`latest_release_version`** (`2.1.5`), `latest_release_version_formatted`, **`latest_release_at`** (`2026-08-06 06:59:24`), `created_at`, `published_at`, `tags_all`, `games_detail`.
- Search: `https://umod.org/plugins/search.json?query=<q>&page=<n>&sort=title&sortdir=asc&filter=&categories[]=universal&categories[]=rust&author=`. Laravel-style pagination: `current_page`, `data[]`, `last_page`, `per_page` (10), `total`, `next_page_url`. Items carry `title`, `slug`, `author`, `latest_release_version`, `latest_release_at`, `updated_at`.
- Download pattern: `https://umod.org/plugins/<ClassName>.cs`. Carbon's admin module uses `https://umod.org/plugins/[ID].cs` and the search endpoint above.
- The slug is not always derivable from the class name; the search endpoint resolves it.
- **Rate limit:** uMod admin Wulf (forum, about 2020 and March 2022): "Not officially an API, so no guarantees", "Non-authenticated users have a very low rate limit", "The rate limit is provided in the headers". A user captured `X-RateLimit-Limit: 1`, `X-RateLimit-Remaining: 0` and exposed headers `Retry-After`, `X-RateLimit-After`, and saw a 60-second wait on one endpoint. Exceeding it gives HTTP 429. The exact current number is UNVERIFIED; design for 429 plus `Retry-After`, serial requests and a local cache. Checking 100 plugins must be spread over minutes.

### 5.3 Codefling — partly VERIFIED

- VERIFIED from Carbon source (`AdminModule.Tabs.Plugins.cs`), which uses these in production:
  - catalogue: `https://codefling.com/db/?category=2` → items with `id`, `title`, `author`, `description`, `version`, `date`, `updated`, `changelog`, `fileName`, `prices`, `tags`, `downloads`, `compatibility`, `rating`, `primaryScreenshot`
  - free download: `https://codefling.com/files/file/[ID]-a?do=download`
  - OAuth device-style login: user opens `https://codefling.com/auth/?pin=<code>`; client polls `https://codefling.com/auth/bearer?code=<code>` for `accesstoken`
  - with `Authorization: Bearer <token>`: `https://codefling.com/api/core/me`, `https://codefling.com/api/nexus/purchases?perPage=100000&itemType=file&itemApp=downloads`, `https://codefling.com/api/downloads/files/{id}/download`
- VERIFIED from a Codefling admin (forum, 2023-08-07): paid downloads need the user's OAuth token; handing tokens to third-party automation is something "we're not going to allow".
- The catalogue URL is disallowed by robots.txt for my fetch tool, so I did not see a live response. Codefling's site advertises a "Public API" and a "REST API" with Basic/Creator/Pro tiers; I could not read their specification. Rate limits UNVERIFIED.
- Practical position: version checking against the public catalogue is feasible; automatic download of paid plugins needs a per-user OAuth login and may conflict with their policy. Ask Codefling before shipping it.

### 5.4 Lone.Design — VERIFIED live

- `https://api.lone.design/plugins.json`: public JSON array, no auth. Item fields: `name`, `version`, `filename`, `price`, `url`, `author`, `vendorUrl`, `description`, `date_created`, `date_modified`, `images`, `categories`, `tags`.
- Version check only. Downloads are paid and behind the shop account.

### 5.5 ChaosCode, MyVector, others — UNVERIFIED

- `chaoscode.io/resources/ccupdatecheck.21/` now redirects to `rusthq.com/product/chaos-update-checker`, so ChaosCode appears to have moved or rebranded to RustHQ. API not inspected.
- MyVector: nothing found.
- The Codefling plugin "Update Checker" (tofurahie, v4.6.4, updated 2026-09-17, paid) lists 13 sources it checks: uMod, Codefling, Lone.Design, Chaos, RustWorkshop, GitHub, ModPulse, RustPlugins, ServerArmour, ImperialPlugins, MyVector, SkyPlugins, Game4Freak. That shows those sites expose something checkable; the endpoints are not public knowledge from this research.

### 5.6 GitHub-hosted plugins — background knowledge

- No convention. Options: raw file URL plus regex on `[Info]`, or the releases API. The unauthenticated API limit applies per IP.

### 5.7 Existing updaters and how they work — VERIFIED from listings and source

| Tool | What it does |
|---|---|
| Update Checker (Codefling, tofurahie) | Scans the plugin folder including unloaded `.cs` files, matches against 13 marketplaces, reports to console and a single edited Discord message, hourly by default. Oxide and Carbon. Notify only as far as the listing says. |
| Lone.Design Update Checker (GitHub, v1.3.4, 2022) | Pulls `api.lone.design/plugins.json`, compares versions, posts to Discord. |
| Carbon Admin module → Plugins tab | Built-in browser for Codefling and uMod: lists, downloads free plugins, logs into Codefling by OAuth to download owned paid plugins. |
| Carbon itself | Self-updates the framework and hooks. It has **no automatic plugin updater**; plugins hot-reload when the file changes. |
| Oxide | No plugin updater. Hot-reloads on file change. `GetLatestVersion` only reports the framework version. |

Because both frameworks hot-reload on file change, a plugin update is "write the new `.cs` atomically" (temp file, then rename), ideally followed by a check that it compiled.

---

## 6. Wipe anatomy

### 6.1 Files in `server/<identity>/`

Confidence is mixed here. I could not read the game code for file naming.

| File | Content | Source / status |
|---|---|---|
| `proceduralmap.<size>.<seed>.<N>.map` | cached generated terrain | patterns VERIFIED (LinuxGSM wipe code uses `proceduralmap.*.map`); meaning of N UNVERIFIED |
| `proceduralmap.<size>.<seed>.<N>.sav` | all entities: buildings, loot, player bodies and inventories | VERIFIED that inventories live in the `.sav` (uMod admin Calytic); name is `World.SaveFolderName/World.SaveFileName` (decompiled snippet in Oxide `docs.json`) |
| `…sav.1`, `…sav.2`, … | rotated save backups | UNVERIFIED (background: count set by `server.savebackupcount`) |
| `player.blueprints.<N>.db` | learned blueprints | VERIFIED pattern (`player.blueprints.*.db` in LinuxGSM; `.4.db` five years ago, `.5.db` in a 2026 hosting guide) |
| `player.deaths.<N>.db` | death records | VERIFIED pattern (LinuxGSM map wipe removes it) |
| `player.identities.<N>.db` | SteamID ↔ display name | forum (was `.3.db`) |
| `player.states.<N>.db` | per-player state such as map markers, hostility timers | forum (was `.201.db`) |
| `player.tokens.db` | SteamID ↔ integer token | forum |
| `sv.files.<N>.db` | stored images, mostly sign art | forum (was `.201.db`); LinuxGSM map wipe removes it |
| `companion.id` | Rust+ pairing identity | forum |
| `cfg/server.cfg`, `cfg/serverauto.cfg`, `cfg/users.cfg`, `cfg/bans.cfg` | config, owners/moderators, bans | Facepunch wiki and Oxide docs for the first two; the last two are background knowledge |
| `*.db-wal`, `*.db-shm`, `*.db-journal` | SQLite sidecars | background knowledge; delete together with their `.db` |
| `oxide/data/…` or `carbon/data/…` | plugin data (not inside the identity folder; under the server root) | VERIFIED locations |

Teams: UNVERIFIED, but a LinuxGSM issue reports that a hard kill without save makes the server "forget teams", which fits team data being stored in the `.sav`.

### 6.2 What the number N means — partly UNVERIFIED

- VERIFIED: `Rust.Protocol` has fields `network`, `save`, `report`, and the printable form is `network.save.report` (Carbon reads them by name).
- Strong inference: N in `.sav`, `.map`, `player.states`, `sv.files` is the **save protocol**. The forum sample with `201` on both `player.states` and `sv.files` fits the save protocol of that era, and today's would be `289`.
- The blueprint, identities and deaths files use small numbers that change rarely (blueprints 4 → 5 over five years). Background knowledge says these are separate persistence/storage protocol constants. A blueprint force-wipe is Facepunch bumping that number. The current values must be read from a live server (list the directory) rather than hard-coded.
- **Design rule: always match with globs (`player.blueprints.*.db`), never a fixed number.**

### 6.3 Wipe types

| Wipe | Delete | Keep |
|---|---|---|
| Map wipe | `*.sav`, `*.sav.*`, optionally `*.map`; usually also `player.deaths.*.db`, `player.states.*.db`, `sv.files.*.db` (+ sidecars) | blueprints, identities, tokens, cfg |
| Blueprint wipe | `player.blueprints.*.db` (+ `-wal`, `-shm`, `-journal`) | everything else |
| Full wipe | both of the above | `cfg/`, `player.identities`, `player.tokens`, `companion.id` |

- LinuxGSM's definitions (VERIFIED, issue #1830): map wipe = `proceduralmap.*.map`, `proceduralmap.*.sav`, `player.deaths.*.db`, `sv.files.*.db`; full wipe adds `player.blueprints.*.db`. Its maintainers insisted blueprints stay out of the default wipe.
- Plugin data is a separate decision per plugin. Carbon's AutoWipe handles it with configurable `PostWipeDeletes` and `PostWipeCommands`.
- Custom maps (`server.levelurl`): the save is named after the map file, not `proceduralmap.*` (UNVERIFIED, background knowledge). Glob `*.sav*` and `*.map` rather than `proceduralmap.*`.

### 6.4 Does changing seed/size/levelurl alone give a fresh map? — UNVERIFIED but well supported

- Seed and world size are part of the save file name, so a new seed or size makes the server look for a file that does not exist and start a new world. The decompiled snippet confirms the mechanism: `SaveRestore.Load` builds `World.SaveFolderName + "/" + World.SaveFileName`, and if the file does not exist it calls `OnNewSave` and starts fresh.
- The old `.sav` and `.map` stay on disk as orphans, and blueprints and other `.db` files are untouched. So it is a map wipe with leftover files, not a clean one.
- The monthly forced wipe works the same way: the save number in the name changes, the old file is not found, a new world is generated. Orphans accumulate unless something deletes them.
- **Plugin hook for "a wipe just happened": `OnNewSave(string filename)`** (VERIFIED). It fires at boot before plugins that load later can see it, so pair it with a persisted marker.

### 6.5 Precedence: command line vs server.cfg vs serverauto.cfg — CONFLICTING primary sources

- Facepunch wiki: "The `server.cfg` file … takes priority over command-line parameters."
- Oxide docs (`setup-server.md`): priority is 1) command line, 2) `server.cfg`, 3) `serverauto.cfg`.
- Both agree `server.cfg` beats `serverauto.cfg`. Oxide docs add: `serverauto.cfg` "should not be edited by the server owner. It is updated automatically at server shutdown or when a server owner use the command `server.writecfg`", and "`server.writecfg` only save some parameters to file `ServerAuto.cfg` but command `server.readcfg` read both".
- I could not settle it from code. The decompiled snippet only shows that `ConsoleSystem.UpdateValuesFromCommandLine()` iterates the command-line switches. **Test empirically** (set `server.seed` differently in all three places and read it back over RCON), and in the meantime make FeedMeUpdates write seed/size/levelurl in exactly one place and warn if it finds them in another.

---

## 7. Force wipe

### 7.1 The rule — VERIFIED

- Facepunch wiki, "Server Wipe Timer" (https://wiki.facepunch.com/rust/server-wipe-timer): default schedules are Monthly = "First Thursday at 19:00 (London time)", Weekly = every Thursday 19:00 London, Biweekly = first and third Thursday 19:00 London. Default day 4 (Thursday), default hour 19, default timezone `Europe/London`.
- So the update time is **19:00 UK local time: 19:00 UTC during GMT (late October to late March), 18:00 UTC during BST (late March to late October)**. In US Eastern terms it is 14:00 almost all year.
- The rule is anchored to the UK clock, not the US one. Proof: on 2023-11-02 and 2022-11-03 the UK had already left summer time and the US had not. Oxide's patch commits those days are at 18:25 and 18:44 UTC, i.e. just before 19:00 UTC, not 18:00 UTC.
- Third-party schedule pages disagree with each other (some say "always 19:00 UTC", one says "anchored to US Eastern"). Both are wrong in different months. Compute the time with `Europe/London` tz data.
- Facepunch's news posts do not state a time. The October 2026 post (https://rust.facepunch.com/news/livestock, Thursday 2026-10-01) mentions neither time nor wipe.

### 7.2 Evidence: Oxide patch commit times (UTC), first Thursday of each month

From `git log` of OxideMod/Oxide.Rust. The commit precedes the public update by roughly 30–45 minutes.

| Date | Commit (UTC) | Protocol | Season | Implied update |
|---|---|---|---|---|
| 2026-10-01 | 17:15 | 2634.289.1 | BST | 18:00 UTC |
| 2026-09-03 | 17:21 | 2633.288.1 | BST | 18:00 UTC |
| 2026-08-06 | 17:29 | 2632.287.1 | BST | 18:00 UTC |
| 2026-07-02 | 16:25 | 2631.286.1 | BST | 18:00 UTC |
| 2026-06-04 | 17:14 | 2627.285.1 | BST | 18:00 UTC |
| 2026-05-07 | 17:22 | 2624.284.1 | BST | 18:00 UTC |
| 2026-04-02 | 17:33 | 2623.283.1 | BST | 18:00 UTC |
| 2026-03-05 | 18:18 | 2622.282.1 | GMT | 19:00 UTC |
| 2026-02-05 | 18:29 | 2621.281.1 | GMT | 19:00 UTC |
| 2026-01-01 | 16:54 | 2617.275.1 | GMT | unclear (commit unusually early) |
| 2025-12-04 | 17:52 | 2615.274.1 | GMT | 19:00 UTC |
| 2025-11-06 | 18:13 | 2604.273.1 | GMT | 19:00 UTC |
| 2025-10-02 | 17:27 | 2602.272.1 | BST | 18:00 UTC |
| 2025-09-04 | 17:24 | 2597.271.1 | BST | 18:00 UTC |
| 2025-08-07 | 17:27 | 2594.270.1 | BST | 18:00 UTC |
| 2025-07-03 | 16:59 | 2592.269.1 | BST | 18:00 UTC |
| 2025-06-05 | 17:40 | 2590.268.1 | BST | 18:00 UTC |
| 2025-05-01 | 17:29 | 2588.267.1 | BST | 18:00 UTC |
| 2025-04-03 | 17:07 | 2585.266.1 | BST | 18:00 UTC |
| 2025-03-06 | 18:10 | 2582.265.1 | GMT | 19:00 UTC |
| 2025-02-06 | 18:26 | 2580.264.1 | GMT | 19:00 UTC |
| 2025-01-02 | 18:35 | 2573.263.1 | GMT | 19:00 UTC |
| 2024-12-05 | 18:04 | 2570.262.1 | GMT | 19:00 UTC |
| 2024-11-07 | 18:28 | 2567.261.1 | GMT | 19:00 UTC |
| 2023-11-02 | 18:25 | 2511.243.1 | GMT (US still on DST) | 19:00 UTC |
| 2022-11-03 | 18:44 | 2362.230.1 | GMT (US still on DST) | 19:00 UTC |

The same pattern holds for every month back to October 2022 in the log.

### 7.3 Is the map wipe forced by Facepunch? — VERIFIED by the protocol history

- The middle number (save protocol) increases on every first-Thursday update in the table, and does not change on hotfixes within a month. Sometimes it jumps by more than one (275 → 281 in February 2026, 254 → 259 in October 2024).
- A server that updates therefore cannot load last month's save and generates a new world. A server that does not update keeps its map but clients on the new network protocol cannot join. That is why it is "forced".
- Blueprint wipes are separate and occasional. No fixed schedule; third-party pages say Facepunch announces them a few days ahead (UNVERIFIED which months in 2025–2026).

### 7.4 Exceptions and other mandatory updates

- In 2022–2026 I found no month where the monthly update moved off the first Thursday, including 2026-01-01 and 2025-01-02 (holiday period) and 2024-07-04.
- The minute is not guaranteed. Example: on 2024-05-02 the Oxide patch commit is at 19:16 UTC and is titled "May 3rd", suggesting that month's update slipped by over an hour. Treat the scheduled time as "not before", and trigger on the actual build change.
- **Mid-month mandatory updates without a map wipe** (network protocol changes, save protocol does not): 2025-10-23 (2603.272.1), 2025-03-13, 2025-12-18, 2026-02-19, 2026-04-17. The October 2026 news post warns "Server owners, please remember this date" about a mandatory Halloween update on **2026-10-22**.
- Hotfixes follow most monthly updates within 24 hours, often several (Oct 1 2026 → hotfix #3 on Oct 2). Each needs a matching Oxide build; Carbon usually only needs its hook update.

### 7.5 Wipe-related convars — VERIFIED from the Facepunch wiki page, via a summarising fetch (exact spelling and casing should be confirmed in-game with `find wipe`)

| Convar / command | Meaning |
|---|---|
| `wipetimer.wipeDayOfWeek` | 0–6, Sunday = 0, default 4 |
| `wipetimer.wipeHourOfDay` | 0–23, decimals allowed (14.5 = 14:30), default 19 |
| `wipetimer.wipeTimezone` | IANA zone id, default `Europe/London` |
| `wipetimer.wipeUnixTimestampOverride` | exact wipe time as a unix timestamp |
| `wipetimer.wipeCronOverride` | cron expression, parsed with the Cronos library |
| `wipetimer.daysToAddTest` | test offset (named in your brief; not in the summary I received) |
| `eventschedulewipeoffset.event_hours_before_wipe` | end-of-wipe events start this many hours before (default 24) |
| `printwipe` | prints the config, the next 10 wipe dates and the time remaining |

- `server.tags` values `monthly`, `biweekly`, `weekly` select the default schedule the timer uses. Other tags (VERIFIED, https://wiki.facepunch.com/rust/server-browser-tags): `vanilla`, `hardcore`, `softcore`, `pve`, `roleplay`, `creative`, `minigame`, `training`, `battlefield`, `broyale`, `builds`, `tut`, `premium`, regions `NA SA EU WA EA OC AF`. Tags in the same group are mutually exclusive; up to four show in the browser.
- The wipe timer drives the missile-silo countdown and the pre-wipe events. Nothing I found says it deletes files or restarts the server.
- **Native auto-wipe:** I found no Facepunch-native "wipe the server on schedule" feature in 2025–2026 sources. I could not list all convars (the Carbon metadata dump was truncated before the `server.*` and `wipetimer.*` entries), so this is "not found", not "does not exist". Check `find wipe` on a current server.
- An updater can set `wipetimer.wipeUnixTimestampOverride` or `wipeCronOverride` so the in-game countdown matches its own schedule.

---

## 8. Controlling a running server

### 8.1 WebRCON — VERIFIED (Facepunch/webrcon source; wiki)

- Enable with `+rcon.web 1`, `+rcon.port <tcp port>`, `+rcon.password <pw>` (the wiki "suggests" `rcon.web 1`).
- Connect: `ws://<host>:<rcon.port>/<password>`. The password is in the URL path, unencrypted.
- Send: `{"Identifier": <int>, "Message": "<console command>", "Name": "WebRcon"}`.
- Receive: JSON with at least `Identifier`, `Message`, `Type`. The reference client treats `Identifier > 1000` as a reply to its own request (it starts its counter at 1001) and everything else as broadcast console output. It handles `Type` values `Generic`, `Log`, `Error`, `Warning`, and `Chat`. A `Stacktrace` field is mentioned in forum descriptions (UNVERIFIED).
- Use a unique positive `Identifier` per command and match replies on it. Unsolicited log lines arrive on the same socket.
- Oxide patches `Facepunch.Rcon.dll` and exposes `OnRconConnection` and an internal RCON message hook, so RCON behaviour can differ slightly under Oxide.

### 8.2 Useful console commands

VERIFIED (Carbon's Rust command metadata, Oxide docs, hook signatures):

| Command | Notes |
|---|---|
| `global.restart [seconds] [message]` | "Schedules a server restart; optionally accepts a countdown in seconds and a broadcast message". Game method `ServerMgr.RestartServer(string strNotice, int iSeconds)` runs a `ServerRestartWarning` coroutine. Oxide hooks `OnServerRestart(string, int)` and `OnServerRestartInterrupt()` can block it. |
| `global.quit` | "Quits the application cleanly with no arguments; **rejects calls with arguments** to prevent accidental exit". Send exactly `quit`. `ServerMgr.Shutdown()` kicks all players with "Server Shutting Down". |
| `global.say <msg>` | chat broadcast |
| `global.status` | connected clients |
| `global.serverinfo` | JSON status (fields not captured in this session) |
| `global.version` | "game version string … including build number and branch" |
| `server.writecfg` / `server.readcfg` | write `serverauto.cfg` / read both cfg files |
| `oxide.version`, `o.version` | Oxide version and branch |
| `oxide.reload|load|unload <name|*>`, `oxide.save`, `oxide.ext` | Oxide plugin and data control |
| `c.version`, `c.reload`, `c.plugins` … | Carbon (§4.6) |

UNVERIFIED (third-party article from 2018, updated 2023, plus background knowledge):
- `restart` defaults to 300 seconds; `restart -1` cancels with "Restart interrupted"; chat shows "Restarting in <n> seconds"; a save is made just before exit.
- `restart` ends the process. It does not relaunch it. A wrapper loop, systemd unit or panel must start it again; Oxide docs say the same of Oxide ("no built-in functionality to handle automatic server restarts, however the launcher script templates include automatic restart").
- `server.save` forces a save. Oxide hook `OnServerSave` is on `SaveRestore.DoAutomatedSave` (VERIFIED).

Recommended stop sequence for an updater: `server.save` → wait for the save line → `quit` (no arguments) → wait for the process to exit, with a timeout before escalating.

### 8.3 Signals and console close

- Linux SIGINT: a September 2026 test in a third-party container project logged "the server exits cleanly on SIGINT without saving the world" (save counter unchanged). LinuxGSM issue #4720 reports the same: Ctrl+C through tmux is "a hard shutdown without full server save" with side effects such as lost teams, and LinuxGSM has no Rust RCON client to do better. Both are third-party observations, consistent with each other. **Treat SIGINT as not saving.**
- SIGTERM: no source found. Assume it does not save.
- Windows Ctrl+C and closing the console window: no source found. Assume it does not save.
- Conclusion: the only supported clean stop is `quit` over RCON (or `restart`). Signals are a last resort after a timeout.

---

## 9. Runtime constraints for a plugin

### 9.1 Compilers and C# version — VERIFIED

| | Oxide | Carbon |
|---|---|---|
| Compiler | external `Oxide.Compiler` process, .NET 10, Roslyn `Microsoft.CodeAnalysis.CSharp` 5.0.0, talks over a named pipe | in-process Roslyn 4.14.0 |
| Language version requested | `CompilerLanguageVersion.V14` → **C# 14** | `LanguageVersion.Preview` (on Roslyn 4.14: C# 13 plus preview features) |
| Flags | `-unsafe true` | — |
| Plugin target | `net48` against the game's own `mscorlib` | the game's Mono runtime |

- Safe common denominator for a plugin that must compile on both: **C# 13 syntax**, avoiding features that need runtime or BCL support missing from Unity's Mono. Carbon ships compiler polyfills; Oxide does not advertise the same.
- Oxide core libraries target `net48`, `netstandard2.0`, `netstandard2.1`. Oxide's default plugin references include `netstandard`.

### 9.2 What a plugin may reference

- Oxide default references (VERIFIED, `RustExtension.DefaultReferences`): `0Harmony`, the Facepunch and Rust assemblies, `System.Drawing`, many `UnityEngine.*` modules including **`UnityEngine.UnityWebRequestModule`**, and `netstandard`. Anything else needs `// Reference: <AssemblyName>` and the DLL must exist in `RustDedicated_Data/Managed`.
- Sandbox: the Oxide.Rust extension still declares `WhitelistAssemblies` and `WhitelistNamespaces`, but a search of current Oxide.CSharp and Oxide.Core source finds no sandbox code. So `System.IO`, `System.Diagnostics.Process` and reflection appear usable in self-distributed plugins. Whether umod.org would accept such a plugin for listing is a separate policy question I did not check.
- Carbon: no blacklist found in the compilation thread for process or file APIs (only a file-level plugin blacklist).

### 9.3 APIs — mostly UNVERIFIED (I could not list the Managed folder)

| Need | Assessment |
|---|---|
| File IO | `System.IO` in mscorlib. Fine. |
| SHA-256 | `System.Security.Cryptography.SHA256` in mscorlib. `System.Security.Cryptography` is even in Oxide's legacy whitelist (VERIFIED). |
| HTTP text | Oxide/Carbon `webrequest.Enqueue(url, body, (code, response) => …, this, RequestMethod.GET, headers, timeout)`. Default timeout 30 s (VERIFIED both). Oxide's is `HttpWebRequest`-based, User-Agent `Oxide Mod (v…; https://umod.org)`, optional gzip/deflate. |
| HTTP binary (zip download) | Oxide's callback only returns a `string`, so it cannot carry a zip safely. Carbon adds `EnqueueData` (`Action<int, byte[]>`) and `EnqueueAsync` (VERIFIED). On Oxide use `UnityWebRequest` (default reference) or `WebClient`/`HttpClient` on a worker thread. |
| TLS | **Both frameworks set `ServicePointManager.ServerCertificateValidationCallback` to always return true** (VERIFIED: Oxide.Core `WebRequests.cs` line 383; Carbon `WebRequest.cs` line 25). This is process-wide, so every `HttpWebRequest`/`WebClient` call in the server skips certificate validation. Oxide's build script pins TLS 1.2 for its own tooling; Unity Mono's TLS 1.2 support with `HttpWebRequest` is generally fine on current Unity but I did not test it. Never trust a download because it came over HTTPS; check a hash from a second channel. |
| Zip extraction | `System.IO.Compression` and `System.IO.Compression.FileSystem` are normally present in a Unity Mono `Managed` folder, and Carbon's preloader references `System.IO.Compression` as a framework assembly and uses `ZipFile.OpenRead` on Windows. On Oxide add `// Reference: System.IO.Compression` and `// Reference: System.IO.Compression.FileSystem`. Presence in Rust's Managed folder is UNVERIFIED; check before relying on it. Carbon carries its own tar.gz reader for Linux. |
| Process spawning | `System.Diagnostics.Process` (System.dll). Carbon itself spawns `curl` this way. Works on Mono; on Linux remember the `LD_PRELOAD`/`LD_LIBRARY_PATH` environment is inherited by children when running under Carbon, which can break a spawned SteamCMD. Clear those variables for child processes. |
| WebSocket client | `System.Net.WebSockets.ClientWebSocket` under Unity Mono is unreliable in my experience (UNVERIFIED). The server ships `Fleck` (server-side only; Oxide references it) and Facepunch's RCON listener. A plugin running inside the server does not need WebRCON at all: call `ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "<cmd>")` directly, as Carbon's AutoWipe does. WebRCON is for the external process. |

### 9.4 Architectural consequence

A plugin cannot replace the game DLLs it is running from, cannot survive `quit`, and cannot restart the process. On Windows the loaded DLLs are locked. Framework and game updates therefore need an **external process** (script or service) that stops the server, runs SteamCMD, installs Oxide or lets Carbon self-update, wipes files, and starts the server. The plugin's useful jobs are: detect and announce, save and quit cleanly, handle `OnNewSave`, and update other plugins' `.cs` files.

---

## 10. Open items to test on a real server

1. `ISteamApps/UpToDateCheck` for 258550 and 252490: does it return anything usable?
2. Precedence of command line vs `server.cfg` (Facepunch wiki and Oxide docs disagree).
3. Exact current file names in `server/<identity>/` after the October 2026 wipe, to confirm which files carry the save number (289) and which carry small persistence numbers.
4. Whether `RustDedicated_Data/Managed` contains `System.IO.Compression.dll` and `System.IO.Compression.FileSystem.dll`.
5. `find wipe` and `find server.` output, to confirm wipetimer convar spellings and look for any native wipe automation.
6. Public URL of Oxide's staging/release-branch builds on downloads.oxidemod.com.
7. SIGTERM on Linux, and Ctrl+C / window close on Windows: save or no save.
8. uMod's current rate-limit headers on `plugins/<slug>.json` and `search.json`.
9. Contents of Carbon's `.info` release assets (possibly a hash or version, useful for verification).
10. SteamCMD exit codes on success and failure on both platforms.

---

## Sources

Primary (code, docs, live APIs):
- https://github.com/OxideMod/Oxide.Rust (csproj, `RustExtension.cs`, `RustCommands.cs`, `RustCore.cs`, workflows, `resources/Rust.opj`, git history and tags)
- https://github.com/OxideMod/Oxide.CSharp, https://github.com/OxideMod/Oxide.Compiler, https://github.com/OxideMod/Oxide.Core, https://github.com/OxideMod/Actions
- https://github.com/OxideMod/Oxide.Docs and https://docs.oxidemod.com/ (install-oxide, setup-server, target-frameworks, webrequests, extensions, commands, `docs.json`)
- https://github.com/CarbonCommunity/Carbon (`SelfUpdater.cs`, `Versions.cs`, `Defines.cs`, core commands, AutoWipe module, admin Plugins tab, `tools/helpers`, `tools/build/runners/build.cs`)
- https://github.com/CarbonCommunity/Carbon.Documentation and https://carbonmod.gg/owners/auto-updates, https://carbonmod.gg/owners/getting-started
- https://api.carbonmod.gg/releases (live 2026-10-04)
- https://github.com/CarbonCommunity/Carbon/releases and https://github.com/OxideMod/Oxide.Rust/releases (via GitHub API)
- https://api.steamcmd.net/v1/info/258550 (live 2026-10-04), https://www.steamcmd.net/
- https://wiki.facepunch.com/rust/Creating-a-server
- https://wiki.facepunch.com/rust/server-wipe-timer
- https://wiki.facepunch.com/rust/server-browser-tags
- https://rust.facepunch.com/news and https://rust.facepunch.com/news/livestock (2026-10-01)
- https://github.com/Facepunch/webrcon (gh-pages `js/rconService.js`, `js/console.js`)
- https://developer.valvesoftware.com/wiki/SteamCMD
- https://umod.org/plugins/vanish.json, https://umod.org/plugins/search.json, https://umod.org/games/rust/latest.json, https://umod.org/games/rust (live 2026-10-04)
- https://api.lone.design/plugins.json (live), https://github.com/Nikedemos/Lone.DesignUpdateChecker

Secondary (forums, third-party docs and articles):
- https://docs.linuxgsm.com/steamcmd/errors
- https://github.com/GameServerManagers/LinuxGSM/commit/458fec5782b380fbf1c2f2e643dd426e3e606b32 (appinfo.vdf cache)
- https://github.com/GameServerManagers/LinuxGSM/issues/1830 (wipe file lists), https://github.com/GameServerManagers/LinuxGSM/issues/4720 (graceful stop)
- https://github.com/abspwgm/absolute-rust-server/issues/28 (SIGINT does not save, 2026-09-20)
- https://umod.org/community/general-support/41271-api-rate-limit, https://umod.org/community/general-support/20234-api-for-checking-for-plugin-updates
- https://umod.org/community/rust/28276-explanation-of-files-under-server-identity, https://umod.org/community/rust/3326-server-wipes-and-which-files-to-delete
- https://umod.org/community/rust/54141-umod-oxide-new-version-for-staging, https://umod.org/community/rust/3012-information-on-rust-rcon-protocol
- https://codefling.com/plugins/update-checker, https://codefling.com/forums/topic/1215-how-to-download-paid-products-with-an-api-url/, https://codefling.com/developers/
- https://github.com/m-this/tf2-archipelago/pull/181 (UpToDateCheck behaviour)
- https://www.corrosionhour.com/rust-server-restart-command/
- https://supercraft.host/wiki/rust/rust_server_wipe_schedule/, https://rustly.com/rust-wipe-timer/, https://low.ms/blog/rust-wipe-schedule-2026, https://hostingbuff.com/guides/rust-server-wipe-guide.html (third-party schedule pages; they contradict each other on the UTC time)
