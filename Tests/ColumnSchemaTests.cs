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
    public void IsCompanionName_HidesInternalColumnWithoutBaseColumn()
    {
        ColumnSchema schema = new([new ColumnInfo("notes__lc", ColumnKind.Scalar)]);
        Assert.True(schema.IsCompanionName("notes__lc"));
        Assert.Empty(schema.VisibleColumns);
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
    public void StripCompanions_DropsInternalColumnWhenBaseColumnIsAbsent()
    {
        List<string> columns = ["notes__lc", "n"];
        List<List<string>> rows = [["hello", "1"]];
        (List<string> cols, List<List<string>> outRows) = ColumnSchema.StripCompanions(columns, rows);
        Assert.Equal(new[] { "n" }, cols);
        Assert.Equal(new[] { "1" }, outRows[0]);
    }
}
