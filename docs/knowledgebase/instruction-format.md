# Instruction file format

`[REPO]` How mod instruction files are structured, which paths are allowed, and what each action type does.

Sources: `.cursorrules`, `Instruction.cs`, `ModComponent.cs`, `FileLoadingService.cs`, `README.md`.

## Supported file formats

Instruction files load by extension (`FileLoadingService.cs`):

| Extension | Format |
|-----------|--------|
| `.toml` | TOML (primary for mod-builds) |
| `.md` | Markdown |
| `.yaml`, `.yml` | YAML |
| `.json` | JSON |
| `.xml` | XML |

Agents working on **mod-builds** full lists typically use `./mod-builds/TOMLs/KOTOR1_Full.toml` and `KOTOR2_Full.toml`.

## Path sandbox (required)

`[REPO]` Per `.cursorrules`, instruction **Source** and **Destination** paths must:

- **Start with** `<<modDirectory>>` or `<<gameDirectory>>` (legacy alias: `<<kotorDirectory>>`)
- **Never** use absolute paths (e.g. `C:\Windows`, `/etc`) in instruction definitions
- Use wildcards (`*`, `?`) only after the placeholder prefix

**Exception:** the `Choose` action may differ; all other actions require placeholders.

At install or dry-run time, the engine replaces placeholders with real paths via `SetRealPaths()` and resolves wildcards through `EnumerateFilesWithWildcards()` on the active file-system provider.

## File structure (conceptual)

An instruction file contains:

1. **Global / preamble content** (optional) — loaded into `MainConfig` (e.g. preamble for wizard)
2. **Mod components** — one entry per mod, each with metadata, graph fields, and an ordered **Instructions** list

Relationship fields on each component (`README.md`, `ModComponent.cs`):

| Field | Meaning |
|-------|---------|
| `Dependencies` | Other mods (by GUID) that must be installed first |
| `Restrictions` | Mods that cannot be installed together with this one |
| `InstallBefore` | This mod must install before the listed GUIDs |
| `InstallAfter` | This mod must install after the listed GUIDs |

For narrative examples of graph fields, see `README.md` or the [pastebin explanation](https://pastebin.com/7gML3zCJ).

## Action types

`[REPO]` `Instruction.ActionType` (`Instruction.cs`):

| Action | Typical use |
|--------|-------------|
| `Extract` | Unpack archive from `<<modDirectory>>` into game or staging |
| `Execute` | Run external command with `Arguments` |
| `Patcher` | Run TSLPatcher / HoloPatcher against `<<kotorDirectory>>` |
| `Move` | Move files between placeholder paths |
| `Copy` | Copy files |
| `Rename` | Rename files |
| `Delete` | Remove files from game or mod tree |
| `DelDuplicate` | Remove duplicate files by content |
| `Choose` | Present options; branches to option-specific instruction lists |
| `Run` | Run a script or binary |
| `CleanList` | Apply clean-list file processing |
| `Unset` | Invalid / uninitialized |

Each instruction row also carries **Source**, **Destination** (when applicable), **Arguments**, **Overwrite**, and optional per-instruction **Dependencies** / **Restrictions**.

### DelDuplicate final sweep (not wired; follow-up)

`[REPO]` `DelDuplicate` runs once, at its place in guide order, and only sees duplicates that exist at that moment. `Instruction.RunFinalDuplicateSweepAsync` can re-apply a guide's DelDuplicate purges after the whole guide installs, but the install loop does **not** call it. An unconditional end-of-install sweep deletes game-directory files outside guide order (see AGENTS.md, "Install order is load-bearing"), has no VFS dry-run equivalent, so validation would not match install, and can remove a file that a later mod ships on purpose.

`[OPEN]` Before the sweep is wired in, it must:

1. run the same sweep in the VFS dry-run, so validation matches install;
2. only remove files of the extension that the owning component's own DelDuplicate purged, and never a file that a later component deliberately ships (for example, respect `Overwrite`/ownership records from the install plan);
3. never run after a failure break or a fail-closed stop.

Until then, `RealWorldInstallationScenariosTests.Install_FinalDuplicateSweep_CatchesLateReintroducedDuplicate` (from `feat/aio-consolidation`) fails, as it already did on that branch tip. It is not skipped or weakened.

## Minimal TOML example

Placeholder-only paths; not a complete real mod:

```toml
[[thisMod]]
Name = "Example Mod"
Guid = "00000000-0000-0000-0000-000000000001"
IsSelected = true
Tier = "1 - Essential"
Category = ["Graphics"]

[[thisMod.Instructions]]
Action = "Extract"
Source = "<<modDirectory>>/ExampleMod.zip"

[[thisMod.Instructions]]
Action = "Move"
Source = "<<modDirectory>>/ExampleMod/Override/*"
Destination = "<<kotorDirectory>>/Override"
Overwrite = true
```

`[OPEN]` Exact TOML key names and nesting follow `ModComponentSerializationService` conventions; when authoring, mirror existing entries in `./mod-builds` or test fixtures rather than inventing new shapes.

## Authoring rules for agents

1. **Never** write absolute paths in Source/Destination.
2. Prefer copying patterns from an existing component in the same instruction file.
3. After edits, run validation with game + mod directories set:

   ```bash
   ./scripts/agents/cli_validate.sh --input path.toml \
     --game-dir ./tmp/kotor_template --source-dir ./tmp/mod_downloads --full
   ```

4. Use [vfs-vs-real-fs.md](vfs-vs-real-fs.md) — validation dry-run simulates on **VFS**, not by mutating disk.

## Related

- [mod-component-model.md](mod-component-model.md) — all component fields
- [validation-pipeline.md](validation-pipeline.md) — what validate checks
- [core-cli-reference.md](core-cli-reference.md) — `validate`, `install`, `convert`, `merge`
- `.cursorrules` — PATH SANDBOXING & VIRTUAL FILE SYSTEM
