---
date: 2026-07-30
type: feat
status: active
origin: docs/brainstorms/2026-07-25-lossless-roundtrip-universal-pipeline-requirements.md
---

# Prove lossless roundtrip and unify guide ingest/emit pipeline

## Summary

Make natural-language prose parsing the primary mechanism for reading `mod-builds` guides (confirmed: the real corpus never uses ModSync's structured `#### Options`/`Guid:` markdown), add first-class parsing for the guide's current `:::note`/`:::warning` admonition-fence format, prove ModSync's own instruction file round-trips losslessly through the guide markdown and back (`C2`), and route both CLI and GUI through one shared ingest/emit path with a single options contract.

This builds on top of the already-open, separately-shipped PR #196 (`fix(core): preserve GUID/instructions/options across markdown round-trip`), which fixed embedded-metadata-block parsing bugs this plan's own round-trip tests depend on. It does not duplicate that work.

---

## Problem Frame

Three confirmed gaps, per the origin brainstorm's repo-history and prior-author-correspondence research (see origin: docs/brainstorms/2026-07-25-lossless-roundtrip-universal-pipeline-requirements.md):

1. **The parser's structural lever is unused by real content.** `MarkdownParser.cs`'s `#### Instructions`/`#### Options`/`**GUID:**` parsing is real, tested code that the actual guide has never once used across ~850 commits. `NaturalLanguageInstructionParser.cs` — an ordered ~40-pattern regex list with no gap-reporting contract — is the only mechanism that actually reads real guide content, but its failures are silent (verbose-log-only) and it has no concept of conditional/branching instructions.
2. **The current guide format is only narrowly handled.** The guide converted to Docusaurus-style `:::note`/`:::warning` admonition fences in Oct 2025. `MarkdownImportProfile.CreateDefault()` has fence-aware regex alternatives, but only for two hardcoded field names ("Installation Instructions", "Known Bugs"); every other field lacks fence support, and the definition-list `:   ` prefix isn't explicitly stripped the way a separate, unrelated parser in `ModBuildConverter.cs` (`ParseSpoilerFreeMarkdown`) already does correctly.
3. **CLI and GUI each bypass the shared port.** `GuideIngestService`/`GuideEmitService` (`src/ModSync.Core/Ports/Guides/`) exist but are used only by tests. CLI `convert`, GUI file-open, and GUI paste each call `MarkdownParser`/`DraftInstructionService` directly, each with its own literal draft-on/off default (file-open: off, paste: on — a deliberate, documented asymmetry per `docs/knowledgebase/guide-ingestion.md` that must be preserved, not collapsed).

---

## Requirements

Traced to origin R1-R15 (see origin document for full text). Grouped here by implementation phase.

**Parser coverage:** R5 (natural-language parsing is primary), R6 (admonition-fence format first-class), R7 (phrasing pattern coverage), R8 (draft-rate floor), R9 (unparsed prose surfaces as a reviewable gap, never silently dropped).

**Roundtrip fidelity:** R1 (C2 lossless loop), R2 (metadata survives C2), R3 (C1 ingest completeness), R4 (C3 content parity, historical/best-effort only per origin Key Decisions).

**Unified pipeline:** R10 (CLI/GUI share one ingest/emit path), R11 (same guide + same options → same output on both surfaces).

**Measurement and corpus handling:** R12 (C1/C2/draft-floor proven by tests in default+agent flows), R13 (regression guards absolute invariants, not a ratchet), R14 (corpus read-only), R15 (pinned repo/ref, tests skip cleanly when absent).

---

## Key Technical Decisions

- **Generalize the existing fence support rather than replacing it.** `MarkdownImportProfile`'s two hardcoded fence alternatives (Installation Instructions, Known Bugs) become a general per-field fence pattern applied uniformly, reusing the definition-list-stripping logic already correct in `ModBuildConverter.cs:1672-1679`'s `ParseSpoilerFreeMarkdown` rather than inventing new stripping logic.
- **Unparsed-gap reporting is a result-contract change, not a regex change.** `DraftInstructionResult` gains a field for unparsed/undraftable prose units; `DraftInstructionService.GenerateDraftInstructions` returns one result per component with Directions — including zero-draft ones — instead of omitting them. `NaturalLanguageInstructionParser.ParseInstructionUnit`'s three real outcomes (matched / recognized-as-commentary / looked-actionable-but-unmatched) become distinguishable to the caller instead of collapsing into one silent no-op.
- **AE7/AE8 (redrob cleanlist, K2CP+HD-Visas) are "detect and flag a gap," not "fully auto-draft," per confirmed scope.** Both are greenfield — no existing scaffolding. The bar is: the conditional nature of the instruction is either correctly captured as structure, or explicitly surfaced as a reviewable gap. Silent misresolution (wrong unconditional deletion, merged conditional+unconditional steps) is the failure mode to eliminate; full automation of every such case is not required.
- **`GuideIngestService` is rewired to call `MarkdownParser` for markdown content**, not the generic `ModComponentSerializationService.DeserializeModComponentFromString` deserializer it uses today — that's the only way to populate preamble/epilogue/widescreen/Aspyr content and the parse trace on the port's result, all of which already exist on `MarkdownParserResult` but are currently dropped at the port boundary.
- **The draft-on/off asymmetry is preserved as an explicit shared option value, not collapsed.** File-open defaults off, paste and `--parse-directions` opt in — unification means one options field with per-surface-supplied defaults, not one hardcoded behavior.
- **Corpus pin: `KOTOR-Community-Portal/mod-builds`, `dev` branch, at a recorded commit** — not the frozen `oldrepublicwizard/mod-builds` TOML repo, and not the `th3w1zard1/mod-builds` mirror `AGENTS.md` currently tells contributors to clone (a doc correction, tracked in Scope Boundaries).
- **New C2 round-trip test suite, not a repurposing of the three dormant MD-anchored suites.** `DocumentationRoundTripTests`/`MarkdownImportTests`/`MarkdownFileTests` compare MD-to-MD or name-list parity; none compare against ModSync's own instruction file, so none can prove `C2`. They still need R15's clean-skip fix independently.

---

## Implementation Units

### Phase A: Parser Core

### U1. Generalize admonition-fence parsing to any field

**Goal:** Every guide field (not just Installation Instructions/Known Bugs) parses correctly whether written as plain bold-inline text or wrapped in a `:::note`/`:::warning` fence with `:   ` definition-list continuation lines, with the `:   ` prefix stripped from captured content.

**Requirements:** R6

**Dependencies:** None

**Files:**
- `src/ModSync.Core/Parsing/MarkdownImportProfile.cs` (generalize the fence-pattern approach beyond the two current hardcoded alternatives)
- `src/ModSync.Core/Parsing/MarkdownParser.cs` (if extraction needs a shared post-processing step to strip `:   ` prefixes from multi-line fence captures)
- `src/ModSync.Tests/MarkdownFileTests.cs` or a new test file for fence-format coverage

**Approach:** Mirror the correct definition-list stripping already implemented in `ModBuildConverter.cs`'s `ParseSpoilerFreeMarkdown` (lines ~1672-1679) rather than reimplementing it. Keep the plain bold-inline alternative as a fallback for any field not yet converted to fence style, since both styles can coexist across different fields in the same document.

**Patterns to follow:** `ModBuildConverter.cs`'s existing `:::` fence stripping; `MarkdownImportProfile.CreateDefault()`'s existing `InstallationInstructionsPattern`/`KnownBugsPattern` as the starting shape to generalize.

**Test scenarios:**
- Happy path: a field written in `:::note` fence style with a `:   ` prefixed single-line body parses identically to the same content written bold-inline.
- Happy path: a multi-line fence body (several `:   `-prefixed lines) has every line's prefix stripped and lines joined into one coherent prose value.
- Edge case: a document mixing fence-style fields and bold-inline fields in the same component parses both correctly.
- Edge case: a `:::warning` fence for a field with no existing hardcoded alternative (i.e., a field name never seen in the two-field hardcode) still parses via the generalized pattern.
- Error path: a malformed/unclosed fence does not crash the parser; it degrades to treating the content as an unrecognized field rather than throwing.

**Verification:** New tests pass; the two existing fence-dependent tests (if any reference `InstallationInstructionsPattern`/`KnownBugsPattern` directly) continue to pass unchanged.

---

### U2. Add unparsed-gap reporting to the draft pipeline

**Goal:** Prose that the natural-language parser cannot interpret as an instruction is never silently dropped — it surfaces as an explicit, reviewable gap the caller can render.

**Requirements:** R9

**Dependencies:** None

**Files:**
- `src/ModSync.Core/Parsing/NaturalLanguageInstructionParser.cs` (`ParseInstructionUnit` outcome signal)
- `src/ModSync.Core/Parsing/DraftInstructionService.cs` (`DraftInstructionResult` shape, `GenerateDraftInstructions` per-component result emission)
- `src/ModSync.Tests/` — new or extended tests for gap surfacing

**Approach:** Distinguish the three real outcomes `ParseInstructionUnit` already computes internally (matched / recognized-as-commentary / looked-actionable-but-unmatched) and return that distinction rather than only logging it at verbose level. `GenerateDraftInstructions` returns one `DraftInstructionResult` per component that has Directions prose, including components where zero instructions drafted, carrying the unparsed unit text so CLI and GUI can both render "N of M directions produced no draft."

**Test scenarios:**
- Happy path: prose that fully matches known patterns produces a result with zero unparsed units.
- Edge case: prose recognized as pure commentary (matches `IsInformationalOnly`) is not counted as an unparsed gap — it's correctly skipped, not flagged.
- Edge case: prose that contains an action verb but matches no pattern is flagged as an unparsed gap distinct from commentary.
- Integration: a component with Directions but zero successfully-drafted instructions still appears in `GenerateDraftInstructions`'s result list (today it's omitted).

**Verification:** `DraftInstructionResult` for a known-gap fixture (e.g. a deliberately unparseable sentence) shows the gap; existing components that draft cleanly show zero gaps and unchanged instruction output.

---

### U3. Clause-level conditional decomposition

**Goal:** A single Directions sentence expressing two distinct actions — one unconditional, one conditional on another mod being present — decomposes into two separate instructions rather than staying one flat unit.

**Requirements:** R7 (AE8)

**Dependencies:** U2 (gap reporting should exist so genuinely undecomposable conditionals still surface cleanly rather than silently misparsing)

**Files:**
- `src/ModSync.Core/Parsing/NaturalLanguageInstructionParser.cs` (`SplitIntoProcessingUnits`, conditional-clause pattern)
- `src/ModSync.Tests/` — AE8 fixture test

**Approach:** Extend the sentence/clause splitter to recognize an explicit "if using X, additionally/also Y" clause boundary as a second processing unit, tagged with its condition, rather than feeding the whole sentence through the flat pattern list as one unit. This is squarely R7's most concrete named case (K2CP+HD-Visas: delete a base file set unconditionally, delete three more files only if a second specific mod is also used).

**Test scenarios:**
- Happy path: the literal K2CP+HD-Visas Directions text (see origin document's quoted example) decomposes into an unconditional deletion instruction and a separate HD-Visas-conditional deletion instruction.
- Covers AE8. Given the K2CP mod's Directions with the nested "if also using HD Visas" clause, when ingested, both the unconditional and conditional deletions are captured as distinct instructions, not merged into one.
- Edge case: a sentence with no conditional clause is unaffected by the new splitter logic (no regression to existing single-unit parsing).

**Verification:** AE8 fixture passes; existing `NaturalLanguageInstructionParser` tests for non-conditional sentences are unaffected.

---

### U4. Redrob cleanlist-style conditional deletion

**Goal:** A Directions entry whose deletion list depends on an externally-maintained cleanlist (not enumerable from the guide text alone) is captured as a conditional reference rather than silently resolved to a wrong fixed file list.

**Requirements:** R7 (AE7)

**Dependencies:** U2

**Files:**
- `src/ModSync.Core/Parsing/NaturalLanguageInstructionParser.cs` or a new dedicated parsing unit for cleanlist-style references
- `src/ModSync.Tests/` — AE7 fixture test

**Approach:** Greenfield — no existing scaffolding (confirmed in research). Per the confirmed scope decision, the bar is detecting that a deletion is conditional-on-an-external-list and preserving that as metadata or an explicit gap — not dynamically fetching/parsing the actual `cleanlist_k1.txt` file from the corpus, which is out of scope here (see Scope Boundaries).

**Test scenarios:**
- Covers AE7. Given a Directions entry referencing conditional per-mod deletion driven by an external cleanlist, when ingested, the conditional nature is preserved as metadata or surfaces as an explicit gap — never silently resolved to a wrong fixed file list.
- Edge case: a Directions entry with a literal, non-cleanlist-referencing file deletion list is unaffected and drafts normally.

**Verification:** AE7 fixture passes without silently fabricating a wrong file list.

---

### U5. Verify HQ Blasters-style multi-step sequence coverage

**Goal:** Confirm the existing pattern set correctly handles the HQ Blasters sequence (delete a file from TSLPatchdata to force an intentional single patcher error, run the patcher, then post-install rename and delete several files) end-to-end, or identify and close the specific gap.

**Requirements:** R7 (AE6)

**Dependencies:** None

**Files:**
- `src/ModSync.Core/Parsing/NaturalLanguageInstructionParser.cs` (verify/extend Patcher + rename + delete pattern sequencing)
- `src/ModSync.Tests/` — AE6 fixture test

**Approach:** This may turn out to be verification-only if the existing Patcher/rename/delete patterns already compose correctly across a multi-step sentence sequence — confirm with the literal HQ Blasters text (see origin document) before assuming new patterns are needed.

**Test scenarios:**
- Covers AE6. Given the HQ Blasters Directions text verbatim, when ingested, either the full instruction sequence drafts correctly, or the undraftable portion surfaces as an explicit gap per U2 — never silently dropped or partially applied without signal.

**Verification:** AE6 fixture passes or produces a clean, reviewable gap (not a silent partial result).

---

### Phase B: Unified Pipeline

### U6. Extend the guide ingest/emit ports

**Goal:** `IGuideIngestService`/`IGuideEmitService` carry everything `MarkdownParser`/`MarkdownParserResult` already produce — preamble/epilogue/widescreen/Aspyr content, parser profile/options, parse trace, and U2's unparsed-gap surface — instead of dropping them at the port boundary.

**Requirements:** R2, R10

**Dependencies:** U1, U2 (the port should carry the new fence-parsing and gap-reporting capabilities, not just the old ones)

**Files:**
- `src/ModSync.Core/Ports/Guides/IGuideServices.cs`
- `src/ModSync.Core/Ports/Guides/GuideServices.cs`
- `src/ModSync.Tests/` — port contract tests

**Approach:** Rewire `GuideIngestService.IngestFromText` to call `MarkdownParser` directly for markdown-format content instead of the generic `ModComponentSerializationService.DeserializeModComponentFromString` deserializer it uses today, so the richer `MarkdownParserResult` fields become available to populate the extended `GuideIngestResult`. Extend `GuideEmitService.EmitMarkdown`/`EmitMarkdownAsync` to accept widescreen/Aspyr content alongside the existing preamble/epilogue parameters.

**Test scenarios:**
- Happy path: ingesting a guide with preamble, epilogue, widescreen, and Aspyr sections populates all four on the port result.
- Integration: a parse that produces U2-style unparsed gaps surfaces them on the port result, not only when calling `MarkdownParser` directly.
- Edge case: ingesting non-markdown formats (TOML/YAML/JSON) through the same port continues to work unchanged (the markdown-specific rewiring must not regress other formats).

**Verification:** Existing port tests continue to pass; new tests confirm preamble/epilogue/widescreen/Aspyr/trace/gap fields are populated for markdown input.

---

### U7. Route CLI and GUI through the extended port

**Goal:** CLI `convert`/`merge` and GUI file-open/paste ingest and emit guides through the U6 port instead of calling `MarkdownParser`/`DraftInstructionService` directly, with the paste-vs-file-open draft-on/off asymmetry expressed as one explicit option value passed by each caller rather than three independent hardcoded literals.

**Requirements:** R10, R11

**Dependencies:** U6

**Files:**
- `src/ModSync.Core/CLI/ModBuildConverter.cs`
- `src/ModSync.GUI/Services/FileLoadingService.cs`
- `src/ModSync.GUI/ViewModels/RegexImportDialogViewModel.cs` (or wherever the editor/config ingest path lives)
- `src/ModSync.Tests/` — CLI/GUI parity tests

**Approach:** Replace each surface's direct `MarkdownParser`/`DraftInstructionService` calls with calls through `IGuideIngestService`/`IGuideEmitService`, passing each surface's existing draft-flag default as the option value (CLI convert: off unless `--parse-directions`; GUI file-open: off; GUI paste: on) rather than changing any surface's actual default behavior.

**Test scenarios:**
- Covers AE4 (origin R11). Given one guide loaded via CLI and GUI with drafting off, both produce identical components and instructions; with drafting on, both produce identical drafts.
- Edge case: GUI paste's draft-on default and GUI file-open's draft-off default are both still correct after the routing change (no behavior collapse).
- Integration: CLI `convert --parse-directions` produces the same drafted instructions as GUI paste for the same input guide.

**Verification:** AE4 passes; no direct `MarkdownParser`/`DraftInstructionService` calls remain in `ModBuildConverter.cs` or the GUI ingest paths outside the port.

---

### Phase C: Measurement and Regression Safety

### U8. Self-contained C2 round-trip regression test

**Goal:** A regression guard proving `IF → emitted guide → IF` preserves every component, instruction, and Choose tree, using inline test data — no external corpus dependency, so it always runs.

**Requirements:** R1, R2, R12, R13 (AE1)

**Dependencies:** U1-U7 (the invariant should be tested against the upgraded parser/pipeline, not the pre-upgrade one)

**Files:**
- `src/ModSync.Tests/C2RoundtripInvariantTests.cs` (new — note this file exists on a separate, unmerged branch from prior work; this unit creates the equivalent on top of this plan's branch, verifying it doesn't already exist before writing)

**Approach:** Mirror the self-contained-fixture pattern (inline instruction file with a Choose component, no corpus dependency) rather than depending on `./mod-builds`.

**Test scenarios:**
- Covers AE1. Given an instruction file with a Choose component, when it is emitted and re-ingested, then the Choose tree and each branch's instructions are identical to the original.
- Happy path: a simple component with instructions round-trips with all fields intact.
- Edge case: multiple components with mixed Choose/non-Choose content preserve count and order.

**Verification:** Test passes without requiring `./mod-builds` to be present.

---

### U9. Corpus-pinned measurement and clean-skip fixes

**Goal:** Pin the corpus to `KOTOR-Community-Portal/mod-builds` `dev` at a recorded commit; add clean-skip-when-absent to every corpus-dependent test that currently hard-fails; measure and record the R8 draft-rate floor and R4 exception list from a live pinned read.

**Requirements:** R4, R8, R12, R13, R14, R15

**Dependencies:** U1-U5 (measurement should reflect the upgraded parser, not the baseline one)

**Files:**
- `src/ModSync.Tests/MarkdownTomlParityTests.cs` (currently hard-fails via `Assert.That(File.Exists(...), Is.True)` when `./mod-builds` is absent — add clean skip)
- `src/ModSync.Tests/DocumentationRoundTripTests.cs`, `MarkdownImportTests.cs`, `MarkdownFileTests.cs` (same clean-skip gap, confirmed via research)
- `AGENTS.md` (correct the `th3w1zard1/mod-builds` clone instruction to `KOTOR-Community-Portal/mod-builds` `dev`)
- A recorded-baseline artifact (e.g. a committed fixture or doc noting the measured draft-rate floor and exception list at the pinned commit)

**Approach:** Introduce one shared skip-check pattern (e.g. a helper checking `./mod-builds` exists before the corpus-dependent tests run, using `Assert.Ignore`/`Assert.Inconclusive` rather than `Assert.Fail`) and apply it consistently across all four files, per the R15 gap confirmed identical across all of them. Note that `MarkdownTomlParityTests.cs`'s current fixture layout assumes one `./mod-builds` clone contains both the canonical guide content and the frozen TOML — since those now live in two different repos per the origin document's Key Decisions, this unit must also decide (and document) how the pinned local corpus is assembled for this test (e.g., vendoring the frozen TOML snapshot separately, or dropping the TOML-comparison test in favor of a documented one-time historical record).

**Test scenarios:**
- Edge case: each of the four corpus-dependent test files skips cleanly (not a hard failure) when `./mod-builds` is absent.
- Integration: with `./mod-builds` present at the pinned commit, the draft-rate floor and C3 exception list are measured and match the recorded baseline.
- Test expectation: none for the `AGENTS.md` correction — pure documentation fix, no behavioral change.

**Verification:** All four files run green in CI without `./mod-builds`; with it present at the pinned commit, they produce the recorded baseline numbers.

---

## Scope Boundaries

**In scope**
- Natural-language parser upgrades for the current (post-Oct-2025) guide format only.
- Unparsed-gap reporting contract change.
- The three named acceptance-example cases (AE6-AE8), to the "detect and flag" bar, not full automation guarantee.
- CLI/GUI pipeline unification via the existing ports.
- Corpus pin correction and clean-skip test fixes.

**Deferred for later**
- Byte-identical markdown reproduction (per origin document).
- The pre-Oct-2025 bold-inline guide format (per origin document, confirmed obsolete).
- Full automated dynamic parsing of redrob's actual `cleanlist_k1.txt` file content (U4 only detects/flags the conditional reference; fetching and interpreting the external cleanlist is separate future work).
- Generalization beyond `KOTOR1_Full`/`KOTOR2_Full` to other authors' guides.

**Outside this product's identity**
- A mandatory bespoke guide markup language (per origin document — the guide author has explicitly declined prose standardization).
- Turning `mod-builds` into a writable store.

---

## Dependencies / Assumptions

- Builds on top of PR #196's already-shipped fixes to embedded-metadata-block parsing (GUID/instruction preservation); does not duplicate that work.
- A `./mod-builds` clone pinned to `KOTOR-Community-Portal/mod-builds` `dev` branch is required for U9's measurement work; U8's regression test is self-contained and does not require it.
- The frozen `oldrepublicwizard/mod-builds` TOML (last commit 2025-10-31) is available only as a historical snapshot for U9's one-time C3 cross-check, not a continuously-updated source.

## Risk Analysis

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| U3/U4 (conditional decomposition, cleanlist detection) prove harder than scoped and drift toward full automation | Medium | Medium | Confirmed scope bar is "detect and flag," not full automation — hold the line at that bar per Key Technical Decisions |
| U7's port rewiring silently changes CLI/GUI draft-on/off behavior for existing users | Low | High | AE4 test explicitly proves parity; existing per-surface defaults are passed through as data, not re-decided |
| U9's corpus-repo split (guide content vs. frozen TOML in two different repos) complicates the existing test's fixture assumptions | Medium | Low | Explicitly called out as a decision this unit must make and document, not silently paper over |

---

## Verification

- All nine units' test scenarios pass.
- `dotnet build ModSync.sln` succeeds with no new errors.
- U8's C2 regression test runs in the default test flow without requiring `./mod-builds`.
- U9's corpus-dependent tests skip cleanly without `./mod-builds`, and produce the recorded baseline with it present at the pinned commit.
- No direct `MarkdownParser`/`DraftInstructionService` calls remain in `ModBuildConverter.cs` or GUI ingest paths outside `IGuideIngestService`/`IGuideEmitService`.
