namespace Quarry;

/// <summary>Read-only dataset access bound to a single DuckDB connection. Cache-warming fans a batch of
/// jobs out over a pool of connections; each job is handed one of these and only ever runs on one thread,
/// so it respects the per-connection "one command at a time" rule. A subset of
/// <see cref="IWildcardQueryBackend"/> plus the preview read, which is all warming needs.</summary>
public interface IDatasetReader
{
    /// <summary>Introspects the dataset's columns and whether each is scalar or list-typed.</summary>
    ColumnSchema GetSchema(string datasetPath);

    /// <summary>Counts the rows matching <paramref name="filter"/> (all rows when the filter is empty).</summary>
    long CountRows(string datasetPath, SqlFilter filter);

    /// <summary>Reads the first <paramref name="limit"/> rows (all columns) as display strings.</summary>
    (List<string> Columns, List<List<string>> Rows) GetSampleRows(string datasetPath, int limit);
}
