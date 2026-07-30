# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Quick Commands

```bash
# Build
dotnet build ModSync.sln --configuration Debug

# Run GUI
dotnet run --project src/ModSync.GUI/ModSync.csproj --configuration Debug --framework net9.0

# Run non-long-running tests
dotnet test src/ModSync.Tests/ModSync.Tests.csproj --filter "FullyQualifiedName!~LongRunning" --configuration Debug

# Run a single test
dotnet test src/ModSync.Tests/ModSync.Tests.csproj --filter "FullyQualifiedName=ModSync.Tests.<TestName>" --configuration Debug

# Lint/format verification
dotnet format ModSync.sln --verify-no-changes

# Run headless Avalonia smoke tests
./scripts/agents/run_headless_tests.sh --filter "FullyQualifiedName~Headless|FullyQualifiedName~GuiSmoke"

# Validate using CLI
./scripts/agents/cli_validate.sh

# Full build and validation tests (requires mod-builds cloned)
./scripts/agents/test_pr110_validation.sh
```

## Architecture Overview

ModSync is a cross-platform mod installer for Star Wars: KOTOR, built with .NET 9 and AvaloniaUI. The solution contains three projects:

### `src/ModSync.Core` — Runtime Engine
The core library containing:

- **Instruction Model**: `Instruction`, `ModComponent`, `Option` classes. Supports TOML, Markdown, YAML, and JSON formats.
- **Serialization**: `FileLoadingService` auto-detects format and deserializes. `ModComponentSerializationService` handles component resolution.
- **Dependency Resolution**: Parses `InstallBefore`, `InstallAfter`, `Dependencies`, and `Restrictions` to compute install order and validate compatibility.
- **Path Handling**: All instruction paths must use placeholders (`<<modDirectory>>`, `<<kotorDirectory>>`). Never absolute paths.
- **Virtual File System**: `VirtualFileSystemProvider` simulates file mutations during validation and dry-run without touching disk.
- **Installation**: `InstallationService` orchestrates the real install, bootstraps Python environment, integrates HoloPatcher.
- **Validation & Analysis**: Dry-run and pre-install checks use the virtual file system, not real filesystem.

### `src/ModSync.GUI` — Avalonia Desktop Shell
The desktop application:

- **MainWindow**: Composes GUI services and routes to install wizard.
- **GUI Services** (`Services/`): Focused service classes handling specific concerns (settings, menus, downloads, validation UI, etc.).
- **Install Wizard** (`Dialogs/WizardPages/`): Multi-step wizard flow (LoadInstruction → Welcome → Preamble? → ModDirectory → GameDirectory → AspyrNotice? → ModSelection → DownloadsExplain → Validate → InstallStart → Installing → BaseInstallComplete → Finished + optional widescreen pages).
- **Preload Args**: Supports `--instructionFile`, `--kotorPath`, `--modDirectory` to auto-load state (preferred for local testing over file-pickers).

### `src/ModSync.Tests` — Unified Test Project
Single test project (do not create additional test projects):

- **Headless Avalonia**: `GuiSmokeHeadlessTests`, `WizardFlowHeadlessTests`, `ControlsHeadlessTests`, etc. use `Avalonia.Headless.XUnit` without display.
- **Core Logic**: `GuideIngestionTests`, `MarkdownAdmonitionFenceTests`, `MarkdownFileTests`, `ModSync.Tests.C2RoundtripInvariantTests`, etc.
- **Full-build**: `RealGuide_*` tests validate against actual mod-builds corpus (at `./mod-builds`).
- **Test Naming**: Tests > 2 minutes use `LongRunning` suffix; excluded from standard runs.

### `vendor/` and `scripts/`

- **`vendor/KPatcher`**: Canonical upstream patcher source (git submodule). Local `src/HoloPatcher*` trees are legacy; don't treat as primary.
- **`scripts/agents/`**: Helper scripts for agent workflows:
  - `run_headless_tests.sh` — Run Avalonia headless tests.
  - `cli_validate.sh` — Validate instruction files via CLI.
  - `launch_gui_desktop.sh` — Launch GUI with preload args.
  - `create_template_kotor_install.sh` — Create fake KOTOR install.
  - `ensure_linux_holopatcher.sh` — Link bundled Linux HoloPatcher.

## Key Architecture Patterns

### Path Sandboxing (CRITICAL)
All instruction definitions must use placeholders:
- `<<modDirectory>>\filename.zip` or `<<kotorDirectory>>/Override`
- Never absolute paths (e.g., `C:\Windows`, `/etc`) in instruction files
- Internal code resolves placeholders at install/dry-run time
- This prevents malicious or buggy TOML/YAML/Markdown from targeting system directories

### Virtual File System (VFS)
For validation and dry-run analysis:
- `VirtualFileSystemProvider` tracks file mutations (extract, move, delete, rename) without touching disk
- Initialize with `InitializeFromRealFileSystemAsync()` to load existing files first
- Use VFS for all validation/analysis logic; never use `RealFileSystemProvider` for those flows
- Real installs use the real filesystem but still coordinate through the instruction model

### Instruction Serialization & Loading
- `FileLoadingService.IngestMarkdown()` / `IngestToml()` / etc. auto-detect format
- `ModComponentSerializationService` deserializes and resolves dependency order
- Result is an `IReadOnlyList<ModComponent>` ready for GUI or install
- Supports both file paths and raw content (Markdown paste, TOML content, etc.)

### Test Duration Classification
Tests taking >2 minutes should use `LongRunning` suffix and are excluded from normal runs:

```bash
# Quick check: run single test with 120s timeout to classify
dotnet test src/ModSync.Tests/ModSync.Tests.csproj \
  --filter "FullyQualifiedName=ModSync.Tests.<TestName>" \
  --configuration Debug
# If it exceeds ~120s, rename to `<Name>_LongRunning`
```

### Avalonia XAML Conventions
- **Do NOT hardcode** font, color, or style properties on controls
- Rely on implicit theme defaults for consistency across light/dark modes
- Use `ZIndex` sparingly or not at all (AvaloniaUI handles z-order differently than WPF)

### Headless vs. Desktop Testing
- **Headless** (`GuiSmokeHeadlessTests`, etc.): Verify control presence, events, layout constraints without a display
  - Faster, CI-friendly, runs on cloud agents
  - Use `Avalonia.Headless.XUnit` with `UseHeadlessDrawing = true`
- **Desktop** (manual/local): Required for visual polish, native file-pickers, full-build installs
  - Requires X11 display and preload args
  - Prefer helper scripts and CLI args over direct xdotool/xwininfo automation
  - Full-build tests expect `mod-builds` cloned at `./mod-builds`

### Known Issues & Gotchas
- `CrossPlatformFileWatcherTests` fail in cloud VMs due to inotify limitations; expected and harmless
- Some xUnit-based UI tests may fail headlessly depending on Avalonia support; pre-existing
- Linux GUI validation may require `scripts/agents/ensure_linux_holopatcher.sh` if HoloPatcher not linked
- `DISPLAY=:1` environment variable required for desktop runs on headless systems

## Routing & Task Selection

### Use Headless .NET Workflow For:
- Changes to `src/ModSync.Core`, `src/ModSync.Tests`, repo-root config/docs
- Build, test, lint, format changes
- Any work where file paths already answer "where should I inspect first?"

**Start:** Check out the Quick Commands above and pick the right `dotnet build/test` command.

### Use GUI Workflow For:
- Changes to `src/ModSync.GUI`, install wizard pages, `scripts/agents/`
- Full-build validation against `mod-builds`
- Manual desktop testing

**Start:** Read `AGENTS.md`, `docs/local_desktop_agent_runbook.md`, and `.cursor/skills/local_desktop_gui_testing/SKILL.md`.

### Handle `telemetry-auth/` Separately
Treat as a Python/Docker sidecar with its own README, CONTRIBUTING, and deployment docs. Do not route through the Avalonia/.NET guidance.

## Testing Conventions

### Test Naming
| Suffix | Meaning | Duration |
|---|---|---|
| `LongRunning` | Long-running local test | >2 minutes |
| _(none)_ | Regular test | <2 minutes |

### Running Tests
```bash
# All non-long-running
dotnet test src/ModSync.Tests/ModSync.Tests.csproj --filter "FullyQualifiedName!~LongRunning" --configuration Debug

# Specific Avalonia headless filters
FullyQualifiedName~GuiSmokeHeadlessTests
FullyQualifiedName~WizardFlowHeadlessTests
FullyQualifiedName~ControlsHeadlessTests
FullyQualifiedName~MainWindowHeadlessTests

# All headless/GuiSmoke combined
FullyQualifiedName~Headless|FullyQualifiedName~GuiSmoke
```

## Installation Wizard Flow

Pages are created in this order (see `AGENTS.md` for full control map):

1. `LoadInstructionPage`
2. `WelcomePage`
3. optional `PreamblePage`
4. `ModDirectoryPage`
5. `GameDirectoryPage`
6. optional `AspyrNoticePage`
7. `ModSelectionPage`
8. `DownloadsExplainPage`
9. `ValidatePage`
10. `InstallStartPage`
11. `InstallingPage`
12. `BaseInstallCompletePage`
13. `FinishedPage`

Widescreen-only pages added dynamically after base install.

## Validation & Dry-Run

Install wizard validation is documented in `docs/knowledgebase/gui-validation-surfaces.md`:

- `ValidatePage` displays stage cards, validation logs, error/warning/passed badges
- Regression test: `./scripts/agents/test_pr110_validation.sh`
- All validation logic uses `VirtualFileSystemProvider` to simulate file state

## Local Desktop Launch Example

```bash
# Ensure mod-builds is cloned
git clone -b dev https://github.com/KOTOR-Community-Portal/mod-builds ./mod-builds

# Create template install dirs
./scripts/agents/create_template_kotor_install.sh

# Ensure Linux HoloPatcher is linked
./scripts/agents/ensure_linux_holopatcher.sh

# Launch with preload args
./scripts/agents/launch_gui_desktop.sh \
  --instruction-file ./mod-builds/TOMLs/KOTOR1_Full.toml \
  --kotor-dir ./tmp/kotor_template \
  --mod-dir ./tmp/mod_downloads

# Then in the wizard: SelectAll → Fetch Downloads → Validate → Install
```

## Conventions & Patterns

- **Instruction Format Support**: TOML (primary), Markdown (with admonition fences), YAML, JSON
- **Dependency Fields**: `InstallBefore`, `InstallAfter`, `Dependencies`, `Restrictions` (parsed by resolver)
- **Serialization**: Auto-detect on load; support round-trip (read & write)
- **Component Model**: `ModComponent` with `Guid`, name, type (Mod/Preset), options, instructions
- **Option Instructions**: `Instruction` with action (Copy, Extract, Move, Delete, etc.), conditions, source/dest paths

## References

- **Full agent runbook**: `docs/local_desktop_agent_runbook.md`
- **Knowledgebase**: `docs/knowledgebase/README.md` (canonical agent index)
- **Validation surfaces**: `docs/knowledgebase/gui-validation-surfaces.md`
- **Agent briefing**: `AGENTS.md` (in-flight PRs, landing queue, autonomous defaults)
- **Copilot routing**: `.github/copilot-instructions.md` (source-of-truth trio with AGENTS.md and .cursorrules)
- **Cursor rules**: `.cursorrules` (XAML, path sandboxing, VFS, test naming, Avalonia gotchas)
- **Skills**: `.cursor/skills/local_desktop_gui_testing/SKILL.md`, `.cursor/skills/full_build_install_validation/SKILL.md`

## Environment Setup

- **.NET 9 SDK** (ensure `DOTNET_ROOT` and `PATH` include it)
- **PowerShell** (`pwsh`) for test classification wrapper
- **X11 libraries** for AvaloniaUI (see README.md for full list)
- **Git submodules** initialized: `src/AvRichTextBox`, `src/RtfDomParserAvalonia`
- **Optional**: `vendor/KPatcher` submodule (not required for main build)

For local desktop validation:
- **`DISPLAY=:1`** environment variable (if on headless VM)
- **`./mod-builds`** cloned at repo root (for full-build tests)
- **Helper scripts** in `scripts/agents/` (preferred over ad hoc commands)
