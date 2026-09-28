---
date: 2026-07-29
type: feat
status: active
origin: docs/brainstorms/2026-07-25-trust-first-install-experience-requirements.md
---

# Trust-first install experience

## Summary

Make long guided installs feel safe to start and safe to continue: a pre-install readiness summary with plain-language blockers, always-visible install progress, and resume/retry that survives closing and reopening the wizard — without ever implying a one-click full-folder rollback.

The implementation already exists, uncommitted, on the current branch and builds clean with its own tests passing (9/9 across 4 new test files). This plan is a verify-and-finish pass, not a build-from-scratch pass: confirm the existing work against R1-R9, close two identified test-coverage gaps, and get it committed and shipped.

---

## Problem Frame

KOTOR full builds are long, order-sensitive, and opaque. ModSync already validates, checkpoints, and can resume components under the hood, but players experience failure as "start, fail, guess, restart" because the wizard never surfaced readiness, health, or recovery clearly. (see origin: docs/brainstorms/2026-07-25-trust-first-install-experience-requirements.md)

Prior to this branch's uncommitted work, the installing page could not be re-entered after marking a run complete, and the deleted `InstallationErrorDialog` offered a rollback choice implying full pristine-folder restoration the product doesn't deliver.

---

## Requirements

**Install-start readiness**

- R1. Readiness summary before install start, reusing existing validation detection.
- R2. Plain-language remediation; player cannot advance while blockers remain.
- R3. Compact pass/fail surface, pointing back to full validation detail rather than duplicating it.

**Progress**

- R4. Active component, overall progress, and health state always visible during install.
- R5. Copy communicates interruption is recoverable; never implies "start over from zero."

**Recovery**

- R6. Resume/retry from the last completed component, offered in the same session.
- R7. Resume/retry stays available after closing and reopening the wizard against the same instruction file and directories.
- R8. Completed-vs-remaining shown on failure; when resume is unavailable, UI says so and never implies one-click pristine-folder restoration.

**Non-regression**

- R9. Headless CLI resume-by-session behavior must not regress.

---

## Current Implementation State (verified this session)

The uncommitted code on this branch was audited directly (build + targeted test runs, not just diff review). All nine requirements have corresponding implementation:

| Requirement | Status | Evidence |
|---|---|---|
| R1 | Done | `ValidationPipelineOptions.InstallStartReadiness` preset (environment-only); `InstallStartPage.RunReadinessAsync` runs it via the existing `InstallationValidationPipeline` |
| R2 | Done | `InstallStartPage.ValidateAsync` blocks `Next` on `_readinessHasCriticalErrors`, surfacing `_readinessErrorMessage` |
| R3 | Done | `_readinessStatusText`/`_readinessDetailText` show only the Environment stage summary, capped to 3 messages |
| R4 | Done | `InstallingPage.ProgressCallback` updates active-mod text, progress bar, count, and `_runStateText` on every callback |
| R5 | Done | `ApplyTerminalOutcomeAsync` copy: "Resume continues remaining mods; it does not restore a pristine game folder." |
| R6 | Done | `InstallingPage.ResumeRetryButton_Click` re-invokes install; `InstallationService` already skips Completed/Skipped/Blocked components |
| R7 | Done | `CheckpointManager.ProbeResumableSessionAsync` reads `install_session.json` from disk on every `InstallStartPage.OnNavigatedToAsync`, independent of in-memory session state |
| R8 | Done | `ApplyTerminalOutcomeAsync` reports completed/remaining counts; states "resume may be unavailable" when applicable; rollback-implying `InstallationErrorDialog` was deleted |
| R9 | Done | CLI resume-by-session logic in `InstallAllSelectedComponentsCoreAsync` is untouched; `--no-checkpoint` now wired to skip only Git checkpoint commits, confirmed by `NoCheckpointInstallTests` |

Build: `dotnet build ModSync.sln` succeeds, 0 errors. New tests: `InstallStartReadinessOptionsTests` (2/2), `NoCheckpointInstallTests` (2/2), `WizardResumeReentryTests` (3/3), `InstallStartPageHeadlessTests` (2/2) — all pass individually (a combined OR filter under-matches due to a vstest filter-parsing quirk, not a real failure).

The deleted `InstallationErrorDialog`'s `InstallationErrorEventArgs`/`ErrorAction` types are **not** dead code — they remain alive via `InstallationCoordinatorService`, consumed by the separate, pre-existing `CheckpointManagementDialog` (the Git checkpoint browser the brainstorm explicitly keeps distinct and out of scope). No removal needed.

---

## Key Technical Decisions

- **Verify-and-finish framing.** Treat the existing uncommitted code as the implementation baseline. Do not rewrite working, tested logic; this plan's units close specific verified gaps and get the result shipped.
- **Resume identity stays destination-path + GUID-overlap.** R7/AE3 say "same instruction file and directories," but the implemented check only matches on destination path and overlapping component GUIDs, not an instruction-file fingerprint. Confirmed with the user: keep the current proxy. A different build sharing components at the same destination triggering a false resume offer is judged a rare edge case not worth the added persistence complexity right now. (see origin: Outstanding Questions)
- **Checkpoint browser stays untouched.** `CheckpointManagementDialog`/`InstallationCoordinatorService` are pre-existing, distinct recovery tooling per the origin document's "three recovery concepts stay distinct" decision. This plan does not touch them.

---

## Implementation Units

### U1. Commit the existing readiness + resume implementation

**Goal:** Get the already-implemented, already-passing trust-first install code (currently entirely uncommitted) into logical, reviewable commits.

**Requirements:** R1-R9

**Dependencies:** None

**Files:**
- `src/ModSync.Core/Installation/InstallCoordinator.cs`
- `src/ModSync.Core/Installation/ResumableSessionInfo.cs` (new)
- `src/ModSync.Core/Services/Checkpoints/CheckpointPaths.cs`
- `src/ModSync.Core/Services/InstallationService.cs`
- `src/ModSync.Core/Services/Validation/InstallationValidationPipeline.cs`
- `src/ModSync.Core/Services/Validation/ValidationPipelineOptions.cs`
- `src/ModSync.Core/CLI/ModBuildConverter.cs`
- `src/ModSync.GUI/Dialogs/WizardPages/InstallStartPage.axaml`, `.axaml.cs`
- `src/ModSync.GUI/Dialogs/WizardPages/InstallingPage.axaml`, `.axaml.cs`
- `src/ModSync.GUI/Dialogs/WizardPages/FinishedPage.axaml`
- `src/ModSync.GUI/Dialogs/WizardPages/BaseInstallCompletePage.axaml`
- `src/ModSync.GUI/Dialogs/InstallationErrorDialog.axaml`, `.axaml.cs` (deletion)
- `src/ModSync.Tests/InstallStartReadinessOptionsTests.cs` (new)
- `src/ModSync.Tests/NoCheckpointInstallTests.cs` (new)
- `src/ModSync.Tests/WizardResumeReentryTests.cs` (new)
- `src/ModSync.Tests/HeadlessUITests/InstallStartPageHeadlessTests.cs` (new)
- `src/ModSync.Tests/HeadlessUITests/FomodGateHeadlessTests.cs`, `InstallingPageHeadlessTests.cs`

**Approach:** No new code. Re-verify build and the four new test files pass (already confirmed this session), then stage and commit in logical groups — e.g., Core services (readiness preset, checkpoint probe, `--no-checkpoint` wiring), GUI wizard pages (readiness UI, resume/retry UI, dialog deletion), and their tests together per group.

**Test scenarios:** Test expectation: none — this unit verifies and commits existing, already-tested code; no new behavior is introduced.

**Verification:** `dotnet build ModSync.sln` succeeds; the four new test files each pass in isolation; `git status` shows a clean tree for these files afterward.

---

### U2. Close InstallStartPage resume/start-over headless coverage gap

**Goal:** Add headless UI coverage for the resume-offer interaction paths that exist in production code but aren't exercised by any test today.

**Requirements:** R7 (AE3)

**Dependencies:** U1

**Files:**
- `src/ModSync.Tests/HeadlessUITests/InstallStartPageHeadlessTests.cs`

**Approach:** Extend the existing headless test fixture (same `[AvaloniaFact]` pattern already in the file) to drive `ResumePreviousButton_Click`, `StartOverButton_Click`, and `ApplyStartOverAsync`, plus the "resume offered, no choice made yet" blocking state.

**Patterns to follow:** `InstallStartPage_Readiness_BlocksWhenDirectoriesUnset` and `InstallStartPage_Readiness_AllowsWhenClean` already in the file — same harness setup, same assertion style.

**Test scenarios:**
- Happy path: a resumable session exists for the destination; the resume offer renders and `Next` is blocked until `ResumeChoice` is set.
- Edge case: clicking **Resume previous** sets the resume choice, unblocks `Next`, and does not clear the on-disk session.
- Edge case: clicking **Start over** calls `ApplyStartOverAsync`, clears resumability, and unblocks `Next`.
- Error path: FOMOD-gate failure during readiness blocks `Next` with the gate's own remediation message (not the generic readiness message).

**Verification:** New tests pass; existing `InstallStartPageHeadlessTests` tests continue to pass unchanged.

---

### U3. Verify InstallingPage resume/retry headless coverage

**Goal:** Confirm the already-modified `InstallingPageHeadlessTests.cs` actually exercises the `ResumeRetryButton_Click` → re-run → success path and the `RunStateText`/`FailureSummaryText` content contracts; add what's missing.

**Requirements:** R5, R6, R8 (AE2)

**Dependencies:** U1

**Files:**
- `src/ModSync.Tests/HeadlessUITests/InstallingPageHeadlessTests.cs`

**Approach:** Read the current file in full first — this unit may turn out to be verification-only if coverage is already adequate (per Phase 2 execution loop: if the work already matches intent, confirm and move on rather than reimplementing). If gaps exist, add scenarios following the existing test patterns in the file.

**Test scenarios:**
- Happy path: a failed run with remaining components; clicking Resume/Retry re-invokes install and completed components are skipped.
- Edge case: `RunStateDisplayText` reflects healthy/failed/cancelled states accurately across a full run.
- Integration: `ApplyTerminalOutcomeAsync`'s completed/remaining counts match the actual component states after a partial failure.

**Verification:** All scenarios above are covered and pass; no regressions in the file's existing tests.

---

### U4. Confirm remaining knowledgebase docs are in sync

**Goal:** `docs/knowledgebase/install-lifecycle.md` was confirmed accurate this session; confirm the other three touched docs are equally in sync with the shipped behavior.

**Requirements:** Documentation accuracy supporting R1-R9

**Dependencies:** U1

**Files:**
- `docs/knowledgebase/core-cli-reference.md`
- `docs/knowledgebase/gui-validation-surfaces.md`
- `docs/knowledgebase/validation-pipeline.md`
- `AGENTS.md`

**Approach:** Read each doc's current (uncommitted) diff against the actual implemented behavior from U1-U3; fix any drift found.

**Test scenarios:** Test expectation: none — documentation-only unit.

**Verification:** Each doc's description of readiness/resume/`--no-checkpoint` behavior matches the actual code.

---

## Scope Boundaries

**In scope**
- Verifying, closing test gaps in, and shipping the already-implemented readiness/progress/resume wizard UX and its CLI non-regression.

**Deferred for later**
- One-action pristine game-folder rollback / CAS pre-image restore (see origin: Scope Boundaries)
- Unsafe operator overrides for readiness blockers
- Redesigning the checkpoint management dialog as the primary player recovery surface
- Instruction-file-fingerprint tightening of resume identity (see Key Technical Decisions)

**Outside this product's identity**
- Becoming a general multi-game mod manager with user-driven conflict resolution
- Embedding an MCP/headless API inside the desktop app

---

## Verification

- `dotnet build ModSync.sln` succeeds with no new errors.
- All existing and newly-added tests in the files listed above pass.
- Manual smoke check: readiness summary blocks on a bad path, install progress updates live, and closing/reopening the wizard against a partially-completed destination offers Resume.
