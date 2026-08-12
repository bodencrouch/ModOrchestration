# Full build install validation

## When to use

Use this skill when the task explicitly involves:

- `mod-builds`
- `KOTOR1_Full.toml`
- `KOTOR2_Full.toml`
- full import / full-build validation
- select-all mod install testing
- proving that the dry-run / VFS step makes sense before a real install

## Read first

- **`docs/knowledgebase/modbuild-paranoia-doctrine.md` — MANDATORY, READ BEFORE TOUCHING A GAME DIRECTORY.**
  The modbuild is **not idempotent**. A doubt is a defect. Never continue past an
  unresolved issue; never append a missed step out of order (restore-and-replay instead);
  never run two writers against one game dir; snapshot before every step; verify from the
  filesystem, never from intent. If provenance of the base is uncertain, **start over**.
- `docs/local_desktop_agent_runbook.md`
- `.cursor/skills/local_desktop_gui_testing/SKILL.md`

## Non-negotiable invariants (summary — full text in the doctrine)

1. Base must be **proven factory-fresh** (fresh Steam install; record a step-0 `BASELINE`).
2. **Stop on any error or doubt.** Roll back → diagnose to root cause → fix → verify → resume.
   Continuing while an earlier step is `blocked`/`failed`/`unverified` is forbidden, even if
   you believe later steps don't depend on it — that is a guess about a non-idempotent system.
3. **One writer per game directory.** Prove no other writer exists via process check, not mtime.
4. **Snapshot before every step** (`rsync -a --link-dest`, ~1s); full-tree when the step touches
   `modules/`, `dialog.tlk`, `lips/`, `streamvoice/`, `movies/`.
5. **Verify from the filesystem**: patcher exit code AND zero `[Error]` lines AND expected file
   delta AND guide-specified deletions absent via `find -iname` (globs are case-sensitive; the
   engine is not). Note some patchers exit 0 even on failure.
6. **No silent skips.** Every step gets a ledger record; a missing step number is a defect.
7. **Follow the guide literally**, including trailing Installation/Download notes; quote the
   instruction verbatim in the ledger.
8. **Verify mod identity by content, not filename** (K1 `LDA_/LKO_/LTS_` vs K2 `DAN_/DXN_/OND_`).

## Required repo state

- repo root contains `./mod-builds`
- local test directories exist or can be created
- the GUI is launched with preload args

## Preparation

1. Clone `mod-builds` to repo root if needed:
   - `git clone https://github.com/th3w1zard1/mod-builds ./mod-builds`
2. Create disposable directories:
   - `./scripts/agents/create_template_kotor_install.sh ./tmp/kotor_template ./tmp/mod_downloads`
3. On Linux, ensure the HoloPatcher resource path exists:
   - `./scripts/agents/ensure_linux_holopatcher.sh`
4. Launch the GUI:
   - `./scripts/agents/launch_gui_desktop.sh --instruction-file ./mod-builds/TOMLs/KOTOR1_Full.toml --kotor-dir ./tmp/kotor_template --mod-dir ./tmp/mod_downloads`

## Full-build GUI workflow

1. Let the wizard auto-load the TOML.
2. Go to `ModSelectionPage`.
3. Click `SelectAllButton`.
4. Confirm the selected count is the expected full-build count.
5. Advance to the downloads step.
6. Click `Fetch Downloads`.
7. Wait until download progress stabilizes.
8. Advance to `ValidatePage`.
9. Click `Run Validation`.
10. Expand `LogExpander`.
11. Review:
   - environment result
   - install-order result
   - dry-run / VFS issues
12. Only continue to install if the current task expects a real install and validation is acceptable.

## What to record

- selected mod count
- whether downloads were already present or newly fetched
- validation pass / fail summary
- whether `Next` is blocked by critical errors
- install completion or the exact blocking dialog

## Project-specific behavior that matters

- `mod-builds` provides metadata and instructions; archives are fetched from third-party sources.
- The wizard download step is not just documentation; it really kicks off background downloads.
- The validation page is the main user-facing entrypoint for the dry-run / VFS check.
- `MainConfig` path placeholders still matter even in GUI-driven full-build flows:
  - `<<modDirectory>>`
  - `<<kotorDirectory>>`

## Interpreting validation results

### Good sign

- environment passes
- install order passes
- dry-run issues are empty or limited to acceptable warnings for the task

### Bad sign

- HoloPatcher missing from `Resources`
- blocking extraction pattern failures for selected mods
- critical errors that stop the wizard from advancing

## Preferred local baseline

Use a Linux desktop VM or workstation with:

- .NET 9.x
- X11 / desktop display
- repo-local `mod-builds`
- helper scripts from `scripts/agents/`

This is the closest local reproduction of the validated VM-style workflow already exercised for this repo.

## Update rule

Whenever a better full-build workflow is discovered, update:

- this skill
- `docs/local_desktop_agent_runbook.md`
- `AGENTS.md`
- `.cursorrules`
