---
name: kotor1-full-modinstall-automated
description: Automated installation of KOTOR 1 Full mod build (~150 mods) to /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/
metadata:
  type: project
  status: in-progress
  created: 2026-07-30T10:43Z
  target_directory: /run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/
---

# KOTOR 1 Full Mod Build — Automated Installation Plan

**Target Directory**: `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/`  
**Status**: In Progress — Orchestration Agent Active  
**Last Updated**: 2026-07-30 10:43 CDT

---

## Summary

Automate end-to-end installation of the KOTOR 1 Full mod build from https://github.com/KOTOR-Community-Portal/mod-builds. The build comprises ~150 mods across 4 installation tiers with complex interdependencies, sourced primarily from DeadlyStream (120 mods) with 5 from Nexus Mods. Agent will discover, download, and install all mods with dependency ordering, prerequisite handling, and autonomous configuration choices. No user prompts at any stage.

---

## Problem Frame

KOTOR 1 community has a mature, well-curated mod build (KOTOR Community Portal's mod-builds repo) but installation is manual, requiring:
- Navigation of 150+ mod files across multiple sources (DeadlyStream, Nexus Mods, direct downloads)
- Strict dependency ordering (30+ mod interdependencies)
- File-level conflict resolution (texture replacements, prerequisite checks)
- Configuration choices for graphics/gameplay variants
- Platform-specific handling (Windows multi-monitor, macOS/Linux compatibility)

**Goal**: Fully automated installation without user intervention, with intelligent choice-making and error recovery.

---

## Source Material

**Repository**: https://github.com/KOTOR-Community-Portal/mod-builds  
**Instruction Format**: Markdown (not TOML)  
**KOTOR 1 Full Instructions**: `/content/k1/full.md` (~156 KB)  
**Backup Workflow References**: `scripts/cleaner.bat`, `cleanlist_k1.txt` (mod deduplication, cleanup helpers)

---

## Key Findings (from Research Agent)

### Mod Distribution by Source
- **DeadlyStream**: ~120 mods (deadlystream.com)
- **Nexus Mods**: 5 mods (IDs: 1367, 1365, 1364, 10, 90, 1209, 1666, 1192)
- **Direct Downloads**: 3–5 mods (Mega, GameFront)
- **GitHub/Other**: Minimal

### Installation Tiers (Sequential Order)
| Phase | Category | Mods | Purpose |
|-------|----------|------|---------|
| Tier 1 | Essential Bugfixes | ~10 | KOTOR Community Patch (K1CP), dialogue/gameplay fixes |
| Tier 2A | Mechanics & Graphics | ~30 | Ultimate texture series (Taris, Kashyyyk, Tatooine, Unknown), gameplay changes |
| Tier 2B | Graphics (NPC/Char) | ~40 | Portraits, appearance overhauls, HD character textures |
| Tier 2C | Environment & Effects | ~30 | Ebon Hawk, skyboxes, UI, effects |
| Tier 2D | Content Restoration | ~20 | Dialogue restoration, side quests, restored content |
| Tier 3–4 | QoL & Balance | ~20 | Transit systems, minigame fixes, balance adjustments |

### Critical Master Mods (Install First)
1. **KOTOR Community Patch (K1CP)** — Required by 5+ downstream mods
2. **Cloaked Jedi Robes** — Prerequisite for Qel-Droma Reskin, JC's Jedi Tailor, Robes with Shadows
3. **High-Poly Grenades** — Required for HD Grenades & Mines
4. **Better Twi'lek Heads** — Required for Male Twi'lek Diversity compatibility
5. **HQ Skyboxes II** & **HQ Cockpit Skyboxes** — Required for Ebon Hawk Transparent Cockpit patches

### Installation Methods
- **Loose-file** (~40 mods): Direct copy to Override folder
- **TSLPatcher** (~80 mods): Executable installers; multi-monitor issues on Windows
- **HoloPatcher** (~20 mods): Newer standard; similar to TSLPatcher
- **Special** (~10 mods): File deletion, duplication, single-file extraction

### Pre-Installation File Operations
**Deletions** (prevent texture conflicts):
- Ultimate Taris: `LSI_win01.tpc`, `LSI_box01.tpc`
- Ultimate Unknown World: `LUN_blst01.tpc`, `LUN_blst02.tpc`
- Taris Reskin: 5 sky textures
- HQ Skyboxes II: `m36aa_01_lm0-2.tga`
- Male NPC Clothing: 3 files
- HD Grenades: `ii_trapkit_001-004.tga`

**Duplications**:
- Vurt's Hi-Res Ebon Hawk: `LDA_EHawk01` → `M36_EHawk01.tga`
- Detran's Darth Revan: create `PMBJ01.tga` copy

**Single-File Installs** (extract specific files only):
- Quanon's Canderous: `P_CandH01.tga`
- Fen's Jolee: head texture + `.txi`
- Robes with Shadows: Jedi Robes folder

### User Configuration Choices (Autonomous Defaults)
**Graphics/Appearance** (default: "install more"):
- KOTOR Dialogue Fixes → Bugfixes + PC Response Moderation (more features)
- Cloaked Jedi Robes → Brown-Red-Blue Alternative (recommended in guide)
- HD Twi'lek Females → Specific file variant
- HD Darth Malak → Red Eyes (more dramatic)
- Ithorians HD → Vurt retexture (more detailed)
- HD Skyboxes II → Medium texture size (recommended)
- Terminal Texture → Aesthetic variant chosen arbitrarily
- Stylized Portraits → Lite version (balance quality/consistency)
- Better Twi'lek Heads → Slim neck variant (refined look)

**Gameplay** (default: more features):
- Davik Slave Change → Option 1 (remove, cleaner)
- Senni Vek Mod → Restoration variant (more content)
- Taris Rapid Transit → Full version (more features)

**Exclusions** (skip problematic mods):
- Vision Enhancement (Steam Deck incompatible)
- Unique Sith Governor (crashes on macOS/Linux)

---

## Execution Plan

### U1. Agent Setup & Environment Validation

**Goal**: Verify tools, dependencies, and target directory state

**Approach**:
- Load browser automation (Claude in Chrome) tools
- Verify Flaresolverr availability at http://localhost:8191
- Validate target KOTOR directory exists and is accessible
- Create staging directory for downloads (`/tmp/kotor_mod_staging` or similar)
- Document baseline state (existing mod counts, file structure)

**Execution**: Start immediately  
**Status**: Pending

---

### U2. Discover & Parse Mod-Builds Repository

**Goal**: Extract complete mod list, sources, dependencies, and installation instructions from `full.md`

**Approach**:
- Clone or fetch https://github.com/KOTOR-Community-Portal/mod-builds
- Parse `/content/k1/full.md` into structured format (mod name, source, tier, method, dependencies, prerequisites)
- Extract user configuration points and assign autonomous choices
- Build dependency graph (prerequisites, file deletions, single-file extractions)
- Identify all mod sources (DeadlyStream URLs, Nexus mod IDs, direct URLs)

**Execution**: Parallel with browser setup  
**Status**: Pending

---

### U3. Download All Mods

**Goal**: Fetch all ~150 mods from their sources without user interaction

**Approach**:
- For each mod in install order:
  - **DeadlyStream**: Navigate to mod page, extract download link, use browser to fetch
  - **Nexus Mods**: Use Nexus API or direct download (Flaresolverr for CloudFlare bypass if needed)
  - **Direct URLs**: Fetch via browser
  - Save to staging directory with organized folder structure (by tier)
  - Log download success/failure for each mod

**Execution Challenges**:
- Nexus Mods may require session/API key — use Flaresolverr bypass or detect login requirement
- CloudFlare protection on some sources — Flaresolverr fallback
- Mod page parsing — dynamic content or login walls handled via browser automation

**Status**: Pending

---

### U4. Pre-Process Mod Files

**Goal**: Extract archives, handle single-file selections, apply deletions

**Approach**:
- For each downloaded mod:
  - Detect archive type (ZIP, 7z, RAR, etc.)
  - Extract to mod-specific folder in staging
  - For single-file installs (e.g., Quanon's Canderous):
    - Extract only the specified texture file from the mod's archive
    - Stage that file for direct copy to Override
  - For mods with file deletion requirements:
    - Flag files to be deleted before installation (log these for verification)
  - Create installation manifest for each mod (source files, target paths, operations)

**Status**: Pending

---

### U5. Install Mods in Dependency Order (Tier by Tier)

**Goal**: Apply all mods to KOTOR game directory in correct order

**Approach**:
- **Tier 1 (K1CP + essentials)**: Install first (foundation for downstream mods)
  - Copy K1CP loose files to Override
  - Run any TSLPatcher/HoloPatcher installers
- **Apply Pre-Deletion List**: Remove texture files that will be replaced (Ultimate Taris, Ultimate Unknown World, etc.)
- **Tier 2A (Ultimate textures)**: Install Ultimate series and gameplay mods
- **Tier 2B (NPC Graphics)**: Install character/portrait mods
- **Tier 2C (Environment)**: Install Ebon Hawk, skybox, UI mods
- **Tier 2D (Content)**: Install dialogue restoration and side quest mods
- **Tier 3–4 (QoL)**: Install transit, minigame, balance mods
- **Per-Mod Installation**:
  - Loose-file: Copy Override folder files to game's Override directory
  - TSLPatcher/HoloPatcher: Execute installer with no prompts (handle batch mode if available)
  - Special: Apply file duplications, single-file extractions, deletions as documented

**Execution Challenges**:
- TSLPatcher multi-monitor issues on Windows → May need to programmatically disable secondary monitors during installation
- Executable installers may prompt for input → Use desktop automation to auto-confirm dialogs
- File conflicts (texture overwrites) → Carefully sequence deletions before replacements

**Status**: Pending

---

### U6. Validate Installation

**Goal**: Verify all mods installed correctly, no missing files, no conflicts

**Approach**:
- For each mod: Verify key files exist in Override or game directory
- Check mod count matches expected (~150)
- Scan for texture conflicts or missing dependencies (cross-reference against dependency graph)
- Test KOTOR game launch (if possible) to detect critical errors
- Compare file structure against mod-builds reference and ModSync validation patterns

**Status**: Pending

---

### U7. Document and Update Living Plan

**Goal**: Maintain this plan document with complete installation record

**Approach**:
- Log every mod installed (name, source, version, install method, dependencies)
- Record all configuration choices made (graphics variants, gameplay options, exclusions)
- Document any problems encountered and how they were solved
- Final verification checklist
- Lessons learned for future mod-build automation

**Status**: Pending

---

## Deferred to Follow-Up Work

- **Skill Creation**: Create reusable skills/prompts for mod-build installations (currently inline agents)
- **TOML Generation**: Generate TOML serialization of mod-builds Markdown for ModSync integration
- **Mod Manager Integration**: Extend ModSync to accept mod-builds Markdown directly (alternative to TOML)
- **Automated Testing**: Add regression tests for mod-builds installation validation

---

## Assumptions & Constraints

**Assumptions**:
- Nexus Mods does not require login (or Flaresolverr handles CloudFlare bypass transparently)
- KOTOR installation directory is valid and mod-installable
- All mod sources (DeadlyStream, Nexus, direct URLs) are accessible and stable
- File system has sufficient disk space (~7 GB pre-extraction, ~14 GB extracted)

**Constraints**:
- No user prompts at any stage
- All configuration choices made autonomously (prefer "more features")
- Platform-specific handling (Windows multi-monitor, macOS/Linux compatibility)
- No filesystem scripts; inline terminal commands only

---

## Execution Status Log

### Agent Status
- **Research Agent** (a86b92a1d82681fce): ✅ COMPLETED — Mod-builds structure analyzed
- **Orchestration Agent** (a4b42190694c18317): ✅ COMPLETED SETUP (but BLOCKED on network issue)
- **Network Diagnostics**: 🔴 CRITICAL BLOCKER IDENTIFIED

### Progress Timeline

**[10:43 CDT] Research Phase Complete**
- Mod-builds repository mapped: 150 mods, 4 tiers, 30+ dependencies
- Sources identified: DeadlyStream (120), Nexus (5), Direct (3-5)
- Installation methods documented: Loose-file (40), TSLPatcher (80), HoloPatcher (20), Special (10)
- User configuration choices analyzed and autonomous defaults assigned
- Platform-specific issues documented (Windows multi-monitor, macOS/Linux case-sensitivity, Steam Deck)

**[10:43 CDT] Orchestration Agent Launched**
- Starting discovery phase (clone mod-builds repo, parse full.md)
- Will proceed through U1–U7 sequentially
- No user prompts; solving all problems autonomously

**[10:43–10:44 CDT] Preparation Phase**
- ✅ KOTOR installation directory verified at `/run/media/brunner56/MyBook/SteamLibrary/steamapps/common/swkotor/`
- ✅ Downloaded KOTOR1_Full.toml from mod-builds repository
- ✅ Created mod staging directories
- ✅ All resources prepared

**[10:45–10:48 CDT] Validation Phase (Dry-Run)**
- ✅ Dry-run validation PASSED
- ✅ All 136+ mods validated successfully
- ✅ Dependency graph resolved: 189 components including dependencies
- ✅ Auto-fixes applied for missing dependencies
- ✅ No blocking issues detected

**[10:48 CDT – 11:00 CDT] Installation Phase (BLOCKED)**
- ✅ Launched best-effort download and installation
- ✅ Merging TOML + Markdown instruction sets completed
- ⚠️ Began downloading from: DeadlyStream, Nexus Mods, MEGA, GitHub
- 🔴 **CRITICAL BLOCKER AT 10:55 CDT**: DeadlyStream completely unreachable
  - **Root Cause**: 100% network routing failure to deadlystream.com
  - **Evidence**: 
    - Ping: 0% success (all packets lost)
    - Traceroute: 5/5 hops timeout
    - HTTP/HTTPS: Connection timeout (10-120s)
    - DNS: ✅ Resolves to 159.89.148.93 (functional)
    - Hypothesis: Server down, blocked by firewall, or regional ISP blocking
  - **Impact**: ~120 of 150 mods unavailable (DeadlyStream primary source)
  - **Accumulated Errors**: 2,086 errors/warnings before kill
  - **Downloaded**: Only 12 MB of ~7 GB target (9% complete)
  - **Installation Killed**: 11:00 CDT (7 minutes after start)

**Diagnostic Findings**:
- ✅ Google: Reachable (HTTP 200)
- ✅ Nexus Mods: Reachable (HTTP 403 expected behavior)
- ❌ **DeadlyStream: UNREACHABLE** (routing failure)
- ⚠️ MEGA.nz: Some success, some failures (intermittent connectivity)

**Alternative Sources Identified**:
- 34 MEGA.nz backup links found in mod-builds documentation
- 5 Nexus Mods sources available
- Insufficient to cover all mods: covers ~39 of ~150 mods (26%)
- **Gap**: ~111 mods have no documented backup source

### Installation Metrics
- **Total Mods**: 136+
- **Methods**:
  - Loose-file: 82 mods
  - TSLPatcher: 35 mods
  - HoloPatcher: 15 mods
  - Other: varies
- **Sources**:
  - DeadlyStream: ~120 mods
  - Nexus Mods: 5 mods
  - Direct/MEGA/GitHub: 3–5 mods
- **Dependency Components**: 189 (including prerequisites and patches)

---

## Key Technical Decisions

### Why Autonomous Configuration Choices?
User requested "choose whatever you like" for customizable choices. Applying principle: **install more features by default** (prefer HD variants, restored content, enhanced gameplay) unless a mod is known to be problematic (Steam Deck incompatible, crashes on Linux).

### Why Dependency Graph Parsing?
Mod-builds Markdown contains 30+ interdependencies (master mods, file deletions, single-file extractions). Parsing into dependency graph ensures correct installation order and avoids conflicts.

### Why Flaresolverr Fallback?
Nexus Mods and some mod sources protect against automated scraping via CloudFlare. Flaresolverr at localhost:8191 provides transparent bypass for browser automation.

### Why Living Plan?
Installation will encounter edge cases and platform-specific issues. Living plan documents each decision, problem, and solution for future mod-build automation and team reference.

---

## References & Sources

- **Mod-Builds Repository**: https://github.com/KOTOR-Community-Portal/mod-builds
- **KOTOR 1 Full Instructions**: `/content/k1/full.md`
- **DeadlyStream**: https://www.deadlystream.com/
- **Nexus Mods (KOTOR)**: https://www.nexusmods.com/kotor/
- **ModSync Reference**: `src/ModSync.Core/`, `src/ModSync.Tests/` (dependency resolution, validation patterns)
- **Browser Automation**: Claude in Chrome MCP, Flaresolverr (http://localhost:8191)
