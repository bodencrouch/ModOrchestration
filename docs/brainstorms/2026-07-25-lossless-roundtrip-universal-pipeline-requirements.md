---
date: 2026-07-25
updated: 2026-07-30
topic: lossless-roundtrip-universal-pipeline
---

# Lossless guide roundtrip + one universal pipeline — requirements

## Summary

Prove that ModSync can carry a `mod-builds` guide through its own instruction file and back without losing components, instructions, or option trees, using natural-language parsing as the primary (not fallback) mechanism, and route both the CLI and GUI through one shared ingest/emit path with a single options contract. Fidelity and pipeline-unity are two independent tracks that may ship separately. The `mod-builds` corpus stays strictly read-only.

**Updated 2026-07-30** with findings from the guide repo's full commit history and prior author correspondence (see Sources / Research). These findings correct two load-bearing assumptions in the original brainstorm — see Key Decisions.

## Problem Frame

Guide ingestion and guide emission both ship, but neither is proven lossless and the two entry surfaces don't share one path. The markdown loader produces 186 components for KOTOR1 while a hand-built TOML translation has 189; a committed test names this a source divergence — the markdown source omits three romance components (`src/ModSync.Tests/MarkdownTomlParityTests.cs:72-90`). The strongest markdown roundtrip suites — `DocumentationRoundTripTests`, `MarkdownImportTests`, `MarkdownFileTests` — were compiled out of the build (`src/ModSync.Tests/ModSync.Tests.csproj:88-95`) as of this brainstorm's original writing; they have since been re-enabled on a separate branch, but the broader unification and fidelity work below was not part of that fix. CLI `convert`/`merge` and the GUI load/paste flows each call `MarkdownParser` and the draft service directly with different draft-on/off behavior, while the `GuideIngestService`/`GuideEmitService` ports meant to be the shared path exist only in tests. The result is two subtly divergent pipelines and a fidelity claim no test guards.

This is the precondition for the strategy's next moves: a share link or a `modsync://` handoff is worthless if the shared instruction file is lossy. Lossless round-trip has to hold before publish/share and consume paths carry other authors' builds.

**What the guide actually looks like, confirmed by reading the corpus's full history and the guide author's own correspondence:** the guide is, and has always been, pure human-authored prose. There is no structured `#### Instructions` / `#### Options` / `Guid:` block anywhere in its ~850-commit history — that is ModSync's own native markdown convention, never the guide's. Every install step, including Choose-style "pick one of the following" branches, is written as free-text sentences inside a single Directions/Installation-Instructions field. The guide author (in direct correspondence, see Sources) explicitly and repeatedly declined to standardize that prose beyond consistent word-choice per instruction type, calling out specific mods (HQ Blasters, redrob's texture cleanlist, a K2CP+HD-Visas nested conditional) as genuinely irreducible to a fixed formula. She was willing to standardize syntax/structure (heading levels, field order, field names) but never instruction wording. A prior effort to maintain a hand-built TOML translation alongside the guide (in a separate repository) was abandoned for exactly this reason and is now frozen.

## Key Decisions

- **Three named fidelity checks, and only one is "lossless."** `C1` ingest completeness: every component and instruction the guide markdown *expresses* appears in the resulting components, and unparsed prose is flagged rather than dropped. `C2` emit round-trip — the lossless loop: `IF → emitted guide → IF` preserves every component, instruction, and Choose tree, with ModSync's own instruction file as ground truth. `C3` content parity: the markdown-derived component set versus a paired full-build instruction file, reported as a diff. Because the instruction file is the source of truth and emit regenerates markdown, `MD→IF→MD` cannot measure fidelity to the source of truth — `C2` is the loop that can, and `C1`/`C3` bracket what markdown ingest and corpus authorship contribute.
- **Correction: the paired TOML is not an upstream artifact and is stale.** The original brainstorm assumed a maintained TOML lived alongside the canonical guide. It does not, and never has — `KOTOR-Community-Portal/mod-builds` (the canonical repo) has zero `.toml` files across its entire history. The `TOMLs/` directory referenced by `C3`/R4 existed only in a separate, non-canonical repository (`oldrepublicwizard/mod-builds`) that hand-translated guide content into ModSync's format, then was abandoned and has had no commits since 2025-10-31 while the canonical guide kept changing. **Any `C3` comparison against that TOML is only valid for guide content as of that date; it is not ground truth for current or future guide revisions.** `C2` (self-consistency against ModSync's own emitted instruction file) is therefore the only fidelity check with a durable source of truth — `C3` is now explicitly a historical/best-effort cross-check, not a release gate.
- **Correction: natural-language parsing is the primary lever, not a secondary one.** R5 previously assumed structured markdown (`#### Options`, Choose-as-structure) was the main round-trip mechanism and prose was the exception. Confirmed false: 0 occurrences of ModSync's structured conventions anywhere in the guide's history; Choose-style branches are always prose. The natural-language parser (`NaturalLanguageInstructionParser.cs`) is the primary mechanism for the overwhelming majority of real content, not a fallback for edge cases.
- **The guide author will not standardize instruction prose — accepted as a permanent constraint, not a temporary gap.** Confirmed directly: syntax/structure (headings, field names, field order) can be kept consistent on request; free-text instruction wording cannot and will not be, because many steps are genuinely conditional and defy a fixed formula. Any future plan must design for permanent prose variance, not assume a standardization effort will eventually close the gap.
- **Target the current guide format only.** The guide converted to Docusaurus-style admonition fences (`:::note` / `:::warning` wrapping a `:   ` definition-list prose block) in commits on 2025-10-20 (K1) and 2025-10-21 (K2), replacing the older flat `**Installation Instructions:** <prose>` inline convention. Confirmed: the current parser has no handling for the fence/definition-list wrapper. This brainstorm scopes to the current (post-Oct-2025) format; the older inline convention is out of scope unless a real guide snapshot using it is reported in the wild.
- **Named irreducible cases become explicit acceptance examples, not just documented exceptions.** HQ Blasters (an undocumented TSLPatcher-quirk workaround), redrob's cleanlist-driven conditional deletion, and the K2CP+HD-Visas nested conditional are real, well-documented, recurring corpus cases the author herself named as maximally hard. They're strong fixtures precisely because they're real, not synthetic edge cases — see AE6-AE8.
- **Reuse and extend the guide ports.** The universal path is the existing `GuideIngestService`/`GuideEmitService`, extended to carry content sections (preamble/widescreen/Aspyr), parser options/profile, parse traces, and the draft flag — a façade to widen, not a wire-only swap.
- **Draft-on-ingest is one shared option, not two hidden behaviors.** File-open defaults to no prose drafting; paste and `--parse-directions` opt in. Unification preserves this as a single explicit flag in the shared contract, resolving today's per-surface divergence.
- **The corpus source is pinned.** `KOTOR-Community-Portal/mod-builds`, `dev` branch (the active/staging branch the author edits directly), at a specific commit recorded alongside any measured parity numbers — not the frozen `oldrepublicwizard/mod-builds` TOML repo, which is not authoritative for guide content.

## Requirements

Priority markers: `[M]` must-have for the v1 bar, `[E]` enabler whose user-visible outcome is another requirement.

### Roundtrip fidelity

- R1. `[M]` The `C2` emit round-trip is lossless: `IF → emitted guide → IF` preserves every component, every instruction (action type, relative order, `<<modDirectory>>`/`<<kotorDirectory>>` placeholders), and every Choose tree with its branches and per-branch instructions.
- R2. `[M]` Semantic metadata survives `C2`: tiers, installation method, and the presence and meaning of the preamble/widescreen/Aspyr sections. Prose wording and whitespace may normalize.
- R3. `[M]` `C1` ingest completeness: every component and instruction the guide markdown expresses appears in the resulting components; nothing expressed is silently dropped.
- R4. `[E]` `C3` content parity between the markdown-derived component set and a paired full-build instruction file is reported with an explicit exception list (today: three KOTOR1 romance components absent from markdown as of the last cross-check). This check is historical/best-effort only — the paired TOML is frozen as of 2025-10-31 and is not ground truth for guide content after that date. No corpus edits, no invented components.

### Parser coverage

- R5. `[M]` **Natural-language prose parsing is the primary round-trip lever.** Nearly all real install constructs — including Choose-style branches — are expressed as prose, not structure, in the actual corpus. Structured markdown (`#### Options` etc.) is a ModSync-native convention the guide has never used; treat it as a lower-priority secondary path, not the primary one.
- R6. `[M]` The current admonition-fence format (`:::note` / `:::warning` wrapping a `:   ` definition-list prose block, live since 2025-10-20/21) is parsed as a first-class format, not an incidental one — confirmed unhandled today. Wording and phrasing inside the fence follow the same prose parsing rules as any other Directions text.
- R7. `[M]` The draft parser recognizes the phrasing patterns the corpus actually uses, catalogued from real guide text: simple moves ("Move everything from X to your Override"), multi-folder moves, exception/exclusion clauses ("EXCEPT the files for..."), delete-before-move, range notation ("file01 through file04"), conditional clauses ("if using X, ..."), copy/rename, and folder-navigation phrasing. Overwrite-handling and file-list conjunctions ("X, Y, and Z" vs "X and Y") vary between guide sections and must both parse.
- R8. `[M]` Draft coverage is quantified against the corpus as the fraction of prose Directions that produce a correct draft instruction, with a recorded floor. Prose carried through as metadata does not count as a surviving instruction.
- R9. `[M]` Prose the parser cannot interpret surfaces as an explicit, reviewable gap on both surfaces (CLI output and GUI), where the author can see and act on it; it is never silently dropped. This is a change to the draft/ingest result contract, not a regex-only change. Per Key Decisions, this is a permanent design requirement, not a temporary gap expected to close as the guide standardizes.

### Unified pipeline

- R10. `[E]` CLI `convert`/`merge` and GUI file-open/paste ingest and emit guides through one shared path that fixes a single options/behavior contract (including the draft-on/off flag); neither surface calls the markdown parser or draft service directly.
- R11. `[M]` Given the same guide and the same options, CLI and GUI produce the same components and instructions. Draft-on-ingest is part of the shared contract, not a per-surface default.

### Measurement and regression safety

- R12. `[M]` `C1`, `C2`, and the R8 draft-rate floor are proven by tests that run in the default and agent flows — re-enabling or replacing any currently excluded suites.
- R13. `[M]` A regression guards the `C2` invariants (zero dropped or invented across `IF→MD→IF`) and the drafted-instruction-rate floor against the pinned full builds. Percentages are diagnostics; release gates on the absolute `C2` invariants, not on a non-decreasing ratchet.

### Corpus handling

- R14. `[M]` `mod-builds` is read-only: ingest, emit, and all tests read the corpus and write exclusively to temp or output directories.
- R15. `[E]` The corpus is pinned to `KOTOR-Community-Portal/mod-builds`, `dev` branch, at a recorded commit. Tests that require it skip cleanly when it is absent rather than hard-failing, and the flows define what "green" means without a local clone.

## Acceptance Examples

- AE1. **Covers R1.** Given an instruction file with a Choose component, when it is emitted and re-ingested, then the Choose tree and each branch's instructions are identical to the original.
- AE2. **Covers R3, R9.** Given a Directions line the parser cannot map to an action, when the guide is ingested, then the line is reported as an unparsed gap on both CLI and GUI, not omitted.
- AE3. **Covers R8.** Given `KOTOR1_Full`, when it is ingested, then the drafted-instruction rate meets or exceeds the recorded floor, and a component whose prose did not draft counts as zero drafted even though its prose survives as metadata.
- AE4. **Covers R11.** Given one guide loaded via CLI and GUI with drafting off, both produce identical components and instructions; with drafting on, both produce identical drafts.
- AE5. **Covers R4.** Given `KOTOR1_Full`, when the markdown-derived component set is compared to the frozen instruction-file set, then the diff equals exactly the documented exception list, with no other missing or extra components.
- AE6. **Covers R7.** Given the HQ Blasters entry's Directions (delete `keblastore.utm` from TSLPatchdata before running the patcher to force an intentional single error, then rename `w_ionrfl_04.*` files to `w_ionrfl_004.*` post-install, then delete several more files), when the guide is ingested, then either the full instruction sequence drafts correctly, or the undraftable portion surfaces as an explicit reviewable gap per R9 — it is never silently dropped or partially applied without signal.
- AE7. **Covers R7.** Given a Directions entry conditioned on redrob's cleanlist (per-mod file deletion driven by an externally maintained `cleanlist_k1.txt`, not deducible from the guide text alone), when the guide is ingested, then the conditional nature of the deletion is preserved as metadata or surfaces as an explicit gap, never silently resolved to a wrong fixed file list.
- AE8. **Covers R7.** Given the K2CP mod's Directions with a nested conditional ("delete these files before moving to override; if also using HD Visas, additionally delete these three more"), when the guide is ingested, then both the unconditional and the HD-Visas-conditional deletions are captured as distinct instructions, not merged into one unconditional step.

## Success Criteria

- A baseline draft-rate floor and the `C3` exception list are measured from a live corpus read (pinned commit, `KOTOR-Community-Portal/mod-builds` `dev`) and recorded in the repository before any regression ratchet takes effect.
- The `C2` lossless invariant runs green in the default and agent test flows, not only in a scheduled or excluded set.
- AE6-AE8 pass or produce an explicit, reviewable gap report — never a silent partial application.
- A reviewer can confirm CLI and GUI share the ingest/emit path and options contract from the test contract alone, without tracing runtime calls.

## Scope Boundaries

**Deferred for later:**

- Byte-identical markdown reproduction (exact whitespace and prose shape). Semantic `C2` parity is the v1 bar.
- Markdown-only installs without the instruction-file merge; the two-source merge remains the installable path.
- Generalization beyond the canonical corpus. v1 measures fidelity on `KOTOR1_Full`/`KOTOR2_Full`; a second author's guide importing cleanly is deferred, not assumed.
- Parsing the pre-2025-10-20 bold-inline-field guide format. Confirmed obsolete on the canonical `dev` branch; revisit only if a real snapshot using it is reported.
- Further validation/install pipeline unification, which shipped separately.

**Outside this product's identity:**

- Turning `mod-builds` into a writable store or authoring target. It is an external corpus ModSync consumes.
- A mandatory bespoke guide markup language. The guide author has explicitly and repeatedly declined to standardize instruction prose; optional authoring conventions she voluntarily adopts to disambiguate a section are not ruled out, but are not to be requested or assumed.

## Dependencies / Assumptions

- A `./mod-builds` clone (pinned to `KOTOR-Community-Portal/mod-builds`, `dev` branch) must be present for fidelity work and roundtrip tests.
- ModSync's own emitted instruction file is the only durable source of truth for `C2`. The historical hand-built TOML (`oldrepublicwizard/mod-builds`, frozen 2025-10-31) is a useful one-time cross-check for `C3` but is not authoritative and will drift further from the canonical guide over time.
- Per the committed parity test, the three missing KOTOR1 romance components are a known source divergence — the markdown source lacks them — not a loader bug, as of the last cross-check against the frozen TOML.
- The guide author has confirmed she will keep syntax/structure consistent on request (heading levels, field names, field order) but will not standardize instruction wording; permanent prose variance is a design constraint, not a temporary gap.

## Outstanding Questions

**Resolved by this update:**

- ~~Which upstream is canonical?~~ Confirmed: `KOTOR-Community-Portal/mod-builds`, `dev` branch is canonical and active; `main` is the released-stable snapshot. `oldrepublicwizard/mod-builds` (the TOML translation repo) is a separate, non-canonical, now-abandoned project — not an authoritative source for guide content.
- ~~Where do Choose and CleanList appear in the corpus — as structure or as prose?~~ Confirmed: always prose. No structured Choose/Options block has ever appeared in the guide's history.

**Resolve before planning:**

- Does the master goal's "back to md without discrepancies" accept semantic `C2` parity with normalized prose and whitespace, or require an author-visible discrepancy budget? The latter materially expands scope.

**Deferred to planning:**

- The exact shape of the shared options contract and the gap-report surface on each of CLI and GUI.
- Whether AE6-AE8 are pursued as fully automated drafting, or as "detect and flag reviewable gap" only — the guide author's own position (see Dependencies) is that some of these may never be fully automatable and per-mod special-casing will remain necessary indefinitely.

## Sources / Research

- `src/ModSync.Tests/MarkdownTomlParityTests.cs:72-90` — `Kotor1Full_SourceFiles_CurrentlyContainKnownSemanticDivergences`: 186/189 and the three source-missing romance components.
- `src/ModSync.Tests/ModSync.Tests.csproj` — `DocumentationRoundTripTests`, `MarkdownImportTests`, `MarkdownFileTests` compile-exclusion status at time of original writing (since re-enabled on a separate branch).
- `src/ModSync.Core/Parsing/NaturalLanguageInstructionParser.cs` — supported prose action patterns; no Choose/CleanList drafting yet.
- `src/ModSync.Core/Parsing/MarkdownParser.cs` — structured `#### Options` parsing into `component.Options` (confirmed unused by the real corpus); no handling for the `:::note`/`:::warning` admonition-fence format the guide has used since 2025-10-20.
- `src/ModSync.Core/Ports/Guides/IGuideServices.cs`, `src/ModSync.Core/Ports/Guides/GuideServices.cs` — the intended shared ingest/emit ports (used only by tests today).
- `src/ModSync.Core/CLI/ModBuildConverter.cs` — CLI ingest calling `MarkdownParser`/`DraftInstructionService` directly.
- `docs/brainstorms/2026-05-29-mod-builds-pipeline-requirements.md` — a completed foundation this bet builds on.
- `STRATEGY.md` — guide ingestion and guide emission tracks; guide-import-fidelity metric.
- **Repo history survey (2026-07-30):** `KOTOR-Community-Portal/mod-builds`, `dev` branch, 850 commits. Format-era boundary confirmed at commit `f1c5ca546a94458873805a9959042a12020d1a4f` (2025-10-20, K1) and `2871947f421d4b9f295efeae6a3b73cff44d51c9` (2025-10-21, K2) — conversion to `:::note`/`:::warning` admonition fences. Zero occurrences of `#### Instructions`, `Guid:`, or structured Options blocks across all history. `oldrepublicwizard/mod-builds` (formerly `th3w1zard1/mod-builds`) confirmed as a separate, non-fork, TOML-only repository, last commit 2025-10-31, since abandoned.
- **Prior author correspondence (Discord DM export, 2023-05 through 2026, reviewed 2026-07-30):** guide field schema (Name/Description/Directions/Dependencies/Restrictions/InstallAfter/InstallBefore/Options), the author's explicit refusal to standardize instruction prose beyond syntax/structure, the HQ Blasters/redrob-cleanlist/K2CP+HD-Visas cases named directly as maximally hard, and the TOML-translation effort's abandonment and removal from active coordination.
