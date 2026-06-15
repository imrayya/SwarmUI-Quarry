namespace Quarry;

/// <summary>Reads a tabular dataset (a Lance dataset, or any path DuckDB can scan) to back a wildcard.
/// Isolates the storage/query engine from the wildcard handler so the engine can be swapped or mocked.</summary>
public interface IWildcardQueryBackend
{
    /// <summary>Introspects the dataset's columns and whether each is scalar or list-typed.</summary>
    ColumnSchema GetSchema(string datasetPath);

    /// <summary>Counts the rows matching <paramref name="filter"/> (all rows when the filter is empty).</summary>
    long CountRows(string datasetPath, SqlFilter filter);

    /// <summary>Returns the <paramref name="promptColumn"/> value of the matching row at the given
    /// zero-based <paramref name="index"/> in stable order. Returns "" if the cell is null or out of range.</summary>
    string GetPromptAt(string datasetPath, string promptColumn, SqlFilter filter, long index);

    /// <summary>Returns the <paramref name="promptColumn"/> value of the row at the given zero-based
    /// <em>unfiltered</em> <paramref name="index"/> in stable order, plus whether that row satisfies
    /// <paramref name="filter"/>. The filter is evaluated as a projected expression rather than a WHERE
    /// clause, so the seek stays a native O(1) pushdown — the primitive the wildcard handler's rejection
    /// sampler uses to find a matching row by cheap random seeks instead of a filtered OFFSET scan. Returns
    /// ("", false) for an out-of-range index.</summary>
    (string Value, bool Matches) GetCandidateAt(string datasetPath, string promptColumn, SqlFilter filter, long index);
}
