using System.IO;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Quarry;

/// <summary>A dataset the extension serves as a <c>&lt;q:&gt;</c> reference: its <see cref="WildcardName"/>
/// (the name used in <c>&lt;q:NAME&gt;</c>), the absolute <see cref="Path"/> to the data file/dataset, and a
/// cheap change <see cref="FileHash"/>.</summary>
public sealed record DatasetEntry(string WildcardName, string Path, string FileHash);

/// <summary>Orchestrates the datasets folder: scans it, tracks a name → dataset map, caches per-dataset
/// schema, and owns the shared <see cref="DuckDbQueryBackend"/>. Quarry serves its own <c>&lt;q:&gt;</c> tag
/// and is fully detached from SwarmUI's Wildcards folder — it writes nothing there (see
/// <see cref="CleanupLegacyPlaceholders"/> for one-time removal of files an older version mirrored).</summary>
public static class DatasetManager
{
    /// <summary>Exact body of the placeholder <c>.txt</c> files older versions wrote into the Wildcards folder.
    /// Used only to recognize and delete them on startup — see <see cref="CleanupLegacyPlaceholders"/>.</summary>
    private const string PlaceholderContent = "# Quarry placeholder - do not edit\n";

    public static bool Enabled { get; set; }

    public static string DatasetsFolder { get; set; } = "";

    public static bool IsActive => Enabled && !string.IsNullOrWhiteSpace(DatasetsFolder);

    // key = wildcard name lowercased
    private static readonly ConcurrentDictionary<string, DatasetEntry> Datasets = new();
    private static readonly ConcurrentDictionary<string, (string Hash, ColumnSchema Schema)> SchemaCache = new();
    private static readonly ConcurrentDictionary<string, (string Hash, string PromptColumn, long Count)> RowCountCache = new();
    private static readonly Dictionary<string, string> PromptColumns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> TagColumns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PromptColumnsLock = new();

    private static DuckDbQueryBackend _backend;

    /// <summary>The shared DuckDB query engine, created on first use.</summary>
    public static DuckDbQueryBackend Backend => _backend ??= new DuckDbQueryBackend();

    public static int Count => Datasets.Count;

    public static void Initialize()
    {
        // One-time housekeeping: an earlier Quarry mirrored every dataset to a placeholder .txt in the
        // Wildcards folder. We no longer do that, so remove any that are still lying around.
        CleanupLegacyPlaceholders();
        if (IsActive)
        {
            Sync();
        }
        Program.ModelRefreshEvent += OnModelRefresh;
    }

    public static void Shutdown()
    {
        Program.ModelRefreshEvent -= OnModelRefresh;
        _backend?.Dispose();
        _backend = null;
    }

    private static void OnModelRefresh()
    {
        if (IsActive)
        {
            Sync();
        }
    }

    /// <summary>Resolves a wildcard name to its dataset, or null when it is not one of ours.</summary>
    public static DatasetEntry Resolve(string wildcardName)
    {
        if (!IsActive || wildcardName is null)
        {
            return null;
        }
        return Datasets.TryGetValue(wildcardName.ToLowerFast(), out DatasetEntry entry) ? entry : null;
    }

    public static string GetConfiguredPromptColumn(string wildcardName)
    {
        lock (PromptColumnsLock)
        {
            return PromptColumns.TryGetValue(wildcardName, out string column) ? column : null;
        }
    }

    public static void SetPromptColumns(IReadOnlyDictionary<string, string> columns)
    {
        lock (PromptColumnsLock)
        {
            PromptColumns.Clear();
            foreach ((string name, string column) in columns)
            {
                if (!string.IsNullOrWhiteSpace(column))
                {
                    PromptColumns[name] = column;
                }
            }
        }
    }

    public static IReadOnlyDictionary<string, string> GetPromptColumnsSnapshot()
    {
        lock (PromptColumnsLock)
        {
            return new Dictionary<string, string>(PromptColumns);
        }
    }

    /// <summary>The columns a user picked as "tag" columns for a dataset, or an empty list when none are set.
    /// The <c>tags</c> keyword in a wildcard filter searches across all of them as one merged column.</summary>
    public static IReadOnlyList<string> GetConfiguredTagColumns(string wildcardName)
    {
        lock (PromptColumnsLock)
        {
            return TagColumns.TryGetValue(wildcardName, out List<string> columns) ? [.. columns] : [];
        }
    }

    public static void SetTagColumns(IReadOnlyDictionary<string, IReadOnlyList<string>> columns)
    {
        lock (PromptColumnsLock)
        {
            TagColumns.Clear();
            foreach ((string name, IReadOnlyList<string> cols) in columns)
            {
                List<string> kept = cols is null ? [] : [.. cols.Where(c => !string.IsNullOrWhiteSpace(c))];
                if (kept.Count > 0)
                {
                    TagColumns[name] = kept;
                }
            }
        }
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagColumnsSnapshot()
    {
        lock (PromptColumnsLock)
        {
            return TagColumns.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value]);
        }
    }

    /// <summary>A snapshot of all currently known datasets, for fanning a glob/comma reference out over them.</summary>
    public static IReadOnlyCollection<DatasetEntry> AllDatasets => [.. Datasets.Values];

    /// <summary>A snapshot of every known dataset's <c>&lt;q:&gt;</c> name — the list a plain reference is fuzzy
    /// matched against (replacing the Wildcards folder we used to lean on).</summary>
    public static IReadOnlyList<string> AllDatasetNames => [.. Datasets.Values.Select(e => e.WildcardName)];

    /// <summary>Returns the dataset's schema, cached until the underlying file changes.</summary>
    public static ColumnSchema GetSchema(DatasetEntry entry)
    {
        string key = entry.WildcardName.ToLowerFast();
        if (SchemaCache.TryGetValue(key, out (string Hash, ColumnSchema Schema) cached) && cached.Hash == entry.FileHash)
        {
            return cached.Schema;
        }
        ColumnSchema schema = Backend.GetSchema(entry.Path);
        SchemaCache[key] = (entry.FileHash, schema);
        return schema;
    }

    /// <summary>Returns the number of rows whose <paramref name="promptColumn"/> is non-empty — i.e. the rows
    /// this dataset can actually contribute as wildcard picks — so the UI count matches what a query yields and
    /// never includes blank rows. Cached until the underlying file OR the resolved prompt column changes (the
    /// column choice affects the count, but not the file hash). A null/empty prompt column (a dataset with no
    /// readable columns) falls back to the raw total.</summary>
    public static long GetRowCount(DatasetEntry entry, string promptColumn)
    {
        string key = entry.WildcardName.ToLowerFast();
        if (RowCountCache.TryGetValue(key, out (string Hash, string PromptColumn, long Count) cached)
            && cached.Hash == entry.FileHash && cached.PromptColumn == promptColumn)
        {
            return cached.Count;
        }
        SqlFilter filter = string.IsNullOrEmpty(promptColumn)
            ? SqlFilter.None
            : SqlFilterBuilder.NonEmptyPrompt(promptColumn);
        long count = Backend.CountRows(entry.Path, filter);
        RowCountCache[key] = (entry.FileHash, promptColumn, count);
        return count;
    }

    /// <summary>Per-dataset info for the settings UI: columns (name + list-ness) and the resolved prompt column.</summary>
    public static List<DatasetInfo> GetDatasetsInfo()
    {
        List<DatasetInfo> result = [];
        foreach (DatasetEntry entry in Datasets.Values.OrderBy(e => e.WildcardName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ColumnSchema schema = GetSchema(entry);
                string resolved = PromptColumnResolver.Resolve(GetConfiguredPromptColumn(entry.WildcardName), schema);
                long? rowCount = null;
                try
                {
                    rowCount = GetRowCount(entry, resolved);
                }
                catch
                {
                    // Row count is a best-effort display value; a count failure must not hide a readable dataset.
                }
                result.Add(new DatasetInfo(entry.WildcardName, [.. schema.Columns], resolved, GetConfiguredPromptColumn(entry.WildcardName), [.. GetConfiguredTagColumns(entry.WildcardName)], rowCount, null));
            }
            catch (Exception ex)
            {
                result.Add(new DatasetInfo(entry.WildcardName, [], null, GetConfiguredPromptColumn(entry.WildcardName), [.. GetConfiguredTagColumns(entry.WildcardName)], null, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Reads up to <paramref name="limit"/> rows from a dataset for the preview UI. Resolves the
    /// wildcard name to its file, then delegates to the backend. Returns success plus the column names and
    /// row values, or a failure with an error message (unknown dataset, or a query/IO error).</summary>
    public static (bool Success, List<string> Columns, List<List<string>> Rows, string Error) PreviewDataset(string wildcardName, int limit)
    {
        DatasetEntry entry = Resolve(wildcardName);
        if (entry is null)
        {
            return (false, null, null, $"Unknown dataset '{wildcardName}'.");
        }
        try
        {
            (List<string> columns, List<List<string>> rows) = Backend.GetSampleRows(entry.Path, limit);
            return (true, columns, rows, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    /// <summary>Scans the datasets folder and rebuilds the name → dataset map. Drops datasets no longer present,
    /// invalidates changed schemas/row counts, and resets the DuckDB connection when anything changed. Writes
    /// nothing to disk — Quarry no longer mirrors datasets into the Wildcards folder.</summary>
    public static void Sync()
    {
        if (!IsActive)
        {
            Datasets.Clear();
            SchemaCache.Clear();
            RowCountCache.Clear();
            return;
        }
        try
        {
            string root = DatasetsFolder;
            if (!Directory.Exists(root))
            {
                Logs.Warning($"Quarry: datasets folder does not exist: '{root}'");
                return;
            }
            HashSet<string> seen = [];
            // Tracks whether any dataset was added, removed, or had its file change. If so we rebuild the
            // DuckDB connection below: a regenerated Lance dataset keeps the same path but gets new fragment
            // files, and the old connection's cached manifest would still point at the deleted ones.
            bool contentChanged = false;
            foreach (string datasetPath in DatasetScanner.Enumerate(root))
            {
                string relative = Path.GetRelativePath(root, datasetPath);
                string name = WildcardNaming.ToWildcardName(relative);
                string key = name.ToLowerFast();
                if (!seen.Add(key))
                {
                    Logs.Warning($"Quarry: multiple files map to '{name}'; keeping the first, ignoring '{relative}'.");
                    continue;
                }
                string hash = ComputeHash(datasetPath);
                if (SchemaCache.TryGetValue(key, out (string Hash, ColumnSchema Schema) cached) && cached.Hash != hash)
                {
                    SchemaCache.TryRemove(key, out _);
                }
                if (RowCountCache.TryGetValue(key, out (string Hash, string PromptColumn, long Count) cachedCount) && cachedCount.Hash != hash)
                {
                    RowCountCache.TryRemove(key, out _);
                }
                if (!Datasets.TryGetValue(key, out DatasetEntry previous) || previous.FileHash != hash)
                {
                    contentChanged = true;
                }
                Datasets[key] = new DatasetEntry(name, datasetPath, hash);
            }
            foreach (string key in Datasets.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                Datasets.TryRemove(key, out _);
                SchemaCache.TryRemove(key, out _);
                RowCountCache.TryRemove(key, out _);
                contentChanged = true;
            }
            // A dataset's files changed on disk, so drop the DuckDB connection's cached metadata (notably
            // stale Lance manifests pointing at regenerated, now-missing fragment files). No-op when the
            // backend was never used (e.g. at startup, before the first query).
            if (contentChanged)
            {
                _backend?.Reset();
            }
            Logs.Info($"Quarry: synced {Datasets.Count} dataset(s).");
        }
        catch (Exception ex)
        {
            Logs.Error($"Quarry: error syncing datasets: {ex.ReadableString()}");
        }
    }

    /// <summary>Removes the placeholder <c>.txt</c> files an older Quarry mirrored into the Wildcards folder.
    /// Deletes only files whose contents are byte-for-byte our <see cref="PlaceholderContent"/> sentinel, so a
    /// real wildcard a user wrote is never touched. Best-effort and idempotent: once the files are gone, later
    /// runs find nothing to do.</summary>
    private static void CleanupLegacyPlaceholders()
    {
        try
        {
            string wildcardDir = WildcardsHelper.Folder;
            if (!Directory.Exists(wildcardDir))
            {
                return;
            }
            long sentinelLength = Encoding.UTF8.GetByteCount(PlaceholderContent);
            int removed = 0;
            foreach (string file in Directory.EnumerateFiles(wildcardDir, "*.txt", SearchOption.AllDirectories))
            {
                try
                {
                    // Cheap guard: skip anything that isn't the exact size of our sentinel before reading it.
                    if (new FileInfo(file).Length != sentinelLength || File.ReadAllText(file) != PlaceholderContent)
                    {
                        continue;
                    }
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // A single unreadable/locked file must not abort the sweep.
                }
            }
            if (removed > 0)
            {
                Logs.Info($"Quarry: removed {removed} legacy placeholder wildcard file(s) from '{wildcardDir}'.");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"Quarry: failed to clean up legacy placeholders: {ex.Message}");
        }
    }

    public static (bool Success, int Count, string Message, string Error) Refresh()
    {
        if (!IsActive)
        {
            return (false, 0, null, "Quarry is not active. Enable it and set a folder first.");
        }
        try
        {
            Sync();
            return (true, Count, $"Synced {Count} dataset(s).", null);
        }
        catch (Exception ex)
        {
            return (false, 0, null, ex.Message);
        }
    }

    private static string ComputeHash(string path)
    {
        try
        {
            if (Directory.Exists(path)) // Lance dataset directory
            {
                return $"dir:{new DirectoryInfo(path).LastWriteTimeUtc.Ticks}";
            }
            FileInfo info = new(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "unknown";
        }
    }
}

/// <summary>Settings-UI view of one dataset: its columns, the resolved prompt column (what would be used
/// now), the explicitly configured column (if any), the count of rows with a non-empty prompt — i.e. usable
/// picks (null when unknown), and an error message if the schema couldn't be read.</summary>
public sealed record DatasetInfo(
    string Name,
    IReadOnlyList<ColumnInfo> Columns,
    string ResolvedPromptColumn,
    string ConfiguredPromptColumn,
    IReadOnlyList<string> ConfiguredTagColumns,
    long? RowCount,
    string Error);
