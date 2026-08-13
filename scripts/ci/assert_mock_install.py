#!/usr/bin/env python3
# Copyright 2021-2025 ModSync
# Licensed under the Business Source License 1.1 (BSL 1.1).
# See LICENSE.txt file in the project root for full license information.
"""Compare a mock KOTOR install before and after a ModSync install.

Run once with ``snapshot`` to record the baseline, then once with ``assert`` after
the install. The assert pass fails unless the game directory really changed, so an
install that exits 0 without touching anything is caught.

Reads only the synthetic fixture written by ModSync.Tests. No game data involved.
"""

import argparse
import hashlib
import json
import os
import struct
import sys


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 16), b""):
            digest.update(chunk)
    return digest.hexdigest()


def count_files(directory):
    total = 0
    for _, _, files in os.walk(directory):
        total += len(files)
    return total


def read_tlk_strings(path):
    """Parse a TLK V3.0 talk table into its list of strings."""
    with open(path, "rb") as handle:
        data = handle.read()

    if data[:8] != b"TLK V3.0":
        raise ValueError("%s is not a TLK V3.0 file" % path)

    count, text_offset = struct.unpack_from("<II", data, 12)
    strings = []
    for index in range(count):
        entry = 20 + (index * 40)
        offset, length = struct.unpack_from("<II", data, entry + 28)
        start = text_offset + offset
        strings.append(data[start:start + length].decode("cp1252", errors="replace"))
    return strings


def read_2da_row_count(path):
    """Read the row count out of a 2DA V2.b header."""
    with open(path, "rb") as handle:
        data = handle.read()

    if data[:9] != b"2DA V2.b\n":
        raise ValueError("%s is not a 2DA V2.b file" % path)

    cursor = 9
    while cursor < len(data) and data[cursor] != 0:
        cursor += 1
    cursor += 1
    return struct.unpack_from("<I", data, cursor)[0]


def collect(args):
    return {
        "overrideFiles": count_files(args.override_dir),
        "dialogTlkBytes": os.path.getsize(args.dialog_tlk),
        "dialogTlkSha256": sha256(args.dialog_tlk),
        "dialogTlkStrings": len(read_tlk_strings(args.dialog_tlk)),
        "tableRows": read_2da_row_count(args.table),
    }


def cmd_snapshot(args):
    state = collect(args)
    with open(args.state, "w", encoding="utf-8") as handle:
        json.dump(state, handle, indent=2, sort_keys=True)
    print("baseline: %s" % json.dumps(state, sort_keys=True))
    return 0


def cmd_assert(args):
    with open(args.state, "r", encoding="utf-8") as handle:
        before = json.load(handle)

    after = collect(args)
    print("before: %s" % json.dumps(before, sort_keys=True))
    print("after:  %s" % json.dumps(after, sort_keys=True))

    failures = []

    if after["overrideFiles"] <= before["overrideFiles"]:
        failures.append(
            "override file count did not grow (%d -> %d)"
            % (before["overrideFiles"], after["overrideFiles"]))

    if after["dialogTlkSha256"] == before["dialogTlkSha256"]:
        failures.append("dialog.tlk is byte-identical, so no TLK patch was applied")

    if after["dialogTlkBytes"] <= before["dialogTlkBytes"]:
        failures.append(
            "dialog.tlk did not grow (%d -> %d bytes)"
            % (before["dialogTlkBytes"], after["dialogTlkBytes"]))

    if after["tableRows"] != before["tableRows"] + 1:
        failures.append(
            "%s row count is %d, expected %d"
            % (os.path.basename(args.table), after["tableRows"], before["tableRows"] + 1))

    strings = read_tlk_strings(args.dialog_tlk)
    for expected in args.expect_string or []:
        if expected not in strings:
            failures.append("dialog.tlk is missing the appended string %r" % expected)

    for name in args.expect_file or []:
        candidate = os.path.join(args.override_dir, name)
        if not os.path.isfile(candidate):
            failures.append("expected file missing from the override directory: %s" % name)

    # A second override directory differing only by case means the install invented
    # its own instead of reusing the game's.
    content_root = os.path.dirname(os.path.abspath(args.override_dir))
    override_dirs = [
        entry for entry in os.listdir(content_root)
        if entry.lower() == "override" and os.path.isdir(os.path.join(content_root, entry))
    ]
    if len(override_dirs) != 1:
        failures.append("expected exactly one override directory, found: %s" % override_dirs)

    if failures:
        print("\nFAILED:")
        for failure in failures:
            print("  - %s" % failure)
        return 1

    print("\nOK: the install changed the game directory in every expected way.")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["snapshot", "assert"])
    parser.add_argument("--override-dir", required=True)
    parser.add_argument("--dialog-tlk", required=True)
    parser.add_argument("--table", required=True, help="2DA the patcher appends a row to")
    parser.add_argument("--state", required=True, help="Baseline JSON path")
    parser.add_argument("--expect-file", action="append", help="Filename required in the override directory")
    parser.add_argument("--expect-string", action="append", help="String required in dialog.tlk")
    args = parser.parse_args(argv)

    return cmd_snapshot(args) if args.mode == "snapshot" else cmd_assert(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
