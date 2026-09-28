# Install workspace inventory — 2026-08-23

This inventory records the evidence and cleanup decisions made while resuming the unified install parity mission. It is intentionally separate from the historical progress narratives; ledgers and semantic reports remain the authority for completed installs.

## Storage policy

- `/home` is the Fedora NVMe (`/dev/nvme0n1p3`, Btrfs, SSD). All live trees, extracts, builds, caches, checkpoints, diffs, and reports belong under `/home/brunner56/modsync-hot`.
- `/run/media/brunner56/MyBook` is a rotational ext4 cold store. Existing archives, vanilla references, guide sources, and cold oracles are read-only inputs during high-I/O work.
- `/run/media/brunner56/MassiveHDD` is a nearly-full rotational NTFS cold store and is not used by this mission.
- Dedicated NVMe paths are `modsync-hot/work/{tmp,extract,build,cache,reports}`. Commands must set their temporary/build/cache output there when the tool does not already write inside a live NVMe tree.

## Protected evidence

| Path | Classification | Reason |
|---|---|---|
| `modsync-hot/K1_manual` | protected oracle | Completed manual K1 tree; ledger reaches step 201. |
| `modsync-hot/k1_ledger.jsonl` | protected authority | Per-step K1 manual evidence. |
| `modsync-hot/K1_manual_holo` | active lane | HoloPatcher differential lane at recorded step 142. |
| `modsync-hot/K1_manual_ody` | active lane | OdyPatcher differential lane at recorded step 142. |
| `modsync-hot/k1_{holo,ody}_ledger.jsonl` | protected authority | Both ledgers record step 142 success. |
| `modsync-hot/k1_{holo,ody}_rollback` | active checkpoints | Needed until the differential lane passes step 142 parity and resumes safely. |
| `modsync-hot/K1_auto_handrepaired` | temporary diagnostic | Invalid acceptance tree, but retained until its unique repair cases are represented by regression tests. |
| `modsync-hot/K2_manual` | protected candidate oracle | Contains a populated `steamassets` tree; must be reconciled with the completed fresh-work ledger/cold oracle before acceptance. |
| `modsync-hot/K2_auto` | protected vanilla candidate | Baseline candidate for a fresh direct-Markdown K2 run; verify against vanilla hashes before use. |
| `modsync-hot/k1_holo_ody_discrepancies` and root `_diag_*` resources | temporary diagnostic | Retain until packing-only versus substantive patcher differences are captured by semantic comparison tests. |
| `modbuild_oracles`, `kotor_mod_archives`, `kotor_vanilla_refs` on MyBook | cold inputs | Do not mutate or use as extraction/build destinations. |
| `odynsscomp` and `OdyPatcher` repositories | active source work | Dirty implementation work; not stale scaffolds. |

## Proven stale or reconstructible

| Path | Decision | Replacement evidence |
|---|---|---|
| `modsync-hot/K1_auto` | remove and reseed from verified vanilla | Partial/invalid acceptance tree; current mission requires a fresh one-shot run. |
| `modsync-hot/k1_auto_extract` | remove | Extract cache belongs to the invalid K1 automatic attempt and can be regenerated from verified archives. |
| `modsync-hot/k1extract` | remove | Completed manual K1 has a live oracle, cold oracle, and ledger; the old extract cache is reconstructible. |
| `modsync-hot/k1_rollback` | remove | Completed manual K1 rollback history is superseded by the final oracle, cold oracle, and 202-entry ledger. Active Holo/Ody rollback trees remain protected. |
| `K1_auto_handrepaired/.modsync/snapshot_staging` | remove | Abandoned duplicate staging snapshot inside a non-acceptance tree. |
| `K1_auto_handrepaired/.modsync/last_good_backup.zip` | remove | Duplicate archive of an invalid diagnostic tree; small `install_session.json` and the live diagnostic result remain. |
| `MyBook/tmp/KPatcher_OverrideType_2a547595a403443186b61249c367e956` | remove after fixture search | June 12 temporary fixture. No active process owns it; retain only if a source/test references its exact content. |
| `MyBook/tmp/KPatcher_Settings_de6ebbeca1604b1c8013b4e05d64f43d` | remove after fixture search | June 12 temporary fixture. No active process owns it; retain only if a source/test references its exact content. |
| `modsync-hot/k1_parallel_driver.pid` | remove before resumption | PID is stale and no ModSync/Holo/Ody driver process is active. Logs and ledgers remain. |

Direct removal from NVMe is not recoverable through the desktop trash. The deleted items above are reconstructible or duplicate data; protected ledgers, live/cold oracles, active lane checkpoints, session metadata, source repositories, and diagnostic cases remain.

