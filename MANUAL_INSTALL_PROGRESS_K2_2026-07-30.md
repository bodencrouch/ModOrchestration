# KOTOR 2 Full Mod Build — Manual (Hand-Installed) Comparison Build

**Target directory:** `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/Knights of the Old Republic II_manual/steamassets`
**Source of truth:** `docs/knowledgebase/kotor2-full-build-canonical-guide.md` (169 mods, exact order and steps, fetched from https://kotor.neocities.org/modding/mod_builds/k2/full)
**Purpose:** Independent, hand-executed install (direct file operations + direct `holopatcher` invocations, no ModSync automation) to compare against the automated K2 install, which was found to have 103/145 (71%) zero-instruction components in its merged TOML (see `INSTALLATION_PROGRESS_K2_2026-07-30.md`).
**Started:** 2026-07-31 (continuing directly from the K1 manual build methodology; see `MANUAL_INSTALL_PROGRESS_2026-07-30.md` for the full K1 log/playbook this mirrors)

Base state: pristine vanilla KOTOR 2 (rsync'd from the live `Knights of the Old Republic II/steamassets` install while still at vanilla baseline — verified `override/` has only the 60 baseline files, `chitin.key` present, before starting).

**Platform note:** this target is the native-Linux Aspyr port (`../Knights of the Old Republic II/KOTOR2` is a real Linux ELF binary + `.so` libs, no Windows `.exe` anywhere in the tree). This means:
- **4GB Patcher** and **3C-FD Patcher** (both Windows `.exe` patchers) are **not applicable** — there is no Windows executable in this installation to patch, exactly analogous to the guide's own stated Mac Appstore exemption ("since the Mac Appstore version is not an executable, this program cannot be utilized"). Documented as N/A, not skipped-without-reason.
- **Water Restoration** and **Stutter Fix and Force Cage Update** (loose-file mods, not exe patches) — these fix Aspyr-patch-introduced regressions and the native Linux port *is* an Aspyr-patch build, so both are installed per the guide's "if you have the Aspyr patch, apply these fixes" instruction.
- Per the guide's own "Linux Players" section: batch-lowercasing of mod files may be needed at the end of the process due to case-sensitivity; will do a final lowercase pass if issues surface, and will keep applying the established `tslpatchdata` lowercase-before-HoloPatcher-invocation fix from the K1 build throughout.

Where a mod already has an extracted/downloaded archive from the automated K2 run's `tmp/mod_downloads_k2/` (155+ files), that archive is reused (read-only source, always re-extracted fresh into `tmp/manual_work_k2/<mod>/` per the K1 build's "don't trust pre-extracted folders" lesson) rather than re-downloaded.

## Progress

| # | Mod | Method | Status | Notes |
|---|---|---|---|---|
