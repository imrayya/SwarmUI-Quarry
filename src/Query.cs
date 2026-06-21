namespace Quarry;

public enum MatchOp
{
    Any,
    All,
    None,
    GreaterOrEqual,
    LessOrEqual,
}

public sealed class QueryClause(string column, MatchOp op, IReadOnlyList<string> values)
{
    public string Column { get; } = column;
    public MatchOp Op { get; } = op;
    public IReadOnlyList<string> Values { get; } = values;
}

public sealed class Query(string name, IReadOnlyList<QueryClause> clauses, string promptColumn = null)
{
    public string Name { get; } = name;
    public IReadOnlyList<QueryClause> Clauses { get; } = clauses;
    public string PromptColumn { get; } = promptColumn;
    public bool HasFilter => Clauses.Count > 0;
}
