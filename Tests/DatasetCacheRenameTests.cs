using Xunit;

namespace Quarry.Tests;

// Covers the cache re-keying that lets a relocated dataset (root -> nl/tags) keep its cached work instead of
// rebuilding it, since an on-disk move preserves the content hash. Each test uses unique keys so the shared
// static cache state can't cross-contaminate.
public class DatasetCacheRenameTests
{
    [Fact]
    public void Rename_MovesRowCountToNewKey()
    {
        const string oldKey = "renametest.rowcount", newKey = "tags/renametest.rowcount", hash = "h1", column = "prompt";
        DatasetCache.StoreRowCount(oldKey, hash, column, 4242);

        DatasetCache.Rename(oldKey, newKey);

        Assert.True(DatasetCache.TryGetRowCount(newKey, hash, column, out long moved));
        Assert.Equal(4242, moved);
        Assert.False(DatasetCache.TryGetRowCount(oldKey, hash, column, out _));
    }

    [Fact]
    public void Rename_RewritesFilteredCountKeyPrefixOnly()
    {
        const string oldKey = "renametest.filtered", newKey = "nl/renametest.filtered";
        string oldCountKey = $"{oldKey}|h1|rating='s'";
        DatasetCache.StoreFilteredCount(oldCountKey, 99);

        DatasetCache.Rename(oldKey, newKey);

        Assert.True(DatasetCache.TryGetFilteredCount($"{newKey}|h1|rating='s'", out long moved));
        Assert.Equal(99, moved);
        Assert.False(DatasetCache.TryGetFilteredCount(oldCountKey, out _));
    }

    [Fact]
    public void Rename_DoesNotTouchOtherDatasets()
    {
        const string oldKey = "renametest.other", newKey = "tags/renametest.other";
        // A different dataset whose name merely shares the prefix as a substring must be untouched.
        const string bystander = "renametest.otherbystander";
        DatasetCache.StoreRowCount(oldKey, "h", "c", 1);
        DatasetCache.StoreRowCount(bystander, "h", "c", 7);
        DatasetCache.StoreFilteredCount($"{bystander}|h|f", 7);

        DatasetCache.Rename(oldKey, newKey);

        Assert.True(DatasetCache.TryGetRowCount(bystander, "h", "c", out long untouched));
        Assert.Equal(7, untouched);
        Assert.True(DatasetCache.TryGetFilteredCount($"{bystander}|h|f", out _));
    }

    [Fact]
    public void Rename_NoOpWhenNothingCachedOrKeysEqual()
    {
        // Neither of these should throw.
        DatasetCache.Rename("renametest.absent", "tags/renametest.absent");
        DatasetCache.Rename("renametest.same", "renametest.same");
        Assert.False(DatasetCache.TryGetRowCount("tags/renametest.absent", "h", "c", out _));
    }
}
