namespace Quarry;

/// <summary>Maps a dataset file's path (relative to the datasets folder) to its <c>&lt;q:&gt;</c> name. Pure
/// and side-effect free.</summary>
public static class WildcardNaming
{
    /// <summary>Normalizes a relative path to a dataset name: forward slashes, no leading slash, and the
    /// final segment's extension stripped. e.g. <c>prompts\1girl.parquet</c> → <c>prompts/1girl</c>,
    /// <c>styles.v2/list.jsonl</c> → <c>styles.v2/list</c>.</summary>
    public static string ToWildcardName(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        int lastSlash = normalized.LastIndexOf('/');
        int lastDot = normalized.LastIndexOf('.');
        // Only strip an extension that belongs to the final path segment (not a dotted directory name).
        if (lastDot > lastSlash)
        {
            normalized = normalized[..lastDot];
        }
        return normalized;
    }
}
