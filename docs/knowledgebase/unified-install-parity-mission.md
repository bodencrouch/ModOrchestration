# Unified install parity mission

This is the authoritative execution prompt for the current ModSync install-parity mission. It consolidates the current user direction, prior Cursor/Claude/Codex sessions, install ledgers, progress documents, repository plans, and the K1 one-shot parity specification. When an older instruction conflicts with this document, follow the supersession rules below.

## Mission prompt

> Resume and finish ModSync as a fail-closed, guide-faithful KOTOR installer. Treat `mod-builds/content/k1/full.md` and `mod-builds/content/k2/full.md` as the current K1 and K2 installation specifications. Make every instruction expressed by those guides parseable, serializable, validatable, and executable without manual repair or a generated/prebuilt full-build TOML standing in for the guide. Preserve guide order exactly.
>
> Build one application-level preparation, validation, and installation pipeline in `ModSync.Core`. The CLI, Avalonia install wizard, legacy GUI actions, single-component GUI tools, widescreen phases, dry-run/VFS tools, and any future frontend must call that same pipeline. Frontends may supply paths, selections, policy decisions, progress callbacks, cancellation, and presentation, but must not implement their own archive resolution, instruction generation, validation, checkpoint, patcher, or install loops. Identical inputs and policy must produce the same ordered plan fingerprint and the same installed resource content through CLI and GUI.
>
> Run reference work fail-closed. Before every guide step, retain a verified recoverable checkpoint. If step N fails, stop, restore the checkpoint from before N, prove restoration, fix the root cause in reusable parser/pipeline/patcher code, rerun N, then continue N+1 onward in guide order. Never skip a required step, append a failed step later, hand-repair the output, accept a silent patcher no-op, or use `--best-effort` for parity/reference runs. Missing-target delete instructions explicitly allowed by a guide are non-errors.
>
> Prove correctness with lossless structured round trips, parser fixtures, focused tests, GUI/CLI plan parity tests, patcher log verification, semantic resource comparisons, and fresh installations. Markdown-to-TOML-to-Markdown and TOML-to-Markdown-to-TOML must preserve all executable meaning. Current guide files govern current installs; historical revisions form a compatibility corpus for syntax and round-trip regression coverage. Support documents such as FAQ, platform, aside, and index pages must be classified correctly and must not be misread as install guides.
>
> Use the Fedora NVMe for all high-I/O work. Keep extracts, builds, caches, temporary files, checkpoints, live install trees, diffs, and semantic comparison indexes under `/home/brunner56/modsync-hot` (or `/tmp` only for small short-lived work). Treat `/run/media/brunner56/MyBook` and `/run/media/brunner56/MassiveHDD` as slow cold storage: read archives, vanilla references, guide sources, and cold oracles only when needed, and never extract or build there. Do not redownload archives that already exist and pass integrity checks.
>
> Complete four independently valid references: K1 manual, K1 one-shot automatic, K2 manual, and K2 one-shot automatic. The automatic trees must start from verified vanilla and consume their respective `full.md` directly in one fail-closed run. Compare automatic results to the manual oracle semantically: loose resources by normalized game-resource identity, modules/ERFs by contained resources, and TLK data by string/reference meaning. Byte differences caused only by valid container packing are acceptable only after semantic equality is proved. Resolve every substantive discrepancy in the shared implementation and replay from the correct checkpoint.
>
> Complete HoloPatcher/OdyPatcher and `odynsscomp` parity needed by the guides. Verify patcher success from its own logs and resulting resources, not process exit code alone. Build integration binaries reproducibly from their source repositories; do not treat an arbitrary copied binary as authoritative. Keep compiler byte-equality and patcher semantic differential fixtures. Do not launch installers on the user's Plasma desktop; use true CLI/headless execution or a sandboxed display. Never interact with or bypass a CAPTCHA; record that specific archive as awaiting user action and continue only where doing so does not violate guide order.
>
> Audit all supplied workspaces, transcripts, plans, scratch trees, ledgers, snapshots, and temporary directories. Preserve unique evidence and active checkpoints. Migrate useful failure cases into tests or documentation. Remove only artifacts proven obsolete, reconstructible, inactive, and no longer evidentiary; prefer recoverable trash for material deletions. Keep the dirty in-progress branch intact and do not discard prior work. Finish with focused tests, the non-long-running suite, Avalonia headless GUI coverage, full guide parsing/round-trip checks, fresh K1/K2 installs, semantic parity reports, and documentation that states the verified final status without overstating it.

## Current authoritative state

- Branch: `feat/aio-consolidation`, with substantial in-progress Core and test changes that belong to this mission and must be preserved.
- K1 manual: complete through ledger step 201 and currently the K1 oracle.
- K1 Holo/Ody differential lanes: both last recorded successful step is 142; the recorded driver PID is stale. Resume only after proving both lane states and their pre-step checkpoints agree semantically.
- K1 automatic: partial/invalid diagnostic output. Reset from verified vanilla before the acceptance run.
- K2 manual: complete according to the 163-entry fresh-work ledger and cold oracle. Seed an NVMe oracle from that verified result before high-I/O comparisons.
- K2 automatic: vanilla baseline only. The former merged-TOML/manual-repair run is diagnostic history, not acceptance evidence.
- `K1_auto_handrepaired`: diagnostic evidence only. Retire it after every unique parser gap it demonstrates is represented by a regression test.
- `odynsscomp` and `OdyPatcher`: active dirty source workspaces, not disposable scaffolds.

## Supersession rules

These rules reconcile useful older work without silently deleting its evidence:

1. A current `content/k1/full.md` or `content/k2/full.md` is the authority for a current reference install. Prebuilt `KOTOR*_Full.toml`, generated merged TOML, and old synthetic full-build documents are fixtures or diagnostics only.
2. Old `--best-effort`, continue-after-error, and manual-supplement instructions are superseded for reference/parity runs because they can invert install order or conceal a missing operation. Best-effort may remain an explicitly non-reference feature.
3. Old instructions targeting live Steam/MyBook game directories are superseded by isolated NVMe install trees until final parity is proved. Real game installs remain untouched during development and acceptance.
4. Old completion claims based on exit code, file counts, or a converged repaired tree are superseded by patcher-log evidence and semantic parity.
5. Old instructions forbidding helper scripts applied to an auditable browser/download session. They do not prohibit reusable production code, repository test wrappers, or deterministic validation tools. Individual browser download actions remain transcript-auditable.
6. Historical guide revisions are not installation authority, but their distinct syntax remains a parser and round-trip compatibility corpus.
7. Existing plans marked active are not automatically current. Their unique requirements must be mapped into this mission or explicitly classified as unrelated before their status is changed or they are archived.

## Required shared pipeline boundary

The single Core pipeline owns this sequence:

1. classify and load Markdown or structured instruction input;
2. infer the target game and normalize paths/policies;
3. resolve existing archives and generate local instructions where required;
4. parse natural-language guide operations while preserving source order and provenance;
5. apply component selection without changing relative guide order;
6. build one immutable ordered install plan and fingerprint it;
7. validate that exact plan against the same VFS semantics used by execution;
8. create and verify checkpoints under the selected fail-closed policy;
9. execute extraction, file operations, patchers, compiler work, and guide-specific phases;
10. verify patcher logs, outputs, and final semantic result;
11. return structured progress, issues, recovery state, and outcome to the frontend.

No GUI code may duplicate those responsibilities. GUI code is limited to adapters and presentation. A GUI-only feature that changes installation behavior must first be represented as a Core pipeline policy or operation and exposed to the CLI.

## Acceptance gates

The mission is complete only when all gates pass:

- Every current K1/K2 guide installation form either maps to an executable typed operation or is explicitly classified as non-install prose.
- Structured conversion preserves executable meaning in both round-trip directions; historical syntax fixtures pass.
- CLI and each GUI installation surface produce the same ordered plan fingerprint for the same request and route execution through the same Core service.
- Injected failures prove checkpoint restoration and forbid continuation or late backfill.
- Patcher success requires meaningful logged operations and verified outputs; silent no-ops fail.
- K1 manual versus fresh K1 one-shot automatic has zero substantive semantic discrepancies.
- K2 manual versus fresh K2 one-shot automatic has zero substantive semantic discrepancies.
- Required HoloPatcher versus OdyPatcher outcomes and `odynsscomp` compiler outputs pass their semantic/byte-exact contracts, proved by additional isolated auto installs on both K1 and K2 (`k1_auto_holo`/`k1_auto_ody`, `k2_auto_holo`/`k2_auto_ody`) after CLI==manual and GUI==CLI. Do not treat the `K1_manual_holo`/`K1_manual_ody` step-142 lanes as that proof.
- High-I/O artifacts are located on NVMe; cold-storage directories contain no new extraction/build/cache churn.
- Stale artifacts are removed only after an inventory records why they are obsolete and what evidence replaced them.
- Focused tests, full non-long-running tests, and Avalonia headless GUI tests pass, with any environment-only exception named and independently bounded.
- Progress documents and knowledgebase pages describe these verified outcomes and distinguish completed reference work from diagnostic history.

