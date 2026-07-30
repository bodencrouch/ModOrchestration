# KOTOR 1 Full Mod Build Installation Plan
**Date Started:** 2026-07-30  
**Target Directory:** `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/`  
**Status:** IN PROGRESS

## Overview
Installing the complete KOTOR 1 Full mod build (136 mods) using ModSync's automated CLI pipeline and manual supplementation where needed.

## Build Specification
**Source:** https://github.com/KOTOR-Community-Portal/mod-builds/blob/main/content/k1/full.md  
**Total Mods:** 136  
- Essential Mods: 6
- Recommended Mods: 49
- Suggested Mods: 56
- Optional Mods: 25

## Installation Architecture

### Tools Used
1. **ModSync.Core CLI** (`dotnet run --project src/ModSync.Core`)
   - Converts markdown → TOML
   - Validates instruction files
   - Performs best-effort installation with automatic downloads
   - Handles TSLPatcher and HoloPatcher integration

2. **cli_full_build_pipeline.sh** (`scripts/agents/cli_full_build_pipeline.sh`)
   - Merges markdown + TOML sources
   - Runs validation (dry-run and full)
   - Coordinates installation process

### Directories
- **Source Markdown:** `/run/media/brunner56/MyBook/Workspaces/ModSync/mod-builds/content/k1/full.md`
- **Converted TOML:** `/run/media/brunner56/MyBook/Workspaces/ModSync/tmp/KOTOR1_Full_from_markdown.toml`
- **Mod Downloads:** `/tmp/kotor_mods_downloads/`
- **Target KOTOR Install:** `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/`

## Installation Steps

### Phase 1: Preparation (COMPLETE)
- [x] Verify KOTOR installation directory exists and is valid
- [x] Clone mod-builds repository to local workspace
- [x] Convert markdown build file to TOML format
- [x] Create staging directories for mod downloads

### Phase 2: Pre-Installation Validation
- [ ] Run dry-run validation to detect conflicts
- [ ] Document any pre-existing mods in Override directory
- [ ] Verify disk space (estimated 25GB required)
- [ ] Check system permissions and paths

### Phase 3: Automated Download & Installation
- [ ] Set Nexus Mods API key (if needed for premium content)
- [ ] Run best-effort installation with ModSync CLI:
  ```bash
  scripts/agents/cli_full_build_pipeline.sh --game k1 \
    --game-dir /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/ \
    --source-dir /tmp/kotor_mods_downloads \
    --install --skip-validation --download-timeout-hours 72
  ```

### Phase 4: Manual Supplement (As Needed)
If automated downloads fail for specific sources:
- Deadly Stream mods (direct download or manual upload)
- MEGA links (time-sensitive, may require refresh)
- Game Front downloads (direct navigation)
- Alternative CDN sources

### Phase 5: Post-Installation Validation
- [ ] Verify all mod files installed to Override directory
- [ ] Check for missing dependencies
- [ ] Test KOTOR launch (if possible on headless system)
- [ ] Generate installation log

## Mod Download Sources
### Nexus Mods (Premium Content - May Require API Key)
- Ultimate Korriban/Kashyyyk/Tatooine/Dantooine/Endar Spire/Manaan/Taris/Character Overhaul/Unknown World
- High-Poly Grenades
- Taris Rapid Transit
- Fen's Jolee
- (~15 mods total)

### Deadly Stream (Free Community Hub)
- ~80+ mods (majority of build)
- Requires free account and navigation
- Direct download available

### MEGA (Cloud Storage)
- Character Startup Changes patch
- Ajunta Pall Appearance patch
- KOTOR Community Patch patch
- Ported VO Replacements (K1CP compatibility)
- Ultimate Korriban patch
- HD Sand People (.tga version)
- HD Canderous Ordo patch
- Juhani Appearance Overhaul patch
- Taris Reskin patch
- Calo Nord Recolor
- Unique Sith Governor

### GitHub
- mod-builds repository (already cloned)
- Potential direct-download releases

### Game Front (Legacy CDN)
- Hi-Res Ebon Hawk

## Critical Installation Notes

### File Deletions Before Install
Several mods require specific file deletions to avoid conflicts:
- Ultimate Taris: Delete LSI_win01.tpc and LSI_box01.tpc
- Ultimate Unknown World: Delete specific .tpc files
- Quanon's HK-47: Delete PO_phk47.tga
- HD Canderous Ordo: Download "new clothes" version
- Male NPC Clothing: Delete specific .tga files
- HD Astromech Droids: Delete po_pt3m33.tga

### Conditional Installs (Choosing More Features)
- Ultimate Character Overhaul: Use 2x option (more features)
- Juhani Appearance Overhaul: Use Body & Lightsaber version
- Korriban: Back in Black: Use K1CP-compatible install
- Sith Uniform Reformation: Use K1CP-compatible installation
- Reflective Lightsaber Blades: Use standard install option
- Manaan Rapid Transit: Choose preferred language version

### Patcher Requirements
- **TSLPatcher:** For script modifications and complex patches (~35 mods)
- **HoloPatcher:** For compatibility layer patches (~15 mods)
- **Loose-File:** Direct file copy to Override (~82 mods)

## Known Issues & Workarounds

### Potential Blockers
1. **Nexus Mods API Key:** Required for ~15-20 mods from Nexus Mods
   - **Status:** Not configured
   - **Workaround:** Can be set via `dotnet run --project src/ModSync.Core -- set-nexus-api-key`
   - **Alternative:** Manual download and placement

2. **Deadly Stream Access:** Some mods may require login or have access restrictions
   - **Workaround:** Direct navigation and manual download

3. **MEGA Link Expiration:** Some MEGA links may be temporary
   - **Workaround:** Alternative sources or re-upload

4. **System Limitations (Headless/Linux):**
   - No display available for some interactive installers
   - Mitigated by using CLI tools and TSLPatcher automation

### Compatibility Notes
- Some mods have known macOS/Linux crash issues (documented in build spec)
- Build targeting Windows Steam installation but applicable to Linux Steam Proton
- Path separator handling managed by ModSync CLI

## Success Criteria

### Installation Complete When:
1. All 136 mods (or configurable subset) downloaded and extracted
2. All mod files placed in `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/Override/`
3. All TSLPatcher and HoloPatcher operations completed
4. No conflicts or missing dependencies reported
5. KOTOR launcher able to start without critical errors
6. All installations logged and documented

### Validation Checks
```bash
# Check mod file count in Override
find /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/Override -type f | wc -l

# Verify no installation errors
# Check TSLPatcher/HoloPatcher logs
# Verify game config updated with any necessary patches
```

## Timeline & Progress Tracking

### Estimated Duration
- Preparation: ~15 minutes
- Download & Installation: 2-6 hours (depending on source speed and API availability)
- Validation & Troubleshooting: ~1 hour

### Phase Completion
| Phase | Status | Start Time | End Time | Notes |
|-------|--------|-----------|----------|-------|
| Preparation | COMPLETE | 10:43 AM | 10:44 AM | Directories created, TOML generated, mod-builds TOML downloaded |
| Validation (Dry-run) | COMPLETE | 10:45 AM | 10:48 AM | ✅ Dry-run validation passed (all 136+ mods parsed) |
| Download/Install | IN PROGRESS | 10:48 AM | - | Started best-effort installation, processing mods |
| Validation (Post-Install) | PENDING | - | - | Awaiting installation completion |

## Execution Command (When Ready)

```bash
cd /run/media/brunner56/MyBook/Workspaces/ModSync

# Dry-run first (recommended before actual install)
./scripts/agents/cli_full_build_pipeline.sh --game k1 \
  --game-dir /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/ \
  --source-dir /tmp/kotor_mods_downloads \
  --dry-run-only

# Then actual installation (if dry-run passes)
./scripts/agents/cli_full_build_pipeline.sh --game k1 \
  --game-dir /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/ \
  --source-dir /tmp/kotor_mods_downloads \
  --install --skip-validation --download-timeout-hours 72
```

## Contingency Plans

### If Nexus Mods API Not Available
1. Identify which mods are from Nexus Mods only
2. Attempt manual downloads from alternative sources
3. Document which Nexus mods couldn't be installed
4. Note in final report

### If Download Bandwidth Limited
1. Increase `--download-timeout-hours` parameter
2. Stagger downloads across multiple runs
3. Prioritize essential mods first

### If Installation Partial
1. Run partial dry-run to identify failed mods
2. Manually install critical mods
3. Continue with remaining installations
4. Document fallback procedure

## Reporting
Final report will document:
- Total mods successfully installed (count + list)
- Any skipped or failed mods (with reasons)
- Installation order executed
- Choices made (widescreen variants, optional content, etc.)
- Problems encountered and solutions applied
- Verification results
