#!/usr/bin/env python3
"""Byte-level + format-aware diff between two KOTOR install trees.

Answers one question precisely: does the ModSync-AUTOMATED install produce the
same game state as the MANUAL modbuild install? Every difference is classified,
and where a raw byte diff would be useless (dialog.tlk, .2da, .mod/.erf/.rim)
the file is parsed and compared structurally so the report says WHAT differs,
not merely THAT it differs.

Read-only. Never writes into either tree.

Usage
-----
    diff_kotor_installs.py A_DIR B_DIR [options]

    A_DIR / B_DIR are the directories that directly contain game content
    (K1: .../swkotor ; K2: .../Knights of the Old Republic II/steamassets).

    --a-name / --b-name   labels for the two sides (default manual / auto)
    --json PATH           machine-readable report (stable ordering)
    --md PATH             human-readable markdown summary
    --ledger PATH         manual harness ledger.jsonl, for attribution
    --install-log PATH    ModSync install log, for attribution
    --jobs N              hashing threads (default 8)
    --no-inode-shortcut   hash even when both sides share an inode
    --allow-shared-inodes the two trees are knowingly hardlink-aliased; report
                          shared inodes as an observation instead of a hazard
    --hash-mode all|sizefirst
    --include GLOB        restrict to matching relative paths (repeatable)
    --exclude GLOB        skip matching relative paths (repeatable)
    --max-examples N      per-category examples embedded in the report
    --selftest            run built-in format-parser tests and exit

Exit codes: 0 = no real differences, 1 = differences found, 2 = usage/IO error.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import struct
import sys
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field

# --------------------------------------------------------------------------
# Classification of paths that are installer bookkeeping rather than game state.
# These are still reported, but under `bookkeeping` so they cannot silently
# inflate or hide the real verdict.
# --------------------------------------------------------------------------
BOOKKEEPING_DIR_PARTS = {
    "backup",  # TSLPatcher/OdyPatcher per-run backups
    "uninstall",
    "tpc-backup",
    ".verification",
    ".mission",
}
BOOKKEEPING_NAMES = {
    "installlog.txt",
    "errorlog.txt",
    "info.rtf",
    "readme.txt",
    "changes.ini",
    ".ds_store",
    "thumbs.db",
}
BOOKKEEPING_SUFFIXES = (".log", ".bak", ".tmp")

CONTAINER_EXT = {".mod", ".erf", ".rim", ".sav", ".hak", ".nwm"}
IMAGE_TWIN_EXT = {".tpc", ".tga", ".dds", ".txb"}

# resource-type id -> extension (KOTOR / Aurora resource types)
RESTYPE = {
    0: "res", 1: "bmp", 2: "mve", 3: "tga", 4: "wav", 5: "wfx", 6: "plt",
    7: "ini", 8: "mp3", 9: "mpg", 10: "txt", 11: "wma", 12: "wmv", 13: "xmv",
    2000: "plh", 2001: "tex", 2002: "mdl", 2003: "thg", 2005: "fnt",
    2007: "lua", 2008: "slt", 2009: "nss", 2010: "ncs", 2011: "mod",
    2012: "are", 2013: "set", 2014: "ifo", 2015: "bic", 2016: "wok",
    2017: "2da", 2018: "tlk", 2022: "txi", 2023: "git", 2024: "bti",
    2025: "uti", 2026: "btc", 2027: "utc", 2029: "dlg", 2030: "itp",
    2031: "btt", 2032: "utt", 2033: "dds", 2034: "bts", 2035: "uts",
    2036: "ltr", 2037: "gff", 2038: "fac", 2039: "bte", 2040: "ute",
    2041: "btd", 2042: "utd", 2043: "btp", 2044: "utp", 2045: "dft",
    2046: "gic", 2047: "gui", 2048: "css", 2049: "ccs", 2050: "btm",
    2051: "utm", 2052: "dwk", 2053: "pwk", 2054: "btg", 2055: "utg",
    2056: "jrl", 2057: "sav", 2058: "utw", 2059: "4pc", 2060: "ssf",
    2061: "hak", 2062: "nwm", 2063: "bik", 2064: "ndb", 2065: "ptm",
    2066: "ptt", 2067: "bak", 3000: "lyt", 3001: "vis", 3002: "rim",
    3003: "pth", 3004: "lip", 3005: "bwm", 3006: "txb", 3007: "tpc",
    3008: "mdx", 3009: "rsv", 3010: "sig", 3011: "xbx", 3012: "1da",
    3033: "erf", 3034: "bif", 3035: "key",
}


def restype_ext(code: int) -> str:
    return RESTYPE.get(code, f"res{code}")


def is_bookkeeping(rel_key: str) -> bool:
    parts = rel_key.split("/")
    if any(p in BOOKKEEPING_DIR_PARTS for p in parts):
        return True
    if parts[-1] in BOOKKEEPING_NAMES:
        return True
    return rel_key.endswith(BOOKKEEPING_SUFFIXES)


# --------------------------------------------------------------------------
# Hashing
# --------------------------------------------------------------------------
def sha256_file(path: str, chunk: int = 1 << 20) -> str:
    h = hashlib.sha256()
    try:
        with open(path, "rb") as fh:
            while True:
                b = fh.read(chunk)
                if not b:
                    break
                h.update(b)
    except OSError as exc:
        return f"ERR:{type(exc).__name__}"
    return h.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------
# Format readers
# --------------------------------------------------------------------------
class FormatError(Exception):
    pass


@dataclass
class TlkEntry:
    index: int
    flags: int
    sound_resref: str
    text: str


def read_tlk(path: str) -> list[TlkEntry]:
    with open(path, "rb") as fh:
        head = fh.read(20)
        if len(head) < 20 or head[:8] not in (b"TLK V3.0", b"TLK V4.0"):
            raise FormatError(f"not a TLK (magic {head[:8]!r})")
        _lang, count, str_off = struct.unpack_from("<III", head, 8)
        table = fh.read(count * 40)
        if len(table) < count * 40:
            raise FormatError("truncated TLK entry table")
        fh.seek(0, os.SEEK_END)
        size = fh.tell()
        blob_len = max(0, size - str_off)
        fh.seek(str_off)
        blob = fh.read(blob_len)

    out: list[TlkEntry] = []
    for i in range(count):
        base = i * 40
        flags, resref, _vv, _pv, off, slen, _sl = struct.unpack_from(
            "<I16sIIIII", table, base
        )
        raw = blob[off:off + slen] if slen else b""
        out.append(
            TlkEntry(
                index=i,
                flags=flags,
                sound_resref=resref.split(b"\x00")[0].decode("latin-1"),
                text=raw.decode("windows-1252", errors="replace"),
            )
        )
    return out


@dataclass
class TwoDA:
    columns: list[str]
    row_labels: list[str]
    rows: list[list[str]]  # parallel to row_labels


def _read_terminated(buf: bytes, pos: int, term: int) -> tuple[str, int]:
    end = buf.find(bytes([term]), pos)
    if end < 0:
        raise FormatError("unterminated string in 2DA")
    return buf[pos:end].decode("windows-1252", errors="replace"), end + 1


def read_2da(path: str) -> TwoDA:
    with open(path, "rb") as fh:
        buf = fh.read()
    if buf[:8] == b"2DA V2.b":
        return _read_2da_binary(buf)
    if buf[:8] in (b"2DA V2.0", b"2DA\tV2.0"):
        return _read_2da_ascii(buf)
    raise FormatError(f"not a 2DA (magic {buf[:8]!r})")


def _read_2da_binary(buf: bytes) -> TwoDA:
    pos = 9  # "2DA V2.b\n"
    columns: list[str] = []
    while True:
        if pos >= len(buf):
            raise FormatError("truncated 2DA column block")
        if buf[pos] == 0:
            pos += 1
            break
        label, pos = _read_terminated(buf, pos, 0x09)
        columns.append(label)
    (row_count,) = struct.unpack_from("<I", buf, pos)
    pos += 4
    row_labels: list[str] = []
    for _ in range(row_count):
        label, pos = _read_terminated(buf, pos, 0x09)
        row_labels.append(label)
    ncells = row_count * len(columns)
    offsets = struct.unpack_from(f"<{ncells}H", buf, pos)
    pos += ncells * 2
    (_data_size,) = struct.unpack_from("<H", buf, pos)
    pos += 2
    data_start = pos
    rows: list[list[str]] = []
    for r in range(row_count):
        row: list[str] = []
        for c in range(len(columns)):
            off = offsets[r * len(columns) + c]
            val, _ = _read_terminated(buf, data_start + off, 0x00)
            row.append(val)
        rows.append(row)
    return TwoDA(columns=columns, row_labels=row_labels, rows=rows)


def _read_2da_ascii(buf: bytes) -> TwoDA:
    text = buf.decode("windows-1252", errors="replace")
    lines = [ln for ln in text.splitlines()]
    if not lines:
        raise FormatError("empty ASCII 2DA")
    body = [ln for ln in lines[1:] if ln.strip() != ""]
    if not body:
        return TwoDA(columns=[], row_labels=[], rows=[])
    columns = body[0].split()
    row_labels: list[str] = []
    rows: list[list[str]] = []
    for ln in body[1:]:
        parts = ln.split()
        if not parts:
            continue
        row_labels.append(parts[0])
        cells = parts[1:]
        cells += [""] * (len(columns) - len(cells))
        rows.append(cells[: len(columns)])
    return TwoDA(columns=columns, row_labels=row_labels, rows=rows)


@dataclass
class ContainerRes:
    name: str  # "resref.ext"
    size: int
    offset: int


def read_container(path: str) -> tuple[str, list[ContainerRes]]:
    """Return (container_type, resources). Supports ERF-family and RIM."""
    with open(path, "rb") as fh:
        head = fh.read(160)
        if len(head) < 32:
            raise FormatError("file too small for a container")
        magic = head[:4].decode("latin-1", errors="replace").strip()
        version = head[4:8].decode("latin-1", errors="replace")
        if version not in ("V1.0", "V1.1"):
            raise FormatError(f"unknown container version {version!r}")
        if magic == "RIM":
            entry_count, entry_off = struct.unpack_from("<II", head, 12)
            fh.seek(entry_off)
            table = fh.read(entry_count * 32)
            if len(table) < entry_count * 32:
                raise FormatError("truncated RIM entry table")
            res = []
            for i in range(entry_count):
                resref, rtype, _rid, off, size = struct.unpack_from(
                    "<16sIIII", table, i * 32
                )
                name = resref.split(b"\x00")[0].decode("latin-1").lower()
                res.append(
                    ContainerRes(f"{name}.{restype_ext(rtype)}", size, off)
                )
            return "RIM", res

        if magic in ("ERF", "MOD", "SAV", "HAK"):
            entry_count = struct.unpack_from("<I", head, 16)[0]
            key_off = struct.unpack_from("<I", head, 24)[0]
            res_off = struct.unpack_from("<I", head, 28)[0]
            fh.seek(key_off)
            keys = fh.read(entry_count * 24)
            fh.seek(res_off)
            reslist = fh.read(entry_count * 8)
            if len(keys) < entry_count * 24 or len(reslist) < entry_count * 8:
                raise FormatError("truncated ERF tables")
            res = []
            for i in range(entry_count):
                resref, _rid, rtype, _unused = struct.unpack_from(
                    "<16sIHH", keys, i * 24
                )
                off, size = struct.unpack_from("<II", reslist, i * 8)
                name = resref.split(b"\x00")[0].decode("latin-1").lower()
                res.append(
                    ContainerRes(f"{name}.{restype_ext(rtype)}", size, off)
                )
            return magic, res

        raise FormatError(f"unknown container magic {magic!r}")


def container_res_hashes(path: str, res: list[ContainerRes]) -> dict[str, str]:
    out: dict[str, str] = {}
    with open(path, "rb") as fh:
        for r in sorted(res, key=lambda x: x.offset):
            fh.seek(r.offset)
            out[r.name] = sha256_bytes(fh.read(r.size))
    return out


# --------------------------------------------------------------------------
# Format-aware comparators. Each returns a JSON-able dict with "kind".
# --------------------------------------------------------------------------
def compare_tlk(pa: str, pb: str, max_examples: int) -> dict:
    a = read_tlk(pa)
    b = read_tlk(pb)
    common = min(len(a), len(b))
    changed = [
        i for i in range(common)
        if a[i].text != b[i].text or a[i].sound_resref != b[i].sound_resref
    ]
    first_div = changed[0] if changed else (common if len(a) != len(b) else None)
    samples = [
        {
            "index": i,
            "a_text": a[i].text[:200],
            "b_text": b[i].text[:200],
            "a_sound": a[i].sound_resref,
            "b_sound": b[i].sound_resref,
        }
        for i in changed[:max_examples]
    ]
    tail_side = "a" if len(a) > len(b) else ("b" if len(b) > len(a) else None)
    tail = (a if tail_side == "a" else b)[common:] if tail_side else []
    return {
        "kind": "tlk",
        "a_string_count": len(a),
        "b_string_count": len(b),
        "string_count_delta": len(b) - len(a),
        "changed_in_common_range": len(changed),
        "first_diverging_index": first_div,
        "changed_samples": samples,
        "extra_tail_side": tail_side,
        "extra_tail_samples": [
            {"index": e.index, "text": e.text[:200], "sound": e.sound_resref}
            for e in tail[:max_examples]
        ],
    }


def compare_2da(pa: str, pb: str, max_examples: int) -> dict:
    a = read_2da(pa)
    b = read_2da(pb)
    cols_a, cols_b = a.columns, b.columns
    common_cols = [c for c in cols_a if c in set(cols_b)]
    ia = {c: i for i, c in enumerate(cols_a)}
    ib = {c: i for i, c in enumerate(cols_b)}

    # Row identity: label first (KOTOR row labels are usually the row index),
    # falling back to positional when labels are non-unique.
    labels_unique = (
        len(set(a.row_labels)) == len(a.row_labels)
        and len(set(b.row_labels)) == len(b.row_labels)
    )
    if labels_unique:
        map_a = {lab: a.rows[i] for i, lab in enumerate(a.row_labels)}
        map_b = {lab: b.rows[i] for i, lab in enumerate(b.row_labels)}
    else:
        map_a = {str(i): r for i, r in enumerate(a.rows)}
        map_b = {str(i): r for i, r in enumerate(b.rows)}

    rows_only_a = sorted(set(map_a) - set(map_b), key=_natkey)
    rows_only_b = sorted(set(map_b) - set(map_a), key=_natkey)
    shared_rows = sorted(set(map_a) & set(map_b), key=_natkey)

    cell_diffs: list[dict] = []
    cell_diff_count = 0
    rows_touched: set[str] = set()
    cols_touched: dict[str, int] = {}
    for lab in shared_rows:
        ra, rb = map_a[lab], map_b[lab]
        for col in common_cols:
            va = ra[ia[col]] if ia[col] < len(ra) else ""
            vb = rb[ib[col]] if ib[col] < len(rb) else ""
            if va != vb:
                cell_diff_count += 1
                rows_touched.add(lab)
                cols_touched[col] = cols_touched.get(col, 0) + 1
                if len(cell_diffs) < max_examples:
                    cell_diffs.append(
                        {"row": lab, "column": col, "a": va, "b": vb}
                    )
    return {
        "kind": "2da",
        "a_rows": len(a.rows),
        "b_rows": len(b.rows),
        "row_count_delta": len(b.rows) - len(a.rows),
        "a_columns": len(cols_a),
        "b_columns": len(cols_b),
        "columns_only_in_a": sorted(set(cols_a) - set(cols_b)),
        "columns_only_in_b": sorted(set(cols_b) - set(cols_a)),
        "rows_only_in_a": rows_only_a[:max_examples],
        "rows_only_in_a_count": len(rows_only_a),
        "rows_only_in_b": rows_only_b[:max_examples],
        "rows_only_in_b_count": len(rows_only_b),
        "cell_diff_count": cell_diff_count,
        "rows_with_cell_diffs": len(rows_touched),
        "columns_with_cell_diffs": dict(
            sorted(cols_touched.items(), key=lambda kv: (-kv[1], kv[0]))
        ),
        "cell_diff_samples": cell_diffs,
    }


def _natkey(s: str):
    return (0, int(s)) if s.isdigit() else (1, s)


def compare_container(pa: str, pb: str, max_examples: int) -> dict:
    ta, ra = read_container(pa)
    tb, rb = read_container(pb)
    ma = {r.name: r for r in ra}
    mb = {r.name: r for r in rb}
    only_a = sorted(set(ma) - set(mb))
    only_b = sorted(set(mb) - set(ma))
    shared = sorted(set(ma) & set(mb))

    size_mismatch = [n for n in shared if ma[n].size != mb[n].size]
    ha = container_res_hashes(pa, [ma[n] for n in shared])
    hb = container_res_hashes(pb, [mb[n] for n in shared])
    changed = sorted(n for n in shared if ha[n] != hb[n])
    return {
        "kind": "container",
        "a_type": ta,
        "b_type": tb,
        "a_resource_count": len(ra),
        "b_resource_count": len(rb),
        "resource_count_delta": len(rb) - len(ra),
        "resources_only_in_a": only_a[:max_examples],
        "resources_only_in_a_count": len(only_a),
        "resources_only_in_b": only_b[:max_examples],
        "resources_only_in_b_count": len(only_b),
        "changed_resource_count": len(changed),
        "size_mismatch_count": len(size_mismatch),
        "identical_resource_count": len(shared) - len(changed),
        "changed_resources": [
            {
                "resource": n,
                "a_size": ma[n].size,
                "b_size": mb[n].size,
                "a_sha256": ha[n],
                "b_sha256": hb[n],
            }
            for n in changed[:max_examples]
        ],
        "reordered_only": len(changed) == 0
        and not only_a
        and not only_b,
    }


def format_compare(rel: str, pa: str, pb: str, max_examples: int) -> dict:
    ext = os.path.splitext(rel)[1].lower()
    try:
        if ext == ".tlk":
            return compare_tlk(pa, pb, max_examples)
        if ext == ".2da":
            return compare_2da(pa, pb, max_examples)
        if ext in CONTAINER_EXT:
            return compare_container(pa, pb, max_examples)
    except (FormatError, struct.error, OSError, IndexError) as exc:
        return {
            "kind": "raw",
            "fallback_reason": f"{type(exc).__name__}: {exc}",
        }
    return {"kind": "raw"}


# --------------------------------------------------------------------------
# Tree indexing
# --------------------------------------------------------------------------
@dataclass
class FileRec:
    rel: str          # actual on-disk relative path
    size: int
    dev: int
    ino: int
    nlink: int
    is_symlink: bool = False
    link_target: str | None = None


@dataclass
class TreeIndex:
    root: str
    files: dict[str, FileRec] = field(default_factory=dict)  # key -> rec
    case_collisions: dict[str, list[str]] = field(default_factory=dict)
    symlink_aliases: dict[str, str] = field(default_factory=dict)
    hardlink_groups: dict[tuple[int, int], list[str]] = field(default_factory=dict)
    walk_errors: list[str] = field(default_factory=list)


def index_tree(
    root: str,
    includes: list[str] | None,
    excludes: list[str] | None,
) -> TreeIndex:
    idx = TreeIndex(root=os.path.abspath(root))
    real_root = os.path.realpath(root)
    pending_links: list[tuple[str, str, os.stat_result]] = []

    def on_err(exc: OSError) -> None:
        idx.walk_errors.append(f"{exc.filename}: {exc.strerror}")

    for dirpath, dirnames, filenames in os.walk(root, onerror=on_err,
                                                followlinks=False):
        # Symlinked directories are not descended into (followlinks=False), so
        # they contribute no phantom files. Record the in-tree ones anyway:
        # ModSync creates `override -> Override` for case folding, and knowing
        # that is the difference between "understood" and "unexplained".
        for dn in dirnames:
            full = os.path.join(dirpath, dn)
            if not os.path.islink(full):
                continue
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            target = os.path.realpath(full)
            if target.startswith(real_root + os.sep) or target == real_root:
                idx.symlink_aliases[rel.lower()] = (
                    os.path.relpath(target, real_root).replace(os.sep, "/")
                    if target != real_root else "."
                )
            else:
                idx.walk_errors.append(
                    f"{rel}: directory symlink escapes the tree -> {target}"
                )

        for fn in filenames:
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            key = rel.lower()
            if excludes and any(fnmatch.fnmatch(key, g.lower()) for g in excludes):
                continue
            if includes and not any(
                fnmatch.fnmatch(key, g.lower()) for g in includes
            ):
                continue
            try:
                st = os.lstat(full)
            except OSError as exc:
                idx.walk_errors.append(f"{full}: {exc.strerror}")
                continue
            if os.path.islink(full):
                target = os.path.realpath(full)
                if target.startswith(real_root + os.sep):
                    trel = os.path.relpath(target, real_root).replace(os.sep, "/")
                    idx.symlink_aliases[key] = trel
                    continue  # case-fold alias, not a distinct file
                pending_links.append((key, rel, st))
                continue
            _add(idx, key, rel, full, st)

    # Symlinks pointing outside the tree are real content contributions.
    for key, rel, _st in pending_links:
        full = os.path.join(root, rel)
        try:
            st = os.stat(full)
        except OSError as exc:
            idx.walk_errors.append(f"{full}: {exc.strerror}")
            continue
        rec = _add(idx, key, rel, full, st)
        if rec:
            rec.is_symlink = True
            rec.link_target = os.path.realpath(full)

    for key, rec in idx.files.items():
        if rec.nlink > 1:
            idx.hardlink_groups.setdefault((rec.dev, rec.ino), []).append(key)
    idx.hardlink_groups = {
        k: sorted(v) for k, v in idx.hardlink_groups.items() if len(v) > 1
    }
    return idx


def _add(idx: TreeIndex, key: str, rel: str, full: str,
         st: os.stat_result) -> FileRec | None:
    if key in idx.files:
        prev = idx.files[key]
        if (prev.dev, prev.ino) != (st.st_dev, st.st_ino):
            idx.case_collisions.setdefault(key, [prev.rel]).append(rel)
        return None
    rec = FileRec(
        rel=rel, size=st.st_size, dev=st.st_dev, ino=st.st_ino,
        nlink=st.st_nlink,
    )
    idx.files[key] = rec
    return rec


# --------------------------------------------------------------------------
# Attribution
# --------------------------------------------------------------------------
@dataclass
class Attribution:
    by_basename: dict[str, list[dict]] = field(default_factory=dict)
    components_seen: list[str] = field(default_factory=list)
    sources: list[str] = field(default_factory=list)

    def lookup(self, rel_key: str) -> list[dict]:
        return self.by_basename.get(os.path.basename(rel_key), [])


# Ledger keys that may carry lists of installed/removed file names. The manual
# harnesses have used several shapes over time; accept all of them rather than
# silently attributing nothing.
LEDGER_FILE_KEYS = (
    "file_names", "files_installed", "added_sample", "added_files",
    "modified_files", "installed", "touches", "modules_modified",
    "modules_added", "streamwaves_added",
)
LEDGER_DELETE_KEYS = ("deletions", "files_removed", "files_quarantined_case_fold",
                      "files_quarantined_tpc_shadow", "quarantine")


def load_ledger(path: str, attr: Attribution, side: str) -> None:
    if not os.path.isfile(path):
        return
    attr.sources.append(f"{side}:ledger={path}")
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            comp = rec.get("component") or rec.get("guide_heading") or "?"
            if comp not in attr.components_seen:
                attr.components_seen.append(comp)
            entry = {
                "side": side,
                "source": "ledger",
                "step": rec.get("step"),
                "component": comp,
                "archives": rec.get("archives_used") or rec.get("source") or [],
                "options": rec.get("options_selected")
                or rec.get("choice")
                or rec.get("namespace")
                or [],
                "patcher": (rec.get("patcher") or {}).get("engine")
                if isinstance(rec.get("patcher"), dict) else None,
            }
            def absorb(keys: tuple[str, ...], note: str | None) -> None:
                for key in keys:
                    val = rec.get(key)
                    if not isinstance(val, list):
                        continue
                    for name in val:
                        if not isinstance(name, str):
                            continue
                        base = os.path.basename(name.replace("\\", "/")).lower()
                        if not base or "/" in base:
                            continue
                        item = dict(entry, ledger_key=key)
                        if note:
                            item["note"] = note
                        attr.by_basename.setdefault(base, []).append(item)

            absorb(LEDGER_FILE_KEYS, None)
            absorb(LEDGER_DELETE_KEYS, "deletion")


_LOG_COMPONENT_MARKERS = (
    "for '",          # "Added 2 instruction(s) from local archive for 'X': ..."
    "Installing ",
    "Component: ",
)


def load_install_log(path: str, attr: Attribution, side: str,
                     wanted: set[str]) -> None:
    """Attribute basenames mentioned in a ModSync install log to the most
    recent component marker preceding the mention."""
    if not os.path.isfile(path):
        return
    attr.sources.append(f"{side}:install_log={path}")
    current = None
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if "for '" in line:
                seg = line.split("for '", 1)[1]
                if "'" in seg:
                    current = seg.split("'", 1)[0]
                    if current not in attr.components_seen:
                        attr.components_seen.append(current)
            elif "Installing " in line:
                current = line.split("Installing ", 1)[1].strip().rstrip(".")
                if current not in attr.components_seen:
                    attr.components_seen.append(current)
            low = line.lower()
            if not wanted:
                continue
            for base in wanted:
                if base in low:
                    attr.by_basename.setdefault(base, []).append(
                        {
                            "side": side,
                            "source": "install_log",
                            "component": current,
                            "line": line.strip()[:240],
                        }
                    )


# --------------------------------------------------------------------------
# Diff engine
# --------------------------------------------------------------------------
def run_diff(args: argparse.Namespace) -> dict:
    a_root, b_root = args.a_dir, args.b_dir
    ia = index_tree(a_root, args.include, args.exclude)
    ib = index_tree(b_root, args.include, args.exclude)

    keys_a, keys_b = set(ia.files), set(ib.files)
    only_a = sorted(keys_a - keys_b)
    only_b = sorted(keys_b - keys_a)
    common = sorted(keys_a & keys_b)

    # Format twins: same directory + stem, different image/asset extension.
    def stem_map(keys: list[str]) -> dict[str, list[str]]:
        out: dict[str, list[str]] = {}
        for k in keys:
            base, ext = os.path.splitext(k)
            if ext in IMAGE_TWIN_EXT:
                out.setdefault(base, []).append(k)
        return out

    twins_a, twins_b = stem_map(only_a), stem_map(only_b)
    format_twins = []
    twin_keys: set[str] = set()
    for stem in sorted(set(twins_a) & set(twins_b)):
        format_twins.append(
            {
                "stem": stem,
                "a_files": sorted(twins_a[stem]),
                "b_files": sorted(twins_b[stem]),
            }
        )
        twin_keys.update(twins_a[stem])
        twin_keys.update(twins_b[stem])

    only_a_final = [k for k in only_a if k not in twin_keys]
    only_b_final = [k for k in only_b if k not in twin_keys]

    # Content comparison over common keys.
    same_inode: list[str] = []
    size_diff: list[str] = []
    to_hash: list[str] = []
    for k in common:
        ra, rb = ia.files[k], ib.files[k]
        if (ra.dev, ra.ino) == (rb.dev, rb.ino):
            same_inode.append(k)
            if not args.no_inode_shortcut:
                continue
        if ra.size != rb.size:
            size_diff.append(k)
            continue
        to_hash.append(k)

    if args.hash_mode == "sizefirst":
        to_hash = []

    hashes_a: dict[str, str] = {}
    hashes_b: dict[str, str] = {}

    def hash_pair(k: str) -> tuple[str, str, str]:
        return (
            k,
            sha256_file(os.path.join(a_root, ia.files[k].rel)),
            sha256_file(os.path.join(b_root, ib.files[k].rel)),
        )

    if to_hash:
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            for k, ha, hb in pool.map(hash_pair, to_hash):
                hashes_a[k], hashes_b[k] = ha, hb

    content_diff = sorted(
        size_diff + [k for k in to_hash if hashes_a[k] != hashes_b[k]]
    )

    # Hash the size-differing files too, so the report always carries both sides.
    if size_diff:
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            for k, ha, hb in pool.map(hash_pair, size_diff):
                hashes_a[k], hashes_b[k] = ha, hb

    # Attribution index built only for the basenames that actually differ.
    attr = Attribution()
    if args.ledger:
        load_ledger(args.ledger, attr, args.a_name)
    wanted = {
        os.path.basename(k)
        for k in content_diff + only_a_final + only_b_final
    }
    if args.install_log:
        load_install_log(args.install_log, attr, args.b_name, wanted)

    # Build per-difference records.
    diffs: list[dict] = []
    for k in content_diff:
        ra, rb = ia.files[k], ib.files[k]
        rec = {
            "category": "content_differs",
            "path": k,
            "a_path": ra.rel,
            "b_path": rb.rel,
            "a_size": ra.size,
            "b_size": rb.size,
            "a_sha256": hashes_a.get(k),
            "b_sha256": hashes_b.get(k),
            "bookkeeping": is_bookkeeping(k),
            "attribution": attr.lookup(k),
        }
        if not args.no_deep and not rec["bookkeeping"]:
            rec["detail"] = format_compare(
                k,
                os.path.join(a_root, ra.rel),
                os.path.join(b_root, rb.rel),
                args.max_examples,
            )
        diffs.append(rec)

    for k in only_a_final:
        ra = ia.files[k]
        diffs.append(
            {
                "category": "only_in_a",
                "path": k,
                "a_path": ra.rel,
                "a_size": ra.size,
                "bookkeeping": is_bookkeeping(k),
                "attribution": attr.lookup(k),
            }
        )
    for k in only_b_final:
        rb = ib.files[k]
        diffs.append(
            {
                "category": "only_in_b",
                "path": k,
                "b_path": rb.rel,
                "b_size": rb.size,
                "bookkeeping": is_bookkeeping(k),
                "attribution": attr.lookup(k),
            }
        )
    for t in format_twins:
        diffs.append(
            {
                "category": "format_twin",
                "path": t["stem"],
                "a_files": t["a_files"],
                "b_files": t["b_files"],
                "bookkeeping": False,
                "attribution": attr.lookup(t["a_files"][0]),
            }
        )
    diffs.sort(key=lambda d: (d["category"], d["path"]))

    # Cross-tree inode sharing. This is a HAZARD only when the two trees were
    # supposed to be independent (a manual build vs an automated build, or a
    # build vs its own rollback snapshot): there, a shared inode means one side
    # is not a real copy and every in-place write silently mutates "both".
    # It is EXPECTED, and merely an observation, for knowingly-aliased reference
    # pairs (e.g. K1_vanilla is hardlinked to snap_0000) and for self-compares.
    self_compare = os.path.realpath(a_root) == os.path.realpath(b_root)
    expect_independent = not (args.allow_shared_inodes or self_compare)
    cross_alias = [
        {
            "path": k,
            "a_path": ia.files[k].rel,
            "b_path": ib.files[k].rel,
            "dev": ia.files[k].dev,
            "ino": ia.files[k].ino,
            "nlink": ia.files[k].nlink,
        }
        for k in same_inode
    ]

    # A case collision only endangers game state if it is in game content;
    # collisions inside installer backup dirs are noise (real example: the
    # manual K1 build's tpc-backup/ holds both LTS_lotrim11.tpc and
    # lts_lotrim11.tpc, which the engine never sees).
    def split_collisions(cols: dict[str, list[str]]):
        game = {k: v for k, v in sorted(cols.items()) if not is_bookkeeping(k)}
        book = {k: v for k, v in sorted(cols.items()) if is_bookkeeping(k)}
        return game, book

    coll_a, coll_a_book = split_collisions(ia.case_collisions)
    coll_b, coll_b_book = split_collisions(ib.case_collisions)

    real = [d for d in diffs if not d["bookkeeping"]]
    bookkeeping = [d for d in diffs if d["bookkeeping"]]
    hazards = len(coll_a) + len(coll_b)
    if expect_independent:
        hazards += len(cross_alias)
    verdict = (
        "MATCH"
        if not real and not hazards
        else f"DIVERGENT ({len(real)} real differences, {hazards} hazards)"
    )

    by_cat: dict[str, int] = {}
    for d in real:
        by_cat[d["category"]] = by_cat.get(d["category"], 0) + 1

    return {
        "schema": "kotor-install-diff/1",
        "label": args.label,
        "a": {"name": args.a_name, "dir": ia.root, "file_count": len(ia.files)},
        "b": {"name": args.b_name, "dir": ib.root, "file_count": len(ib.files)},
        "settings": {
            "hash_mode": args.hash_mode,
            "deep_format_compare": not args.no_deep,
            "inode_shortcut": not args.no_inode_shortcut,
            "trees_expected_independent": expect_independent,
            "self_compare": self_compare,
            "include": args.include or [],
            "exclude": args.exclude or [],
            "max_examples": args.max_examples,
        },
        "counts": {
            "common": len(common),
            "hashed": len(set(to_hash) | set(size_diff)),
            "real_differences": len(real),
            "bookkeeping_differences": len(bookkeeping),
            "hazards": hazards,
            "by_category": dict(sorted(by_cat.items())),
        },
        "hazards": {
            "case_collisions_a": coll_a,
            "case_collisions_b": coll_b,
            "case_collisions_bookkeeping_a": coll_a_book,
            "case_collisions_bookkeeping_b": coll_b_book,
            "cross_tree_shared_inodes": cross_alias[: args.max_examples],
            "cross_tree_shared_inode_count": len(cross_alias),
            "cross_tree_shared_inodes_are_hazard": expect_independent,
            "intra_tree_hardlink_groups_a": len(ia.hardlink_groups),
            "intra_tree_hardlink_groups_b": len(ib.hardlink_groups),
            "walk_errors_a": ia.walk_errors[:20],
            "walk_errors_b": ib.walk_errors[:20],
        },
        "symlink_aliases": {
            "a_count": len(ia.symlink_aliases),
            "b_count": len(ib.symlink_aliases),
            "a_examples": dict(sorted(ia.symlink_aliases.items())[: args.max_examples]),
            "b_examples": dict(sorted(ib.symlink_aliases.items())[: args.max_examples]),
        },
        "attribution_sources": attr.sources,
        "unattributed_real_differences": sum(
            1 for d in real if not d["attribution"]
        ),
        "differences": diffs,
        "verdict": verdict,
    }


# --------------------------------------------------------------------------
# Markdown rendering
# --------------------------------------------------------------------------
def render_md(rep: dict, max_rows: int = 40) -> str:
    a, b = rep["a"], rep["b"]
    c = rep["counts"]
    lines = [
        f"# KOTOR install diff{' — ' + rep['label'] if rep['label'] else ''}",
        "",
        f"- **{a['name']} (A)**: `{a['dir']}` — {a['file_count']} files",
        f"- **{b['name']} (B)**: `{b['dir']}` — {b['file_count']} files",
        f"- common paths: {c['common']}, content-hashed: {c['hashed']}",
        "",
        f"## Verdict: {rep['verdict']}",
        "",
        "| category | count |",
        "|---|---|",
    ]
    for cat, n in c["by_category"].items():
        lines.append(f"| {cat} | {n} |")
    lines += [
        f"| bookkeeping (excluded from verdict) | {c['bookkeeping_differences']} |",
        f"| hazards | {c['hazards']} |",
        f"| unattributed real differences | {rep['unattributed_real_differences']} |",
        "",
    ]

    hz = rep["hazards"]
    if hz["cross_tree_shared_inode_count"]:
        hazardous = hz["cross_tree_shared_inodes_are_hazard"]
        lines += [
            ("## HAZARD: cross-tree hardlink aliasing" if hazardous
             else "## Observation: cross-tree shared inodes (expected)"),
            "",
            f"{hz['cross_tree_shared_inode_count']} paths share an inode "
            f"between `{a['dir']}` and `{b['dir']}`. These are the SAME file on "
            "disk, so an in-place write to one mutates the other.",
            "",
        ]
        lines.append(
            "**The two trees were assumed independent, so any 'match' on these "
            "paths is meaningless — one tree is not a real copy.**"
            if hazardous else
            "These trees are declared knowingly aliased (`--allow-shared-inodes` "
            "or a self-compare), so this is reported for context only and does "
            "not affect the verdict."
        )
        lines.append("")
        for x in hz["cross_tree_shared_inodes"][:max_rows]:
            lines.append(f"- `{x['path']}` (ino={x['ino']}, nlink={x['nlink']})")
        lines.append("")
    for side in ("a", "b"):
        col = hz[f"case_collisions_{side}"]
        if col:
            lines += [
                f"## HAZARD: case collisions in {rep[side]['name']}",
                "",
                "Two distinct files whose paths differ only by case. The game "
                "engine is case-insensitive, so which one wins is undefined.",
                "",
            ]
            for k, paths in list(col.items())[:max_rows]:
                lines.append(f"- `{k}`: {' | '.join(paths)}")
            lines.append("")

    real = [d for d in rep["differences"] if not d["bookkeeping"]]

    def section(title: str, cat: str, fmt) -> None:
        items = [d for d in real if d["category"] == cat]
        if not items:
            return
        lines.append(f"## {title} ({len(items)})")
        lines.append("")
        for d in items[:max_rows]:
            lines.append(fmt(d))
        if len(items) > max_rows:
            lines.append(f"- ... +{len(items) - max_rows} more (see JSON)")
        lines.append("")

    def attr_str(d: dict) -> str:
        at = d.get("attribution") or []
        if not at:
            return " — **unattributed**"
        first = at[0]
        comp = first.get("component") or "?"
        step = first.get("step")
        extra = ""
        others = {
            a_["component"] for a_ in at[1:] if a_.get("component") != comp
        }
        if others:
            extra = f" (+{len(others)} other component(s))"
        return (
            f" — attributed: {comp}"
            + (f" [step {step}]" if step is not None else "")
            + extra
        )

    section(
        f"Present only in {a['name']} (B did not install)",
        "only_in_a",
        lambda d: f"- `{d['a_path']}` ({d['a_size']} B){attr_str(d)}",
    )
    section(
        f"Present only in {b['name']} (A did not install)",
        "only_in_b",
        lambda d: f"- `{d['b_path']}` ({d['b_size']} B){attr_str(d)}",
    )
    section(
        "Format twins (same asset, different container format)",
        "format_twin",
        lambda d: f"- `{d['path']}`: A={d['a_files']} vs B={d['b_files']}",
    )

    content = [d for d in real if d["category"] == "content_differs"]
    if content:
        lines += [f"## Content differs ({len(content)})", ""]
        # Deep-format items first: they carry the actionable detail.
        def rank(d: dict) -> tuple:
            kind = (d.get("detail") or {}).get("kind", "raw")
            order = {"tlk": 0, "2da": 1, "container": 2, "raw": 3}
            return (order.get(kind, 4), d["path"])

        for d in sorted(content, key=rank)[:max_rows]:
            det = d.get("detail") or {"kind": "raw"}
            head = (
                f"### `{d['a_path']}` [{det['kind']}]{attr_str(d)}\n\n"
                f"- A: {d['a_size']} B, sha256 `{(d['a_sha256'] or '')[:16]}`\n"
                f"- B: {d['b_size']} B, sha256 `{(d['b_sha256'] or '')[:16]}`"
            )
            lines.append(head)
            if det["kind"] == "tlk":
                lines.append(
                    f"- strings: A={det['a_string_count']} B={det['b_string_count']} "
                    f"(delta {det['string_count_delta']:+d}); "
                    f"changed within common range: {det['changed_in_common_range']}; "
                    f"first diverging index: {det['first_diverging_index']}"
                )
                for s in det["changed_samples"][:5]:
                    lines.append(
                        f"  - [{s['index']}] A={s['a_text']!r} B={s['b_text']!r}"
                    )
                for s in det["extra_tail_samples"][:5]:
                    lines.append(
                        f"  - extra in {det['extra_tail_side'].upper()} "
                        f"[{s['index']}] {s['text']!r}"
                    )
            elif det["kind"] == "2da":
                lines.append(
                    f"- rows: A={det['a_rows']} B={det['b_rows']} "
                    f"(delta {det['row_count_delta']:+d}); "
                    f"columns: A={det['a_columns']} B={det['b_columns']}; "
                    f"differing cells: {det['cell_diff_count']} across "
                    f"{det['rows_with_cell_diffs']} rows"
                )
                if det["columns_only_in_a"] or det["columns_only_in_b"]:
                    lines.append(
                        f"- columns only in A: {det['columns_only_in_a']}; "
                        f"only in B: {det['columns_only_in_b']}"
                    )
                if det["columns_with_cell_diffs"]:
                    top = list(det["columns_with_cell_diffs"].items())[:6]
                    lines.append(f"- hottest columns: {top}")
                for s in det["cell_diff_samples"][:5]:
                    lines.append(
                        f"  - row {s['row']} col {s['column']}: "
                        f"A={s['a']!r} B={s['b']!r}"
                    )
            elif det["kind"] == "container":
                lines.append(
                    f"- resources: A={det['a_resource_count']} "
                    f"B={det['b_resource_count']} "
                    f"(delta {det['resource_count_delta']:+d}); "
                    f"changed: {det['changed_resource_count']}; "
                    f"identical: {det['identical_resource_count']}"
                )
                if det["reordered_only"]:
                    lines.append(
                        "- **byte difference is ordering/padding only — "
                        "resource content is identical**"
                    )
                if det["resources_only_in_a_count"]:
                    lines.append(
                        f"- only in A ({det['resources_only_in_a_count']}): "
                        f"{det['resources_only_in_a'][:8]}"
                    )
                if det["resources_only_in_b_count"]:
                    lines.append(
                        f"- only in B ({det['resources_only_in_b_count']}): "
                        f"{det['resources_only_in_b'][:8]}"
                    )
                for s in det["changed_resources"][:5]:
                    lines.append(
                        f"  - `{s['resource']}` {s['a_size']} vs {s['b_size']} B"
                    )
            elif det.get("fallback_reason"):
                lines.append(f"- raw compare (parse failed: {det['fallback_reason']})")
            lines.append("")
        if len(content) > max_rows:
            lines.append(f"- ... +{len(content) - max_rows} more (see JSON)")
            lines.append("")

    lines += [
        "## What to look at first",
        "",
        "1. Any **cross-tree hardlink alias** flagged as a hazard — it "
        "invalidates the comparison itself before any content question matters.",
        "2. `dialog.tlk` string-count delta — a nonzero delta means the two sides "
        "ran different TLK-merge semantics (e.g. `Replace0` honored vs whole "
        "`append.tlk` appended).",
        "3. `.2da` row-count deltas and hot columns — these are patcher-semantics "
        "differences, not cosmetics.",
        "4. Containers where `reordered_only` is false — genuinely different "
        "module content.",
        "5. `only_in_*` entries that are **unattributed** — nothing in either "
        "install record explains them.",
        "",
    ]
    return "\n".join(lines)


# --------------------------------------------------------------------------
# Self-test: synthesize fixtures and assert the comparators say the right thing.
# --------------------------------------------------------------------------
def _write_tlk(path: str, strings: list[str]) -> None:
    entries = b""
    blob = b""
    for s in strings:
        raw = s.encode("windows-1252")
        entries += struct.pack(
            "<I16sIIIII", 7, b"\x00" * 16, 0, 0, len(blob), len(raw), 0
        )
        blob += raw
    head = struct.pack("<8sIII", b"TLK V3.0", 0, len(strings), 20 + len(entries))
    with open(path, "wb") as fh:
        fh.write(head + entries + blob)


def _write_2da(path: str, columns: list[str], row_labels: list[str],
               rows: list[list[str]]) -> None:
    out = b"2DA V2.b\n"
    for c in columns:
        out += c.encode("windows-1252") + b"\t"
    out += b"\x00"
    out += struct.pack("<I", len(rows))
    for lab in row_labels:
        out += lab.encode("windows-1252") + b"\t"
    data = b""
    offsets: list[int] = []
    pool: dict[str, int] = {}
    for row in rows:
        for cell in row:
            if cell not in pool:
                pool[cell] = len(data)
                data += cell.encode("windows-1252") + b"\x00"
            offsets.append(pool[cell])
    out += struct.pack(f"<{len(offsets)}H", *offsets)
    out += struct.pack("<H", len(data))
    out += data
    with open(path, "wb") as fh:
        fh.write(out)


def _write_erf(path: str, resources: list[tuple[str, int, bytes]]) -> None:
    n = len(resources)
    key_off = 160
    res_off = key_off + n * 24
    data_off = res_off + n * 8
    head = struct.pack(
        "<8sIIIIIIIII", b"ERF V1.0", 0, 0, n, key_off, key_off, res_off,
        2026, 100, 0xFFFFFFFF,
    )
    head += b"\x00" * (160 - len(head))
    keys = b""
    reslist = b""
    data = b""
    for i, (name, rtype, payload) in enumerate(resources):
        keys += struct.pack(
            "<16sIHH", name.encode("latin-1").ljust(16, b"\x00"), i, rtype, 0
        )
        reslist += struct.pack("<II", data_off + len(data), len(payload))
        data += payload
    with open(path, "wb") as fh:
        fh.write(head + keys + reslist + data)


def _write_rim(path: str, resources: list[tuple[str, int, bytes]]) -> None:
    n = len(resources)
    entry_off = 120
    data_off = entry_off + n * 32
    head = struct.pack("<8sIII", b"RIM V1.0", 0, n, entry_off)
    head += b"\x00" * (120 - len(head))
    entries = b""
    data = b""
    for i, (name, rtype, payload) in enumerate(resources):
        entries += struct.pack(
            "<16sIIII", name.encode("latin-1").ljust(16, b"\x00"), rtype, i,
            data_off + len(data), len(payload),
        )
        data += payload
    with open(path, "wb") as fh:
        fh.write(head + entries + data)


def selftest() -> int:
    import tempfile

    failures: list[str] = []

    def check(name: str, cond: bool, got=None) -> None:
        if cond:
            print(f"  PASS  {name}")
        else:
            print(f"  FAIL  {name}  got={got!r}")
            failures.append(name)

    with tempfile.TemporaryDirectory(prefix="kdiff-selftest-") as td:
        print("TLK")
        base = ["hello", "world", "third"]
        _write_tlk(f"{td}/a.tlk", base)
        _write_tlk(f"{td}/b.tlk", base)
        _write_tlk(f"{td}/c.tlk", base + ["appended-1", "appended-2"])
        _write_tlk(f"{td}/d.tlk", ["hello", "REPLACED", "third"])
        r = compare_tlk(f"{td}/a.tlk", f"{td}/b.tlk", 5)
        check("tlk identical -> no changes", r["changed_in_common_range"] == 0
              and r["string_count_delta"] == 0, r)
        r = compare_tlk(f"{td}/a.tlk", f"{td}/c.tlk", 5)
        check("tlk append -> +2 count, 0 in-range changes",
              r["string_count_delta"] == 2
              and r["changed_in_common_range"] == 0
              and r["extra_tail_side"] == "b"
              and r["extra_tail_samples"][0]["text"] == "appended-1", r)
        r = compare_tlk(f"{td}/a.tlk", f"{td}/d.tlk", 5)
        check("tlk in-place replace -> index 1 diverges",
              r["string_count_delta"] == 0
              and r["first_diverging_index"] == 1
              and r["changed_samples"][0]["b_text"] == "REPLACED", r)

        print("2DA")
        cols = ["label", "race"]
        _write_2da(f"{td}/a.2da", cols, ["0", "1"],
                   [["Alpha", "1"], ["Beta", "2"]])
        _write_2da(f"{td}/b.2da", cols, ["0", "1"],
                   [["Alpha", "1"], ["Beta", "2"]])
        _write_2da(f"{td}/c.2da", cols, ["0", "1"],
                   [["Alpha", "1"], ["Beta", "99"]])
        _write_2da(f"{td}/d.2da", cols, ["0", "1", "2"],
                   [["Alpha", "1"], ["Beta", "2"], ["Gamma", "3"]])
        _write_2da(f"{td}/e.2da", cols + ["newcol"], ["0", "1"],
                   [["Alpha", "1", "x"], ["Beta", "2", "y"]])
        parsed = read_2da(f"{td}/a.2da")
        check("2da roundtrip", parsed.columns == cols
              and parsed.rows == [["Alpha", "1"], ["Beta", "2"]], parsed)
        r = compare_2da(f"{td}/a.2da", f"{td}/b.2da", 5)
        check("2da identical", r["cell_diff_count"] == 0
              and r["row_count_delta"] == 0, r)
        r = compare_2da(f"{td}/a.2da", f"{td}/c.2da", 5)
        check("2da one cell changed -> row 1 col race",
              r["cell_diff_count"] == 1
              and r["cell_diff_samples"][0] == {
                  "row": "1", "column": "race", "a": "2", "b": "99"}, r)
        r = compare_2da(f"{td}/a.2da", f"{td}/d.2da", 5)
        check("2da row appended -> +1 row, rows_only_in_b=['2']",
              r["row_count_delta"] == 1 and r["rows_only_in_b"] == ["2"]
              and r["cell_diff_count"] == 0, r)
        r = compare_2da(f"{td}/a.2da", f"{td}/e.2da", 5)
        check("2da new column detected",
              r["columns_only_in_b"] == ["newcol"]
              and r["cell_diff_count"] == 0, r)

        print("ERF/RIM containers")
        res = [("aa", 2027, b"AAAA"), ("bb", 2029, b"BBBBBB")]
        _write_erf(f"{td}/a.mod", res)
        _write_erf(f"{td}/b.mod", list(reversed(res)))  # reordered only
        _write_erf(f"{td}/c.mod", [("aa", 2027, b"AAAA"),
                                   ("bb", 2029, b"CHANGED")])
        _write_erf(f"{td}/d.mod", res + [("cc", 2025, b"CC")])
        _write_rim(f"{td}/a.rim", res)
        t, parsed_res = read_container(f"{td}/a.mod")
        check("erf parse", t == "ERF"
              and [x.name for x in parsed_res] == ["aa.utc", "bb.dlg"],
              [x.name for x in parsed_res])
        t, parsed_res = read_container(f"{td}/a.rim")
        check("rim parse", t == "RIM"
              and [x.name for x in parsed_res] == ["aa.utc", "bb.dlg"],
              [x.name for x in parsed_res])
        r = compare_container(f"{td}/a.mod", f"{td}/b.mod", 5)
        check("container reorder -> reordered_only", r["reordered_only"] is True
              and r["changed_resource_count"] == 0, r)
        r = compare_container(f"{td}/a.mod", f"{td}/c.mod", 5)
        check("container changed resource -> bb.dlg",
              r["changed_resource_count"] == 1
              and r["changed_resources"][0]["resource"] == "bb.dlg", r)
        r = compare_container(f"{td}/a.mod", f"{td}/d.mod", 5)
        check("container added resource -> cc.uti only in B",
              r["resources_only_in_b"] == ["cc.uti"]
              and r["resource_count_delta"] == 1, r)

        print("tree indexing")
        ta, tb = f"{td}/treeA", f"{td}/treeB"
        for t_ in (ta, tb):
            os.makedirs(f"{t_}/Override", exist_ok=True)
            with open(f"{t_}/Override/same.txt", "w") as fh:
                fh.write("same")
        # case-fold symlink dir: treeB/override -> Override
        os.symlink("Override", f"{tb}/override")
        # genuine case collision inside treeA
        with open(f"{ta}/Override/Case.txt", "w") as fh:
            fh.write("one")
        os.makedirs(f"{ta}/override", exist_ok=True)
        with open(f"{ta}/override/case.txt", "w") as fh:
            fh.write("two")
        ia = index_tree(ta, None, None)
        ib_ = index_tree(tb, None, None)
        check("symlinked case-fold dir is not walked as content",
              len(ib_.files) == 1, sorted(ib_.files))
        check("symlinked case-fold dir IS recorded as an alias",
              ib_.symlink_aliases.get("override") == "Override",
              ib_.symlink_aliases)
        check("genuine case collision detected",
              "override/case.txt" in ia.case_collisions,
              ia.case_collisions)

        print("case collisions in bookkeeping dirs are not game-state hazards")
        tbk = f"{td}/bk"
        os.makedirs(f"{tbk}/tpc-backup", exist_ok=True)
        with open(f"{tbk}/tpc-backup/LTS_x.tpc", "wb") as fh:
            fh.write(b"one")
        with open(f"{tbk}/tpc-backup/lts_x.tpc", "wb") as fh:
            fh.write(b"two")
        tbk2 = f"{td}/bk2"
        os.makedirs(tbk2, exist_ok=True)
        ns0 = argparse.Namespace(
            a_dir=tbk, b_dir=tbk2, a_name="A", b_name="B", label="",
            include=None, exclude=None, jobs=2, hash_mode="all",
            no_inode_shortcut=False, no_deep=True, max_examples=5,
            ledger=None, install_log=None, allow_shared_inodes=False,
        )
        rep = run_diff(ns0)
        check("tpc-backup case collision -> bookkeeping, hazards=0",
              rep["counts"]["hazards"] == 0
              and len(rep["hazards"]["case_collisions_bookkeeping_a"]) == 1
              and len(rep["hazards"]["case_collisions_a"]) == 0,
              rep["hazards"])

        print("hardlink aliasing")
        thA, th = f"{td}/aliasA", f"{td}/aliasB"
        os.makedirs(thA, exist_ok=True)
        os.makedirs(th, exist_ok=True)
        with open(f"{thA}/same.txt", "w") as fh:
            fh.write("same")
        os.link(f"{thA}/same.txt", f"{th}/same.txt")
        ns = argparse.Namespace(
            a_dir=thA, b_dir=th, a_name="A", b_name="B",
            label="", include=None, exclude=None, jobs=2, hash_mode="all",
            no_inode_shortcut=False, no_deep=True, max_examples=5,
            ledger=None, install_log=None, allow_shared_inodes=False,
        )
        rep = run_diff(ns)
        check("cross-tree shared inode surfaced as hazard when independence "
              "assumed",
              rep["hazards"]["cross_tree_shared_inode_count"] == 1
              and rep["hazards"]["cross_tree_shared_inodes_are_hazard"] is True
              and rep["verdict"].startswith("DIVERGENT"),
              rep["hazards"]["cross_tree_shared_inode_count"])

        ns.allow_shared_inodes = True
        rep = run_diff(ns)
        check("known-aliased reference pair -> observation, not hazard",
              rep["hazards"]["cross_tree_shared_inode_count"] == 1
              and rep["hazards"]["cross_tree_shared_inodes_are_hazard"] is False
              and rep["verdict"] == "MATCH", rep["counts"])
        ns.allow_shared_inodes = False

        print("cp -al alias mutated in place (the oracle-corruption failure mode)")
        tm, tm2 = f"{td}/treeM", f"{td}/treeM2"
        os.makedirs(tm, exist_ok=True)
        os.makedirs(tm2, exist_ok=True)
        with open(f"{tm}/x.bin", "wb") as fh:
            fh.write(b"original")
        os.link(f"{tm}/x.bin", f"{tm2}/x.bin")   # what `cp -al` produces
        with open(f"{tm}/x.bin", "r+b") as fh:   # in-place write hits BOTH
            fh.write(b"MUTATED!")
        with open(f"{tm2}/x.bin", "rb") as fh:
            leaked = fh.read()
        ns.a_dir, ns.b_dir = tm, tm2
        rep = run_diff(ns)
        check("the mutation really did leak through the alias (fixture sanity)",
              leaked == b"MUTATED!", leaked)
        check("aliased+mutated pair is NOT reported as a clean match",
              rep["counts"]["real_differences"] == 0
              and rep["counts"]["hazards"] == 1
              and rep["verdict"].startswith("DIVERGENT"), rep["counts"])

        print("clean self-compare")
        ns.a_dir, ns.b_dir = f"{ta}/Override", f"{ta}/Override"
        ns.no_inode_shortcut = True
        rep = run_diff(ns)
        check("dir vs itself -> zero differences, zero hazards, MATCH",
              rep["counts"]["real_differences"] == 0
              and rep["counts"]["hazards"] == 0
              and rep["settings"]["self_compare"] is True
              and rep["verdict"] == "MATCH", rep["counts"])

        tc1, tc2 = f"{td}/copy1", f"{td}/copy2"
        for t_ in (tc1, tc2):
            os.makedirs(f"{t_}/Override", exist_ok=True)
            with open(f"{t_}/Override/a.txt", "w") as fh:
                fh.write("content")
            with open(f"{t_}/Override/b.bin", "wb") as fh:
                fh.write(b"\x00\x01\x02")
        ns.a_dir, ns.b_dir = tc1, tc2
        rep = run_diff(ns)
        check("independent identical copies -> MATCH, zero hazards",
              rep["verdict"] == "MATCH" and rep["counts"]["hazards"] == 0, rep["counts"])

        print("missing / extra files in both directions")
        td1, td2 = f"{td}/setA", f"{td}/setB"
        os.makedirs(f"{td1}/Override", exist_ok=True)
        os.makedirs(f"{td2}/Override", exist_ok=True)
        for name in ("shared.utc", "manual_only.ncs"):
            with open(f"{td1}/Override/{name}", "wb") as fh:
                fh.write(b"x")
        for name in ("shared.utc", "auto_only.ncs"):
            with open(f"{td2}/Override/{name}", "wb") as fh:
                fh.write(b"x")
        ns.a_dir, ns.b_dir = td1, td2
        ns.no_inode_shortcut = False
        rep = run_diff(ns)
        paths = {d["category"]: d["path"] for d in rep["differences"]}
        check("file missing from B and file extra in B both reported",
              rep["counts"]["by_category"] == {"only_in_a": 1, "only_in_b": 1}
              and paths["only_in_a"] == "override/manual_only.ncs"
              and paths["only_in_b"] == "override/auto_only.ncs", paths)

        print("format twins")
        os.makedirs(f"{td}/tw1/Override", exist_ok=True)
        os.makedirs(f"{td}/tw2/Override", exist_ok=True)
        with open(f"{td}/tw1/Override/foo.tpc", "wb") as fh:
            fh.write(b"tpc")
        with open(f"{td}/tw2/Override/foo.tga", "wb") as fh:
            fh.write(b"tga")
        ns.a_dir, ns.b_dir = f"{td}/tw1", f"{td}/tw2"
        rep = run_diff(ns)
        cats = rep["counts"]["by_category"]
        check("tpc vs tga same stem -> format_twin, not 2 orphans",
              cats == {"format_twin": 1}, cats)

    print()
    if failures:
        print(f"SELFTEST FAILED: {len(failures)} check(s): {failures}")
        return 1
    print("SELFTEST PASSED")
    return 0


# --------------------------------------------------------------------------
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Format-aware diff between two KOTOR install trees."
    )
    ap.add_argument("a_dir", nargs="?", help="tree A (default: manual)")
    ap.add_argument("b_dir", nargs="?", help="tree B (default: automated)")
    ap.add_argument("--a-name", default="manual")
    ap.add_argument("--b-name", default="auto")
    ap.add_argument("--label", default="")
    ap.add_argument("--json", dest="json_out")
    ap.add_argument("--md", dest="md_out")
    ap.add_argument("--ledger", help="manual harness ledger.jsonl")
    ap.add_argument("--install-log", help="ModSync install log")
    ap.add_argument("--jobs", type=int, default=8)
    ap.add_argument("--hash-mode", choices=("all", "sizefirst"), default="all")
    ap.add_argument("--no-inode-shortcut", action="store_true")
    ap.add_argument(
        "--allow-shared-inodes", action="store_true",
        help="the trees are knowingly hardlink-aliased (e.g. a reference tree "
             "linked to a snapshot); downgrade shared inodes to an observation",
    )
    ap.add_argument("--no-deep", action="store_true",
                    help="skip format-aware parsing; hash compare only")
    ap.add_argument("--include", action="append")
    ap.add_argument("--exclude", action="append")
    ap.add_argument("--max-examples", type=int, default=25)
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)

    if args.selftest:
        return selftest()
    if not args.a_dir or not args.b_dir:
        ap.error("A_DIR and B_DIR are required (or use --selftest)")
    for d in (args.a_dir, args.b_dir):
        if not os.path.isdir(d):
            print(f"not a directory: {d}", file=sys.stderr)
            return 2

    rep = run_diff(args)

    if args.json_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.json_out)), exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=2, sort_keys=False)
            fh.write("\n")
    md = render_md(rep)
    if args.md_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.md_out)), exist_ok=True)
        with open(args.md_out, "w", encoding="utf-8") as fh:
            fh.write(md)
    if not args.quiet:
        print(md)
    else:
        print(f"{rep['verdict']}  "
              f"real={rep['counts']['real_differences']} "
              f"hazards={rep['counts']['hazards']} "
              f"bookkeeping={rep['counts']['bookkeeping_differences']}")

    return 0 if rep["verdict"] == "MATCH" else 1


if __name__ == "__main__":
    sys.exit(main())
