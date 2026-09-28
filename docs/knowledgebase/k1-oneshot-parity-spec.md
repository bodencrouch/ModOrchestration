# K1 one-shot parity spec

**Goal.** A single command, starting from a vanilla K1 tree and **`mod-builds/content/k1/full.md`
only** (never a pre-built TOML), must produce a build byte-equivalent to the hand-built
`K1_manual` reference:

```bash
dotnet exec src/ModSync.Core/bin/Debug/net9.0/ModSync.Core.dll install \
  --plaintext -v -i mod-builds/content/k1/full.md \
  -g /home/brunner56/modsync-hot/K1_auto \
  -s /run/media/brunner56/MyBook/kotor_mod_archives \
  --direct-markdown --skip-validation --no-checkpoint \
  -y --no-managed --non-interactive
```

**Parity-test rule (mandatory):** do **not** use `--best-effort` / `--continue-on-mod-failure`.
A failed mod contaminates the game tree. On any failure: stop, restore the game folder to the
state from *before* that mod (or reset the whole tree from the vanilla copy when there is no
usable snapshot), apply the code fix, then replay from that mod onward in guide order. Never
compare a tree that continued past a failure to `K1_manual`.

**Reference:** `/home/brunner56/modsync-hot/K1_manual` (Override 5808 files, `modules/` 344).
**Ledger (authoritative per-step record):** `/home/brunner56/modsync-hot/k1_ledger.jsonl` — records
for every guide step the archive used, the namespace chosen, patch counts, files installed and
deletions. This is the oracle for *what correct looks like*; it is not an input to the installer.

**Status (2026-08-22 convert feedback loop from `full.md` only):** rename/copy-as deserialization
fixed (Detran + Hi-Res Ebon Hawk emit `Rename`; Hi-Res Beam Effects resolves from folder;
Character Textures emits `CleanList`; Remove Duplicate emits `DelDuplicate`). Remaining convert
gaps: NPC Clothing paste+rename coalesce, HQ Blasters post-patcher renames/deletes, 4GB Patcher
unresolved (expected WidescreenOnly skip). Next: vanilla reset of `K1_auto` + one-shot `install`
from `full.md`, then stem/ERF diff vs `K1_manual`.

**Acceptance:** zero discrepancies. Concretely:
1. `Override` asset sets identical (compare by stem, treating `.tga`/`.tpc`/`.dds` as one asset).
2. `modules/` — same filenames **and** identical ERF resource sets inside every `.mod`.
3. No stale `.tpc` shadowing a `.tga`/`.dds` (crash risk), no non-game-content in `Override`.
4. Achieved in **one run**, with no manual post-steps.

---

## The actual requirement: `full.md` **is** the specification

ModSync's purpose is to deserialize `full.md` into instructions that install **exactly** what the
guide says. Every gap below is therefore a *deserialization bug*, not something to work around by
hand. If a guide sentence cannot currently be turned into an instruction, the parser must learn
that sentence form.

**Exhaustive survey of the instruction forms actually present in K1 `full.md`.** 82 of the 213
`###` sections carry a `:::note Installation Instructions` block (the other 131 are prose sections
or plain "move everything to Override" mods where the default applies). Across those 82:

| Instruction form | Mods | Example components |
|---|---|---|
| **choose an option / namespace** | 27 | Ported VO Replacements, PFHB02 Eye Fix, Better Twi'lek Heads |
| **delete file(s)** | 18 | Ultimate Taris, Ultimate Unknown World, HD Gizka |
| **conditional on another mod being installed** | 14 | Ultimate Taris, HD Darth Malak, JC's Jedi Tailor |
| **run the installer / patcher** | 11 | KOTOR Community Patch, Ported VO Replacements |
| **run the installer more than once** | 7 | Ported VO Replacements, Korriban: Back in Black, JC's Jedi Tailor |
| **move only a named subfolder** | 7 | Ajunta Pall Appearance, Yavin Station Hangar, Taris Reskin |
| **rename / copy-as** | 5 | Detran's Darth Revan, Male NPC Clothing, Hi-Res Ebon Hawk |
| **overwrite guidance (replace vs keep)** | 4 | Ajunta Pall Appearance, Ultimate Ebon Hawk Repairs |
| **multiple downloads for one entry** | 1 | Taris Reskin |
| **external script / cleanlist** | 1 | Character Textures & Model Fixes |

Reproduce this survey with the script in the session scratchpad, or re-derive it by extracting
`:::note\n Installation Instructions` blocks per `###` section. **The top three forms — option
selection (27), deletion (18), and cross-mod conditionals (14) — carry most of the risk**, and note
that option selection is nearly twice as common as anything else, so getting namespace choice right
matters more than any single other verb.

## Gap A — components never generated from `full.md`

`full.md` has 213 `###` headings; the run produced **183** components. Most of the difference is
non-mod prose sections (`Linux Players`, `Known Bugs`, `Zeroing Step`, …) which correctly produce
nothing. But two real mods are dropped:

| Missing component | Guide heading | Ledger step |
|---|---|---|
| **Hires Beam Effects** | `### Hires Beam Effects` | 096 (loose-file, 3 `fx_beam0*.tga`) |
| **Character Textures & Model Fixes** | `### Character Textures & Model Fixes` | 185 (Redrob41 Upscale+, 666 files after 115 cleanlist deletions) |

Both must become real components. Note the second is the largest single mod in the build.

## Gap B — instructions generated, but wrong or incomplete

**B1. Silent no-ops — the dominant defect.** ~30 of 183 components extract their archive and then
emit **no Move and no Patcher**, logging `succeeded`. Every patcher-driven mod must get a `Patcher`
instruction. Examples: `A Crashed Republic Cruiser on a Nameless World` (1209 patches missing),
`New Lightsaber Blade Models` (159), `Sith Uniform Reformation Revised` (182),
`Diversified Jedi Captives` (163), `Korriban: Back in Black` (190).

**B2. Partial install — Move emitted, Patcher omitted.** `Yavin Station Hangar` ran 52 moves and
**0 patches** where the reference ran 266. Distinct from B1 because the component *does* work, so a
"did it do anything?" check passes.

**B3. Multi-run patchers.** Several mods require the installer to run more than once with different
namespace options. The count and order matter:

| Component | Namespaces (in order) | Ledger step |
|---|---|---|
| A Crashed Republic Cruiser | 0 (Main), 1 (HQ Blasters), 2 (Colored Loadscreens) | 138 |
| K1 Ported Alien VO Replacements | 0 (Main), 1 (K1CP compat) | 008 |
| KOTOR 1 Twi'lek Male NPC Diversity | 0, 2 | 112 |
| Republic Soldier's New Shade | 3, 5 | 184 |
| Diversified Jedi Captives | 0, 1 | 158 |

The optional namespaces are conditional on other mods being present in the build (e.g. the Crashed
Cruiser's HQ Blasters and Colored Loadscreens integrations) — the guide prose states the condition.

**B4 / B5 are fully derivable from `full.md` — verified by reading the source.** An earlier draft of
this spec speculated they might not be; that was wrong. Every one of these steps is stated
explicitly in a `:::note Installation Instructions` block with quoted filenames. Verbatim examples:

> **Hi-Res Ebon Hawk** — "Once the mod is extracted, copy the file 'LDA_EHawk01' and make a
> duplicate of it. Rename this duplicate to 'M36_EHawk01.tga' and then move all files to the
> override."

> **Detran's Darth Revan** — "Make a copy of the file and rename it PMBJ01.tga, then move all files
> to override."

> **HQ Blasters** — "…navigate to the 'TSLPatchdata' folder, and delete the file 'keblastore.utm'.
> Run the installer—it will give you a single error, this is intended. After the install has
> completed, rename the files 'w_ionrfl_04.mdl' and 'w_ionrfl_04.mdx' to 'w_ionrfl_004.mdl' and
> 'w_ionrfl_004.mdx'. Delete the following files from your override directory:
> w_rptnblstr_004.mdl, w_rptnblstr_004.mdx, w_blstrpstl_006.mdl, w_blstrpstl_006.mdx and
> g1_w_rptnblstr01.uti"

> **Ultimate Taris** — "Make sure to delete LSI_win01.tpc and LSI_box01.tpc **before** moving to
> override."

So the generator needs three verbs it currently lacks, driven from this prose:
* **Copy-as / rename** — `copy X … rename … to Y`, `make a copy of the file and rename it Y`.
* **Delete before move** — the "**before** moving to override" ordering is explicit and matters.
* **Delete after install** — HQ Blasters deletes from the *override directory* only after the
  patcher has run, and also deletes a file from `tslpatchdata` *before* running it. Three distinct
  timings in one component.

**B5b. The conditional cleanlist (Character Textures & Model Fixes).** This one is indirect but
still derivable. The prose does not enumerate the deletions; it links them:

> "…go to [this page](…/scripts/cleaner.bat) … then go to [here](…/scripts/cleanlist_k1.txt) …
> The batch file will automatically delete the incompatible files based on which mods you select
> that you've used in your install … the first thing it will ask you is whether you've installed
> 'Mandatory Deletions'; rather than a mod, this is a required step … so just approve it."

The linked `cleanlist_k1.txt` is **already in this repository** at `mod-builds/scripts/cleanlist_k1.txt`
(30 lines, 115 files), so no download is needed. Semantics: line 1 is unconditional; each remaining
line is `<mod name>,<file>,<file>,…` and applies only if that mod is part of the build.

**Implement the condition by evidence, not by name.** Matching cleanlist mod names against
component names was tried and produces false negatives — it scored "HD War Droids by Dark Hope"
against the component "War Droid Mk 1 HD" at 0.67 and dropped it. The robust test is: **a listed
file already present in `Override` means some installed mod owns it, so Redrob41's copy must be
dropped from the payload; if nothing else provides it, keep his.** That is exactly the intent of the
`.bat`'s yes/no prompt, and it needs no name matching at all. Applied to a build, this produced 51
deletions where the reference build did 115 — correctly fewer, because that build contains fewer of
the conflicting mods.

**B6. Multi-file mod pages.** One guide entry can require several downloads:
`Korriban Sith Art` needs both the "Sith Art" and "Door Mural" Nexus files; only one was fetched.

**B7. Final dedup step.** Guide step 181 `Remove Duplicate TGA/TPC` has **no component at all**. It
must run last and move every `.tpc` that has a same-stem `.tga`/`.dds` out of `Override`
(`mod-builds/scripts/k1/tpc-deduper.sh`). `AGENTS.md` records that a stale `.tpc` shadowing a newer
`.tga` **crashes the game**, so this is correctness, not tidiness. Reference moved 246 files.

**B8. Non-content exclusion.** Blanket `Move <folder>/*` sweeps readmes, screenshots, `.DS_Store`,
`._*` resource forks and `TSLPatcher.exe` into `Override`. (A fix for this has landed; verify it holds
end-to-end.)

## Gap C — archive resolution

Largely fixed (174/186 → 185/186 resolved), but one confirmed survivor:

| Component | Wrongly resolves to | Correct archive |
|---|---|---|
| `K1 Ported Alien VO Replacements` | `Quarterstaff Replacements.rar` (matched on "Replacements") | `K1 PAVOR v1.3.2.7z` |

## Gap D — patcher execution on Linux

**D1. NSS compiler bug.** `AttributeError: 'str' object has no attribute 'info'` from the vendored
HoloPatcher 1.5.1. Affects several mods. Workaround proven repeatedly: substitute `#2DAMEMORY<n>#`
tokens with row indices read from **the target build's own** 2DAs (never copied between builds),
plus `#StrRef<n>#` from `append.tlk` matched against `dialog.tlk`, then compile with
`wine nwnnsscomp.exe`. Some mods omit `nwscript.nss` and need a game-matched copy supplied.
A real fix is a HoloPatcher built from `v1.80-patcher`, where `compile_nss()` forces `errorlog`
keyword-only.

**D2. `namespaces.ini` normalisation.** Linux HoloPatcher 1.5.1 dies on two malformed-but-common
shapes, each as a **blocking GUI modal** (so any automation must run under a headless display with a
timeout, or it hangs forever):
* a section omitting `IniName` → `KeyError CaseInsensitiveWrappedStr(IniName)`;
* a declared `InfoName` resolving to a `.rte` on disk → imports a Windows-only rte_editor →
  `module 'ctypes' has no attribute 'windll'`. Materialising an `.rtf` twin is **not** always enough;
  for `Sentinel Sneak Attack` and `Multifire` the namespace had to be flattened (drop
  `namespaces.ini`, delete the `.rte`).

## Known-benign differences (not blockers)

* **`modules/*.mod` byte hashes differ** while ERF resource sets are identical — packing/ordering
  inside the container. Compare resource sets, not hashes.
* **`dialog.tlk` size differs.** Re-running a patcher appends its TLK strings again, so any
  repair-by-re-running inflates the file with duplicate, unreferenced entries. A true one-shot run
  should not exhibit this — treat a `dialog.tlk` delta in a one-shot build as a real signal.
* **`desktop.ini` / `.DS_Store`** exist in the reference but are OS metadata the non-content
  quarantine deliberately removes. Acceptable to omit.
