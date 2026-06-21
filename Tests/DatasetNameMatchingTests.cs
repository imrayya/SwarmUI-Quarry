using Xunit;

namespace Quarry.Tests;

public class DatasetNameMatchingTests
{
    [Fact]
    public void SplitNames_BareName_SingleElement()
    {
        Assert.Equal(new[] { "prompts/1girl" }, DatasetNameMatching.SplitNames("prompts/1girl"));
    }

    [Fact]
    public void SplitNames_TrimsAndDropsEmpties()
    {
        Assert.Equal(new[] { "a", "b", "c" }, DatasetNameMatching.SplitNames(" a , b ,, c , "));
    }

    [Fact]
    public void SplitNames_Null_ReturnsEmpty()
    {
        Assert.Empty(DatasetNameMatching.SplitNames(null));
    }

    [Theory]
    [InlineData("quarry/*", true)]
    [InlineData("a?b", true)]
    [InlineData("prompts/1girl", false)]
    [InlineData("", false)]
    public void IsGlob_DetectsMetacharacters(string pattern, bool expected)
    {
        Assert.Equal(expected, DatasetNameMatching.IsGlob(pattern));
    }

    [Theory]
    [InlineData("quarry/*", "quarry/a", true)]
    [InlineData("quarry/*", "quarry/sub/a", false)] // a lone '*' stays within one folder level
    [InlineData("quarry/*", "other/a", false)]
    [InlineData("*", "anything/at/all", false)] // '*' alone hits only root-level datasets
    [InlineData("QUARRY/*", "quarry/a", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("prompts/1girl", "prompts/1girl", true)]
    [InlineData("prompts/1girl", "prompts/1girl/extra", false)]
    public void GlobMatches_AnchoredCaseInsensitive(string pattern, string candidate, bool expected)
    {
        Assert.Equal(expected, DatasetNameMatching.GlobMatches(pattern, candidate));
    }

    [Theory]
    // A lone '*' (and '?') matches within a single folder level only — never across a '/'.
    [InlineData("*", "toplevel", true)]
    [InlineData("*", "anime/1girl", false)]
    [InlineData("anime/*", "anime/1girl", true)]
    [InlineData("anime/*", "anime/sub/1girl", false)]
    [InlineData("a?c", "a/c", false)]
    // A '**' run is a globstar: it recurses across '/'.
    [InlineData("**", "toplevel", true)]
    [InlineData("**", "anime/1girl", true)]
    [InlineData("**", "anime/sub/1girl", true)]
    [InlineData("anime/**", "anime/1girl", true)]
    [InlineData("anime/**", "anime/sub/1girl", true)]
    [InlineData("anime/**", "other/1girl", false)]
    // Three-or-more stars collapse to the same recursive match as '**'.
    [InlineData("***", "anime/sub/1girl", true)]
    public void GlobMatches_SingleStarSingleLevel_DoubleStarRecurses(string pattern, string candidate, bool expected)
    {
        Assert.Equal(expected, DatasetNameMatching.GlobMatches(pattern, candidate));
    }

    [Fact]
    public void MatchMissingDirectory_FindsRelocatedDataset()
    {
        string[] datasets = ["tags/deepghs.sankaku_full", "nl/midjourney"];
        Assert.Equal("tags/deepghs.sankaku_full", DatasetNameMatching.MatchMissingDirectory("deepghs.sankaku_full", datasets));
        Assert.Equal("nl/midjourney", DatasetNameMatching.MatchMissingDirectory("midjourney", datasets));
    }

    [Fact]
    public void MatchMissingDirectory_IsCaseInsensitive()
    {
        string[] datasets = ["tags/deepghs.sankaku_full"];
        Assert.Equal("tags/deepghs.sankaku_full", DatasetNameMatching.MatchMissingDirectory("DEEPGHS.Sankaku_Full", datasets));
    }

    [Fact]
    public void MatchMissingDirectory_RequiresWholeTrailingSegment()
    {
        string[] datasets = ["tags/deepghs.sankaku_full"];
        // A misspelling must not match.
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("deepghs.sankaku_fullbar", datasets));
        // A partial final segment must not match (the boundary char before the name must be '/').
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("sankaku_full", datasets));
    }

    [Fact]
    public void MatchMissingDirectory_MatchesNestedSubpath()
    {
        string[] datasets = ["tags/anime/1girl"];
        Assert.Equal("tags/anime/1girl", DatasetNameMatching.MatchMissingDirectory("anime/1girl", datasets));
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("1girl", datasets));
    }

    [Fact]
    public void MatchMissingDirectory_MultipleMatches_PicksAlphabeticallyFirst()
    {
        string[] datasets = ["tags/deepghs.sankaku_full", "nl/deepghs.sankaku_full"];
        Assert.Equal("nl/deepghs.sankaku_full", DatasetNameMatching.MatchMissingDirectory("deepghs.sankaku_full", datasets));
    }

    [Fact]
    public void MatchMissingDirectory_NoMatch_ReturnsNull()
    {
        string[] datasets = ["tags/other"];
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("deepghs.sankaku_full", datasets));
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("", datasets));
        Assert.Null(DatasetNameMatching.MatchMissingDirectory("x", null));
    }

    [Fact]
    public void GlobMatches_EscapesRegexMetacharacters()
    {
        // A '.' in the pattern is literal, not "any char".
        Assert.True(DatasetNameMatching.GlobMatches("styles.v2/*", "styles.v2/list"));
        Assert.False(DatasetNameMatching.GlobMatches("styles.v2/*", "stylesXv2/list"));
    }
}
