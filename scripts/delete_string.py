#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# ///
"""Delete every occurrence of a string from a text file, overwriting in place.

Reads the target file, removes all (non-overlapping) occurrences of the given
string, and writes the result back to the same file. The substring is matched
*literally* -- no regular-expression or glob interpretation -- so characters
like ``.``, ``*`` or ``(`` mean themselves.

The new content is written to a temp file in the destination directory first,
then atomically moved into place, so a failure mid-write can't corrupt the
target (or an existing --output file).

By default the file is read and written as UTF-8 text. Pass --encoding to use a
different codec. Deleting an empty string is refused (it would be a no-op that
"matches" between every character).

Usage:
    python delete_string.py <file> <string>
    python delete_string.py notes.txt "TODO: "
    python delete_string.py config.ini "secret_key" -o config.clean.ini
    python delete_string.py data.txt $'\r'          # strip carriage returns
    python delete_string.py page.html "<script>" --count

Run it with the project venv:
    .venv/bin/python scripts/delete_string.py notes.txt "TODO: "

or standalone with uv (no third-party deps required):
    uv run scripts/delete_string.py notes.txt "TODO: "
"""

from __future__ import annotations

import argparse
import os
import sys
import tempfile
from pathlib import Path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Delete every occurrence of a literal string from a text file.",
    )
    parser.add_argument("file", help="the text file to edit")
    parser.add_argument("string", help="the literal substring to remove (matched as-is)")
    parser.add_argument(
        "-o",
        "--output",
        default=None,
        help="write the result here instead of overwriting the file in place",
    )
    parser.add_argument(
        "--encoding",
        default="utf-8",
        help="text encoding used to read and write the file (default: utf-8)",
    )
    parser.add_argument(
        "--count",
        action="store_true",
        help="report how many occurrences were removed",
    )
    args = parser.parse_args(argv)

    if args.string == "":
        raise SystemExit("error: refusing to delete an empty string (no-op)")

    path = Path(args.file)
    if not path.is_file():
        raise SystemExit(f"error: no such file: {path}")

    dest = Path(args.output) if args.output else path

    try:
        text = path.read_text(encoding=args.encoding)
    except UnicodeDecodeError as exc:
        raise SystemExit(
            f"error: cannot decode {path} as {args.encoding} ({exc}); "
            f"pass --encoding for the right codec"
        )

    occurrences = text.count(args.string)
    new_text = text.replace(args.string, "")

    # Write to a temp file in the destination dir, then atomically replace, so a
    # failure can't corrupt the target / existing output file.
    fd, tmp_name = tempfile.mkstemp(
        dir=str(dest.parent), prefix=f".{dest.name}.", suffix=".tmp"
    )
    tmp_path = Path(tmp_name)
    try:
        with os.fdopen(fd, "w", encoding=args.encoding, newline="") as fh:
            fh.write(new_text)
        os.replace(tmp_path, dest)
    except BaseException:
        tmp_path.unlink(missing_ok=True)
        raise

    detail = f" ({occurrences} occurrence(s))" if args.count else ""
    print(f"Removed {args.string!r}{detail} from {path} -> {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
