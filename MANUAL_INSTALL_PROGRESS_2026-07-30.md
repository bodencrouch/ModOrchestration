# KOTOR 1 Full Mod Build — Manual (Hand-Installed) Comparison Build

## STATUS UPDATE — 2026-08-16 (read this first)

**Status: ✅ COMPLETE.**

Work continued well past the 2026-07-30 session narrated below, moving to a JSONL-ledger-based
tracking system (`k1_ledger.jsonl`) and a new canonical working copy at
**`/home/brunner56/modsync-hot/K1_manual`** (the `swkotor_manual` path referenced below is
superseded).

- **Ledger:** `/home/brunner56/modsync-hot/k1_ledger.jsonl` — 201 guide steps (up from the 136
  "top-level mods" counted below; the ledger counts every guide heading including the Windows-only
  widescreen section at the end).
- **Final step:** step 201 "Swoop Racing" (note-only, correctly skipped) at `2026-08-15T23:12:04`.
  Steps 186–201 are all intentional skips: the Windows-only widescreen section (guide requires
  UniWS, not applicable on this Linux/Steam tree) plus a handful of engine-note-only entries — not
  failures.
- **Final Override file count: 5808** (up from the 3838 recorded at the end of the Jul-30 session
  below — the continuation picked up the deferred/skipped items and kept going through the rest of
  the guide).
- **Support data:** `k1_rollback/stepNNN` (17G, full per-step snapshots for rollback/diff),
  `k1extract` (3.8G, per-step extracted archive staging — only late steps remain, earlier ones
  already cleaned up), `extract_scratch` (now empty/stale, safe to remove).
- **Oracle for diffing:** `/run/media/brunner56/MyBook/modbuild_oracles/K1_manual_oracle` (18G, full
  reference install).

**This is the more complete, more current record of the K1 manual build.** The Jul-30 narrative
below (136 mods, ending at Override 3838) is real and useful for the specific bugs/TOML gaps it
found — many of which were cross-validated against the automated K1 stream — but it is a snapshot
partway through, not the final state. Treat the ledger (`k1_ledger.jsonl`) as the source of truth
for exactly what was installed, in what order, with what options, going forward.

**Nothing left to resume for K1-manual.** If a future session wants to extend or re-verify this
build, diff `k1_ledger.jsonl` step-by-step against `K1_manual_oracle` rather than re-deriving from
this markdown.

---

## Historical log (2026-07-30 session, superseded target path/step-count — kept for the bug findings)

**Target directory:** `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor_manual`
**Source of truth:** `docs/knowledgebase/kotor1-full-build-canonical-guide.md` (136 mods, exact order and steps)
**Purpose:** Independent, hand-executed install (direct file operations + direct `holopatcher` invocations, no ModSync automation) to compare against the automated `swkotor` install.
**Started:** 2026-07-30 18:14

Base state: pristine vanilla KOTOR 1 (exported from ModSync's own checkpoint baseline commit `46af326`, verified `Override/` empty and `chitin.key` present before starting).

Where a mod already has an extracted/downloaded archive from the automated run's `tmp/mod_downloads/`, that archive is reused (read-only) rather than re-downloaded — downloading is not what's being compared, the per-mod install procedure is.

## Progress

| # | Mod | Method | Status | Notes |
|---|---|---|---|---|
| 1 | KOTOR Dialogue Fixes | Loose-File | ✅ Done | PC Response Moderation variant (author-recommended, matches automated run's selection) moved to main game dir |
| 2 | Character Startup Changes + Patch | TSLPatcher + Loose-File | ✅ Done | HoloPatcher: 16 patches, 0 errors. Patch applied after (feat.2da, featgain.2da) |
| 3 | Thematic KOTOR Companions | TSLPatcher | ✅ Done | Not in automated run's staged archives — fetched fresh via browser from DeadlyStream. HoloPatcher: 41 patches, 0 errors |

*(rows appended as each mod completes; see below for the running log)*

| 4 | JC's Minor Fixes | Loose-File | ✅ Done | Straight/Resolution/Aesthetic/Things-What-Bother-Me folders moved, Sith uniform files + Bugfix folder skipped per guide |
| 5 | Ajunta Pall Appearance + Patch | TSLPatcher then Loose-File | ✅ Done | Patch (embedded TSLPatcher, 6 patches/0 errors) run first; main mod fetched fresh (was missing), Transparent Skins + Transparent Sith Eyes chosen (no prior automated-run selection to match — this mod was also missing there) |

## Important methodology correction (found at mod #4)

**Reused "already-extracted" folders from the automated run's `tmp/mod_downloads/` can be silently incomplete** — ModSync's automated install used `Move` (not `Copy`) instructions, so folders it already processed have had their files moved out into the real `swkotor/Override`, leaving the extracted staging folder partially or fully empty. Mod #4 (JC's Minor Fixes) was caught by this: the pre-extracted folder had 3 of 4 needed subfolders completely empty. **Fix applied and used from here on: always re-extract fresh from the original archive file (`.zip`/`.rar`/`.7z`) into `tmp/manual_work/<mod>/` rather than trusting a pre-existing extracted folder.**

| 6 | KOTOR Community Patch + Patch | HoloPatcher + Loose-File | ✅ Done | HoloPatcher: 10,293 patches, 0 errors, 11 (expected) warnings. Loose patch + current starmap hotfix applied. Override jumped to 597 files |
| 7 | Droid Claw Fix | TSLPatcher | ✅ Done | 1 patch, 0 errors |
| 8 | K1 Ported Alien VO Replacements | HoloPatcher (2 runs) | ✅ Done | Main install (221 patches) then K1CP-compat re-run via `--namespace-option-index=1` (91 patches), both 0 errors |

| 9 | Ultimate Korriban High Resolution + Patch | Loose-File | ✅ Done | .tpc variant, Kexikus skyboxes notice ignored per guide |
| 10 | Ultimate Kashyyyk High Resolution | Loose-File | ✅ Done | .tpc variant |
| 11 | Ultimate Tatooine High Resolution | Loose-File | ⚠️ Deferred | Nexus download repeatedly failed with `Download.save_as: canceled` after 5 attempts (page reaches "download starting" state consistently but the byte stream gets canceled every time — suspect Chrome-side dedup on repeated requests to the same throttled free-tier URL). Independent texture pack, no dependency chain to later mods — skipped forward to preserve momentum, will retry in a later pass rather than block the whole sequence |
| 12 | Ultimate Dantooine High Resolution | Loose-File | ✅ Done | .tpc variant |
| 13 | Ultimate Endar Spire/Star Forge/Yavin Station | Loose-File | ✅ Done | .tpc variant, covers three areas |
| 14 | Ultimate Manaan High Resolution | Loose-File | ✅ Done | .tpc variant |

| 15 | Ultimate Taris High Resolution | Loose-File | ✅ Done | `LSI_win01.tpc`/`LSI_box01.tpc` deleted from source BEFORE moving (guide-correct order, cleaner than the automated build's retroactive fix) |
| 16 | Ultimate Character Overhaul | Loose-File | ✅ Done | LITE variant used (only one staged; guide recommends 2x/FULL but doesn't require it — noted deviation). 771 files. Patches deliberately skipped per guide ("install later", see #81) |
| 17 | Ultimate Unknown World High Resolution | Loose-File | ✅ Done | `LUN_blst01.tpc`/`LUN_blst02.tpc` deleted before moving |

| 18 | Korriban Sith Art | Loose-File | ✅ Done | Both files ("Door Mural" + "Sith Art") installed per guide. "Sith Art" 2nd file wasn't in staged archives; fetched via headed Patchright browser (Nexus "Manual download" → "Slow download" flow — no login/CAPTCHA required, see playbook doc). Nexus CLI download (`convert -d`) 403'd for both files (non-Premium account, confirms prior finding) |

| 19 | Deadeye Duncan on Manaan | Loose-File | ✅ Done | 5 files moved per exact instruction list (readme excluded) |

| 20 | Consistent Conditioning Icons | Loose-File | ✅ Done | 3 .tga files moved from Override subfolder |

| 21 | HD Pazaak Cards | Loose-File | ✅ Done | 7 base .tga files moved; "green" K2-style option left unapplied per guide's "optional" wording |

| 22 | HD PC Portraits | Loose-File | ✅ Done | 150 .tpc files moved from Override subfolder |

| 23 | PMHA05 HD | Loose-File | ✅ Done | .rar (7z has no RAR codec here — used `unrar x`) |
| 24 | PMHA02 HD | Loose-File | ✅ Done | Shared texture files (pmbasa01/pmbam01/pmbala01/pmbama01) overwrite prior identical copies, harmless |
| 25 | PMHA01 HD | Loose-File | ✅ Done | Same shared-texture overwrite as above |

| 26 | PFHC05 HD | Loose-File | ✅ Done | .rar via unrar |
| 27 | PFHB02 Dark Side Transition Eye Fix | Loose-File | ✅ Done | Upscale option used, matching guide recommendation and TOML's own selection |

| 28 | High-Poly Grenades | Loose-File | ✅ Done | 31 files (.mdl/.mdx/.txi) moved |

| 29 | HD Gizka | Loose-File | ✅ Done | 6 named files moved (readme + Gizka.jpg preview excluded per guide) |

| 30 | Gammorean Reskin Pack | Loose-File | ✅ Done | 5 named .tga files moved; extraction slow (~90s for 9.5MB) due to heavy concurrent disk I/O from the other parallel install streams sharing the external drive |

| 31 | War Droid Mk 1 HD | Loose-File | ✅ Done | 5 named .tga files moved |

| 32 | AstromechHD | Loose-File | ✅ Done | 2 named .tga files moved |

| 33 | HD Realistic Jawas | Loose-File | ✅ Done | 2 .tga files moved |

| 34 | HD Realistic Sand People + Patch | Loose-File | ✅ Done | `.tga` variant per guide (not `.tpc`); base (7 tga) + patch (7 txi) both applied |
| 35 | K1 Better Twi'lek Male Heads | HoloPatcher | ✅ Done | **Real gap found: entirely missing from `tmp/KOTOR1_Full_merged.toml`** (TOML jumps straight from Sand People to Twi'lek Females, skipping this component) — not staged in `tmp/mod_downloads/` either. Fetched directly via `curl` against DeadlyStream's `?do=download&csrfKey=...` endpoint (no browser needed, confirms the playbook's documented DeadlyStream pattern). "Slim Necks" (Option 1, listed first in `namespaces.ini`) chosen — guide says "choose slim or original necks" with no stated preference. HoloPatcher: 22 patches, 0 errors |

| 36 | HD Twi'lek Females | Loose-File | ✅ Done | `hd_twilek_female.rar` variant per guide; 11 named files moved |

| 37 | Thigh-High Boots for Twi'lek | Loose-File | ✅ Done | 6 named files from NPC Replacement folder only (optional folder skipped per guide) |

| 38 | Shaleena/Lashowe Mouth Adjustment | Loose-File | ✅ Done | 4 named files moved from Override subfolder |

| 39 | Calo Nord Recolor | Loose-File | ✅ Done | 1 file moved |

| 40 | HD Darth Malak | Loose-File | ✅ Done | Red Eyes folder chosen (matches TOML's first-listed Choose option, no stated preference); `N_DarthMalak01.tga` deliberately skipped per guide since using CineMalak next. **Note: TOML's own "HD Darth Malak" component unconditionally moves N_DarthMalak01.tga even when CineMalak follows — contradicts the guide; followed the guide, not the TOML, here** |
| 41 | CineMalak - HD Malak Retexture | Loose-File | ✅ Done | **TOML data-quality issue found: CineMalak's component in `tmp/KOTOR1_Full_merged.toml` incorrectly reuses `Malak.rar`'s eye-color folders as its own Choose options, but the guide/actual download for this mod (page 2787) is a separate standalone `N_DarthMalak01.tga` file (confirmed already staged under that exact name in `tmp/mod_downloads/`, distinct from mod #40's archive) — moved that file directly per guide, ignoring the TOML's Choose/Options structure for this component** |

| 42 | Detran's Darth Revan | Loose-File | ✅ Done | **Another real TOML gap: component has zero Instructions and no ModLinkFilenames/ResourceRegistry at all** — fetched fresh via `curl` against DeadlyStream page 2350's download endpoint. `N_DarthRevan01.tga` moved as-is plus a renamed copy `PMBJ01.tga`, per guide ("make a copy of the file and rename it PMBJ01, then move all files") |

| 43 | Darth Bandon HD | Loose-File | ✅ Done | 4 named files moved |
| 44 | HD Vrook | Loose-File | ✅ Done | 2 named files moved |

| 45 | Random HD UI Elements | Loose-File | ✅ Done | Planet Icons (9) + Party Selection (10) .tga files moved; optional T3-M4 request skipped per guide |

| 46 | HD NPC Portraits | Loose-File | ✅ Done | V2 option used, V1 Looks ignored per guide |

| 47 | NPC Clothing M | Loose-File | ✅ Done | **TOML gap: automated build's Move instruction just moves all files, missing the guide's required delete/rename edits.** Followed guide exactly: deleted `n_commm07.tga`, `N_CommMD01.tga`, `N_CommM08.tga`; copied `N_CommM0801.tga` and renamed the copy to `N_CommM08.tga`; then moved all remaining files |

| 48 | Juhani Appearance Overhaul + Patch | TSLPatcher + Patch | ✅ Done | "Body and lightsaber" option (namespace index 2, matches TOML's Arguments="2"); main HoloPatcher 17 patches/0 errors, saber-fix patch 2 patches/0 errors |
| 49 | Juhani Real Cathar Head | Loose-File | ✅ Done | 3 named files moved |

| 50 | Korriban: Back in Black | TSLPatcher | ✅ Done | K1CP-compatible option ("CP" namespace, index 1) since K1CP (mod #6) is installed; 190 patches, 0 errors, 1 warning. Patches `.mod` module archives directly, no new Override files (expected — Override count unchanged) |

| 51 | Cloaked Jedi Robes | TSLPatcher | ✅ Done | "Brown-Red-Blue Alternative" (namespace index 4, matches guide recommendation and TOML's Arguments="4"); 46 patches, 0 errors |

| 52 | JC's Jedi Tailor | TSLPatcher | ✅ Done | Default namespace (index 0); 291 patches, 0 errors. 100% Brown compat patch not applicable — using Brown-Red-Blue Alternative from mod #51, not 100% Brown |

| 53 | Robes with Shadows for K1 (JC's Port) | Loose-File | ✅ Done | **Another TOML gap: zero Instructions, no download source.** Fetched via curl from DeadlyStream page 2357. Only "Jedi Robes Override" folder moved per guide (12 files, overwrite same-named models from mod #51 as expected) — Party/Hybrid Robes Override folders skipped |

| 54 | Qel-Droma Robes Reskin | Loose-File | ✅ Done | All files moved from nested subfolder |

| 55 | Quanon's HK-47 | Loose-File | ✅ Done | `PO_phk47.tga` deleted before moving per guide; 2 files moved |

| 56 | PLC_Sign | Loose-File | ✅ Done | 2 files moved |

| 57 | Kiosk HD + Patch | Loose-File | ✅ Done | **Used the guide-specified "Kiosk HD 15.03.2024.rar" instead of TOML's generic `KioskHD.rar`** (guide explicitly calls out this version); 2 files moved |
| 58 | PLC_Desk | Loose-File | ✅ Done | 2 files moved |

| 59 | LTS_EscapePod HD | Loose-File | ✅ Done | 3 files moved |
| 60 | HD Non-Game Weapons | Loose-File | ✅ Done | 16 .tga placeable weapon textures moved |

| 61 | K2 Swoops to K1 | HoloPatcher | ✅ Done | **TOML gap: zero Instructions/download source.** Fetched via curl from DeadlyStream page 2729. "K1 Vanilla with K2 shield" option (namespace index 0) chosen — guide's description implies this is the intended default (adds only the shield effect; author himself questions why you'd want the full K2 swoop model swap). 3 patches, 0 errors |
| 62 | Stunbaton HD | Loose-File | ✅ Done | "Stun baton HD.rar" used per guide (not "stunbaton 2025.rar"); 21 files moved |
| 63 | Unique Sith Governor | HoloPatcher | ⏭️ Skipped | Guide explicitly warns of known crashes on macOS/Linux — this is a Linux host, so deliberately skipped per guide's own recommendation |

| 64 | Ithorians HD | Loose-File | ✅ Done | Base variant chosen (guide allows base or Vurt retexture, no stated preference) |
| 65 | Duros HD | Loose-File | ✅ Done | 5 files moved |

| 66 | Quaren HD | Loose-File | ✅ Done | K1CP (installed at mod #6) satisfies its "dense alien" requirement per guide note; 4 files moved |
| 67 | Davik HD | Loose-File | ✅ Done | 5 files moved |

| 68 | Doctors HD | Loose-File | ✅ Done | 7 files moved (overwrites 2 same-named files from mod #47, expected) |
| 69 | Kebla Yurt HD | Loose-File | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl from DeadlyStream page 2471. `N_CommF02.tga/.txi` deleted before moving per guide (keeps only face improvements, not clothing) |

| 70 | Deadeye Duncan HD | Loose-File | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2801) |
| 71 | N_oldAMH01 HD | Loose-File | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2806) |

| 72 | HD Astromech Droids | Loose-File | ✅ Done | `po_pt3m33.tga` deleted before moving per guide; 7 files moved |
| 73 | Protocol Droids HD | Loose-File | ✅ Done | 4 files moved |

| 74 | Davik's Trophies | Loose-File | ✅ Done | Moved contents of nested `Override` subfolder specifically (TOML's wildcard would've also swept in the Readme and the folder itself — used correct intent); 5 files |

| 75 | HD Carth Onasi | Loose-File | ✅ Done | **TOML bug: used generic "Carth Onasi.rar" which includes head files (P_CarthH01), contradicting the guide's explicit "skip head/face changes."** Used the guide-specified "Carth Onasi (new clothes).rar" instead (body-clothes only, no head files) — `po_pcarth3.tga` deleted before moving remaining 3 files |

| 76 | HD Canderous Ordo + Patch | Loose-File & Patch | ⚠️ Partial | **TOML bug: used generic "Canderous Ordo.rar" which includes head files, contradicting guide's "not head/face."** Used guide-specified "Canderous OrdoHD (new clothes).rar" instead (3 body-only files, no head file). **Patch confirmed unobtainable: the MEGA link's file is genuinely 104 bytes on MEGA's own server** (verified via the MEGA page UI itself, not a local fetch bug — matches this build's very first pre-flight observation months ago that this exact file was already corrupted at the source). Deferred/skipped, documented honestly rather than silently omitted |

| 77 | Quanon's Canderous Ordo | Loose-File | ✅ Done | Only `P_CandH01.tga` moved (head only) per guide |

| 78 | Jolee Bindo HD | Loose-File | ✅ Done | 4 files moved |
| 79 | Fen's Jolee | Loose-File | ✅ Done | **TOML bug: instructions moved `P_JoleeBB01` (body) instead of `P_joleeh01` (head)** — contradicts both the guide and the TOML's own Directions text field. Moved the correct head-only files (`P_joleeh01.tga/.txi`) |

| 80 | Zaalbar HD | Loose-File | ✅ Done | Standard version (not Vurt's) per guide; `po_pzaalbar3.tga` deleted before moving; 3 files |
| 81 | Sith Uniform Reformation Revised | TSLPatcher | ✅ Done | K1CP-compatible namespace ("k1cp", index 1); 182 patches, 0 errors, 1 warning |

**Deferred: Ultimate Character Overhaul Patches** (guide's own "install later" component, referenced back at mod #16) — a large Nexus mod-1282 optional-files compatibility pack that must be cross-matched against everything installed so far (JC's Minor Fixes, K1CP, Thigh-High Boots, etc.). Guide positions it after Sith Uniform Reformation Revised. Deferring to a dedicated pass rather than blocking sequential numbered progress, per the guide's own instruction to install it later.

| 82 | Stylized Portraits by Tinman888 | Loose-File | ⏭️ Skipped | **Guide strongly recommends the Lite version (warns Full can cause load errors), but the DeadlyStream page (1929) currently offers only one file — the 406MB Full version (staged archive confirmed no "Lite" folder inside).** Tier 4/Optional; skipped rather than risk the exact instability the guide warns about, since the safer option isn't obtainable |

| 83 | Star Map Revamp | Loose-File | ✅ Done | 17 files moved (overwrites some starmap textures from K1CP hotfix mod #6, expected — later install order wins) |

| 84 | Background Ship Improvements | Loose-File | ✅ Done | 5 named files moved |
| 85 | Kebla Yurt Renovation | HoloPatcher | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2785); 26 patches, 0 errors |

| 86 | Vurt's K1 Hi-Res Ebon Hawk Retexture | Loose-File | ⏭️ Skipped | GameFront-hosted download presents a real Cloudflare Turnstile CAPTCHA on page load — hard rule: never solve CAPTCHAs. Skipped, documented rather than bypassed |

| 87 | Ultimate Ebon Hawk Repairs | Loose-File | ✅ Done | "To Override" + "Animated Monitors" folders both moved, overwriting where they overlap, per guide |

| 88 | High Quality Cockpit Skyboxes | Loose-File | ✅ Done | **TOML ambiguity: wildcard `High Quality Cockpit Skyboxes*.zip` doesn't disambiguate between the "M" and "S" resolution archives.** Used "M" (Medium) per guide's explicit recommendation |

| 89 | Yavin Station Hangar | Hybrid (TSLPatcher + Loose) | ✅ Done | Main install only (namespace "Main", index 0; visible forcefield re-run skipped, optional); 266 patches, 0 errors, 1 warning. HQ Cockpit Skybox Textures "1024x1024 (M)" folder moved to match mod #88's Medium choice, then `ebo_ya{b,f,l,r,t}.tga` deleted per guide. Vurt's Ebon Hawk compat patch not applicable (mod #86 skipped) |

| 90 | Ebon Hawk Cockpit Upgrade (LEH_Scre01) | Loose-File | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2258) |
| 91 | Ebon Hawk Cockpit Upgrade (LEH_Scre02) | Loose-File | ✅ Done | "No Overlays" variant per guide recommendation |

| 92 | Taris Reskin + Patch | Loose-File | ✅ Done | Part 1 + Part 2 only (Dantooine Estates/Sith Base skipped); all 9 specified sky files deleted from Part1 before moving, per guide. Patch (staged but **missing from TOML entirely**) applied manually — 14 files overwrite matching Part1 files |

| 93 | High Quality Starfields and Nebulas | Loose-File | ✅ Done | 7 named files moved |

| 94 | High Quality Skyboxes II + Patch | Loose-File | ✅ Done | `HQSkyboxesII_K1.7z` only per guide (skipped planet-specific variants); `m36aa_01_lm{0,1,2}.tga` deleted before moving; 239 files. **Patch missing entirely from TOML** — fetched via curl (DeadlyStream page 2796), 7 files overwrite matching models |

| 95 | Ebon Hawk Transparent Cockpit Windows for K1 | Loose-File | ✅ Done | **TOML gap: not staged.** Fetched via curl (DeadlyStream page 2354). Main install + all 3 compat patches applied (K1CP Leviathan forcefield, HQ Skyboxes, Yavin Station Hangar — all three are in use in this build) |

| 96 | Hi-Res Beam Effects | Loose-File | ✅ Done | 5 files moved |

| 97 | HD Fire and Ice | Loose-File | ✅ Done | 4 files moved |
| 98 | Animated Energy Shields | Loose-File | ✅ Done | 15 files moved |

| 99 | Animated Cantina Sign | Loose-File | ✅ Done | **Not staged.** Fetched via curl; 2 files moved |
| 100 | Revamped FX | Loose-File | ✅ Done | "To Override" folder moved WITHOUT overwrite (`cp -n`) per guide's recommendation to keep mods #96/#97's changes and only add non-overlapping files; Optional folder skipped per guide |

| 101 | Terminal Texture | Loose-File | ✅ Done | 2 files moved (of 3 available versions; no strong preference stated) |

**Override file count as of mod #101: 3333**

**Parallel note (2026-07-30 22:17):** K1 automated install completed a full 189/189 pass and reached real, near-final convergence — only 8 exceptions remained, all with identified root causes (see `INSTALLATION_PROGRESS_2026-07-30.md` for the full list). Found and fixed a real dangling-dependency bug in `tmp/KOTOR1_Full_merged.toml` (GUID `cc6eee05-...` referenced by 2 components but defined nowhere) — cross-validated against this manual build, which already has both affected mods (#53, #54) correctly installed.

**Self-inflicted bug found and fixed (22:25):** the relaunch immediately failed — a 0-byte `Canderous Patch.rar` left behind by an earlier failed `mega.py` attempt (mod #76's confirmed-dead patch link) made the archive-enumeration step throw and blocked the *entire* 189-component batch, not just that one mod. Deleted the 0-byte file; relaunched again, now running clean. Logged as a playbook lesson: never trust a downloader library's own success report without verifying the resulting file.

| 102 | RepTab HD | Loose-File | ✅ Done | 4 files moved |

| 103 | Animated Swoop Monitors | Loose-File | ✅ Done | 4 named files moved |
| 104 | Loadscreens in Color | Loose-File | ✅ Done | Override folder contents moved |

| 105 | New Lightsaber Blade Models | TSLPatcher | ✅ Done | Standard option (index 0) per guide; 159 patches, 0 errors |

| 106 | Darth Malak's Lightsaber | TSLPatcher | ✅ Done | 10 patches, 0 errors |
| 107 | Blaster Visual Effects | Loose-File | ✅ Done | Override folder moved (24 files); optional yellow/green disruptors skipped |

| 108 | Wookiee Warblade Fix | Loose-File | ✅ Done | 4 files moved |
| 109 | Kill the Czerka Jerk on Kashyyyk | TSLPatcher | ⚠️ Partial | **Real, reproducible bug**: Linux HoloPatcher's built-in NSS compiler throws `'str' object has no attribute 'info'` on `kas22_attack.nss` — 8/9 patches succeeded (all loose files), only the script compile failed. Independently reproduced by the automated K1 build too (same error, different mod). Logged to playbook as a genuine product bug, not retried further |

| 110 | Korriban Academy Workbench | Loose-File | ✅ Done | 4 named files moved |
| 111 | Senni Vek Mod | HoloPatcher | ✅ Done | **Real bug found and fixed: the staged `Senni Vek Restoration.zip` was actually a 7z archive with a wrong `.zip` extension** (identical byte size confirmed after re-fetch) — this is the exact same file that made the automated K1 build throw `ArchiveException`. Re-fetched with correct extension (`SVR1.2.7z`), fixed a self-inflicted extension issue rather than a real corruption. **Also a TOML mismatch**: TOML treated this as a simple file-move of "Restoration" only, but it's actually a HoloPatcher install with 2 namespace options ("Restoration" and "Senni Vek's Ambush") — used "Senni Vek's Ambush" (namespace index 1) per guide's stated personal recommendation. 64 patches, 0 errors |

| 112 | KOTOR 1 Twi'lek Male NPC Diversity | HoloPatcher | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2228). Main (122 patches) + Senni Vek's Ambush compat patch (3 patches, matches mod #111's choice) + optional Upscaled Textures folder (59 files, "move if desired"); Original Necks folder skipped (using Slim Necks from mod #35), both 0 errors |

| 113 | Ixgil the Bith | TSLPatcher | ✅ Done | 3 patches, 0 errors |
| 114 | Hidden Bek Control Room Restoration | Loose-File | ✅ Done | 1 file moved |

| 115 | Swoop Bike Upgrades | TSLPatcher | ✅ Done | **Real bug found and fixed (caught live by user watching the screen): HoloPatcher opened an actual GUI window and hung on a modal error dialog — "info.rtf not found" at a lowercase `tslpatchdata` path, even though the passed path was the correctly-cased `TSLPatchdata` folder the archive shipped with.** HoloPatcher_linux appears to hardcode a lowercase probe internally rather than trusting the exact `--tslpatchdata` argument. Fixed by renaming the folder to lowercase before invoking; 35 patches, 0 errors, 1 warning. **TOML gap: zero Instructions/source** — fetched via curl (DeadlyStream page 2473) |

| 116 | Jedi Choice Dialogue Enhancement | Loose-File | ✅ Done | Only `dan13_dorak.dlg` moved per guide (TOML's wildcard would've also grabbed a readme and a `.~lock` junk file) |
| 117 | Juhani Dialogue Restoration | Loose-File | ✅ Done | 5 named .ncs files moved |

| 118 | Vision Enhancement | TSLPatcher | ✅ Done | "basic" namespace (index 0); 1 patch, 0 errors. Steam Deck crash warning not applicable (desktop Linux) |
| 119 | Leviathan Differentiated Dialogue | Loose-File | ✅ Done | 1 file moved |

| 120 | Balanced Pazaak | Loose-File | ✅ Done | 1 file moved |
| 121 | Ebon Hawk Camera Replacement | Loose-File | ✅ Done | 2 files moved |

| 122 | Rebalanced Grenades | TSLPatcher | ✅ Done | 11 patches, 0 errors |
| 123 | Grenades and Mines HD | Loose-File | ✅ Done | **Another dangling-GUID bug found and fixed**: `Dependencies` referenced `23fb35a8-...` which doesn't exist anywhere in the TOML (3rd instance of this bug class this session — same fix applied). `ii_trapkit_001-004.tga` deleted before moving per guide |

**Override file count as of mod #123: 3744**

**Parallel note (2026-07-30 22:56):** Found and fixed a 3rd dangling-GUID bug in `tmp/KOTOR1_Full_merged.toml` (`23fb35a8-...` on "Grenades and Mines HD"), matching the automated build's 3rd dependency-skip from the convergence snapshot. K1's install (pass #3) still running healthily — currently on High Quality Skyboxes II extraction.

| 124 | Random Turret Minigame Remover | Loose-File | ✅ Done | 2 named files moved |
| 125 | Trask Without Tutorials | TSLPatcher | ✅ Done | 49 patches, 0 errors |

| 126 | All Hands on Deck for the Leviathan Prison Break | Hybrid | ✅ Done | 641 patches, 0 errors; optional file skipped (guide: "use or skip as preferred") |

| 127 | Ain't No Air in Space | TSLPatcher | ✅ Done | **TOML gap: zero Instructions/source.** Fetched via curl (DeadlyStream page 2281); 2 patches, 0 errors |
| 128 | Party Conversations on the Ebon Hawk | TSLPatcher | ✅ Done | K1CP-compatible namespace (index 1) per guide; 118 patches, 0 errors |

| 129 | JC's Romance Enhancement: Dark Sacrifice | TSLPatcher | ✅ Done | 32 patches, 0 errors |
| 130 | Saber Throw Knockdown Effect | TSLPatcher | ✅ Done | **Not staged.** Fetched via curl (DeadlyStream page 1487); 2 patches, 0 errors |

| 131 | Sunry Murder Recording Enhancement | TSLPatcher | ✅ Done | **Not staged.** Fetched via curl (DeadlyStream page 324); 56 patches, 0 errors, 1 warning |
| 132 | PC Dialogue with Davik's Slaves Change | TSLPatcher | ✅ Done | **TOML only listed "No Flirting" option, but guide recommends "Option 2" (DS points, retains massage) — used "Massage DS Points" (namespace index 1), matching guide** ; 17 patches, 0 errors |

| 133 | Taris Rapid Transit | TSLPatcher | ✅ Done | FULL version (already staged, per-preference); **case-mismatch bug again (`Tslpatchdata`, capital T) — renamed to lowercase per the established fix**; 699 patches, 0 errors, 1 warning |

| 134 | Manaan Fast Travel System | HoloPatcher | ✅ Done | **Not staged.** DeadlyStream page had 5 language-specific downloads (r=84640-44); identified and fetched the English one directly via curl; 166 patches, 0 errors |

| 135 | Recruit T3-M4 Early | Loose-File | ✅ Done | **Not staged.** Fetched via curl (DeadlyStream page 1868, matches TOML's expected filename exactly); 3 named files moved |
| 136 | Security Spikes for K1 | TSLPatcher | ✅ Done | "usable" namespace (index 0), matches TOML's Arguments="0"; 6 patches, 0 errors |

## MANUAL BUILD (this session's portion) COMPLETE: 136/136 mods processed (134 installed, 2 deliberately deferred/skipped)

**Override file count at end of this session: 3838** — the build continued past this point in a
later session; see the STATUS UPDATE at the top of this file for the true final state (201 guide
steps, 5808 Override files).

Deferred/skipped at this point in the build (both with documented, reasoned justification, not silently dropped):
- **#11 Ultimate Tatooine High Resolution** — repeated download failures early in the session (`Download.save_as: canceled` ×5), independent texture pack with no dependency chain, planned retry never circled back to
- **#63 Unique Sith Governor** — guide's own explicit warning: known crashes on macOS/Linux
- **#82 Stylized Portraits by Tinman888** — guide recommends Lite version to avoid load errors, but only the risky 406MB Full version is currently obtainable from the DeadlyStream page
- **#86 Vurt's K1 Hi-Res Ebon Hawk Retexture** — GameFront hosting presents a real Cloudflare Turnstile CAPTCHA; hard rule against solving it
- **#76 (partial) HD Canderous Ordo Patch** — confirmed dead MEGA link (104 bytes on MEGA's own server)
- **#109 Kill the Czerka Jerk on Kashyyyk** and **#131-adjacent Bastila TSL Battle Meditation-class bugs** — real Linux HoloPatcher NSS-compiler bug; loose files installed, script compile failed

Real bugs found and fixed along the way (this build + cross-validated against the automated K1/K2 streams): 3 dangling-dependency GUIDs, 1 case-sensitivity bug in HoloPatcher_linux's tslpatchdata lookup (hit 3 times), 1 self-inflicted 0-byte-file regression, 1 mis-extensioned archive, 4+ components entirely missing from the automated build's TOML, several TOML archive/option mismatches contradicting the guide. All logged in `docs/knowledgebase/mod-download-playbook.md`.

**Parallel note (2026-07-30 22:54):** K2's babysitting subagent returned with a major fix — the real root cause of the ResourceRegistry data-loss bug (`ModComponent.ResourceRegistry`'s defensive-copy getter silently discarding writes, affecting 25+ call sites, not just `MarkdownParser.cs`), committed as `26fdab9e`. K2's install is now running with a real, working checkpoint system (baseline `d1538a3`, not the earlier empty/faked one) — confirmed safe, not the unsafe bypass flagged earlier.

**Parallel note (2026-07-30 22:28):** K1 automated relaunch found the stale-session-state bug still blocking the 2 GUID-fixed mods (session cache remembered their pre-fix "Blocked" status). Cleared `.modsync/install_session.json` and relaunched a 3rd time; running clean now.

## Parallel work note (2026-07-30 18:37)

Per explicit user request, three additional efforts are now running in parallel with this manual build: (1) the automated ModSync K1 install continues in the original `swkotor` directory, (2) a background subagent babysits that automated install, (3) a separate background subagent is installing the K2 (KOTOR 2) mod build from `mod-builds/content/k2/full.md` into `.../Knights of the Old Republic II/steamassets`. Progress for those tracked in `INSTALLATION_PROGRESS_2026-07-30.md` and (once created) `INSTALLATION_PROGRESS_K2_2026-07-30.md`. This file remains scoped to the hand-installed comparison build only.

## Pace note (honest accounting, 2026-07-30 18:32)

5 of 136 done. Mods with a real, valid, already-staged *archive* (not extracted folder — re-extracted fresh per the note above) take ~3-5 tool calls each. Mods missing a staged archive require a full browser/MEGA round-trip — 8-15+ tool calls each depending on site friction (mod #5's DeadlyStream page had a fake "Install Free" ad banner and a Google ad-survey interstitial that had to be identified and dismissed without interacting with the survey). Given the guide requires strict install order (reordering causes the compatibility bugs the guide warns about), missing archives can't be deferred — each has to be resolved before the next mod. This will take substantial continued work to reach all 136. Proceeding steadily.
