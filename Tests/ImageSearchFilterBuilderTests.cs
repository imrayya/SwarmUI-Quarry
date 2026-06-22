using Newtonsoft.Json.Linq;
using Xunit;

namespace Quarry.Tests;

public class ImageSearchFilterBuilderTests
{
    private static SqlFilter Build(string json) => ImageSearchFilterBuilder.Build(JArray.Parse(json));

    [Fact]
    public void Empty_ReturnsNone()
    {
        Assert.True(ImageSearchFilterBuilder.Build(null).IsEmpty);
        Assert.True(Build("[]").IsEmpty);
    }

    [Fact]
    public void TextContains_BuildsContains()
    {
        SqlFilter f = Build("""[{"field":"prompt","op":"contains","value":"cat"}]""");
        Assert.Equal("contains(lower(\"prompt\"), $p0)", f.WhereClause);
        Assert.Equal("p0", f.Parameters[0].Name);
        Assert.Equal("cat", f.Parameters[0].Value);
    }

    [Fact]
    public void TextContains_WithLowercaseCompanion_MatchesCompanionAndLowercasesValue()
    {
        // When the index has an NGRAM-indexed `<col>__lc` companion, the image browser matches it directly (NGRAM
        // pushdown) and lowercases the value app-side -- the same contract as the wildcard filter builder.
        ColumnSchema schema = new(
        [
            new ColumnInfo("prompt", ColumnKind.Scalar),
            new ColumnInfo("prompt__lc", ColumnKind.Scalar, hasNgramIndex: true),
        ]);
        SqlFilter f = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"contains","value":"Cat"}]"""), schema);
        Assert.Equal("contains(\"prompt__lc\", $p0)", f.WhereClause);
        Assert.Equal("cat", f.Parameters[0].Value);
    }

    [Fact]
    public void TextEqualsNeNotContains_WithCompanion_RouteToCompanionAndLowercaseValue()
    {
        // Companion routing + app-side value lowercasing is shared by every text operator, not just `contains`.
        ColumnSchema schema = new(
        [
            new ColumnInfo("prompt", ColumnKind.Scalar),
            new ColumnInfo("prompt__lc", ColumnKind.Scalar, hasNgramIndex: true),
        ]);
        SqlFilter eq = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"equals","value":"Cat"}]"""), schema);
        Assert.Equal("\"prompt__lc\" = $p0", eq.WhereClause);
        Assert.Equal("cat", eq.Parameters[0].Value);

        SqlFilter ne = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"ne","value":"Cat"}]"""), schema);
        Assert.Equal("\"prompt__lc\" != $p0", ne.WhereClause);

        SqlFilter nc = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"not_contains","value":"Cat"}]"""), schema);
        Assert.Equal("NOT contains(\"prompt__lc\", $p0)", nc.WhereClause);
    }

    [Fact]
    public void TextContains_UnindexedCompanion_FallsBackToLower()
    {
        // Without an NGRAM index on the companion, the image browser must not redirect to it -- it scans lower(col).
        ColumnSchema schema = new(
        [
            new ColumnInfo("prompt", ColumnKind.Scalar),
            new ColumnInfo("prompt__lc", ColumnKind.Scalar),
        ]);
        SqlFilter f = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"contains","value":"Cat"}]"""), schema);
        Assert.Equal("contains(lower(\"prompt\"), $p0)", f.WhereClause);
    }

    [Fact]
    public void TextContains_ShortValue_BypassesCompanionIndex()
    {
        // 1-2 char terms can't use a trigram index; they must scan lower(col) even when a companion exists.
        ColumnSchema schema = new(
        [
            new ColumnInfo("prompt", ColumnKind.Scalar),
            new ColumnInfo("prompt__lc", ColumnKind.Scalar, hasNgramIndex: true),
        ]);
        SqlFilter f = ImageSearchFilterBuilder.Build(JArray.Parse("""[{"field":"prompt","op":"contains","value":"ab"}]"""), schema);
        Assert.Equal("contains(lower(\"prompt\"), $p0)", f.WhereClause);
    }

    [Fact]
    public void NumberGe_BuildsCastComparison()
    {
        SqlFilter f = Build("""[{"field":"steps","op":"ge","value":"20"}]""");
        Assert.Equal("\"steps\" >= CAST($p0 AS DOUBLE)", f.WhereClause);
        Assert.Equal("20", f.Parameters[0].Value);
    }

    [Fact]
    public void ListContains_BuildsListFilter()
    {
        SqlFilter f = Build("""[{"field":"loras","op":"contains","value":"foo"}]""");
        Assert.Equal("len(list_filter(\"loras\", x -> contains(lower(x), $p0))) > 0", f.WhereClause);
    }

    [Fact]
    public void BoolIsTrue_NoParameter()
    {
        SqlFilter f = Build("""[{"field":"is_starred","op":"is_true","value":""}]""");
        Assert.Equal("\"is_starred\" = TRUE", f.WhereClause);
        Assert.Empty(f.Parameters);
    }

    [Fact]
    public void DiscoveredField_UsesJsonExtract()
    {
        SqlFilter f = Build("""[{"field":"scheduler","op":"equals","value":"karras"}]""");
        Assert.Equal("lower(json_extract_string(\"meta_json\", '$.\"scheduler\"')) = $p0", f.WhereClause);
        Assert.Equal("karras", f.Parameters[0].Value);
    }

    [Fact]
    public void DiscoveredNumeric_CastsJsonExtract()
    {
        SqlFilter f = Build("""[{"field":"clipskip","op":"eq","value":"2"}]""");
        Assert.Equal("TRY_CAST(json_extract_string(\"meta_json\", '$.\"clipskip\"') AS DOUBLE) = CAST($p0 AS DOUBLE)", f.WhereClause);
    }

    [Fact]
    public void MultipleRows_JoinedWithAnd()
    {
        SqlFilter f = Build("""[{"field":"model","op":"contains","value":"sdxl"},{"field":"steps","op":"gt","value":"10"}]""");
        Assert.Equal("contains(lower(\"model\"), $p0) AND \"steps\" > CAST($p1 AS DOUBLE)", f.WhereClause);
        Assert.Equal(2, f.Parameters.Count);
    }

    [Fact]
    public void UnknownOpAndMissingFieldRows_Skipped()
    {
        // The backend keeps the one valid row (empty values are a legitimate query and are pre-filtered client-side);
        // it drops the unknown-operator row and the row with no field.
        SqlFilter f = Build("""[{"field":"prompt","op":"contains","value":"keep"},{"field":"prompt","op":"bogus","value":"x"},{"field":"","op":"contains","value":"y"}]""");
        Assert.Equal("contains(lower(\"prompt\"), $p0)", f.WhereClause);
        Assert.Single(f.Parameters);
        Assert.Equal("keep", f.Parameters[0].Value);
    }

    [Fact]
    public void DiscoveredFieldName_IsSafelyEscaped()
    {
        // A malicious field name must not break out of the JSON path literal.
        SqlFilter f = Build("""[{"field":"a\"b","op":"contains","value":"v"}]""");
        Assert.Contains("json_extract_string(\"meta_json\", '$.\"a\\\"b\"')", f.WhereClause);
    }

    [Fact]
    public void NumberFilter_NonNumericValue_IsDropped()
    {
        // A non-numeric value on a numeric field must never reach CAST($p AS DOUBLE), which would 500 the search;
        // the term is dropped instead (an unparseable numeric filter constrains nothing). Covers core + discovered.
        Assert.True(Build("""[{"field":"steps","op":"gt","value":"abc"}]""").IsEmpty);
        Assert.True(Build("""[{"field":"clipskip","op":"eq","value":"not-a-number"}]""").IsEmpty);
    }

    [Fact]
    public void OperatorsByType_AdvertisesExactlyTheOperatorsTheBuilderAccepts()
    {
        // The UI renders its dropdowns from this catalog, so it must list exactly the operators each type supports.
        Assert.Equal(new[] { "eq", "ne", "gt", "lt", "ge", "le" },
            ImageSearchFilterBuilder.OperatorsByType["number"].Select(o => o.Value));
        Assert.Equal(new[] { "contains", "not_contains" },
            ImageSearchFilterBuilder.OperatorsByType["list"].Select(o => o.Value));
        Assert.Equal(new[] { "is_true", "is_false" },
            ImageSearchFilterBuilder.OperatorsByType["bool"].Select(o => o.Value));
        Assert.Contains("discovered", ImageSearchFilterBuilder.OperatorsByType.Keys);
    }
}
