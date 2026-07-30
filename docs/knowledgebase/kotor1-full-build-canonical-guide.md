# KOTOR 1 Full Mod Build — Canonical Install Guide (kotor.neocities.org)

**Source:** https://kotor.neocities.org/modding/mod_builds/k1/full (fetched 2026-07-30)

This is the authoritative, human-authored install guide the `mod-builds` GitHub repo's own
README explicitly defers to ("PLEASE FOLLOW THE INSTRUCTIONS ON THE WEBSITE"). It is more
precise than the automated `KOTOR1_Full.toml` for per-mod manual steps (specific file
deletions, folder/option selection, overwrite requirements) and should be treated as the
source of truth when it and the automated instruction file disagree. Any automated
--best-effort install should be audited against this doc, not the other way around.

## Pre-Installation Requirements

- Fresh game install (perform a "zeroing step": uninstall, delete all remaining files, reinstall)
- Game directory and subfolders must NOT be read-only
- Single KOTOR installation only
- Use WinRAR or 7zip (not Windows built-in extractor)
- Extract all archives before running installers
- Extract each installer mod to a separate folder

## Prerequisites

1. Create free accounts on both DeadlyStream and Nexus Mods (DeadlyStream rate-limits anonymous downloads; Nexus does not permit downloads at all without an account).
2. Select your language before installing — cannot be changed afterward without overwriting many downloaded mod files.

## Installation Rules (in order of importance)

1. Install mods in the **exact order presented** — reordering causes significant bugs.
2. Overwrite files when prompted — the order is tailored so overwrites resolve compatibility correctly.
3. Do **not** use Vortex Mod Manager or Steam Workshop with KOTOR mods — both have trouble functioning correctly.
4. Follow special installation instructions from both this guide and individual mod creators.
5. Track "master" mods — some mods depend on others; the master cannot be removed if you want the dependent mod.
6. Don't continue old saves unless using only texture files (no `.2da` filetypes).
7. Identify external/prerequisite mods beforehand — some have mid-list insertion points.
8. Monitor mod tiers for context on importance.
9. Check OS compatibility notes if not on Windows.
10. Avoid readme spoilers.

## Environment notes relevant to this (Linux) install

- **Linux users: batch-lowercase mod files before and after installation.** KOTOR's engine expects case-insensitive matching that Windows filesystems provide for free; Linux filesystems are case-sensitive, so mixed-case filenames from Windows-authored mods can silently fail to be found unless lowercased.
- **Mac/Linux: "Unique Sith Governor" (mod #63) causes crashes — consider skipping.**
- Multi-monitor: disable secondary monitors during TSLPatcher/HoloPatcher installs.
- AMD: update drivers; rollback if crashes persist.
- Steam Deck: "Vision Enhancement" (mod #118) is incompatible — skip it.

## Complete mod list, in required install order, with special instructions

1. **KOTOR Dialogue Fixes** (Tier 1) — Loose-File. Move chosen `dialog.tlk` to the main game directory (where the .exe is), **NOT** Override.
2. **Character Startup Changes + Patch** (Tier 2) — TSLPatcher + Loose-File Patch. Install the patch *after* the main mod to enable feat selection.
3. **Thematic KOTOR Companions** (Tier 2) — TSLPatcher. No master dependency.
4. **JC's Minor Fixes** (Tier 2) — Loose-File. Move files from "Straight Fixes," "Resolution Fixes," "Aesthetic Improvements," and "Things what bother me" folders to Override. **Skip:** `N_AdmrlSaulKar.mdl/.mdx`, `N_SithComF.mdl/.mdx`, `N_SithComM.mdl/.mdx`, all "MAN26" and "plc_kiosk" files, and the entire Bugfix folder.
5. **Ajunta Pall Appearance + Patch** (Tier 2) — TSLPatcher patch **first**, then Loose-File mod. Only use Transparent/Non-Transparent folders from the main file; optionally add the Sith eyes subfolder. Do not move files from the main mod's root.
6. **KOTOR Community Patch + Patch** (Tier 1) — HoloPatcher + Loose-File Patch. Run HoloPatcher installer, then move patch files to Override.
7. **Droid Claw Fix** (Tier 3) — TSLPatcher. Note: significant difficulty increase in some areas.
8. **K1 Ported Alien VO Replacements** (Tier 3) — HoloPatcher. Install main mod, then re-run patcher and select the K1CP compatibility option.
9. **Ultimate Korriban High Resolution + Patch** (Tier 2) — Loose-File, `.tpc` variant only. Ignore the Kexikus skyboxes requirement notice.
10. **Ultimate Kashyyyk High Resolution** (Tier 2) — Loose-File, `.tpc` variant only.
11. **Ultimate Tatooine High Resolution** (Tier 2) — Loose-File, `.tpc` variant only.
12. **Ultimate Dantooine High Resolution** (Tier 2) — Loose-File, `.tpc` variant only.
13. **Ultimate Endar Spire/Star Forge/Yavin Station** (Tier 2) — Loose-File, `.tpc` variant only (covers three areas).
14. **Ultimate Manaan High Resolution** (Tier 2) — Loose-File, `.tpc` variant only.
15. **Ultimate Taris High Resolution** (Tier 2) — Loose-File, `.tpc` variant only. **Delete `LSI_win01.tpc` and `LSI_box01.tpc` BEFORE moving to Override.** Visual bugs confirmed without Quanon's Taris Retexture (installed later, #92).
16. **Ultimate Character Overhaul** (Tier 2) — Loose-File, `.tpc` file, 2x version recommended. **Ignore all patches for now — install later** (see #81's relationship and any later patch entries).
17. **Ultimate Unknown World High Resolution** (Tier 2) — Loose-File, `.tpc` variant only. Delete `LUN_blst01.tpc` and `LUN_blst02.tpc` before moving to Override.
18. **Korriban Sith Art** (Tier 2) — Loose-File. Download and install both files.
19. **Deadeye Duncan on Manaan** (Tier 3) — Loose-File.
20. **Consistent Conditioning Icons** (Tier 4) — Loose-File.
21. **HD Pazaak Cards** (Tier 3) — Loose-File. Optional: move "green" folder files for K2-style specialty cards.
22. **HD PC Portraits** (Tier 3) — Loose-File.
23. **PMHA05 HD** (Tier 3) — Loose-File.
24. **PMHA02 HD** (Tier 3) — Loose-File.
25. **PMHA01 HD** (Tier 3) — Loose-File.
26. **PFHC05 HD** (Tier 2) — Loose-File.
27. **PFHB02 Dark Side Transition Eye Fix** (Tier 2) — Loose-File. Recommend the upscale option.
28. **High-Poly Grenades** (Tier 4) — Loose-File.
29. **HD Gizka** (Tier 4) — Loose-File. Move files from the Creatures folder only; skip readme/.jpg preview.
30. **Gammorean Reskin Pack** (Tier 2) — Loose-File.
31. **War Droid Mk 1 HD** (Tier 2) — Loose-File.
32. **AstromechHD** (Tier 3) — Loose-File.
33. **HD Realistic Jawas** (Tier 3) — Loose-File.
34. **HD Realistic Sand People + Patch** (Tier 3) — Loose-File, `.tga` filetype version (not `.tpc`).
35. **K1 Better Twi'lek Male Heads** (Tier 3) — HoloPatcher. Choose slim or original necks.
36. **HD Twi'lek Females** (Tier 2) — Loose-File. Only `hd_twilek_female.rar`; ignore other versions (older mod version, fewer head changes than current screenshots).
37. **Thigh-High Boots for Twi'lek** (Tier 2) — Loose-File. From the NPC Replacement folder, move the six files (not the optional folder) to Override.
38. **Shaleena/Lashowe Mouth Adjustment** (Tier 3) — Loose-File.
39. **Calo Nord Recolor** (Tier 3) — Loose-File.
40. **HD Darth Malak** (Tier 2) — Loose-File. Do NOT download the `.tga` file. If using CineMalak (#41, recommended), select the Malak (Red Eyes) or (Blue Eyes) folder textures; ignore `N_DarthMalak01.tga` unless skipping CineMalak.
41. **CineMalak - HD Malak Retexture** (Tier 2) — Loose-File. Move the loose `N_DarthMalak01.tga` directly to Override.
42. **Detran's Darth Revan** (Tier 2) — Loose-File. Copy the file, rename the duplicate to `PMBJ01.tga`, move all files to Override.
43. **Darth Bandon HD** (Tier 2) — Loose-File.
44. **HD Vrook** (Tier 2) — Loose-File.
45. **Random HD UI Elements** (Tier 3) — Loose-File. Download "random UI elements" only; skip the optional T3-M4 request.
46. **HD NPC Portraits** (Tier 3) — Loose-File. V2 option only; ignore V1 Looks.
47. **NPC Clothing M** (Tier 2) — Loose-File. Master: K1 Community Patch. Ignore `txi.rar`. Delete `n_commm07.tga` and `N_CommMD01.tga`. Delete `N_CommM08.tga`, duplicate `N_CommM0801`, rename the duplicate to `N_CommM08.tga`, move all files to Override.
48. **Juhani Appearance Overhaul + Patch** (Tier 2) — TSLPatcher + Patch. "Body & Lightsaber" version only — do NOT use head changes. Install the patch after to fix a lightsaber-disappearance bug. (Head model is replaced by #49.)
49. **Juhani Real Cathar Head** (Tier 2) — Loose-File.
50. **Korriban: Back in Black** (Tier 2) — TSLPatcher. Install the K1CP-compatible option. Optional: re-run patcher for alternate Master Uthar/Yuthura Ban outfits.
51. **Cloaked Jedi Robes** (Tier 2) — TSLPatcher. Use screenshots to choose robe style; "Brown-Red-Blue Alternative" strongly recommended.
52. **JC's Jedi Tailor** (Tier 4) — TSLPatcher. Non-English: NO. If using Cloaked Jedi Robes' 100% Brown option, install the 100% Brown compatibility patch after.
53. **Robes with Shadows for K1** (Tier 2) — Loose-File. Master: Cloaked Jedi Robes. Move files from the "Jedi Robes Override" folder only.
54. **Qel-Droma Robes Reskin** (Tier 2) — Loose-File. Master: JC's Cloaked Jedi Robes.
55. **Quanon's HK-47** (Tier 2) — Loose-File. Delete `PO_phk47.tga` before moving other files to Override.
56. **PLC_Sign** (Tier 3) — Loose-File.
57. **Kiosk HD + Patch** (Tier 3) — Loose-File. Use the "Kiosk HD 15.03.2024" version.
58. **PLC_Desk** (Tier 3) — Loose-File.
59. **LTS_EscapePod HD** (Tier 3) — Loose-File.
60. **HD Non-Game Weapons** (Tier 2) — Loose-File.
61. **K2 Swoops to K1** (Tier 3) — HoloPatcher.
62. **Stunbaton HD** (Tier 2) — Loose-File. "Stun baton HD" file only (skip "stunbaton 2025" unless preferring the non-vanilla icon).
63. **Unique Sith Governor** (Tier 3) — HoloPatcher. **WARNING: known crashes on macOS and possibly Linux — consider skipping on this platform.**
64. **Ithorians HD** (Tier 2) — Loose-File. Choose base or Vurt retexture.
65. **Duros HD** (Tier 2) — Loose-File.
66. **Quaren HD** (Tier 2) — Loose-File. Master: K1CP (not strictly JC's Dense Aliens as the mod page states).
67. **Davik HD** (Tier 2) — Loose-File.
68. **Doctors HD** (Tier 2) — Loose-File.
69. **Kebla Yurt HD** (Tier 2) — Loose-File. Delete `N_CommF02.tga` & `.txi` to preserve only face improvements (not clothing).
70. **Deadeye Duncan HD** (Tier 2) — Loose-File.
71. **N_oldAMH01 HD** (Tier 2) — Loose-File.
72. **HD Astromech Droids** (Tier 2) — Loose-File. Delete `po_pt3m33.tga` before moving files to Override.
73. **Protocol Droids HD** (Tier 2) — Loose-File.
74. **Davik's Trophies** (Tier 3) — Loose-File.
75. **HD Carth Onasi** (Tier 3) — Loose-File. "Carth Onasi (new clothes).rar" file (skip head/face changes). Delete `PO_pcarth3.tga` before moving other files to Override.
76. **HD Canderous Ordo + Patch** (Tier 2) — Loose-File & Patch. 'new clothes' version only (not head/face) — remember the patch; head texture comes from #77.
77. **Quanon's Canderous Ordo** (Tier 2) — Loose-File. Move ONLY `P_CandH01.tga` to Override (head only, not body).
78. **Jolee Bindo HD** (Tier 2) — Loose-File.
79. **Fen's Jolee** (Tier 2) — Loose-File. Default version only (not iconic recolor). Move ONLY `P_joleeh01.tga` and `P_joleeh01.txi` to Override.
80. **Zaalbar HD** (Tier 2) — Loose-File. Standard version recommended (avoid "Vurt's KotOR Visual Resurgence"). Delete `po_pzaalbar3.tga` before moving to Override.
81. **Sith Uniform Reformation Revised** (Tier 2) — TSLPatcher. Select the K1CP-compatible install option.
82. **Stylized Portraits by Tinman888** (Tier 4) — Loose-File. Use the Lite version recommended. Do NOT install the PC folder unless wanting a Revan portrait override.
83. **Star Map Revamp** (Tier 3) — Loose-File.
84. **Background Ship Improvements** (Tier 3) — Loose-File. `hd_kt_400_military_droid_carrier_and_lethisk_class_armed_freighter.rar`.
85. **Kebla Yurt Renovation** (Tier 3) — HoloPatcher.
86. **Vurt's K1 Hi-Res Ebon Hawk Retexture** (Tier 2) — Loose-File. Copy `LDA_EHawk01`, rename the duplicate to `M36_EHawk01.tga`, move all files to Override.
87. **Ultimate Ebon Hawk Repairs** (Tier 2) — Loose-File. Move "to override" files, then the "Animated Monitors" folder files (overwrite when prompted).
88. **High Quality Cockpit Skyboxes** (Tier 2) — Loose-File. Select resolution based on performance; Medium recommended. Very large sizes risk save corruption.
89. **Yavin Station Hangar** (Tier 4) — TSLPatcher & Loose-File with situational patches. Optional: re-run installer for a visible forcefield. If using HQ Cockpit Skyboxes: move matching-resolution folder files to Override, delete `ebo_yab/yaf/yal/yar/yat.tga`. If using Vurt's Ebon Hawk: download and apply the provided patch.
90. **Ebon Hawk Cockpit Upgrade (LEH_Scre01)** (Tier 3) — Loose-File.
91. **Ebon Hawk Cockpit Upgrade (LEH_Scre02)** (Tier 3) — Loose-File. Recommend the version without overlays.
92. **Taris Reskin + Patch** (Tier 2) — Loose-File. Install ONLY Part 1 and Part 2 (skip Dantooine Estates and Sith Base modifications). Delete `LTS_Bsky01.tga`, `LTS_Bsky02.tga`, `LTS_sky0001.tga` through `LTS_SKY0005.tga` from Part 1 before moving to Override.
93. **High Quality Starfields and Nebulas** (Tier 3) — Loose-File.
94. **High Quality Skyboxes II + Patch** (Tier 2) — Loose-File. `HQSkyboxesII_K1.7z` only. Delete `m36aa_01_lm0` through `m36aa_01_lm2.tga` before moving to Override, then apply the patch.
95. **Ebon Hawk Transparent Cockpit Windows for K1** (Tier 3) — Loose-File. Apply the main install, then relevant compatibility patches: K1CP Leviathan forcefield, HQ Skyboxes compat, Yavin Station Hangar (if using those mods).
96. **Hi-Res Beam Effects** (Tier 2) — Loose-File.
97. **HD Fire and Ice** (Tier 2) — Loose-File.
98. **Animated Energy Shields** (Tier 2) — Loose-File.
99. **Animated Cantina Sign** (Tier 3) — Loose-File.
100. **Revamped FX** (Tier 3) — Loose-File. Alternative to HD Fire/Ice & Hi-Res Beam Effects (partial overlap) — can install with overwrite to keep non-overlapping additions. Recommend against included optional files.
101. **Terminal Texture** (Tier 2) — Loose-File. Choose from 3 versions per preference.
102. **RepTab HD** (Tier 3) — Loose-File.
103. **Animated Swoop Monitors** (Tier 3) — Loose-File.
104. **Loadscreens in Color** (Tier 3) — Loose-File.
105. **New Lightsaber Blade Models** (Tier 1) — TSLPatcher. Use the standard install option only (others untested).
106. **Darth Malak's Lightsaber** (Tier 1) — HoloPatcher.
107. **Blaster Visual Effects** (Tier 3) — Loose-File. Move override folder files; optionally move yellow/green disruptor files from the optional folder after.
108. **Wookiee Warblade Fix** (Tier 3) — Loose-File.
109. **Kill the Czerka Jerk on Kashyyyk** (Tier 3) — TSLPatcher. Non-English: NO.
110. **Korriban Academy Workbench** (Tier 3) — Loose-File.
111. **Senni Vek Mod** (Tier 3) — HoloPatcher. Choose "Senni Vek's Ambush" (recommended) or "Senni Vek Restoration".
112. **KOTOR 1 Twi'lek Male NPC Diversity** (Tier 3) — HoloPatcher. Optionally move upscaled textures. If using Better Twi'lek Males' original-necks option, move "Optional - Original Necks" folder files. If using Senni Vek Mod, re-run installer and select the compatibility patch.
113. **Ixgil the Bith** (Tier 4) — TSLPatcher.
114. **Hidden Bek Control Room Restoration** (Tier 4) — Loose-File.
115. **Swoop Bike Upgrades** (Tier 4) — TSLPatcher.
116. **Jedi Choice Dialogue Enhancement** (Tier 3) — Loose-File. Non-English: NO. Move ONLY `dan13_dorak.dlg`.
117. **Juhani Dialogue Restoration** (Tier 2) — Loose-File.
118. **Vision Enhancement** (Tier 3) — TSLPatcher. **WARNING: incompatible with Steam Deck (crashes).**
119. **Leviathan Differentiated Dialogue** (Tier 3) — Loose-File. Non-English: NO.
120. **Balanced Pazaak** (Tier 3) — Loose-File.
121. **Ebon Hawk Camera Replacement** (Tier 1) — Loose-File.
122. **Rebalanced Grenades** (Tier 2) — HoloPatcher.
123. **Grenades and Mines HD** (Tier 3) — Loose-File. Master: High-Poly Grenades. Delete `ii_trapkit_001.tga` through `ii_trapkit_004.tga` before installing.
124. **Random Turret Minigame Remover** (Tier 3) — Loose-File.
125. **Trask Without Tutorials** (Tier 2) — TSLPatcher.
126. **All Hands on Deck for the Leviathan Prison Break** (Tier 2) — TSLPatcher. Included optional file is compatible; use or skip as preferred.
127. **Ain't No Air in Space** (Tier 4) — TSLPatcher.
128. **Party Conversations on the Ebon Hawk** (Tier 1) — TSLPatcher. Use the K1CP-compatible install option.
129. **Dark Sacrifice** (Tier 1) — TSLPatcher. Restores a cut Dark Side romance ending with Carth; optional in-playthrough choice.
130. **Saber Throw Knockdown Effect** (Tier 2) — TSLPatcher.
131. **Sunry Murder Recording Enhancement** (Tier 2) — TSLPatcher. Non-English: NO.
132. **PC Dialogue with Davik's Slaves Change** (Tier 2) — TSLPatcher. Option 2 recommended (retains massage, adds DS points; also adds DS points for threatening).
133. **Taris Rapid Transit** (Tier 3) — TSLPatcher. Non-English: NO. Full or Light version per preference.
134. **Manaan Fast Travel System** (Tier 3) — HoloPatcher. Match language version if non-English.
135. **Recruit T3-M4 Early** (Tier 2) — Loose-File. Non-English: NO.
136. **Security Spikes for K1** (Tier 2) — TSLPatcher.

## Installation statistics (per the guide)

- Total pre-extracted filesize: ~7GB
- Total extracted (excluding upscaled movies): ~14GB
- Total with HD movies (1920x1080): ~25GB
- Tier distribution: 8 Essential, 62 Recommended, 51 Suggested, 15 Optional
- Method distribution: ~68 Loose-File, ~48 TSLPatcher, ~20 HoloPatcher

## How this differs from / should correct the automated pipeline

The merged `KOTOR1_Full.toml`/Markdown instruction set (189 resolved components after
dependency expansion, vs. 136 top-level mods here) drives ModSync's `--best-effort`
automated install. That automation does not necessarily encode every per-file deletion,
folder-only selection, or "install later" ordering note above. When auditing or fixing a
stuck/failed component, **check its entry in this guide first** — a mismatch here (wrong
file variant downloaded, a deletion step not applied, wrong install-order position) is a
more likely root cause than a bug in ModSync itself.
