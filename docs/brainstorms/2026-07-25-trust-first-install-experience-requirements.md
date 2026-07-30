---
title: "Trust-first install experience"
date: 2026-07-25
topic: trust-first-install-experience
strategy_track: player install trust
related: docs/knowledgebase/install-lifecycle.md
---

# Trust-first install experience

## Summary

Make long guided installs feel safe to start and safe to continue: catch bad environments before install begins, show honest progress while it runs, and make resume/retry after failure an obvious player action. Extend existing checkpoint/resume behavior rather than shipping a new full-folder rollback product.

## Problem Frame

KOTOR full builds are long, order-sensitive, and opaque. Across mod managers, the highest-friction failures are bad paths/permissions at start and mid-install breaks with no clear recovery. ModSync already validates, checkpoints, and can resume components under the hood, but players often experience the loop as “start, fail, guess, restart.” Industry patterns for guided installers (Wabbajack-class) prioritize preflight, transparent progress, and resumability over open-ended conflict tooling.

## Key Decisions

- **Trust over manager parity.** Optimize for curated build install trust, not Vortex/MO2 general-manager features (per-mod conflict UI, arbitrary mix-and-match).
- **Extend checkpoint/resume, don’t replace it.** Resume already happens automatically inside the install service when session state is valid; the product gap is wizard discovery and failure UX, not new recovery machinery.
- **Three recovery concepts stay distinct.** Component-session resume (skip completed work) is the MVP; the Git checkpoint browser (roll back to a commit) is existing post-hoc tooling; CAS pristine restore stays deferred. Copy must not blur them.
- **Players first in this pass.** Author publish/share and agent/CLI parity improvements stay adjacent tracks, not this MVP.
- **Evidence assumption.** Framing rests on strategy, install-lifecycle docs, and cross-manager best-practice research — not a single named player incident captured in this brainstorm.

## Actors

- A1. Player installing a curated instruction-file build through the install wizard
- A2. Maintainer/agent running the same install path headlessly (CLI) — must not regress; player-facing clarity is the primary design target

## Requirements

**Install-start readiness**

- R1. Before a real install starts, the player sees an install-start readiness summary covering detectable start blockers — at minimum path/drive safety and required tools such as HoloPatcher. Reuse existing detection rather than building a second validation pipeline; add new probes only for failure modes validation cannot catch.
- R2. Failed readiness items use plain-language remediation, and the player cannot advance into install while blockers remain.
- R3. The readiness summary is a compact pass/fail surface on install start, pointing back to validation detail for validation-owned blockers rather than duplicating the full report.

**Progress**

- R4. During install, the player always sees which component is active, overall progress through the selected set, and whether the run is healthy, waiting, or failed.
- R5. Long installs communicate that interruption is recoverable when resume is available; progress does not imply “start over from zero” after a non-fatal stop.

**Recovery**

- R6. When an install fails or stops mid-run, the player is offered a clear next action: resume/retry from the last successfully completed component when session state allows it.
- R7. Resume/retry stays available when the player reopens the install wizard for the same instruction file and directories, not only while the failed run's window is still open.
- R8. On failure the player sees what completed versus what remains; when resume is unavailable the UI says so, and recovery copy never implies one-click restoration of the pre-install game folder.

**Non-regression**

- R9. Do not break existing headless recoverability: resume-by-session must keep working for CLI installs. Wizard trust UX is the delivery target, and CLI behavior changes only where required to keep current recoverability honest.

## Key Flows

- F1. Clean start
  - **Trigger:** Player reaches install start with a valid selection and directories set.
  - **Actors:** A1
  - **Steps:** Readiness check runs → blockers cleared → player confirms → install begins with live component progress.
  - **Outcome:** Install proceeds only when readiness is known.

- F2. Mid-install failure with resume
  - **Trigger:** A component fails after earlier components succeeded and checkpoint/resume state is intact.
  - **Actors:** A1
  - **Steps:** Failure surfaces with completed/remaining summary → player chooses resume/retry → install continues from the next incomplete selected component.
  - **Outcome:** Player does not reinstall already-completed components by default.

- F2b. Recovery after reopening
  - **Trigger:** Player closes the wizard after a failure and returns later with session state intact.
  - **Actors:** A1
  - **Steps:** Wizard detects resumable session for the same instruction file and directories → offers resume → install continues from the next incomplete component.
  - **Outcome:** Walking away does not cost the completed work.

- F3. Failure without resume
  - **Trigger:** Install stops and resume is unavailable or invalid.
  - **Actors:** A1
  - **Steps:** Failure surfaces completed/remaining truthfully → player is directed to validate/fix root cause or restart with eyes open — not promised a full undo.
  - **Outcome:** No false recovery promise.

## Acceptance Examples

- AE1. Covers R1, R2
  - **Given:** Game path is on a protected/invalid location ModSync can detect, or HoloPatcher is missing on Linux.
  - **When:** Player attempts to start install.
  - **Then:** Readiness blocks with a remediation message and install does not begin until fixed.

- AE2. Covers R4, R6
  - **Given:** Components 1–10 of 40 completed, then component 11 fails.
  - **When:** The player returns to recover in the same session.
  - **Then:** UI offers resume/retry from after the last successful component and shows that 1–10 are done.

- AE3. Covers R7
  - **Given:** The player closes the wizard after a failed run and reopens it against the same instruction file and directories.
  - **When:** The install flow is re-entered.
  - **Then:** Resume/retry is offered instead of a silent restart from the first component.

- AE4. Covers R8
  - **Given:** Session state is missing or invalid after a failure.
  - **When:** Failure UI is shown.
  - **Then:** Copy states resume is unavailable and does not claim one-click restoration of the pre-install game folder.

## Success Criteria

- A player can tell, before clicking install, whether the environment is ready and what to fix if not.
- After a mid-build failure with intact resume state, the default recovery path is continue — not “wipe and start over.”
- Failure copy never claims full game-directory rollback for this MVP.
- Existing headless resume-by-session behavior remains usable for agents after the wizard trust work ships.

## Scope Boundaries

**In scope**

- Wizard-facing readiness summary, progress clarity, and resume/retry affordances for classic guided installs
- Reuse of existing validation detectors; install-start readiness is a gate plus summary, not a second validation product
- Rewriting existing install and completion copy that currently implies rollback the product does not deliver
- Non-regression of headless resume

**Deferred for later**

- One-action pristine game-folder rollback / CAS pre-image restore for in-place patcher overwrites
- Unsafe operator overrides for readiness blockers
- Redesigning the checkpoint management dialog as the primary player recovery surface
- Managed-vs-classic DryRun staging parity as a trust narrative
- Author publish/share, structured JSON validation reports, profile CLI CRUD
- FOMOD image/advanced-conditional polish beyond existing fail-closed gate

**Outside this product's identity**

- Becoming a general multi-game mod manager with user-driven conflict resolution as the core loop
- Embedding an MCP/headless API inside the desktop app (CLI/scripts remain the agent path)

## Dependencies / Assumptions

- Checkpoint/resume via the install coordinator remains the recovery foundation to extend. Verified: session/resume state exists in `src/ModSync.Core/Installation/` and is consumed by `src/ModSync.Core/Services/InstallationService.cs`, while the GUI exposes resume only for downloads — the wizard-facing recovery surface is the gap.
- Validation pipeline and FOMOD configure-before-install gate stay prerequisites to install start, and already cover much of the environment detection the readiness summary will surface.
- The installing page currently cannot be re-entered after it marks a run complete, so wizard flow changes are a prerequisite for the reopen-and-resume behavior.
- Industry pattern priority for guided installers: preflight + resumable progress over open-ended conflict tooling.
- No firsthand player incident was captured in this brainstorm; priority is inferred from strategy metrics (full-build pass rate) and external mod-manager pain patterns.

## Outstanding Questions

**Deferred to Planning**

- Which readiness signals are re-surfaced from validation versus genuinely new probes (disk space and protected-path policy are the likely net-new candidates)
- How the wizard detects and offers a resumable session on re-entry, given the installing page is currently one-shot and treats any exit as completion
- Whether the documented-but-unread `--no-checkpoint` CLI option is wired up or removed, since it is a no-op today
- Exact progress presentation on the installing page versus download-status surfaces

## Sources / Research

- `STRATEGY.md` — player-primary hire; full-build pass rate metric
- `docs/knowledgebase/install-lifecycle.md` — wizard order, checkpoints, resume
- `docs/knowledgebase/product-vision.md` — vision vs current state
- `docs/plans/vortex-mo2-feature-parity-living-plan.md` — parity residuals (rollback/CAS deferred)
- External patterns (2024–2026): Wabbajack/Collections-style guided installers prioritize preflight, manifest transparency, and resumable long installs; Vortex/MO2 communities cite deployment/path failures and missing rollback as top trust failures
