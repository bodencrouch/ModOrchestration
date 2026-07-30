---
date: 2026-07-25
topic: prove-roundtrip-c2-invariant
strategy_track: guide-import-fidelity
related:
  - docs/brainstorms/2026-07-25-lossless-roundtrip-universal-pipeline-requirements.md
  - docs/knowledgebase/product-vision.md
  - STRATEGY.md
---

# Prove roundtrip C2 invariant

## Summary

Enable the excluded roundtrip test suites, fix any issues they surface, and establish a regression guard for the C2 invariant (IF → MD → IF preserves all components, instructions, and Choose trees). This proves the core product claim that instruction files are round-trippable and unblocks multi-author share/publish trust.

---

## Problem Frame

ModSync's product vision hinges on instruction files being the source of truth, with markdown guides as derived views. The C2 invariant — that an instruction file can be emitted as markdown and re-ingested without losing components, instructions, or option trees — is the technical foundation for this claim.

Today, the three strongest roundtrip test suites (`DocumentationRoundTripTests`, `MarkdownImportTests`, `MarkdownFileTests`) are excluded from compilation in `src/ModSync.Tests/ModSync.Tests.csproj:88-95`. The product documentation claims guides are "round-trippable" but no test enforces this in normal or agent runs. The 186-vs-189 component divergence between markdown-derived and TOML instruction files (documented in `src/ModSync.Tests/MarkdownTomlParityTests.cs:72-90`) is a known exception, but without active roundtrip tests, there's no way to distinguish "known exception" from "regression."

This gap blocks the strategy's multi-author share/publish track: a share link or `modsync://` handoff is worthless if the shared instruction file is lossy.

---

## Actors

- A1. Developer or CI agent running the test suite — needs roundtrip tests to pass in default flows
- A2. Mod-build author using guide ingestion/emission — needs confidence that round-tripping preserves their work

---

## Requirements

**Test re-enablement**

- R1. The `DocumentationRoundTripTests`, `MarkdownImportTests`, and `MarkdownFileTests` suites are re-included in the default test compilation (remove the `Condition` that excludes them from `ModSync.Tests.csproj`).
- R2. All re-enabled tests pass in the default test configuration. Tests that cannot pass due to known corpus divergences (e.g., the three KOTOR1 romance components) are marked with explicit skip reasons referencing the documented exception list, not blanket exclusions.

**C2 invariant regression guard**

- R3. A regression test enforces the C2 invariant: given any instruction file in the test corpus, emitting it to markdown and re-ingesting produces identical components, instructions, and Choose trees. The test runs in the default agent flow (not a scheduled or excluded set).
- R4. The C2 test uses the pinned `mod-builds` corpus (or a committed subset) so results are reproducible across agents and CI. When the corpus is absent, the test skips cleanly rather than hard-failing.

**Known divergence handling**

- R5. The three KOTOR1 romance components missing from the markdown source are carried as a documented exception list, not as test failures. The exception list is explicit, versioned, and referenced by the regression test.
- R6. Any new divergence discovered during test re-enablement is either fixed (if a parser bug) or added to the exception list with a rationale (if a genuine source divergence).

---

## Acceptance Examples

- AE1. **Covers R1, R2.** Given the default test configuration, when `dotnet test` runs, then the roundtrip test suites execute and pass (or skip with explicit exception-list reasons).
- AE2. **Covers R3.** Given the `KOTOR1_Full` instruction file, when it is emitted to markdown and re-ingested, then the resulting components and instructions are identical to the original.
- AE3. **Covers R5.** Given the KOTOR1 roundtrip test, when it encounters the three romance components, then it skips with a reason referencing the documented exception list, not a blanket "not sure if I want to support" message.

---

## Success Criteria

- The roundtrip test suites are part of the default test flow and run on every CI build.
- The C2 invariant is explicitly tested and regression-guarded against the pinned corpus.
- A new divergence is immediately visible as a test failure or a documented exception, never as silent data loss.
- The product can truthfully claim "instruction files are round-trippable" with test evidence to back it.

---

## Scope Boundaries

**In scope**

- Re-enabling the three excluded roundtrip test suites
- Fixing parser or emitter issues that prevent tests from passing
- Establishing a C2 regression guard test
- Documenting known divergences as explicit exceptions

**Deferred for later**

- The broader universal pipeline unification (CLI and GUI sharing one ingest/emit path) — covered by `2026-07-25-lossless-roundtrip-universal-pipeline-requirements.md`
- C1 (ingest completeness) and C3 (content parity) measurement — separate tracks
- Natural-language Choose/CleanList parsing — only structural constructs are required for C2
- Lossless prose reproduction (byte-identical markdown) — semantic parity is the bar

**Outside this product's identity**

- Making `mod-builds` a writable store or authoring target
- A mandatory bespoke guide markup language

---

## Key Decisions

- **Semantic C2 parity, not byte-identical.** The instruction file is ground truth; prose wording and whitespace may normalize. This matches the existing test expectations.
- **Exception list over corpus edits.** The three missing romance components are a source divergence (markdown omits them), not a loader bug. The test skips them explicitly rather than editing the read-only corpus.
- **Re-enable then fix, not rewrite.** The test suites already exist and were passing before exclusion. The priority is re-enabling them and fixing any rot, not building new test infrastructure.

---

## Dependencies / Assumptions

- The test suites were excluded for reasons that may include now-stale test failures or build configuration changes. Re-enabling may surface issues that need fixing.
- A `./mod-builds` clone or committed test subset is needed for the C2 regression test. The existing parity test already uses a committed subset, so this pattern is established.
- The `MarkdownParser`, `ModComponentSerializationService`, and `DraftInstructionService` are the code paths under test. Any fixes needed are in these services, not in new infrastructure.

---

## Outstanding Questions

### Resolve Before Planning

- None. The scope is well-defined by the existing test suites and the documented C2 invariant.

### Deferred to Planning

- [Needs research] Which specific assertions in the excluded test suites are likely to fail given current parser state? A quick scan of the test code would surface this before planning begins.
- [Needs research] Whether the excluded tests use the same `mod-builds` commit as the parity test, or if they need corpus pinning.
