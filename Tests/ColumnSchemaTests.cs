using Xunit;

namespace Quarry.Tests;

public class ColumnSchemaTests
{
    [Fact]
    public void VisibleColumns_HidesCompanionsButKeepsThemQueryable()
    {
        ColumnSchema schema = new(
        [
            new ColumnInfo("prompt", ColumnKind.Scalar),
            new ColumnInfo("prompt__lc", ColumnKind.Scalar, hasNgramIndex: true),
            new ColumnInfo("tags", ColumnKind.List),
        ]);
        // The companion is hidden from the user-facing list...
        Assert.Equal(new[] { "prompt", "tags" }, schema.VisibleColumns.Select(c => c.Name));
        // ...but still present in the schema for query routing.
        Assert.True(schema.TryGet("prompt__lc", out _));
        Assert.True(schema.IsCompanionName("prompt__lc"));
    }

    [Fact]
    public void IsCompanionName_RequiresAnExistingBaseColumn()
    {
        ColumnSchema schema = new([new ColumnInfo("notes__lc", ColumnKind.Scalar)]);
        // "notes__lc" with no "notes" base is a genuine column, not a companion -> not hidden.
        Assert.False(schema.IsCompanionName("notes__lc"));
        Assert.Equal(new[] { "notes__lc" }, schema.VisibleColumns.Select(c => c.Name));
    }

    [Fact]
    public void VisibleColumns_SingleUnderscore_IsNotTreatedAsCompanion()
    {
        // Only the double-underscore "__lc" convention is internal; a legacy/real "_lc" column stays visible.
        ColumnSchema schema = new(
        [
            new ColumnInfo("model", ColumnKind.Scalar),
            new ColumnInfo("model_lc", ColumnKind.Scalar),
        ]);
        Assert.Equal(new[] { "model", "model_lc" }, schema.VisibleColumns.Select(c => c.Name));
    }

    [Fact]
    public void StripCompanions_DropsCompanionColumnsAndTheirCells()
    {
        List<string> columns = ["prompt", "prompt__lc", "steps"];
        List<List<string>> rows =
        [
            ["A Cat", "a cat", "20"],
            ["Dog", "dog", "30"],
        ];
        (List<string> cols, List<List<string>> outRows) = ColumnSchema.StripCompanions(columns, rows);
        Assert.Equal(new[] { "prompt", "steps" }, cols);
        Assert.Equal(new[] { "A Cat", "20" }, outRows[0]);
        Assert.Equal(new[] { "Dog", "30" }, outRows[1]);
    }

    [Fact]
    public void StripCompanions_NoCompanions_ReturnsInputUntouched()
    {
        List<string> columns = ["prompt", "steps"];
        List<List<string>> rows = [["x", "1"]];
        (List<string> cols, List<List<string>> outRows) = ColumnSchema.StripCompanions(columns, rows);
        Assert.Same(columns, cols);
        Assert.Same(rows, outRows);
    }

    [Fact]
    public void StripCompanions_KeepsCompanionWhenBaseColumnIsAbsent()
    {
        // "notes__lc" with no "notes" column present is a real column, so it must survive.
        List<string> columns = ["notes__lc", "n"];
        List<List<string>> rows = [["hello", "1"]];
        (List<string> cols, _) = ColumnSchema.StripCompanions(columns, rows);
        Assert.Equal(new[] { "notes__lc", "n" }, cols);
    }
}
