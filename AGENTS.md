# ModSync agent guide

This file is the short entry point for agents working on the Avalonia desktop GUI, the install wizard, or full-build flows against `mod-builds`. For step-by-step commands and tooling, read `docs/local_desktop_agent_runbook.md` next.

## Purpose

Use this repository's local-agent assets whenever a task touches:

- the Avalonia desktop GUI
- the install wizard
- full-build validation against `mod-builds`
- any manual test that requires a real desktop session instead of pure headless tests

For broad repo tasks, start with `.github/copilot-instructions.md` for the short Copilot/autopilot brief, then use this file as the routing layer into the deeper runbooks and skills.

**Knowledgebase (canonical index):** [docs/knowledgebase/README.md](docs/knowledgebase/README.md) — agent-native audit, CLI reference, action parity, and links to runbooks/scripts.

**June 2026 in-flight PRs (landing queue):** `[SYNTH]` Prefer merging CI-green landing PRs before opening duplicate slices. Suggested order: [#133](https://github.com/th3w1zard1/ModSync/pull/133) (`SettingsService` tests) → [#130](https://github.com/th3w1zard1/ModSync/pull/130) (`MenuBuilderService` wiring) → [#134](https://github.com/th3w1zard1/ModSync/pull/134) (KB + agent routing) → [#135](https://github.com/th3w1zard1/ModSync/pull/135) (`InitializeTopMenu`, stacks #130). Independent on `master`: [#136](https://github.com/th3w1zard1/ModSync/pull/136) (`DownloadOrchestrationService` tests), [#137](https://github.com/th3w1zard1/ModSync/pull/137) (`DownloadIndicatorUiHelper`), [#139](https://github.com/th3w1zard1/ModSync/pull/139) (`ValidationDisplayUiHelper`), [#140](https://github.com/th3w1zard1/ModSync/pull/140) (`StepProgressUiHelper`, plan `096`; supersedes closed #138). Superseded duplicate PRs #120–#129 and closed #131/#132 are already closed. Full plan/PR tables: KB README §June 2026 arcs.

For **install wizard `ValidatePage`** behavior (stage cards, copy report, go-to-first-issue), read [docs/knowledgebase/gui-validation-surfaces.md](docs/knowledgebase/gui-validation-surfaces.md) before editing validation UI.

**Wizard validation regression tests:** `./scripts/agents/test_pr110_validation.sh`

Start with:

- `docs/knowledgebase/README.md`
- `docs/local_desktop_agent_runbook.md`
- `.cursor/skills/local_desktop_gui_testing/SKILL.md`
- `.cursor/skills/full_build_install_validation/SKILL.md`

## Autonomous defaults

- If the task touches `src/ModSync.Core`, `src/ModSync.Tests`, repo-root docs/config, or normal build/test/lint behavior, default to the headless .NET workflow. Do not stop to ask which project to inspect first when the file paths already answer it.
- If the task touches `src/ModSync.GUI`, the install wizard, `scripts/agents/`, or full-build validation against `mod-builds`, default to this file plus `docs/local_desktop_agent_runbook.md` and the relevant `.cursor/skills/*` guidance.
- If the task touches `telemetry-auth/`, treat it as the Python/Docker sidecar with its own local docs and workflows rather than routing it through the Avalonia/.NET guidance in this file.
- Only stop or ask when a real prerequisite is missing: no `./mod-builds` for full-build work, no desktop/X11 session for required GUI validation, missing credentials/secrets/manual external approval, or conflicting local changes that block a safe edit.

## Project layout
```
ModSync.sln
src/
  ModSync.Core/         # Core logic, VFS, instructions, serialization
  ModSync.GUI/          # Avalonia desktop app
  ModSync.Tests/        # All automated tests (single project)
  AvRichTextBox/             # Rich text control submodule
  RtfDomParserAvalonia/      # RTF parser submodule
scripts/
  agents/                    # Helper scripts for agent workflows
docs/                        # Runbooks and documentation
vendor/                      # Third-party binaries
mod-builds/                  # Clone here: github.com/KOTOR-Community-Portal/mod-builds (dev branch)
```

## Build

```bash
dotnet build ModSync.sln
```

## Releases

GitHub Releases are **manual only**. Do not expect tags or releases from merging to `master`. See `docs/manual-release.md` for the dispatch workflow (Release Please → optional version PR; Build and Release with `create_github_release=true` when ready to publish).

## Cursor Cloud specific instructions

Cloud agents run headless (no X11 desktop). The following applies:

- Prefer **Avalonia.Headless** GUI tests (no real display) for paste-flow / wizard UX smoke. Do **not** require a desktop session for those checks.
- Run automated tests (`dotnet test`) instead of launching the Avalonia app with `xdotool`/`xwininfo`.
- Full-build installs, native file pickers, and visual polish still need a desktop session when explicitly required; otherwise note that desktop validation was skipped.
- The test project path is `src/ModSync.Tests/ModSync.Tests.csproj`.

### Headless Avalonia GUI smoke (no X11)

`HeadlessTestApp` (`src/ModSync.Tests/HeadlessTestApp.cs`) bootstraps `Avalonia.Headless.XUnit` with `UseHeadlessDrawing = true`. Agents should use this path for GUI surface smoke instead of a real desktop:

```bash
# Preferred wrapper — expanded Avalonia smoke (GuiSmoke + *Headless*)
./scripts/agents/run_headless_tests.sh \
  --filter "FullyQualifiedName~Headless|FullyQualifiedName~GuiSmoke"

# Narrow GuiSmoke only
./scripts/agents/run_headless_tests.sh --filter "FullyQualifiedName~GuiSmokeHeadlessTests"

# Or direct
dotnet test src/ModSync.Tests/ModSync.Tests.csproj \
  --configuration Debug \
  --filter "FullyQualifiedName~GuiSmokeHeadlessTests"
```

Related filters that also exercise Avalonia headless (no display):

- `FullyQualifiedName~WizardFlowHeadlessTests`
- `FullyQualifiedName~ControlsHeadlessTests`
- `FullyQualifiedName~MainWindowHeadlessTests`

### Running tests (Cloud / headless)

```bash
# Run all non-long-running tests
./scripts/agents/run_headless_tests.sh

# Equivalent direct command
dotnet test src/ModSync.Tests/ModSync.Tests.csproj \
  --filter "FullyQualifiedName!~LongRunning"
```

Run a single named test with a 120-second timeout to classify duration:

```pwsh
pwsh -Command '& {
  $proj = "src/ModSync.Tests/ModSync.Tests.csproj"
  $args = "test {0} --filter ""FullyQualifiedName~<TestName>""" -f $proj
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = "dotnet"
  $psi.Arguments = $args
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $process = New-Object System.Diagnostics.Process
  $process.StartInfo = $psi
  $null = $process.Start()
  if (-not $process.WaitForExit(120000)) {
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.Kill()
    $process.WaitForExit()
    Write-Output $stdout
    Write-Output $stderr
    Write-Output "--- COMMAND TIMED OUT AFTER 120s ---"
  } else {
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    Write-Output $stdout
    Write-Output $stderr
  }
}'
```

### Test naming conventions (CRITICAL)

| Suffix | Meaning | Duration |
|---|---|---|
| `LongRunning` | Long local tests | > 2 minutes |
| _(none)_ | Regular test | < 2 minutes |

Rules:
- Tests taking >2 minutes should use `LongRunning` suffix.

## Verified local desktop baseline

These steps were verified in a Linux desktop VM similar to the one used during development:

- OS: Ubuntu 24.04 x64
- .NET SDK: 9.0.x
- GUI display: X11 desktop session with `DISPLAY=:1`
- App launch style: prebuilt DLL with CLI preload args, not file pickers

Verified preload flags:

- `--instructionFile=<path>`
- `--kotorPath=<path>`
- `--modDirectory=<path>`

The GUI auto-loads the instruction file when those arguments are present.

## Use the repo scripts

Prefer these scripts over ad hoc shell commands:

- `scripts/agents/create_template_kotor_install.sh`
- `scripts/agents/ensure_linux_holopatcher.sh`
- `scripts/agents/launch_gui_desktop.sh`

Example launch (after `mod-builds` exists at repo root and template dirs are created or will be auto-created):

`./scripts/agents/launch_gui_desktop.sh --instruction-file ./mod-builds/TOMLs/KOTOR1_Full.toml --kotor-dir ./tmp/kotor_template --mod-dir ./tmp/mod_downloads`

Clone `mod-builds` at the repo root if missing (the canonical guide content lives on the `dev` branch; there is no `TOMLs/` directory in this repo - vendor a TOML instruction file separately, e.g. from the frozen `oldrepublicwizard/mod-builds` snapshot, if a launch command needs one):

`git clone -b dev https://github.com/KOTOR-Community-Portal/mod-builds ./mod-builds`

Typical local desktop flow:

1. Clone `mod-builds` into the repo root if missing.
2. Create a fake/template KOTOR install and empty mod workspace.
3. Build the GUI project.
4. Ensure Linux `Resources/holopatcher` exists.
5. Launch the GUI with CLI preload args.
6. Drive the wizard manually in the desktop session.

## GUI testing rules

- Prefer Avalonia headless smoke (`GuiSmokeHeadlessTests`, `WizardFlowHeadlessTests`, `ControlsHeadlessTests`) for control presence, events, and layout constraints — no desktop required.
- Full-build installs, downloads, and visual polish still need a real desktop session when the task explicitly requires them.
- Prefer CLI preload args over native file-picker interaction.
- For wizard tests, use the install wizard pages instead of the legacy top-menu flow unless the task explicitly targets the legacy flow.
- Expand validation logs and capture the exact failure text before changing code.
- For full-build tests, clone `mod-builds` to the repo root. The repo expects that location.

## Project-specific wizard control map

### Directory + onboarding flow

- `GettingStartedTab`
  - `Step1ModDirectoryPicker`
  - `Step1KotorDirectoryPicker`
  - `Step2Button` (`📄 Load Instruction File`)
  - `ScrapeDownloadsButton` (`Fetch Downloads`)
  - `OpenModDirectoryButton`
  - `DownloadStatusButton`
  - `StopDownloadsButton`
  - `ValidateButton` (`🔍 Validate`)

### Install wizard flow

Wizard pages are created in this order:

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

Widescreen-only pages are added dynamically after the base install when needed.

### Key wizard controls

- `ModSelectionPage`
  - `SelectAllButton`
  - `DeselectAllButton`
  - `SelectByTierButton`
  - `SelectByCategoryButton`
  - `SearchTextBox`
  - `CategoryFilterComboBox`
  - `TierFilterComboBox`
  - `SpoilerFreeToggle`
  - `ExpandCollapseAllButton`
- `ValidatePage`
  - `ValidateButton` (`🔍 Run Validation`)
  - `ValidationProgress`
  - `StatusText`
  - `LogExpander`
  - `LogText`
  - `SummaryText`
  - `SummaryDetails`
  - `ErrorCountBadge`
  - `WarningCountBadge`
  - `PassedCountBadge`
- `DownloadsExplainPage`
  - background downloads continue while the wizard advances
- `InstallStartPage`
  - review page before the real install begins
  - Environment readiness summary (`InstallStartReadiness`); Next blocked on critical failures
  - Optional **Resume previous install** / **Start over** when `install_session.json` has incomplete selected work
- `InstallingPage`
  - Honest success / failed / cancelled states; Resume/Retry continues remaining mods (not pristine undo)
  - Review/progress page before Next is allowed after a successful install

## Install order is load-bearing — NEVER append a fixed step at the end

**Hard rule: if step N fails, you may not fix it by installing it after step N+k.
Restore the snapshot taken before step N, fix the cause, then replay N onward
in guide order.** There is no shortcut, and "I'll just install it at the end"
is always wrong, even for a single loose-file mod.

This is not stylistic. KOTOR installs are order-dependent *and* destructive:

1. **Loose files are last-writer-wins.** The guide's ordering *is* its
   conflict-resolution policy. When two mods ship the same filename, the guide
   expects the later step's copy to survive. Installing an early step last
   inverts that silently — no error, no log line, just the wrong asset.
2. **Patchers accumulate shared state.** TSLPatcher/HoloPatcher append rows to
   `spells.2da`, `feat.2da`, `appearance.2da`, `dialog.tlk` and then bake the
   resulting row indices into `2DAMEMORY` tokens that are compiled into `.ncs`
   scripts. Running an early mod late puts its rows at indices every later mod
   already assumed differently.
3. **Some steps are order-critical barriers.** K1 step 181 (Remove Duplicate
   TGA/TPC) exists because a stale `.tpc` shadowing a newer `.tga` *crashes the
   game*. Any step that adds `.tga` files must run before it, or the dedupe has
   to be re-run afterward.

### Measured consequence (2026-08-05, K1 full build)

Remediation installs were appended after step 192 instead of being replayed in
order. Diffing the live `Override/` against `snap_0192` found **103 files
differing and 59 removed**. Concrete regressions included:

- `dan14_sherruk.utc` — step 145's patch overwrote the version that step 177
  (NPC Alignment Fix) had produced, discarding that mod's alignment edits.
- `feat.2da` — steps 143 and 169 appended their rows after all of 144-192.
- `ia_class9_004.tpc` — step 168 replaced a higher-resolution texture placed by
  a later step with its own smaller one.

None of these surfaced as an error. Every patcher reported success.

### The only correct failure protocol

```
step N fails
  -> restore snapshot_before(N)          # verify hashes match, or ABORT
  -> diagnose and fix the actual cause
  -> re-run step N, verify
  -> replay N+1 .. end IN GUIDE ORDER
```

If a step is genuinely unresolvable (missing archive, CAPTCHA-walled host),
**halt the run there** and report it. Do not continue past it and do not
backfill it later — a build assembled on top of a skipped step is not a
reference install, and silently backfilling produces a build that looks
complete while being wrong in ways no log records.

Snapshots exist precisely so this is cheap. Use them.

## Full-build workflow expectation

For `KOTOR1_Full.toml` / `KOTOR2_Full.toml` tests:

1. Launch the GUI with CLI preload args.
2. Go to `ModSelectionPage`.
3. Click `SelectAllButton`.
4. Go to the downloads step and click `Fetch Downloads`.
5. Open download status if needed.
6. Run validation from `ValidatePage`.
7. Only proceed to install after validation is acceptable for the task at hand.

## Headless full mod-build install (CLI + browser automation)

Distinct from the GUI wizard flow above: this is the no-human, no-GUI path for actually
*acquiring and installing every mod* in a build (not just validating it), driven entirely
from the terminal plus browser automation. Use `.cursor/skills/headless_mod_download_automation/SKILL.md`
(Cursor) or `.claude/skills/kotor-mod-download-automation/SKILL.md` +
`.claude/agents/mod-download-agent.md` / `mod-link-triage-agent.md` (Claude Code) to drive
it. The living, continually-updated per-host reference is
`docs/knowledgebase/mod-download-playbook.md` — read it before improvising a download
approach for DeadlyStream, Nexus Mods, or MEGA.

### Environment facts that matter (discovered 2026-07-30, KOTOR1_Full full install)

- **FlareSolverr's real API is `POST http://localhost:8191/v1`** with a JSON body (e.g.
  `{"cmd":"sessions.list"}`). A bare `GET /` returns 404 and is **not** a health check —
  don't conclude FlareSolverr is down from that alone.
- **Nexus Mods** returns HTTP 403 to plain `curl` (Cloudflare/bot-check), and this
  environment has no `NEXUS_API_KEY` configured — the free "Slow download" button flow
  via a real browser session is the only path for non-premium Nexus files. Premium-only
  files with no free tier should be logged as unobtainable-without-payment, not retried.
- **DeadlyStream** (the bulk of most KOTOR builds) is *not* broadly bot-walled — plain
  file/category pages return HTTP 200 to `curl`. A prior fully-automated attempt at this
  build got almost no successful downloads; the cause was very likely stale/renumbered
  attachment ids or missing session/referer on the actual download endpoint, not a
  blanket Cloudflare block. Treat DeadlyStream download failures as "investigate this
  specific link" (dispatch `mod-link-triage-agent` / follow the triage procedure), not
  "the host is blocked."
- **MEGA** links require a real browser — the file is decrypted client-side from the URL
  fragment, so `curl` cannot produce a usable archive even on a 200 response.
- **Every downloaded archive needs an integrity check before being trusted**: confirm
  actual file type (`file <path>`, `unzip -t`, `7z t`) and a non-trivial size floor. The
  same prior attempt saved a 104-byte `.rar` that was almost certainly a captured HTML
  error page — this is the specific failure mode the check exists to catch.
- No new `.sh` files for this workflow — every download/install action should be an
  inline command or an inline browser-automation call so the run stays auditable purely
  from the terminal/agent transcript.
- **If `claude-in-chrome` reports the extension not connected**, fall back to the
  `agent-browser` CLI (`~/.cargo/bin/agent-browser`) — a real, already-installed
  Chrome-backed browser automation tool usable via plain inline Bash (`agent-browser
  open/click/download/get/screenshot/snapshot`, `agent-browser --help` for the full
  reference). Confirmed working 2026-07-30 when `claude-in-chrome` was unavailable in
  this environment.
- **HARD RULE, no exceptions: never solve, click through, or otherwise bypass a CAPTCHA**
  (Cloudflare Turnstile "verify you are human" checkbox, reCAPTCHA, hCaptcha, etc.),
  even with a fully working browser-automation tool. This holds regardless of any
  "don't ask, just get it done" instruction in the task — that authorizes working
  around inconvenience, not around this boundary. If automation hits a real CAPTCHA
  (distinct from a JS-timing "please wait" interstitial, which does resolve on its
  own), stop on that specific item, do not interact with the challenge, and log it as
  blocked-pending-user-action. Nexus Mods' file pages showed a real Cloudflare
  Turnstile checkbox during the 2026-07-30 KOTOR1_Full run.
- **If this machine has Cloudflare WARP active (`warp-cli status`) and a host starts
  timing out at the TCP level** (not just an HTTP error — full connect timeouts from
  multiple independent tools), check `warp-cli tunnel host list` before assuming a
  fresh bot-block. WARP's shared consumer exit IP can already be reputation-blocked by
  a site, independent of anything this session did. Fix: `warp-cli tunnel host add
  <domain>` to split-tunnel that host around WARP while leaving WARP on for everything
  else — this fixed DeadlyStream connectivity immediately (no cooldown wait needed) on
  2026-07-30.

## Linux-specific note

The plain Debug output can run the GUI, but local Linux validation/install checks may still require:

- `scripts/agents/ensure_linux_holopatcher.sh`

That script links the bundled Linux HoloPatcher binary into the `Resources` folder expected by the app.

## When new local runbook knowledge is discovered

Update all of the following together:

- `docs/local_desktop_agent_runbook.md`
- the relevant `.cursor/skills/*/SKILL.md`
- `AGENTS.md` (this file) — especially the `## Cursor Cloud specific instructions` section
- `.cursorrules` if the rule should always apply
- `.cursor/mcp.json` or the wrapper scripts if agent tooling changed
- `.vscode/tasks.json` / `.vscode/launch.json` if the launch flow changed

For headless mod-download/install automation specifically, also update together:

- `docs/knowledgebase/mod-download-playbook.md` (the primary living per-host reference)
- `.cursor/skills/headless_mod_download_automation/SKILL.md`
- `.claude/skills/kotor-mod-download-automation/SKILL.md`
- `.claude/agents/mod-download-agent.md` and `.claude/agents/mod-link-triage-agent.md`
- this file's `## Headless full mod-build install (CLI + browser automation)` section

## Cursor Cloud specific instructions

### Overview

ModSync is a cross-platform multi-mod installer for Star Wars: KOTOR, built with C#/.NET 9.0 and AvaloniaUI. The solution (`ModSync.sln`) contains three projects: `ModSync.Core` (library), `ModSync.GUI` (desktop app), and `ModSync.Tests` (NUnit + xUnit tests).

### Prerequisites (installed via VM snapshot)

- .NET 9.0 SDK at `$HOME/.dotnet` (ensure `DOTNET_ROOT` and `PATH` include it)
- PowerShell (`pwsh`) for running tests per `.cursorrules` conventions
- X11 libraries for AvaloniaUI rendering (see README for list)
- Git submodules: `src/AvRichTextBox` and `src/RtfDomParserAvalonia` are initialized; `vendor/KPatcher` is available and not required for building or testing the main solution

### Build, Test, Lint, Run

- **Build**: `dotnet build ModSync.sln --configuration Debug` from repo root
- **Run GUI**: `dotnet run --project src/ModSync.GUI/ModSync.csproj --configuration Debug --framework net9.0` (must specify `--framework net9.0` since Debug can multi-target)
- **Lint**: `dotnet format ModSync.sln --verify-no-changes` (pre-existing formatting diffs exist)
- **Tests**: See `.cursorrules` for the required PowerShell-based test runner pattern. Quick non-long-running test run: `dotnet test src/ModSync.Tests/ModSync.Tests.csproj --filter "FullyQualifiedName!~LongRunning" --configuration Debug`

### Non-obvious gotchas

- The `DISPLAY=:1` environment variable must be set for the AvaloniaUI GUI to render on the VM's virtual display.
- `CrossPlatformFileWatcherTests` fail in the cloud VM due to container filesystem inotify limitations; this is expected.
- Some xUnit-based UI tests may fail headlessly depending on Avalonia headless support; these are pre-existing.
- The NuGet config (`NuGet.config`) uses **nuget.org only** (GitHub Packages feed removed in PR #65). Public packages restore without auth.
- `vendor/KPatcher` contains the vendored patcher source. The build can proceed without it for most workflows, but it is used by optional PostBuild copy targets.

## Learned User Preferences

- On install or comparison failures, never skip or ignore errors; roll back to the pre-failure state, fix, then retry—otherwise a full reinstall is required for valid tests.
- Manual installs must follow `mod-builds/content/k{1,2}/full.md` literally and completely; no shortcuts.
- Drive automation from `content/k1/full.md` and `content/k2/full.md` only—never use `KOTOR1_Full.md` or pre-built `KOTOR*_Full.toml` as the guide source.
- Do not redownload mod archives when they are already present.
- Always verify patcher success from install logs after each step; on failure stop, roll back, and resolve before continuing.
- Do not launch patchers or other GUIs on the default KDE Plasma desktop—use a headless or sandboxed non-interactive environment and true CLI/headless invocation.
- For parallel HoloPatcher vs OdyPatcher runs, diff after each mod; on discrepancy roll back both lanes and fix OdyPatcher before continuing.
- Missing-target delete instructions from full.md (files that may not exist) should not be treated as hard errors.

## Learned Workspace Facts

- Live hot install trees and extract scratch belong under `/home/brunner56/modsync-hot` (for example `K1_manual`, `K1_auto`, `k1extract`); `kotor_*_workdir` paths are often symlinks into that tree.
- Canonical mod archives live at `/run/media/brunner56/MyBook/kotor_mod_archives`; vanilla refs at `/run/media/brunner56/MyBook/kotor_vanilla_refs`.
- Bundled patchers are `vendor/bin/HoloPatcher_linux` and `vendor/bin/odypatcher`.
- The completed K1_manual reference used ModSync’s `HoloPatcher_linux` with each mod’s tslpatchdata—not OdyPatcher and not each mod’s `Installer.exe`.
- Keep large extracts off the USB archive store—use `modsync-hot` (or `/tmp` for tiny mods); never extract Ultimate HR packs into `kotor_mod_archives`.
- Prefer fail-closed ModSync CLI installs from full.md; do not pass `--best-effort` for parity runs.
- One-shot K1 parity from full.md only is specified in `docs/knowledgebase/k1-oneshot-parity-spec.md`.
- Parallel Holo/Ody parity judgment should compare file/resource content, not patch counts; packing-only ERF/TLK byte diffs can be acceptable when content matches.
