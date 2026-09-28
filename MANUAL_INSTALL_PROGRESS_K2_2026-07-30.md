# KOTOR 2 Full Mod Build — Manual (Hand-Installed) Comparison Build

## STATUS UPDATE — 2026-08-16 (read this first)

**Status: ✅ COMPLETE.**

This build never progressed past a 0-row stub in the 2026-07-30 session (the table below was empty
— see the original header preserved below for the intended methodology). The real work happened in
a later session (2026-08-13 → 2026-08-15) using a JSONL-ledger-based tracking system instead of a
hand-maintained markdown table, mirroring the same evolution the K1 manual build went through.

- **Working copy:** `/run/media/brunner56/MyBook/Workspaces/ModSync/kotor_manual_workdir/K2_manual/steamassets`
  (the K2 Aspyr/Steam layout uses a lowercase `steamassets/override`, `steamassets/modules` — not
  the flat layout K1 uses).
- **Ledger:** `/run/media/brunner56/MyBook/Workspaces/ModSync/tmp/manual_work_k2_fresh/ledger.jsonl`
  — 161 unique guide steps (step 000 = seed verification, 001–160 = guide content).
- **Seeded from a verified pristine baseline**: `rsync -a` from
  `/run/media/brunner56/MyBook/modbuild_oracles/K2_manual_oracle` (never `cp -al`, never the live
  Steam install) — confirmed vanilla before starting: `override/` empty, `dialog.tlk` at the
  vanilla-sized 10,233,069 bytes / sha256 `d362a515...`, 334 modules. **This deliberately avoided
  the real Steam K2 directory**, which at seed time already held a *different*, older 6548-file
  modbuild and must not have been overwritten.
- **Final tally: 150 steps passed, 11 skipped (all with documented reasons — N/A platform steps,
  mutually-exclusive alternatives, engine-bug notes with no install action), 0 blocked.** Final
  content entry: step 160 "Dialogue Skipping" (note-only, correctly skipped) at
  `2026-08-15T19:44:55`. (The single previously-blocked step 013 was re-verified on 2026-08-21 and
  corrected to `pass` — see "The former step-013 gap" below.)
- **Final Override file count: 6522** (verified — the ledger's own `override_after: 6522` on the
  last entry matches a direct `find steamassets/override -type f | wc -l` count on disk).
- `dialog.tlk` grew from the vanilla 10,233,069 bytes to a final size tracked per-step in the
  ledger (TSLRCM and other dialogue-adding mods extend it); 334 modules held steady throughout
  (no new `.mod` files added/removed — expected for a loose-file/TSLPatcher-only build without new
  areas).
- **Rollback snapshots**: `tmp/manual_work_k2_fresh/rollback/step000` through `step160` — one full
  snapshot per step, usable for step-by-step diffing against
  `/run/media/brunner56/MyBook/modbuild_oracles/K2_manual_oracle` if a specific step needs
  re-verification.

### The former step-013 gap — RESOLVED 2026-08-21 (it was a misdiagnosis, not a real gap)

Step 013, "Robes with Shadows for TSL" (PapaZinos, DeadlyStream file **2075**), was originally
recorded as `blocked` on the belief that the archive was never obtained. **That was wrong on both
counts — the archive was present all along, and the payload is fully installed.**

**1. The archive was never missing.** `/run/media/brunner56/MyBook/kotor_mod_archives/Robes with
Shadows.7z` *is* DeadlyStream file 2075, as is its byte-identical twin
`Ultimate_Robes_Repair_For_TSL_v1.3.7z` (both md5 `725865d07acfa1c53cff963da3150e2e`, 2,652,648 B).
PapaZinos renamed the mod page but the attachment kept the mod's **old internal name**, so the
earlier session mistook it for an unrelated mod. Confirmed four ways: the archive's own
`ReadMe.txt` line 1 reads "Robes With Shadows For TSL v1.3"; the DeadlyStream page's uninstall
instructions tell you to remove `Ultimate_Robes_Repair_For_TSL` from your override; the page's
"Included Files" list matches the archive's 22 base-folder files exactly; and the page's subfolders
are precisely the ones the guide says to ignore. The genuine decoy is
`Robes_With_Shadows_JC_K1_v1.2.0.7z` (different md5) — a *derivative* that ports these shadows to
K1, correctly ruled out.
*(The knowledgebase had already documented this exact trap on 2026-08-05 —
`docs/knowledgebase/mod-download-playbook.md` names file 2075's attachment and advises md5-comparing
before assuming an archive is a different mod. Consulting it would have prevented the blocked verdict.)*

**2. The payload is installed anyway, via legitimate downstream supersession.** Verified by
extracting the archive and md5-comparing all 22 base-folder files against this build's own override
(`kotor_manual_workdir/K2_manual/steamassets/override`) — **0 of 22 are missing**; all 22 are present
but differ from PapaZinos' originals, because later guide steps legitimately overwrote them:
- **18 of 22** (`P_Kreia1hBB`, `P_KreiaBB`, `P_VisasBB`, `PFBIM`, `PFBMM`, `PFBNM`, `PMBIM`, `PMBMM`,
  `PMBNM` × `.mdl`/`.mdx`) are installed by **step 150, "Character Textures & Model Fixes"**
  (Redrob41 Upscale+) — confirmed 18/18 coverage directly from this ledger's own `file_names` for
  that step. Step 150 runs *after* step 013, so it would have overwritten PapaZinos' versions
  regardless. **The final on-disk state is identical whether or not step 013 ever ran.**
- **The other 4** (`P_HandmaidenBB`/`P_HandmaidenBD` × `.mdl`/`.mdx`) are correctly owned by
  **step 131, "Handmaiden - Fit and Athletic"**, and are deliberately *excluded* from step 150 by
  `mod-builds/scripts/cleanlist_k2.txt` line 15, which explicitly pairs these two mods. Verified none
  of the 4 appear in step 150's installed-file list — the cleanlist is working as designed.

**Ledger updated:** a correction entry for step 013 was appended to
`tmp/manual_work_k2_fresh/ledger.jsonl` (append-only, original entry preserved for audit; a
`.bak` of the pre-correction file sits alongside it) re-verdicting it `pass` with the evidence above.
Final per-step verdicts are now **150 pass / 11 skipped / 0 blocked**.

**This build has no outstanding action items.**

### Skipped steps (all correct per guide, not gaps)

| Step | Component | Why skipped |
|---|---|---|
| 001 | Zeroing Step | Guide's "delete and reinstall" instruction; superseded by the verified-vanilla-oracle seed used instead |
| 002 | KOTOR 2 on Steam (Workshop check) | This tree is an isolated Aspyr copy, not Steam-Workshop-integrated |
| 003 | 3C-FD Patcher | Windows `.exe` patcher; not applicable to the native-Linux Aspyr port |
| 006 | 4GB Patcher | Guide: only needed if not on the Aspyr patch; this tree is Aspyr |
| 050 | Stylized Portraits TSL | Optional alternative to the standard party portraits already installed |
| 153 | KOTOR 2 Remastered Cutscenes | Guide: mutually exclusive with step 152's Pops Maellard cutscenes (installed) |
| 154 | K2 Loading Screen Rescaled | Included in step 152; standalone only needed if Pops cutscenes are skipped |
| 157 | Crash After Character Creation | Engine-bug workaround note, not a mod |
| 158 | Character Stuck After Combat | Engine-bug workaround note, not a mod |
| 159 | Swoop Racing | Windows-compatibility-mode workaround, not applicable on Linux |
| 160 | Dialogue Skipping | Basegame/Aspyr memory-leak note; Windows mitigation (3C-FD) already N/A here |

**Nothing left to resume for K2-manual** beyond the single step-013 archive gap above. Use
`tmp/manual_work_k2_fresh/ledger.jsonl` as the source of truth for exactly what was installed, in
what order, with what options — it is far more detailed (per-step file lists, before/after Override
counts, verification hashes) than a hand-maintained markdown table could practically stay in sync
with, which is why the table below was abandoned in favor of it partway through.

This build is also the **ground-truth reference** for finishing the K2-auto effort
(`INSTALLATION_PROGRESS_K2_2026-07-30.md`) — its merged TOML has 71% zero-instruction components, so
any future hand-authoring pass on K2-auto should cross-check option/namespace/deletion choices
against this ledger rather than re-deriving them from scratch.

---

## Original session header (2026-07-30/31, methodology — still accurate, table below was never filled in)

**Target directory:** `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/Knights of the Old Republic II_manual/steamassets`
**Source of truth:** `docs/knowledgebase/kotor2-full-build-canonical-guide.md` (169 mods, exact order and steps, fetched from https://kotor.neocities.org/modding/mod_builds/k2/full)
**Purpose:** Independent, hand-executed install (direct file operations + direct `holopatcher` invocations, no ModSync automation) to compare against the automated K2 install, which was found to have 103/145 (71%) zero-instruction components in its merged TOML (see `INSTALLATION_PROGRESS_K2_2026-07-30.md`).
**Started:** 2026-07-31 (continuing directly from the K1 manual build methodology; see `MANUAL_INSTALL_PROGRESS_2026-07-30.md` for the full K1 log/playbook this mirrors)

Base state: pristine vanilla KOTOR 2. (This original session planned to rsync from the live Steam
install while at vanilla baseline; the run that actually completed the build instead seeded from
`modbuild_oracles/K2_manual_oracle`, a dedicated verified-vanilla reference copy — see the STATUS
UPDATE above for exactly what was used and why.)

**Platform note:** this target is the native-Linux Aspyr port (`../Knights of the Old Republic II/KOTOR2` is a real Linux ELF binary + `.so` libs, no Windows `.exe` anywhere in the tree). This means:
- **4GB Patcher** and **3C-FD Patcher** (both Windows `.exe` patchers) are **not applicable** — there is no Windows executable in this installation to patch, exactly analogous to the guide's own stated Mac Appstore exemption ("since the Mac Appstore version is not an executable, this program cannot be utilized"). Documented as N/A, not skipped-without-reason.
- **Water Restoration** and **Stutter Fix and Force Cage Update** (loose-file mods, not exe patches) — these fix Aspyr-patch-introduced regressions and the native Linux port *is* an Aspyr-patch build, so both are installed per the guide's "if you have the Aspyr patch, apply these fixes" instruction.
- Per the guide's own "Linux Players" section: batch-lowercasing of mod files may be needed at the end of the process due to case-sensitivity; the K1 build's established `tslpatchdata` lowercase-before-HoloPatcher-invocation fix was applied throughout wherever the same case-sensitivity issue recurred.

Where a mod already has an extracted/downloaded archive from the automated K2 run's `tmp/mod_downloads_k2/` (155+ files), that archive is reused (read-only source, always re-extracted fresh into `tmp/manual_work_k2/<mod>/` per the K1 build's "don't trust pre-extracted folders" lesson) rather than re-downloaded.

## Progress

*(This table was never filled in during the 2026-07-30/31 session — the build restarted later under
the JSONL ledger system described in the STATUS UPDATE above. See
`tmp/manual_work_k2_fresh/ledger.jsonl` for the complete, real step-by-step record: 161 steps, 150
passed / 11 correctly skipped / 1 blocked on a missing archive.)*

| # | Mod | Method | Status | Notes |
|---|---|---|---|---|
