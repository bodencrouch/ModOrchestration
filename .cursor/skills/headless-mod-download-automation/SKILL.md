# Headless mod download automation

## When to use

Use this skill when the task explicitly involves:

- fully downloading and installing a `mod-builds` TOML (`KOTOR1_Full.toml`, `KOTOR2_Full.toml`, or any other build) with no human clicking through download pages
- resolving DeadlyStream / Nexus Mods / MEGA download links via browser automation and/or FlareSolverr
- converging a real install to zero remaining `validate --full` errors

This is the **headless CLI** counterpart to `full_build_install_validation` (which drives the Avalonia GUI wizard by hand). Use this skill instead when the goal is unattended acquisition of every mod archive, not exercising the wizard.

## Read first

- `docs/knowledgebase/mod-download-playbook.md` — canonical, continually-updated per-host download strategy and FlareSolverr usage. Read before improvising; append to it when you learn something new.
- `AGENTS.md` — `## Headless full mod-build install (CLI + browser automation)` section.
- `.claude/skills/kotor-mod-download-automation/SKILL.md` and `.claude/agents/mod-download-agent.md` / `mod-link-triage-agent.md` if running under Claude Code — this Cursor skill and those Claude Code assets describe the same procedure; keep them in sync.

## Required repo state

- `./mod-builds` cloned at repo root
- `ModSync.Core` builds (`dotnet build src/ModSync.Core/ModSync.Core.csproj -c Debug -f net9.0`)
- FlareSolverr reachable at `http://localhost:8191` — verify via `POST /v1` with a JSON body (e.g. `{"cmd":"sessions.list"}`); a bare `GET /` 404s and is not a health check
- Browser automation available (Playwright/Patchright/claude-in-chrome, whichever the current agent has) for Nexus/DeadlyStream/MEGA flows

## Procedure

1. Generate or confirm a fresh merged instruction file (TOML + Markdown merged) for the target build.
2. Check the real game directory's `Override/` file count as a starting baseline.
3. Per mod not yet present as a verified archive in staging, route by host:
   - **deadlystream.com**: browser navigate to the file page, click the real download control, capture the download. Most failures here are stale/renumbered attachment ids or a missing session, not a bot-wall — investigate per link.
   - **nexusmods.com**: browser "Slow download" flow, honor the countdown timer; no API key available in this environment, so this is the only free path.
   - **mega.nz**: browser only, wait for client-side decrypt before triggering download.
   - **github.com**: direct `curl -L`.
   - misc one-offs (Drive, pastebin, gamefront): handle individually.
4. Verify every download before trusting it: correct archive type via `file`/`unzip -t`/`7z t`, and a non-trivial size floor. A small HTML error page saved under an archive extension is the classic silent-failure mode this catches.
5. Batch install (not one mod at a time):
   ```
   dotnet run --project src/ModSync.Core/ModSync.Core.csproj -f net9.0 --no-build -- \
     install -i <merged-toml> -g <game-dir> -s <staging-dir> \
     -d --concurrent --best-effort --skip-validation --download-timeout-hours 72
   ```
6. Run a full validate pass periodically:
   ```
   dotnet run --project src/ModSync.Core/ModSync.Core.csproj -f net9.0 -- \
     validate -i <merged-toml> -g <game-dir> -s <staging-dir> --full
   ```
7. Feed validate failures back into step 3. For any link that fails repeatedly after direct investigation, resolve it individually (a corrected id, a mirror, a required access step, or a confirmed-unobtainable verdict) rather than retrying it forever or silently dropping it.
8. Repeat 3-7 until validate is clean or every remaining failure is an explicitly logged, individually-investigated exception.

## What to record

- Starting and ending `Override/` file counts (real numbers, not estimates)
- Mods installed vs. named exceptions with reasons
- Any new per-host quirks discovered (fold into the playbook doc, not just this file)

## Project-specific behavior that matters

- `--best-effort --skip-validation` on `install` is what makes batch/partial installs safe to run repeatedly without re-validating everything each time — validation is a separate, explicit step.
- The merged instruction file must be regenerated if the source TOML/Markdown changes after it was produced — check timestamps.
- No new `.sh` files for this workflow — every action is an inline command or an inline browser-automation call, so the whole run stays auditable from the terminal transcript alone.

## Update rule

Whenever a better download/triage approach or a new host quirk is discovered, update together:

- `docs/knowledgebase/mod-download-playbook.md`
- this skill
- `.claude/skills/kotor-mod-download-automation/SKILL.md` and the `.claude/agents/*.md` pair (Claude Code side)
- `AGENTS.md`'s `## Headless full mod-build install (CLI + browser automation)` section
