namespace Quarry;

public static class DuckDbTypeMapper
{
    public static ColumnKind MapKind(string duckDbType)
    {
        if (string.IsNullOrWhiteSpace(duckDbType))
        {
            return ColumnKind.Scalar;
        }
        string type = duckDbType.Trim();
        bool isList = type.EndsWith(']')
            || type.StartsWith("LIST(", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("ARRAY(", StringComparison.OrdinalIgnoreCase);
        return isList ? ColumnKind.List : ColumnKind.Scalar;
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TINYINT", "INT1",
        "SMALLINT", "INT2", "SHORT",
        "INTEGER", "INT", "INT4", "SIGNED",
        "BIGINT", "INT8", "LONG",
        "HUGEINT",
        "UTINYINT", "USMALLINT", "UINTEGER", "UBIGINT", "UHUGEINT",
        "FLOAT", "FLOAT4", "REAL",
        "DOUBLE", "FLOAT8",
        "DECIMAL", "NUMERIC",
    };

    public static bool IsNumeric(string duckDbType)
    {
        if (string.IsNullOrWhiteSpace(duckDbType) || MapKind(duckDbType) == ColumnKind.List)
        {
            return false;
        }
        string type = duckDbType.Trim();
        int paren = type.IndexOf('(');
        if (paren >= 0)
        {
            type = type[..paren].Trim();
        }
        return NumericTypes.Contains(type);
    }
}
