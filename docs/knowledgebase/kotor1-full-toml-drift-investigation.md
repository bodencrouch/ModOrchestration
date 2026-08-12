# KOTOR1_Full.toml — drift investigation

Investigation only. This TOML is **not** used as an install source for the
manual-parity runs; those follow `mod-builds/content/k1/full.md` exclusively.
The purpose here is to answer "how far has the hand-built TOML drifted from the
guide, and what would it take to resolve its wildcards automatically."

Measured against `mod-builds/content/k1/full.md` (guide) and
`mod-builds/TOMLs/KOTOR1_Full.toml` (the hand-built instruction set).

## Component drift

| | count |
|---|---|
| Guide mods (`**Name:**` entries) | 197 |
| TOML components (`[[thisMod]]` blocks) | 189 |
| Guide mods with no TOML component | 11 |
| TOML components with no guide counterpart | 3 |
| Duplicate component names in TOML | 0 |

The headline "189 vs 197" gap is **not** 8 missing mods. It resolves cleanly:

**The 11 guide mods absent from the TOML are exactly the optional-widescreen
and cutscene block** — Turret Cockpit Widescreen, KOTOR High Resolution Menus,
HD UI Menu Pack, Workbench Upgrade Screen Camera Tweak, Pretty Good! Icons, HD
Robe Icons for JC's Cloaked Jedi Robes, Upscaled Computer, Widescreen Fade Fix,
Main Menu Widescreen Fix, K1 Cutscenes Rescaled, KOTOR Remastered Cutscenes.

These are not missing by accident. They live inside the TOML's
`epilogueContent` field — a single multi-kilobyte **prose string** that carries
the guide's trailing markdown verbatim, including 15 `###` headings. So they
exist in the file as *documentation*, but not as installable components. Any
consumer that counts `[[thisMod]]` blocks will under-report the build by
exactly these 11, and any consumer that installs from components alone will
silently skip them.

**The 3 TOML components with no guide counterpart** are romance mods:
`Carth Onasi and Male PC Romance`, `JC's Romance Enhancement: Pan-Galactic
Flirting for K1`, and `JC's Romance Enhancement: Biromantic Bastila for K1`.
These are the genuinely stale entries — content the guide no longer lists.

## Wildcard usage

The hand-added wildcards are widespread: **207 of 594 `Source` paths (34%)
contain a `*`**. They fall into three shapes with *different* resolution
semantics, which matters because a single naive glob would be wrong for two of
them.

| shape | count | example | correct semantics |
|---|---|---|---|
| mid-path `*\` — version-agnostic folder | 90 | `<<modDirectory>>\JC's Minor Fixes for K1*\Straight Fixes\*` | must resolve to **exactly one** directory; 0 or 2+ is an error |
| trailing `*` — "everything in this dir" | 61 | `...\Aesthetic Improvements\*` | expands to **N files**; 0 matches is suspicious, not fatal |
| archive version suffix | 47 | `<<modDirectory>>\KotOR_Dialogue_Fixes*.7z` | must resolve to **exactly one** archive |
| other | 9 | — | case-by-case |

The recurring driver is the same in all three: mod archives and their extracted
folders carry version strings the guide never mentions
(`Sentinel Sneak Attack-1710-1-2-0-1757276890`, `..._v1.0.2b`,
`...-1282-4-1-1629713341`), so a literal path would break on every mod update.

### On "smart replace on deserialization"

Deferred per the brief — recording the findings rather than implementing.
If it is picked up later, the important part is that **the three shapes need
different failure modes, not one shared glob**:

- *Exactly-one* shapes (folder, archive) should hard-fail on ambiguity. Silently
  taking the first match is how a build installs the wrong version of a mod and
  nobody notices — precisely the class of failure the manual-parity runs exist
  to catch.
- *Expansion* shapes should preserve "0 matches" as a distinct, reportable state
  rather than an empty no-op, for the same reason.
- Resolution should happen against the **staged download directory**, and the
  resolved literal path should be recorded in the install log. Otherwise a later
  audit cannot reconstruct which file a wildcard actually matched at run time.

A cheap intermediate step, well short of full smart-replacement: a validator
that resolves every wildcard against the current mod directory and reports the
match count per path. That surfaces both stale entries and newly-ambiguous
globs without changing any install behaviour.

## Why this matters beyond the TOML

The `epilogueContent` finding generalises: a build file that stores part of its
mod list as prose and part as structured components will always mislead a
consumer that reads only one of them. Component count is not build size. Any
ModSync feature that reports "N mods in this build" should either parse the
prose sections too or state plainly that it is counting components only.
