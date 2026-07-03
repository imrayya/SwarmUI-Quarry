#!/usr/bin/env python3
"""Browse a parquet / csv / tsv / json / jsonl / sqlite / lance dataset in the terminal.

Thin wrapper around VisiData (the right tool for the job): a terminal
spreadsheet with native mouse-wheel scrolling for rows and arrow-key /
hjkl navigation across columns. VisiData reads every file format below, so this
script just picks the correct loader and guards against giant BLOB columns
in parquet files (e.g. image datasets) that would otherwise load gigabytes
into memory.

A Lance dataset (a ``*.lance`` directory) has no native VisiData loader, so it
is streamed to a temporary parquet file first -- with BLOB columns replaced by
their byte size, exactly like the parquet path -- and that temp file is browsed.

Usage:
    uv run python scripts/browse.py FILE
    uv run python scripts/browse.py DATASET.lance
    uv run python scripts/browse.py FILE --blobs   # keep raw blob bytes (risky)

Navigation once open (VisiData):
    mouse wheel / j / k / arrows   scroll rows
    h / l / arrows                 move across columns
    /                              search in a column
    [ / ]                          sort by current column
    Enter                          dive into a cell (structs, long text)
    gq / q                         quit

For a sqlite file the first sheet lists its tables; press Enter on a row
to dive into that table, and q to come back to the table list.
"""
import argparse
import os
import subprocess
import sys
import tempfile
from pathlib import Path

# extension -> VisiData loader name
LOADERS = {
    ".parquet": "parquet",
    ".csv": "csv",
    ".tsv": "tsv",
    ".json": "json",
    ".jsonl": "jsonl",
    ".ndjson": "jsonl",
    ".sqlite": "sqlite",
    ".sqlite3": "sqlite",
    ".db": "sqlite",
    ".lance": "lance",  # a Lance dataset directory (streamed to temp parquet)
}


def describe(path):
    """Return [(column_name, column_type), ...] for a parquet file via DuckDB."""
    import duckdb

    con = duckdb.connect()
    rows = con.sql(
        "SELECT column_name, column_type "
        "FROM (DESCRIBE SELECT * FROM read_parquet(?))",
        params=[str(path)],
    ).fetchall()
    return [(name, dtype) for name, dtype in rows]


def blob_columns(path):
    """Return names of columns whose type contains BLOB (top-level or nested).

    Image blobs in HF datasets are typically STRUCT(bytes BLOB, path VARCHAR).
    """
    return [name for name, dtype in describe(path) if "BLOB" in dtype.upper()]


def make_blobless_view(path, blob_cols):
    """Write a temp parquet where each BLOB column becomes its byte length.

    Keeps every other column intact so the whole table stays browsable without
    pulling image bytes into memory. Returns the temp file path.
    """
    import duckdb

    con = duckdb.connect()
    schema = describe(path)
    blob_set = set(blob_cols)

    def project(col, dtype):
        q = f'"{col}"'
        if col not in blob_set:
            return q
        up = dtype.upper()
        if up.startswith("STRUCT") and "BYTES BLOB" in up:
            # STRUCT(bytes BLOB, path VARCHAR) -> byte size of the nested blob.
            return f'octet_length({q}.bytes) AS {q}'
        # Plain BLOB column.
        return f"octet_length({q}) AS {q}"

    select = ", ".join(project(c, t) for c, t in schema)
    tmp = tempfile.NamedTemporaryFile(suffix=".parquet", delete=False)
    tmp.close()
    con.sql(
        f"COPY (SELECT {select} FROM read_parquet(?)) "
        f"TO '{tmp.name}' (FORMAT parquet)",
        params=[str(path)],
    )
    return Path(tmp.name)


def _lance_ident(name):
    """Backtick-quote a Lance/DataFusion identifier (Lance requires backticks)."""
    return "`" + name.replace("`", "``") + "`"


def lance_blob_projection(field):
    """Return a scanner expression that replaces a BLOB field with its byte size.

    Handles a plain binary column and the HF-image STRUCT(bytes binary, path ...)
    shape. Returns None for a non-blob field (project it through unchanged).
    """
    import pyarrow as pa

    ident = _lance_ident(field.name)
    t = field.type
    if pa.types.is_binary(t) or pa.types.is_large_binary(t) or pa.types.is_fixed_size_binary(t):
        return f"octet_length({ident})"
    if pa.types.is_struct(t):
        sub = {f.name for f in t}
        if "bytes" in sub:
            # STRUCT(bytes binary, path VARCHAR) -> byte size of the nested blob.
            return f"octet_length({ident}.{_lance_ident('bytes')})"
    return None


def make_lance_parquet(path, keep_blobs):
    """Stream a Lance dataset to a temp parquet, shrinking BLOB columns to sizes.

    Streams via a Lance scanner and pyarrow ParquetWriter so raw blob bytes are
    never materialized (their byte length is computed in-engine). Returns
    (temp_path, blob_col_names). With keep_blobs, blobs are passed through raw.
    """
    import lance
    import pyarrow.parquet as pq

    ds = lance.dataset(str(path))
    blob_cols = []
    columns = {}
    for field in ds.schema:
        expr = None if keep_blobs else lance_blob_projection(field)
        if expr is None:
            columns[field.name] = _lance_ident(field.name)  # pass through unchanged
        else:
            blob_cols.append(field.name)
            columns[field.name] = expr

    tmp = tempfile.NamedTemporaryFile(suffix=".parquet", delete=False)
    tmp.close()
    reader = ds.scanner(columns=columns, batch_size=8192).to_reader()
    writer = pq.ParquetWriter(tmp.name, reader.schema)
    try:
        for batch in reader:
            writer.write_batch(batch)
    finally:
        writer.close()
    return Path(tmp.name), blob_cols


def main(argv):
    parser = argparse.ArgumentParser(
        description="Browse a parquet/csv/tsv/json/jsonl/sqlite file in the terminal."
    )
    parser.add_argument("file", help="path to the data file")
    parser.add_argument(
        "--blobs",
        action="store_true",
        help="load raw BLOB bytes for parquet (default: replace with byte size)",
    )
    args = parser.parse_args(argv[1:])

    path = Path(args.file).expanduser()

    loader = LOADERS.get(path.suffix.lower())
    if loader is None:
        print(
            f"Error: unsupported extension '{path.suffix}'. "
            f"Supported: {', '.join(sorted(LOADERS))}",
            file=sys.stderr,
        )
        return 2

    # A Lance dataset is a directory; everything else is a single file.
    if loader == "lance":
        if not path.is_dir():
            print(f"Error: Lance dataset not found: {path}", file=sys.stderr)
            return 1
    elif not path.is_file():
        print(f"Error: file not found: {path}", file=sys.stderr)
        return 1

    open_path = path
    tmp_path = None
    if loader == "lance":
        tmp_path, cols = make_lance_parquet(path, args.blobs)
        open_path = tmp_path
        loader = "parquet"  # VisiData browses the streamed temp parquet
        if cols and not args.blobs:
            print(
                f"Note: {len(cols)} BLOB column(s) ({', '.join(cols)}) shown as "
                f"byte sizes to avoid loading raw bytes. Use --blobs to override.",
                file=sys.stderr,
            )
    elif loader == "parquet" and not args.blobs:
        cols = blob_columns(path)
        if cols:
            print(
                f"Note: {len(cols)} BLOB column(s) ({', '.join(cols)}) shown as "
                f"byte sizes to avoid loading raw bytes. Use --blobs to override.",
                file=sys.stderr,
            )
            tmp_path = make_blobless_view(path, cols)
            open_path = tmp_path

    try:
        # Hand off to VisiData; it inherits the tty so scrolling works.
        return subprocess.call(["vd", "-f", loader, str(open_path)])
    except FileNotFoundError:
        print(
            "Error: 'vd' (VisiData) not found. Run inside the project venv: "
            "uv run python scripts/browse.py ...",
            file=sys.stderr,
        )
        return 127
    finally:
        if tmp_path is not None:
            try:
                os.unlink(tmp_path)
            except OSError:
                pass


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
