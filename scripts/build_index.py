#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# dependencies = ["duckdb>=1.0", "pylance", "pyarrow"]
# ///
"""Build Quarry search indices on standalone Lance dataset(s).

For each chosen text column ``X`` this builds a Lance **NGRAM** scalar index so Quarry's substring filters
(``contains``) are pushed down by the DuckDB lance extension -- turning a full-column scan into an index
lookup. Quarry discovers the index via ``SHOW INDEXES`` and matches case-insensitively by lowercasing the
search value; for that to be correct the indexed column must hold lowercased text, so:

  * if ``X`` is already **fully lowercase**, the NGRAM index is built **in place** on ``X`` (no extra column);
  * otherwise a lowercased companion ``X_lc`` (= ``lower(X)``) is added and the index is built on it.

The original column ``X`` is never modified and stays what Quarry reads for output, so display case is kept.
Pass ``--always-companion`` to force the ``X_lc`` form even for lowercase columns.

Before indexing, each dataset is first cleaned by ``lance_clean.py`` (run automatically): list columns are
flattened to strings, then empty and normalized-duplicate prompt rows are deleted and fragments compacted -- so a
list prompt column becomes indexable and the index covers the final, deduped data. This is **destructive** (rows
are deleted, history pruned). Pass ``--no-clean`` to skip it, the ``--no-flatten`` / ``--no-dedup`` /
``--no-compact`` toggles or ``--prompt-column`` to tune it, or ``--dry-run`` to preview what cleaning would remove
without writing or indexing anything.

Auto-builds a ``BTREE`` scalar index on every **declared-numeric** column (integer/float/decimal) so numeric
range filters (Quarry's ``+=`` / ``-=``) push down -- Quarry casts the bound value to the column's own type for
this. Detection is by declared type, NOT by sampling values: a numbers-stored-as-text (VARCHAR) column is
deliberately skipped, since Quarry won't range-query a non-numeric-typed column and a BTREE on text answers
``col >= '99000'`` lexicographically (wrong) -- retype such a column at ingestion instead. ``--no-btree``
disables this; ``--btree a,b`` adds extras. ``--bitmap`` is also accepted, but Quarry's ``<q>`` uses substring
(``contains``) not equality, so a BITMAP index is only used by exact-equality queries (not the wildcard path)
and is usually not worthwhile for low-cardinality columns (a scan is often faster).

Everything lives inside the ``.lance`` directory, so it travels with the dataset: build once, ship/share the
dataset, every reader gets the speedup. Re-running is idempotent (existing companion reused; indices rebuilt).

Lance is versioned, so each run writes a new version and leaves the old data/index files behind. By default
this prunes everything but the current version afterward (``cleanup_old_versions``) so repeated runs don't
bloat; pass ``--keep-history`` to retain old versions (and time-travel). Do not run against a dataset that a
live reader has open, since pruning deletes versions an in-flight read may have pinned -- restart/refresh after.

When given a directory, datasets are indexed in parallel (``-j`` / ``--jobs``, default 4); each dataset's report
is printed as one block when it finishes. The heavy Lance index builds release the GIL, so this is a real
speedup -- but each job is itself multi-threaded, so very high ``-j`` can oversubscribe. Use ``-j 1`` for serial
output with live per-step progress.

Usage:
    build_index.py <dataset.lance | dir-searched-recursively> [options]
    build_index.py ~/data/AIConfigs/Quarry        # whole tree; NGRAM on text + auto-BTREE on numeric cols
    build_index.py ~/data/AIConfigs/Quarry -j 8   # index 8 datasets at once
    build_index.py ./tags/                        # NGRAM per text column (in place when lowercase)
    build_index.py ./nl/   --text-columns prompt,caption
    build_index.py deepghs.danbooru2024.lance     # fav_count (BIGINT) is auto-BTREE'd; no flag needed
    build_index.py ./mixed/ --always-companion    # force X_lc even for lowercase columns
    build_index.py ~/data/AIConfigs/Quarry --dry-run   # preview what cleaning would remove; build nothing
    build_index.py ~/data/AIConfigs/Quarry --no-clean  # index only, skip the clean pass

Run with the project venv:  .venv/bin/python scripts/build_index.py <path>
or standalone:              uv run scripts/build_index.py <path>
"""

from __future__ import annotations

import argparse
import os
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import timedelta
from pathlib import Path

import duckdb
import lance
import pyarrow as pa

import lance_clean  # sibling script: the clean pass run before indexing

LC_SUFFIX = "__lc"

# Quarry-managed internal dirs we must never index or prune, however the path is reached: the live, self-managed
# image-history index, and the HF upload-cache copies.
MANAGED_INTERNAL_DIRS = {".image-history", ".cache"}

# DuckDB connections aren't safe to share across threads, so each worker thread gets its own. The heavy Lance
# index builds (and the DuckDB scan) release the GIL, so threading over datasets gives real parallelism.
_tlocal = threading.local()
_print_lock = threading.Lock()


def duck() -> "duckdb.DuckDBPyConnection":
    con = getattr(_tlocal, "con", None)
    if con is None:
        con = duckdb.connect()
        con.execute("INSTALL lance; LOAD lance;")
        _tlocal.con = con
    return con


def is_text(field: pa.Field) -> bool:
    return pa.types.is_string(field.type) or pa.types.is_large_string(field.type)


def is_numeric(field: pa.Field) -> bool:
    # Detect by the column's declared type, not by sampling values. A numbers-stored-as-text (VARCHAR) column is
    # NOT a BTREE candidate: Quarry only range-queries declared-numeric columns (a `+=`/`-=` on a text column
    # errors), and a BTREE on text answers `col >= '99000'` lexicographically (wrong) and isn't used at all once
    # the value is cast to a number. The fix for numbers-as-strings is to retype the column at ingestion.
    t = field.type
    return pa.types.is_integer(t) or pa.types.is_floating(t) or pa.types.is_decimal(t)


def numeric_columns(ds: "lance.LanceDataset") -> list[str]:
    return [f.name for f in ds.schema if is_numeric(f)]


def quote(name: str) -> str:
    """Double-quote a SQL identifier (DuckDB / standard SQL)."""
    return '"' + name.replace('"', '""') + '"'


def sql_literal(value: str) -> str:
    """Single-quote a SQL string literal (DuckDB / standard SQL), doubling embedded quotes so a path containing a
    `'` (e.g. /data/o'brien/foo.lance) can't break or inject into the query."""
    return "'" + str(value).replace("'", "''") + "'"


def is_managed_internal(path: Path) -> bool:
    """True if the path lies inside a Quarry-managed internal dir -- the live image-history index or the HF
    upload cache -- which this script must never index or prune."""
    return any(part in MANAGED_INTERNAL_DIRS for part in path.parts)


def lance_ident(name: str) -> str:
    """Backtick-quote a column reference for a Lance ``add_columns`` SQL expression.

    Lance's expression engine treats double quotes as a *string literal* (so ``lower("prompt")`` yields the
    constant 'prompt'); backticks are its identifier quote and handle mixed-case / odd names correctly.
    """
    return "`" + name.replace("`", "``") + "`"


def dir_size(path: Path) -> int:
    total = 0
    for root, _dirs, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def human(n: int) -> str:
    f = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if f < 1024 or unit == "TB":
            return f"{f:.1f} {unit}"
        f /= 1024


def all_lowercase(path: str, col: str) -> bool:
    """True if every non-null value of ``col`` already equals its lowercase form (full scan, not a sample --
    a single missed uppercase row would silently fail to match a lowercased query)."""
    q = quote(col)
    n = duck().execute(
        f"SELECT count(*) FROM {sql_literal(path)} WHERE {q} IS NOT NULL AND {q} != lower({q})"
    ).fetchone()[0]
    return n == 0


def find_datasets(root: Path) -> list[Path]:
    """All Lance datasets at or under ``root`` (recursively): directories ending in ``.lance`` that hold a
    ``_versions/`` manifest. Does not descend into a dataset, so its internal ``data/*.lance`` fragment dirs
    are never mistaken for datasets. Handles both flat (``tags/foo.lance``) and nested (``tags/sub/bar.lance``)
    layouts."""
    if root.name.startswith("."):
        return []  # never walk into a hidden root (e.g. pointing directly at the self-managed .image-history index)
    found: list[Path] = []
    for dirpath, dirnames, _files in os.walk(root):
        # Skip hidden dirs like .cache (HF upload-cache copies) and .image-history (the self-managed image index).
        dirnames[:] = [d for d in dirnames if not d.startswith(".")]
        p = Path(dirpath)
        if p.name.endswith(".lance") and (p / "_versions").is_dir():
            found.append(p)
            dirnames[:] = []  # a dataset's own subdirs are internals, not nested datasets
    return sorted(found)


def resolve_text_columns(ds: "lance.LanceDataset", requested: list[str] | None) -> list[str]:
    names = set(ds.schema.names)
    if requested:
        missing = [c for c in requested if c not in names]
        if missing:
            raise SystemExit(f"error: --text-columns not found: {', '.join(missing)} (have: {', '.join(sorted(names))})")
        return requested
    # auto: every string column that isn't itself a companion (and skip a base column whose companion we'd clash with)
    return [f.name for f in ds.schema if is_text(f) and not f.name.endswith(LC_SUFFIX)]


def clean_one(path: Path, prompt_column: str | None, dry_run: bool, flatten: bool, dedup: bool, compact: bool,
              emit) -> None:
    """Run lance_clean's per-dataset pass (flatten list columns, drop empty/duplicate rows, compact) before
    indexing, so the index covers the final, deduped data and a list prompt column becomes indexable."""
    try:
        r = lance_clean.process_dataset(path, prompt_column, dry_run, compact, dedup, flatten)
    except lance_clean.DatasetError as exc:
        emit(f"  !! clean skipped: {exc}")
        return
    flat = r["list_columns"] if dry_run else r["flattened"]
    empties = r["empty"] if dry_run else r["empty_removed"]
    dups = r["duplicate"] if dry_run else r["duplicate_removed"]
    verb = "would clean" if dry_run else "cleaned"
    emit(f"  {verb} [{r['column']}]: flatten {flat} list col(s), "
         f"remove {empties} empty + {dups} duplicate of {r['total']} row(s)")


def build_one(path: Path, text_columns: list[str] | None, bitmap: list[str], btree: list[str],
              always_companion: bool, auto_btree: bool, keep_history: bool, emit,
              clean: bool = True, flatten: bool = True, dedup: bool = True, compact: bool = True,
              prompt_column: str | None = None, dry_run: bool = False) -> None:
    emit(f"\n=== {path} ===")
    if clean:
        clean_one(path, prompt_column, dry_run, flatten, dedup, compact, emit)
    if dry_run:
        return  # preview only -- the data wasn't actually cleaned, so there's nothing to index
    ds = lance.dataset(str(path))
    before = dir_size(path)
    for col in resolve_text_columns(ds, text_columns):
        if not always_companion and all_lowercase(str(path), col):
            t = time.time()
            ds.create_scalar_index(col, "NGRAM", replace=True)
            emit(f"  {col!r}: already lowercase -> NGRAM in place in {time.time() - t:.1f}s")
            continue
        companion = col + LC_SUFFIX
        t = time.time()
        if companion in ds.schema.names:
            # Recompute from scratch (drop + re-add) rather than reuse, so a companion that went stale because the
            # base column changed since the last run can't silently linger and mis-match searches.
            ds.drop_columns([companion])
            ds = lance.dataset(str(path))  # reopen without the stale column
            verb = "refreshed"
        else:
            verb = "added"
        ds.add_columns({companion: f"lower({lance_ident(col)})"})
        ds = lance.dataset(str(path))  # reopen to see the new column
        emit(f"  {col!r}: {verb} companion {companion!r} in {time.time() - t:.1f}s")
        t = time.time()
        ds.create_scalar_index(companion, "NGRAM", replace=True)
        emit(f"  {col!r}: NGRAM index on {companion!r} built in {time.time() - t:.1f}s")
    # BTREE on every declared-numeric column by default (auto), plus any explicitly named; deduped, order-preserving.
    btree_cols = list(dict.fromkeys((numeric_columns(ds) if auto_btree else []) + btree))
    for col, kind in [(c, "BITMAP") for c in bitmap] + [(c, "BTREE") for c in btree_cols]:
        if col not in ds.schema.names:
            emit(f"  !! {kind} column {col!r} not in schema; skipping")
            continue
        t = time.time()
        ds.create_scalar_index(col, kind, replace=True)
        emit(f"  {col!r}: {kind} index built in {time.time() - t:.1f}s")
    # Each add_columns / create_scalar_index writes a new Lance version and leaves the prior data+index files
    # behind, so repeated runs accumulate history and bloat. Prune everything but the current version by default.
    if not keep_history:
        stats = lance.dataset(str(path)).cleanup_old_versions(older_than=timedelta(0), delete_unverified=True)
        emit(f"  pruned {stats.old_versions} old version(s), reclaimed {human(stats.bytes_removed)}")
    ds = lance.dataset(str(path))
    after = dir_size(path)
    emit(f"  indices: {[(i['name'], i['type']) for i in ds.list_indices()]}")
    emit(f"  size: {human(before)} -> {human(after)}  (+{human(after - before)})")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Build Quarry NGRAM/scalar indices on Lance dataset(s).")
    p.add_argument("input", help="a .lance dataset, or a directory searched recursively for them (hidden dirs like .cache / .image-history are skipped)")
    p.add_argument("--text-columns", default=None,
                   help="comma-separated text columns to index (default: all string columns)")
    p.add_argument("--bitmap", default="", help="comma-separated low-cardinality columns for BITMAP indices")
    p.add_argument("--btree", default="", help="extra columns to BTREE, beyond the auto-detected numeric ones")
    p.add_argument("--no-btree", action="store_true",
                   help="don't auto-build BTREE indices on declared-numeric columns")
    p.add_argument("--always-companion", action="store_true",
                   help="always build the X_lc companion, even for already-lowercase columns")
    p.add_argument("--keep-history", action="store_true",
                   help="keep old Lance versions (default: prune all but the current version to reclaim space)")
    p.add_argument("-j", "--jobs", type=int, default=4,
                   help="datasets to index in parallel when given a directory (default: 4). Each job is itself "
                        "multi-threaded, so very high values can oversubscribe CPU/IO. 1 = serial with live "
                        "per-step output; >1 buffers each dataset's output and prints it as a block when it finishes.")
    # Clean pass (lance_clean) run before indexing; on by default. These mirror lance_clean's own flags.
    p.add_argument("--clean", action=argparse.BooleanOptionalAction, default=True,
                   help="first clean each dataset with lance_clean (flatten list cols, drop empty/duplicate rows, "
                        "compact) before indexing -- DESTRUCTIVE (default: on; --no-clean to skip)")
    p.add_argument("--flatten", action=argparse.BooleanOptionalAction, default=True,
                   help="clean pass: flatten list columns to ', '-joined strings (default: on)")
    p.add_argument("--dedup", action=argparse.BooleanOptionalAction, default=True,
                   help="clean pass: delete normalized-duplicate prompt rows (default: on)")
    p.add_argument("--compact", action=argparse.BooleanOptionalAction, default=True,
                   help="clean pass: compact fragments and purge old versions after deleting (default: on)")
    p.add_argument("--prompt-column", default=None,
                   help="clean pass: the prompt column whose empty/duplicate rows are removed (default: the first "
                        "of prompt/text/caption/description/value, else the first column)")
    p.add_argument("--dry-run", action="store_true",
                   help="preview what the clean pass would remove, without writing or building any indices")
    args = p.parse_args(argv)

    def split(s: str) -> list[str]:
        return [x.strip() for x in s.split(",") if x.strip()]

    text_columns = split(args.text_columns) if args.text_columns else None
    bitmap, btree = split(args.bitmap), split(args.btree)

    root = Path(args.input)
    if not root.exists():
        raise SystemExit(f"error: no such path: {root}")
    if root.name.endswith(".lance"):
        targets = [root]
    else:
        targets = find_datasets(root)
        if not targets:
            raise SystemExit(f"error: no .lance datasets found under {root}")
    for blocked in [t for t in targets if is_managed_internal(t)]:
        print(f"  !! refusing {blocked}: inside a Quarry-managed internal dir "
              f"({', '.join(sorted(MANAGED_INTERNAL_DIRS))}); skipping", file=sys.stderr)
    targets = [t for t in targets if not is_managed_internal(t)]
    if not targets:
        raise SystemExit(f"error: no eligible .lance datasets under {root} (all were Quarry-managed internals)")

    def run_one(path, emit) -> None:
        build_one(path, text_columns, bitmap, btree, args.always_companion,
                  auto_btree=not args.no_btree, keep_history=args.keep_history, emit=emit,
                  clean=args.clean, flatten=args.flatten, dedup=args.dedup, compact=args.compact,
                  prompt_column=args.prompt_column, dry_run=args.dry_run)

    failed = 0
    jobs = max(1, min(args.jobs, len(targets)))
    if jobs == 1:
        for path in targets:
            try:
                run_one(path, lambda line: print(line, flush=True))
            except Exception as exc:  # keep going through a directory
                failed += 1
                print(f"  FAIL {path}: {exc}", file=sys.stderr)
    else:
        duck()  # install the lance extension once up front, so workers don't race on a cold extension cache

        def work(path):
            lines: list[str] = []
            try:
                run_one(path, lines.append)
                return path, lines, None
            except Exception as exc:  # keep going through a directory
                return path, lines, exc

        with ThreadPoolExecutor(max_workers=jobs) as pool:
            for future in as_completed([pool.submit(work, path) for path in targets]):
                path, lines, exc = future.result()
                with _print_lock:  # keep each dataset's output together despite parallel workers
                    for line in lines:
                        print(line, flush=True)
                    if exc is not None:
                        print(f"  FAIL {path}: {exc}", file=sys.stderr, flush=True)
                if exc is not None:
                    failed += 1

    if len(targets) > 1:
        verb = "Previewed" if args.dry_run else "Indexed"
        print(f"\n{verb} {len(targets) - failed}/{len(targets)} dataset(s) with {jobs} job(s); {failed} failed.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
