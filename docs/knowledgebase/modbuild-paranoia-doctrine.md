# Modbuild paranoia doctrine (NON-NEGOTIABLE)

Applies to every manual or automated KOTOR modbuild install (K1 and K2), and to any
agent or skill that touches a game directory.

## Axiom 0 — the build is NOT idempotent

Mod installs are **order-dependent and destructive**. Patchers rewrite 2DA rows, append
TLK entries, and mutate GFF/ERF archives in place. Re-running a step, running it out of
order, or running it on a base that is not exactly what the guide assumed produces a
build that is **silently wrong** — it will not error, it will just be broken later, often
only at runtime hours into a playthrough.

Therefore: **a doubt is a defect.** If you cannot *prove* the state is correct, it is
incorrect until proven otherwise.

## Rule 1 — never continue past an unresolved issue

If a step errors, partially applies, produces an unexpected file delta, or you are simply
unsure it worked:

1. **STOP.** Do not start the next step.
2. Roll back from that step's snapshot.
3. Diagnose to root cause. "It probably worked" is not a diagnosis.
4. Fix, re-run, verify, and only then continue.

Continuing while an earlier step is `blocked` / `failed` / `unverified` is forbidden,
**even if you believe later steps do not depend on it.** That belief is a guess about a
non-idempotent system. If a step genuinely must be deferred, that decision belongs to the
user, not the agent — surface it and ask.

## Rule 2 — restore-and-replay, never append-out-of-order

If step N failed and steps N+1..M already ran, you may NOT simply install N afterwards.
Correct remediation:

```
restore from rollback_<game>/stepN   →  install N  →  replay N+1..M in order
```

Appending a missed step at the end changes TLK indices, 2DA row ordering, and file
overwrite precedence relative to what the guide specifies. TLK **appends** shift indices
for everything after them; TLK **replaces** do not, but a later mod may have replaced the
same entry, in which case a late install silently clobbers it. You cannot know without
checking — so restore and replay.

## Rule 3 — verify from the filesystem, never from intent

Never record `pass` because a command exited 0. Verify:

- **Patcher runs**: exit code AND zero `[Error]` lines AND the reported patch count.
  Note: some patchers (notably `nwnnsscomp.exe`) **always exit 0 even on failure**, and
  write diagnostics to stdout. Exit status is not evidence.
- **File deltas**: before/after counts, and that they match what the mod actually ships.
  A step that adds 0 files when it should add 20 is a failure, not a pass.
- **Deletions**: `rsync --exclude` and shell globs are **case-sensitive**; the game engine
  is **case-insensitive**. Always re-check with `find -iname` that each guide-specified
  deletion is actually absent. (Real defect: `DAN_Birds.tpc` leaked past a lowercase
  exclude and had to be removed post-hoc.)
- **Counting**: prefer a single consistent method and sanity-check it. Do not trust a
  count taken while another process is writing.

## Rule 4 — one writer per game directory, ever

Before dispatching any agent that writes to a game dir, **prove no other writer exists** —
check for live processes, not just file mtimes. A quiet ledger does NOT mean a dead agent;
it may be mid-extraction or mid-patch.

Two concurrent writers WILL corrupt a non-idempotent build. (Real incident: a duplicate
agent was spawned on a false "previous agent died" inference, ran `rsync -a --delete` from
a stale snapshot over a live install, and deleted ~58 files from completed steps. Recovery
was only possible because per-step snapshots existed.)

## Rule 5 — snapshot before every single step

Ext4 hardlink snapshots are effectively free (`rsync -a --link-dest=...`, ~1s for 19k
files). There is no excuse to skip one. Snapshot the *full* content tree for any step that
touches `modules/`, `dialog.tlk`, `lips/`, `streamvoice/`, or `movies/` — an
override-only snapshot cannot restore those, and you will discover that only when you
need it.

## Rule 6 — the base must be factory-fresh and proven

A modbuild may only start from a verified factory install. Proof means: fresh Steam
download, `StateFlags=4`, override empty (or exactly the vanilla count), zero mod-added
archives, and `dialog.tlk` at the known vanilla byte size. A "restored" or "cleaned"
install is NOT acceptable — residue you did not detect will silently poison everything.
Record the proof as a step-0 `BASELINE` ledger entry.

## Rule 7 — no silent skips, ever

Every step gets a ledger record. Permitted verdicts:

| verdict | meaning |
|---|---|
| `pass` | installed AND verified from the filesystem |
| `skipped` | guide itself says to skip, or platform-inapplicable — quote the guide |
| `deferred` | user explicitly deferred it (e.g. widescreen, 4GB patcher) |
| `blocked-pending-*` | genuinely blocked; **build must stop**, not continue |
| `pending-user-download` | asset unobtainable without the user; **build must stop** at this step unless the user authorised continuing |

A missing step number in the ledger is itself a defect. Audit for gaps continuously, not
at the end.

## Rule 8 — follow the guide literally

Read the **entire** guide section before acting, including trailing "Installation
Instructions" / "Download Instructions" notes that appear *after* the description. Several
real defects came from acting on the first paragraph and missing a trailing exclusion note.

Quote the guide instruction verbatim in the ledger `choice` field. If the guide says
"install both files", "delete X before moving", "do not overwrite", "use the 2x tpc
version", or "ignore the subfolders" — that is a literal instruction, not a suggestion.

## Rule 9 — verify identity, not filename

Many mods ship K1 and K2 variants with near-identical names. Verify by **content**
(texture prefixes, mod id, readme) before installing:

- K1 module prefixes: `LDA_ LKO_ LTS_ LEH_ LUN_ ...`
- K2 module prefixes: `DAN_ DXN_ OND_ KOR_ NAR_ TEL_ PER_ MAL_ ...`

Real defect: the staged "Ultimate Dantooine/Korriban" archives were the **K1** packs; the
K2 build would have silently installed wrong-game textures.

## Rule 10 — when in doubt, start over

Restarting is cheap relative to shipping a corrupt build that fails hours into play. If
the provenance of the base is uncertain, or an unknown number of steps ran against a bad
state, **wipe and restart from a fresh Steam install**. Do not attempt to "repair forward".

---

## Enforcement checklist (paste into any modbuild agent brief)

- [ ] Base proven factory-fresh, recorded as step-0 BASELINE
- [ ] Sole writer confirmed (process check, not mtime)
- [ ] Snapshot taken before EVERY step, full-tree when needed
- [ ] Full guide section read, including trailing notes
- [ ] Mod identity verified by content, not filename
- [ ] Post-install verified from filesystem (counts, deletions via `-iname`, patcher errors)
- [ ] Ledger record written for EVERY step, no gaps
- [ ] On any error/doubt: STOP → roll back → diagnose → fix → verify → resume
- [ ] Never append a missed step out of order — restore and replay
