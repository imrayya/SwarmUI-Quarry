namespace Quarry;

/// <summary>A bound query parameter: a placeholder name (without the leading <c>$</c>) and the
/// string value to bind. Values are always bound, never interpolated, to prevent SQL injection
/// from user-supplied tag text.</summary>
public sealed class QueryParameter
{
    public string Name { get; }
    public string Value { get; }

    public QueryParameter(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

/// <summary>A SQL WHERE expression plus its bound parameters. <see cref="WhereClause"/> is the
/// boolean expression WITHOUT the leading <c>WHERE</c>; it is empty when the query has no filter.</summary>
public sealed class SqlFilter
{
    public static readonly SqlFilter None = new("", Array.Empty<QueryParameter>());

    public string WhereClause { get; }
    public IReadOnlyList<QueryParameter> Parameters { get; }

    public bool IsEmpty => WhereClause.Length == 0;

    /// <summary>A stable, canonical key identifying this filter — its WHERE expression and its bound values in
    /// order — for use as a cache key so a repeated query reuses a computed count. Empty for an empty filter.
    /// Placeholders are generated in a fixed order, so equal clause text with equal ordered values yields the
    /// same key; a different column, operator, or value yields a different one. Each part is length-prefixed
    /// (<c>len:text</c>), so no clause or value content can ever be confused for the structure.</summary>
    public string CacheKey => IsEmpty
        ? ""
        : $"{WhereClause.Length}:{WhereClause}|{string.Join("|", Parameters.Select(parameter => $"{parameter.Value.Length}:{parameter.Value}"))}";

    public SqlFilter(string whereClause, IReadOnlyList<QueryParameter> parameters)
    {
        WhereClause = whereClause;
        Parameters = parameters;
    }
}
