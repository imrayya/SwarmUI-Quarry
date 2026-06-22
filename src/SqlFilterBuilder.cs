namespace Quarry;

public static class SqlFilterBuilder
{
    public const string TagsKeyword = "tags";
    public static SqlFilter Build(Query query, ColumnSchema schema) => Build(query, schema, []);

    public static SqlFilter Build(Query query, ColumnSchema schema, IReadOnlyList<ColumnInfo> tagColumns)
    {
        if (!query.HasFilter)
        {
            return SqlFilter.None;
        }
        List<string> terms = [];
        List<QueryParameter> parameters = [];
        foreach (QueryClause clause in query.Clauses)
        {
            if (tagColumns.Count > 0 && string.Equals(clause.Column, TagsKeyword, StringComparison.OrdinalIgnoreCase))
            {
                if (clause.Op is MatchOp.GreaterOrEqual or MatchOp.LessOrEqual)
                {
                    throw new NonNumericComparisonException(TagsKeyword);
                }
                terms.Add(BuildMergedTagTerm(tagColumns, clause, parameters, schema));
                continue;
            }
            if (!schema.TryGet(clause.Column, out ColumnInfo column))
            {
                throw new QueryException(
                    $"Column '{clause.Column}' does not exist in dataset '{query.Name}'.");
            }
            string quoted = SqlText.QuoteIdentifier(column.Name);
            if (clause.Op is MatchOp.GreaterOrEqual or MatchOp.LessOrEqual)
            {
                if (!column.IsNumeric)
                {
                    throw new NonNumericComparisonException(column.Name);
                }
                terms.Add(BuildNumericTerm(column, clause, parameters));
                continue;
            }
            terms.Add(column.Kind == ColumnKind.List
                ? BuildListTerm(quoted, clause, parameters)
                : BuildContainsTerm(column, schema, clause, parameters));
        }
        return new SqlFilter(string.Join(" AND ", terms), parameters);
    }

    private static string BuildMergedTagTerm(IReadOnlyList<ColumnInfo> tagColumns, QueryClause clause, List<QueryParameter> parameters, ColumnSchema schema)
    {
        string[] valueMatches = new string[clause.Values.Count];
        for (int i = 0; i < clause.Values.Count; i++)
        {
            string value = MatchValue(clause.Values[i]);
            string name = $"p{parameters.Count}";
            parameters.Add(new QueryParameter(name, value));
            string placeholder = $"${name}";
            string[] perColumn = new string[tagColumns.Count];
            for (int c = 0; c < tagColumns.Count; c++)
            {
                if (tagColumns[c].Kind == ColumnKind.List)
                {
                    perColumn[c] = ListElementContains(SqlText.QuoteIdentifier(tagColumns[c].Name), placeholder);
                }
                else
                {
                    string indexed = SearchColumn(tagColumns[c], schema);
                    string scan = $"lower({SqlText.QuoteIdentifier(tagColumns[c].Name)})";
                    perColumn[c] = ScalarContains(MatchExpr(value, indexed, scan), placeholder);
                }
            }
            valueMatches[i] = perColumn.Length == 1 ? perColumn[0] : $"({string.Join(" OR ", perColumn)})";
        }
        return Combine(clause, valueMatches);
    }

    private static string BuildNumericTerm(ColumnInfo column, QueryClause clause, List<QueryParameter> parameters)
    {
        bool atLeast = clause.Op == MatchOp.GreaterOrEqual;
        string op = atLeast ? ">=" : "<=";
        string castType = string.IsNullOrEmpty(column.NumericType) ? "DOUBLE" : column.NumericType;
        bool integral = DuckDbTypeMapper.IsIntegerType(castType);
        string quoted = SqlText.QuoteIdentifier(column.Name);
        string[] checks = new string[clause.Values.Count];
        for (int i = 0; i < clause.Values.Count; i++)
        {
            string value = clause.Values[i];
            string name = $"p{parameters.Count}";
            parameters.Add(new QueryParameter(name, value));
            string bound = integral && !IsIntegerLiteral(value)
                ? $"TRY_CAST({(atLeast ? "CEIL" : "FLOOR")}(TRY_CAST(${name} AS DOUBLE)) AS {castType})"
                : $"TRY_CAST(${name} AS {castType})";
            checks[i] = $"{quoted} {op} {bound}";
        }
        return $"({string.Join(" OR ", checks)})";
    }

    private static bool IsIntegerLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        int start = value[0] is '+' or '-' ? 1 : 0;
        if (start == value.Length)
        {
            return false;
        }
        for (int i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static string BuildContainsTerm(ColumnInfo column, ColumnSchema schema, QueryClause clause, List<QueryParameter> parameters)
    {
        string indexed = SearchColumn(column, schema);
        string scan = $"lower({SqlText.QuoteIdentifier(column.Name)})";
        string[] checks = new string[clause.Values.Count];
        for (int i = 0; i < clause.Values.Count; i++)
        {
            string value = MatchValue(clause.Values[i]);
            string name = $"p{parameters.Count}";
            parameters.Add(new QueryParameter(name, value));
            checks[i] = ScalarContains(MatchExpr(value, indexed, scan), $"${name}");
        }
        return Combine(clause, checks);
    }

    private static string BuildListTerm(string column, QueryClause clause, List<QueryParameter> parameters)
    {
        string[] checks = new string[clause.Values.Count];
        for (int i = 0; i < clause.Values.Count; i++)
        {
            string name = $"p{parameters.Count}";
            parameters.Add(new QueryParameter(name, MatchValue(clause.Values[i])));
            checks[i] = ListElementContains(column, $"${name}");
        }
        return Combine(clause, checks);
    }

    private static string MatchValue(string value) => value.ToLowerInvariant();

    internal const int NgramMinLength = 3;
    internal static string MatchExpr(string value, string indexedExpr, string scanExpr)
        => value.Length < NgramMinLength ? scanExpr : indexedExpr;

    internal static string SearchColumn(ColumnInfo column, ColumnSchema schema)
    {
        if (schema.TryGet(column.Name + ColumnSchema.CompanionSuffix, out ColumnInfo companion)
            && companion.Kind == ColumnKind.Scalar && companion.HasNgramIndex)
        {
            return SqlText.QuoteIdentifier(companion.Name);
        }
        if (column.HasNgramIndex)
        {
            return SqlText.QuoteIdentifier(column.Name);
        }
        return $"lower({SqlText.QuoteIdentifier(column.Name)})";
    }

    private static string ScalarContains(string searchColumn, string placeholder)
        => $"contains({searchColumn}, {placeholder})";

    private static string ListElementContains(string column, string placeholder)
        => $"len(list_filter({column}, x -> contains(lower(x), {placeholder}))) > 0";

    private static string Combine(QueryClause clause, IReadOnlyList<string> checks) => clause.Op switch
    {
        MatchOp.Any => $"({string.Join(" OR ", checks)})",
        MatchOp.All => $"({string.Join(" AND ", checks)})",
        MatchOp.None => $"NOT ({string.Join(" OR ", checks)})",
        _ => throw new QueryException($"Unsupported operator for column '{clause.Column}'."),
    };
}
