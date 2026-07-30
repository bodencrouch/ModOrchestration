---
name: kotor-mod-download-automation
description: "Headless, no-GUI, no-human-in-the-loop pipeline to fully download and install a mod-builds TOML/Markdown build (KOTOR1_Full, KOTOR2_Full, or any other build) via ModSync's CLI, browser automation, and FlareSolverr. Use when the task is to actually acquire and install every mod in a build (not just validate it) without a human clicking through DeadlyStream/Nexus/MEGA download pages. Different from the GUI-driven full_build_install_validation Cursor skill, which drives the Avalonia wizard by hand."
---

# KOTOR mod-build download automation

This skill covers the headless CLI path for turning a `mod-builds` TOML into a fully installed game directory, with every archive fetched by automation instead of a human. It complements (does not replace) `.cursor/skills/full_build_install_validation/SKILL.md`, which is the GUI-wizard-driven version of the same underlying goal.

## When to use this vs. the GUI skill

- **This skill**: the task is to actually acquire and install all mods with nobody watching — e.g. "fully install KOTOR1_Full", "get every mod downloaded and in Override", a long unattended run.
- **GUI skill** (`.cursor/skills/full_build_install_validation/SKILL.md`): the task is to exercise/verify the wizard's select-all → fetch downloads → validate flow, typically with a human or agent driving the desktop app directly.

## Read first

- `docs/knowledgebase/mod-download-playbook.md` — the canonical, continually-updated per-host download strategy reference (DeadlyStream, Nexus, MEGA, GitHub, misc), FlareSolverr's real API shape, and the archive-integrity check. **This is the single source of truth for host-specific gotchas — read it before improvising a download approach, and append to it when you learn something new.**
- `AGENTS.md` — `## Headless full mod-build install (CLI + browser automation)` section for repo-level context and environment gotchas.

## Required repo/environment state

- `mod-builds` cloned at repo root (`git clone -b dev https://github.com/KOTOR-Community-Portal/mod-builds ./mod-builds`, or the archived fork if that's unavailable).
- `ModSync.Core` builds (`dotnet build src/ModSync.Core/ModSync.Core.csproj -c Debug -f net9.0`).
- FlareSolverr reachable at `http://localhost:8191` — verify with `POST /v1` (`{"cmd":"sessions.list"}`), **not** a bare `GET /` (that 404s and is not a health check).
- `claude-in-chrome` browser tools available (load via `ToolSearch` with `select:mcp__claude-in-chrome__...` if deferred) for any host needing a real browser session.
- A merged instruction file (TOML + Markdown merged) — generate with the repo's existing merge/convert path if one isn't already present in `tmp/`.

## Procedure

1. Confirm the target game directory is in the state you expect (check `Override/` file count) before starting — know your starting point so progress numbers mean something.
2. Dispatch (or act as) `mod-download-agent` with: merged instruction file path, real game directory, staging directory. That agent owns the download → verify → batch-install → validate loop end to end.
3. For any individual link that repeatedly fails, dispatch `mod-link-triage-agent` with just that one mod's name/URL/error — don't let one dead link stall the whole batch. Fold its verdict (fixed URL, mirror, access step, or confirmed-unobtainable) back into the next batch.
4. Loop steps 2-3 until a full `validate --full` pass shows zero errors, or every remaining failure has been triaged to a confirmed-unobtainable verdict with a recorded reason.
5. Record final numbers (installed file count, mods installed vs. named exceptions) in a progress doc — never report success without a real `find <override-dir> -type f | wc -l` and the tail of the last validate run to back it up.

## Known gotchas (see AGENTS.md and the playbook doc for the full, living list)

- FlareSolverr's real endpoint is `POST /v1`, not `GET /`.
- Nexus Mods blocks plain `curl` (403, Cloudflare) and this environment has no `NEXUS_API_KEY` — the free "Slow download" browser flow is the only path for non-premium files.
- DeadlyStream pages themselves are not bot-walled (plain `curl` gets 200), so download failures there are usually stale/renumbered attachment ids or a missing session — investigate per-link, don't assume a blanket block.
- MEGA links need a real browser (client-side JS decryption) — `curl` cannot produce a usable file even on a 200 response.
- A "downloaded" archive is not trustworthy until it passes the file-type integrity check — a small HTML error page saved with a `.rar`/`.zip` extension will otherwise silently corrupt the install.

## Update rule

Whenever this pipeline reveals a new host quirk, a better failure-recovery pattern, or a tooling change (e.g. FlareSolverr version bump, a new browser-automation approach), update, together:

- `docs/knowledgebase/mod-download-playbook.md` (the primary living reference)
- this skill
- `.cursor/skills/headless_mod_download_automation/SKILL.md` (Cursor-side mirror)
- `AGENTS.md`'s `## Headless full mod-build install (CLI + browser automation)` section
