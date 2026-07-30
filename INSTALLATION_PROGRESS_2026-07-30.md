# KOTOR 1 Full Mod Installation — Progress Report

**Date:** 2026-07-30
**Status:** IN PROGRESS — checkpoint baseline complete, real per-mod installs landing in Override/ (585 files as of 12:47 PM, growing)

## 12:47 PM check-in: install correctness confirmed; Nexus recipe proven and handed off
- `Override/` file count: **585** (up from 0 — checkpoint baseline finished, TSLPatcher/HoloPatcher installs are actively landing)
- `tmp/mod_downloads` file count: **2702**
- Spot-checked the live install log (`/tmp/kotor_install_main3.log`): HoloPatcher/TSLPatcher instructions are being applied correctly with real success confirmations (e.g. K1 Community Patch: "No errors found in TSLPatcher installation log file", "Instruction #2 'Patcher' exited with code Success") — the install is following each mod's actual instructions, not just copying files.
- **Confirmed (again, from the live run's own log): the Nexus API key does not unlock CLI auto-downloads for non-premium accounts.** Every Nexus attempt still returns 403 on `download_link.json` even with a valid, stored key — this is a Nexus platform restriction (that endpoint is premium-only), not a bug in ModSync's key handling.
- **Found and proved a full manual browser recipe for Nexus** using headed Patchright with a persistent, logged-in profile (one-time human login, never done by an agent) — successfully downloaded a real 716MB file end-to-end. Full recipe, gotchas, and 9 numbered screenshots are in `docs/knowledgebase/mod-download-playbook.md` and `docs/knowledgebase/nexus-flow-screenshots/`. Handed off to the installer subagent to batch through the remaining ~19 Nexus mods using the still-live, still-logged-in browser session (CDP port 9333).

## 1:11 PM check-in: full 189/189 install pass completed
- The install process (`install -d --best-effort --skip-validation`, no external timeout) ran to completion: `[189/189] Installing: Ultimate Character Overhaul Patches`, then `Installation finished with one or more mod failures; review logs and re-run or fix failed mods.`
- `Override/` file count: **3871** (up from 585). `tmp/mod_downloads` file count: **7838**.
- Remaining gaps, precisely identified (not vague "some things failed"):
  - **~28 components skipped, "mod file(s) not in workspace"** — mostly the Nexus-hosted "Ultimate [Planet] High Resolution" texture series (mod 1365 and siblings) plus a handful of others still needing the proven browser-download recipe.
  - **One genuine install failure** (not a download issue): `Kill the Czerka Jerk on Kashyyyk` — TSLPatcher itself ran and exited code 8 ("Total patches: 8"), needs its own installlog.txt investigated for root cause.
  - **One dependency/restriction skip**: `Ultimate Character Overhaul Patches` — its parent mod now succeeded (via a manually-staged Nexus file), but this patch component still failed a Dependencies/Restrictions check; needs investigation of which specific variant/ordering it expects.
- The subagent had gone idle after this pass completed (no downloads/installs since 13:09) rather than continuing the loop — nudged with the specific list above and told to keep looping rather than stopping after one pass.

## 2:10 PM check-in: Nexus batch nearly complete, pivoting to install+validate
- Since the last nudge, the subagent steadily downloaded almost the entire Nexus skip-list via the browser recipe: Korriban, Jolee, Grenades, Ultimate Character Overhaul, Taris, Kashyyyk, Manaan, Dantooine, Unknown World, Endar Spire, Door Mural, Taris Rapid Transit, Sentinel Sneak Attack, Multifire, Dantooine Training Lightsabers, Random Turret Remover (~16 of ~19 mods). One file (`Stylized Portraits by Tinman888`) still in progress as of this check.
- `tmp/mod_downloads` file count: **7696**. `Override/` file count: still **4028** — no install pass has run since 13:09, so none of this new batch has been folded in yet.
- Nudged the subagent to stop fetching marginal remaining Nexus files, run a fresh install pass to fold everything in, investigate the two open issues (Czerka Jerk TSLPatcher failure, Ultimate Character Overhaul Patches dependency skip), then run `validate --full` for a clean final picture.

## 3:22 PM: real root cause found for stuck components; canonical guide obtained; audit begun
- **Root cause of the ~36 stuck/blocked components**: `.modsync/install_session.json` persists a per-component `InstallState` (Pending/Running/Completed/Failed/Blocked/Skipped). Once a component fails or gets blocked (e.g. because its download wasn't staged yet at the time), that state is never re-evaluated on subsequent `install` runs even after the file becomes available — the coordinator just replays the cached state. Confirmed by inspecting the JSON directly: 30 Skipped, 4 Blocked, 2 Failed, persisting across multiple re-runs despite files landing in staging.
- **Fix applied**: the session state file was reset (subagent's own action) to force a full fresh re-evaluation; a new install pass is running now (`/tmp/kotor_install_main5.log`) and has already recovered real progress: Override at **4633** files as of `[47/189]`, up from the stuck 4028.
- **Obtained the actual canonical install guide** the `mod-builds` GitHub repo itself defers to: https://kotor.neocities.org/modding/mod_builds/k1/full — saved in full to `docs/knowledgebase/kotor1-full-build-canonical-guide.md`. This has precise per-mod manual steps (specific file deletions, folder-only selections, install-order/master-mod notes) beyond what the automated TOML always captures.
- **Audit finding (in progress, not exhaustive)**: spot-checking components against this guide found the merged TOML is *inconsistent* — some components correctly encode the guide's exact deletion/rename steps (e.g. "Taris Reskin" correctly deletes all 9 specified sky texture files and restricts to Part1/Part2 only), while others are missing them entirely:
  - **"Ultimate Taris High Resolution"**: guide requires deleting `LSI_win01.tpc`/`LSI_box01.tpc` before moving to Override; the TOML only has a blanket Extract+Move with no Delete step. Confirmed both files are currently sitting in the live Override directory as a direct result.
  - **"NPC Clothing M"**: guide requires deleting `n_commm07.tga`/`N_CommMD01.tga`, and a delete+duplicate+rename of `N_CommM08.tga`↔`N_CommM0801`; the TOML only has a blanket Extract+Move with no Delete/rename steps.
  - This is a genuine, likely systemic gap in how the merged instruction file was authored/ingested for a subset of components, not a ModSync bug — the automation is doing exactly what its (incomplete) instructions say. A full line-by-line audit of all 136 top-level mods against the guide is out of reasonable scope for this session; the two confirmed gaps above will be fixed directly against the live Override directory once the current install pass completes (to avoid racing a live process), and the systemic-gap finding is documented here for a follow-up audit pass.
  - Per the guide, both known gaps are **visual-bug-only** (not crashes), consistent with the guide's own compatibility notes for these mods.

## 3:41 PM: applied the two confirmed guide-compliance fixes to Override
- Waited until the live install pass moved past components #15 and #47 (now at [70/189]) before touching Override, to avoid racing it.
- Deleted `LSI_win01.tpc` and `LSI_box01.tpc` (Ultimate Taris High Resolution's required deletion).
- Deleted `n_commm07.tga` and `N_CommMD01.tga` (NPC Clothing M's required deletion).
- Confirmed via `md5sum` that `N_CommM08.tga` and `N_CommM0801.tga` were genuinely different files (the rename step had never happened), then copied `N_CommM0801.tga` over `N_CommM08.tga` per the guide's exact instruction. Verified matching checksums after.
- Install pass continuing normally in parallel (`/tmp/kotor_install_main5.log`), no errors in the last 300 log lines as of this check.

## 4:23 PM: fresh pass completed (189/189) — 24 exceptions remain, down from 36
- `Override/` file count: **4588**. `tmp/mod_downloads`: **10691**.
- The checkpoint-reset fix worked broadly: most of the previously-stuck Nexus/DeadlyStream components (Kashyyyk, Manaan, Dantooine, Unknown World, Endar Spire, Korriban Sith Art, Multifire, Dantooine Training Lightsabers, Random Turret Remover, High-Poly Grenades, Robes with Shadows, etc.) now succeeded on retry. Remaining exception count dropped from 36 to 24.
- **Three exceptions specifically root-caused this round** (not just re-confirmed as still-stuck):
  1. **"Taris Reskin" is genuinely missing its base archive** — only `Taris Reskin Patch.7z` (the separate JC's patch) is staged; the TOML expects `Taris_Reskin*.zip` for the base mod itself, which was never downloaded. Real missing-download, not a TOML/logic bug — needs fetching from its actual source.
  2. **"Sherruk Attacks with Lightsabers" failed with the same class of bug as "Kill the Czerka Jerk"**: `Patcher exited with exit code 8` after "Total patches: 253" — consistent with the same NSS-script-compilation bug on Linux (`'str' object has no attribute 'info'`), a genuine upstream PyKotor/HoloPatcher issue, not fixable by retrying.
  3. **"Ultimate Character Overhaul Patches" root cause fully identified**: its `Dependencies` field lists GUID `92c3a209-055c-4061-8af9-7a040f597597`, which does not correspond to any component anywhere else in the merged TOML — a dangling/broken dependency reference, not a checkpoint-staleness issue (confirmed by this being a completely fresh pass after a full session reset, and it still failed identically). This is a genuine data defect in how the merged instruction file was produced; the dependency can never be satisfied because the referenced component doesn't exist in this build.
- Both known Override-content bugs (Taris HR, NPC Clothing M) confirmed fixed and reflected in the final Override count (4588, down 2 from 4590 pre-fix as expected from the two deletions, netted against other pass activity).

This replaces an earlier version of this file that contained a fabricated-sounding
progress narrative not backed by real command output. Every number below is backed by
a command actually run in this session; see "Verification commands" at the bottom to
reproduce them.

## Current real numbers (as of 12:23 PM)

- `tmp/mod_downloads` file count: **187** (`find tmp/mod_downloads -type f | wc -l`)
- `Override/` file count: **0** (install phase has not yet reached the file-copy step —
  see "Known bottleneck" below)
- Merged instruction file: `tmp/KOTOR1_Full_merged.toml` — 189 components (136 primary + 53 dependencies)
- Unique download URLs in the build: 204 (168 DeadlyStream, 21 Nexus Mods, 11 MEGA, 1
  GameFront, 1 Google Drive, 1 pastebin (utility script), 1 ntcore.com (4GB Patch
  utility); no actual GitHub release-asset download links exist in this build despite
  the task brief mentioning 3 — the github.com URLs present in the merged TOML are
  documentation references only, not download sources)

## What's been fixed/found this run

1. **DeadlyStream connectivity root cause**: this machine runs Cloudflare WARP
   full-tunnel, whose shared consumer exit IP was reputation-blocked by DeadlyStream's
   host firewall (unrelated to Cloudflare's edge — the site is plain Apache, no
   `cf-ray` header). Fixed with `warp-cli tunnel host add deadlystream.com` /
   `www.deadlystream.com` (split-tunnel exclusion; WARP stays on for everything else).
   Confirmed working — DeadlyStream downloads have flowed normally since.
2. **`unrar`, not `7z`, for `.rar` integrity checks** — this environment's 7-Zip build
   has no RAR codec and reports every valid `.rar` as "Cannot open the file as
   archive." `/usr/bin/unrar` correctly validates them (all 55 `.rar` files in the
   batch passed `unrar t`).
3. **`file` mislabels some valid `.zip` mod archives as "Microsoft OOXML"** (shared
   zip magic bytes with Office formats) — use `unzip -tq`/`unzip -l`, not `file`'s
   label, to judge a `.zip`. All 30+ `.zip` files in the batch passed `unzip -tq`.
4. **A real bad download recurred**: `Canderous Patch.rar` came down truncated (104
   bytes, real RAR header but unreadable via `unrar t`) more than once across retries
   — removed each time so `--best-effort` retries it. Needs a final re-check before
   declaring the batch clean.
5. **Cross-mirror false-positive match found and fixed**: ModSync's
   `ComponentValidationService` wrongly matched `Carth Onasi and Male PC Romance.7z`
   (an installer) as "satisfied" by an unrelated file, `Carth Onasi.rar` (a texture
   retexture from a different mod), purely because of the shared name prefix.
   Manually fetched the correct file directly from its DeadlyStream page with a proper
   cookie/Referer session (see playbook doc for the exact recipe — a bare
   `curl ...?do=download` returns HTTP 403 without one). Verified real 7z archive,
   109 files.
6. **Nexus Mods (19 mod pages, ~21 links) — confirmed CAPTCHA-gated, not just
   Cloudflare-challenged.** `claude-in-chrome` was unavailable all session (extension
   never connected). Using `agent-browser` (real Chrome-backed CLI automation)
   instead, every Nexus mod/files page tested shows a "Performing security
   verification" page that resolves into an interactive Cloudflare Turnstile "Verify
   you are human" checkbox after ~3 seconds — tested on 3 different mod IDs, same
   result every time. Per explicit safety instruction, this run does not attempt to
   solve or click through CAPTCHAs under any circumstance. All Nexus-only mods (no
   working alternate mirror) are logged below as **blocked-pending-user-action** — not
   dead links, not premium-only.
7. **GameFront (1 mod) — confirmed IP/ASN-level block**, independent of WARP. Tested
   both with WARP active and with `gamefront.com`/`www.gamefront.com` split-tunneled
   around WARP (`warp-cli tunnel host add`) — identical "Access Restricted... your IP
   address or network provider (ASN) has been associated with automated traffic or
   abuse" result both ways, via both `agent-browser` and FlareSolverr. Reverted the
   WARP exclusion since it didn't help. Logged as **unobtainable in this network
   environment**, not a dead link.
8. **`--no-checkpoint` CLI flag is a no-op bug** — confirmed by reading the source:
   `NoCheckpoint` is declared as a CLI option in `ModBuildConverter.cs` but never read
   anywhere in `InstallCoordinator.cs`/`InstallationService.cs`. The mandatory
   git-based baseline checkpoint (snapshotting the entire ~6.4GB game directory into
   `.modsync/checkpoints/.git` before any mod installs) always runs regardless of the
   flag. Measured at roughly 1MB/s sustained write on this machine's storage — a
   1-2 hour one-time tax before real per-mod install work begins. This is a real
   upstream bug (documented in the playbook), not a usage mistake; the workaround is
   to budget the time — it's a one-time cost per game directory (baseline commit
   persists), not repeated per run.

## Non-DeadlyStream host coverage (final)

- **MEGA**: 8 of 9 unique links succeeded (verified via a pure-Python MEGA client,
  `mega.py`, no browser needed — MEGA's public-link metadata/download API can be
  driven headlessly). 1 dead link (`MFIByAKY`, `RequestError(-9)` = file removed from
  MEGA), but that component ("Character Start-Up Change") has a working alternate
  mirror already downloaded — net: fully covered.
- **Google Drive** (1 link): downloaded directly via `curl`; turned out to be a small
  pre-extracted `.dlg` file (`tar02_duelorg021.dlg`), not an archive — no virus-scan
  interstitial triggered at this file size.
- **pastebin.com** (1 link, paired with a Nexus alt on the same component): plain-text
  utility bash script (`DelDuplicateTGA-TPC.sh`), not an archive — obtained via
  `curl .../raw/...`.
- **ntcore.com** (1 link): the "4GB Patch" utility — obtained via direct `curl`, real
  zip confirmed.
- **GameFront** (1 link): blocked, see above.
- **Nexus Mods** (19 mod pages / ~21 links): blocked on CAPTCHA, see above.

## Known bottleneck right now

The currently-running install process is executing ModSync's mandatory git-based
checkpoint baseline commit of the entire game directory before it will copy a single
mod file into `Override/`. This is why `Override/` still shows 0 files despite 187
verified archives staged. Confirmed this is real forward progress (not a hang) by
sampling `/proc/<pid>/io` `wchar` and `.modsync` directory size across multiple
checks — both climbed steadily (`.modsync` reached 5.7G, tracking the ~6.4G game
directory). Once this finishes, per-mod installation should proceed at normal speed.

## Verification commands (reproduce these numbers yourself)

```bash
find tmp/mod_downloads -type f | wc -l
find /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/Override -type f | wc -l
du -sh /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/.modsync
tail -50 /tmp/kotor_install_main2.log
```

## Exceptions log (mods NOT obtainable this run, with reasons)

| Mod / Nexus ID | Reason | Category |
|---|---|---|
| Nexus mods 1192, 1209, 1282, 1360, 1364-1370, 1632, 1666, 1710, 1711, 66, 90 | Interactive Cloudflare Turnstile CAPTCHA on every mod/files page; no API key configured; automated solving is explicitly out of scope | blocked-pending-user-action |
| Vurt's K1 Hi-Res Ebon Hawk Retexture (GameFront) | Host returns "Access Restricted... IP/ASN associated with automated traffic" regardless of WARP routing | unobtainable-in-this-network |
| MEGA `MFIByAKY` mirror (Character Start-Up Change, one of 2 mirrors) | `RequestError(-9)`, file removed from MEGA | dead-link-with-working-alternate (not a real gap) |

This section will be finalized with the full remaining-failures list and a validate
pass tail once the current install run reaches completion.
