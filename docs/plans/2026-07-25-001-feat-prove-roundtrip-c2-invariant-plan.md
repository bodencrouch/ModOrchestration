---
date: 2026-07-25
type: feat
status: active
origin: docs/brainstorms/2026-07-25-prove-roundtrip-c2-invariant-requirements.md
---

# Prove roundtrip C2 invariant

## Summary

Enable the three excluded roundtrip test suites (`DocumentationRoundTripTests`, `MarkdownFileTests`, `MarkdownImportTests`), fix any issues they surface, and establish a regression guard for the C2 invariant (instruction file → markdown → instruction file preserves all components, instructions, and Choose trees).

---

## Problem Roundtrip C2 invariant

ModSync's product vision hinges on instruction files being the source of truth, with markdown guides as derived views. The C2 invariant — that an instruction file can be emitted as markdown and re-ingested without losing components, instructions, or option trees — is the technical foundation for this claim.

Today, the three strongest roundtrip test suites are excluded from compilation in `src/ModSync.Tests/ModSync.Tests.csproj:91-93`. The product documentation claims guides are "round-trippable" but no test enforces this in normal or agent runs. This gap blocks the strategy's multi-author share/publish track.

---

## Implementation Units

### U1. Re-enable excluded test suites

**Goal:** Remove the `<Compile Remove="...">` entries for the three roundtrip test suites so they compile and run.

**Requirements:** R1

**Dependencies:** None

**Files:**
- `src/ModSync.Tests/ModSync.Tests.csproj`

**Approach:**
Remove the three `<Compile Remove="...">` lines for `DocumentationRoundTripTests.cs`, `MarkdownFileTests.cs`, and `MarkdownImportTests.cs`. Keep the other exclusions (`RegexPreviewHighlightingTests.cs`, `MainWindowUITests.cs`, `CheckpointSystemIntegrationTests.cs`, `ModSyncMetadataTests.cs`, `ResourceMetadataSerializationTests.cs`) unchanged — those are excluded for different reasons.

**Test scenarios:**
- After removal, `dotnet build src/ModSync.Tests/ModSync.Tests.csproj` succeeds with no compilation errors in the re-enabled test files
- The re-enabled test classes are discoverable by the test runner

**Verification:** Build succeeds; `dotnet test --filter "FullyQualifiedName~MarkdownFileTests|FullyQualifiedName~MarkdownImportTests|FullyQualifiedName~DocumentationRoundTripTests" --list-tests` shows the test methods.

---

### U2. Fix test infrastructure dependencies

**Goal:** Ensure the re-enabled tests can run without external dependencies that may not be present (test fixture files, `mod-builds` corpus).

**Requirements:** R2, R4

**Dependencies:** U1

**Files:**
- `src/ModSync.Tests/DocumentationRoundTripTests.cs`
- `src/ModSync.Tests/MarkdownImportTests.cs`

**Approach:**
The `DocumentationRoundTripTests.Setup()` method looks for `test_modbuild_k1.md` in the test directory. If this file doesn't exist, the test fails with `Assert.Fail`. Two options:
1. Commit a small test fixture markdown file that the tests can use
2. Change the tests to skip gracefully when the fixture is missing

The `MarkdownImportTests.FullMarkdownFile_ParsesAllMods` test depends on `mod-builds/content/k1/full.md` which requires a `mod-builds` clone. This test should skip cleanly when the corpus is absent (use `[Ignore]` with a clear reason, or check for file existence and `Assert.Inconclusive`).

**Test scenarios:**
- `DocumentationRoundTripTests` passes when the test fixture file exists
- `DocumentationRoundTripTests` skips gracefully when the fixture is missing (not a hard failure)
- `MarkdownImportTests.FullMarkdownFile_ParsesAllMods` skips when `mod-builds` is absent
- All other `MarkdownImportTests` and `MarkdownFileTests` pass (they use inline test data, no external deps)

**Verification:** Run the re-enabled tests both with and without the test fixtures; confirm skips are clean, not failures.

---

### U3. Add C2 regression guard test

**Goal:** Add a dedicated test that enforces the C2 invariant: IF → MD → IF preserves components, instructions, and Choose trees.

**Requirements:** R3

**Dependencies:** U2

**Files:**
- `src/ModSync.Tests/C2RoundtripInvariantTests.cs` (new file)

**Approach:**
Create a new test class that:
1. Takes an instruction file (TOML) as input
2. Emits it to markdown using `ModComponentSerializationService.GenerateModDocumentation`
3. Re-ingests the markdown using `MarkdownParser`
4. Asserts that the re-ingested components are identical to the original: same count, same names, same GUIDs, same instructions (action type, source, destination), same Choose trees (options, branches, per-branch instructions)

The test should use inline test data (a small instruction file with a Choose component) rather than depending on external corpus. This keeps it self-contained and always runnable.

**Test scenarios:**
- Round-trip with a simple component (no options) preserves all fields
- Round-trip with a Choose component preserves the option tree structure
- Round-trip with multiple components preserves component count and order
- Round-trip with instructions containing `<<modDirectory>>` and `<<kotorDirectory>>` placeholders preserves placeholders
- Component metadata (tier, category, author, installation method) survives round-trip

**Verification:** `dotnet test --filter "FullyQualifiedName~C2RoundtripInvariantTests"` passes.

---

### U4. Document known divergences

**Goal:** Ensure the three KOTOR1 romance components missing from the markdown source are documented as explicit exceptions, not test failures.

**Requirements:** R5

**Dependencies:** U2

**Files:**
- `src/ModSync.Tests/MarkdownTomlParityTests.cs` (verify existing documentation)

**Approach:**
The existing `MarkdownTomlParityTests.Kotor1Full_SourceFiles_CurrentlyContainKnownSemanticDivergences` test already documents the 186-vs-189 divergence. Verify this test is not excluded from compilation and that its skip reason or assertion message references the exception list clearly. No new code needed — just verification.

**Test scenarios:**
- The parity test is included in the default test suite
- The parity test's skip/assertion message is clear and references the exception list

**Verification:** `dotnet test --filter "FullyQualifiedName~Kotor1Full_SourceFiles_CurrentlyContainKnownSemanticDivergences"` runs and passes/skips with a clear reason.

---

## Scope Boundaries

**In scope**
- Re-enabling the three excluded roundtrip test suites
- Fixing test infrastructure issues (missing fixtures, corpus dependencies)
- Establishing a C2 regression guard test
- Documenting known divergences as explicit exceptions

**Deferred for later**
- Broader universal pipeline unification (CLI and GUI sharing one ingest/emit path)
- C1 (ingest completeness) and C3 (content parity) measurement
- Natural-language Choose/CleanList parsing
- Lossless prose reproduction (byte-identical markdown)

**Outside this product's identity**
- Making `mod-builds` a writable store or authoring target
- A mandatory bespoke guide markup language

---

## Key Technical Decisions

- **Semantic C2 parity, not byte-identical.** The instruction file is ground truth; prose wording and whitespace may normalize. This matches existing test expectations.
- **Exception list over corpus edits.** The three missing romance components are a source divergence (markdown omits them), not a loader bug. Tests skip them explicitly rather than editing the read-only corpus.
- **Self-contained C2 test.** The new regression guard uses inline test data, not the external `mod-builds` corpus, so it always runs.

---

## Dependencies / Assumptions

- The test suites were excluded for reasons that may include now-stale test failures or build configuration changes. Re-enabling may surface issues that need fixing.
- A committed test fixture file (`test_modbuild_k1.md`) may need to be created or the tests modified to skip gracefully.
- The `MarkdownParser`, `ModComponentSerializationService`, and `DraftInstructionService` are the code paths under test.

---

## Risk Analysis

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Re-enabled tests fail due to parser regressions | Medium | Low | Fix parser issues or document as known limitations |
| Test fixture file missing | High | Low | Create fixture or modify tests to skip gracefully |
| `mod-builds` corpus not present in CI | High | Low | Tests skip cleanly when absent |
| Other excluded tests have same issues | Low | Low | Out of scope — handle separately |

---

## Verification

- `dotnet build src/ModSync.Tests/ModSync.Tests.csproj` succeeds
- `dotnet test --filter "FullyQualifiedName~MarkdownFileTests|FullyQualifiedName~MarkdownImportTests|FullyQualifiedName~DocumentationRoundTripTests|FullyQualifiedName~C2RoundtripInvariantTests"` passes
- The C2 invariant is explicitly tested and regression-guarded
