using FreneticUtilities.FreneticExtensions;
using SwarmUI.Utils;

namespace Quarry;

public static class DatasetWarmer
{
    private static int Parallelism => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    private static readonly SingleFlight<string, bool> _warmFlight = new(StringComparer.Ordinal);

    public static int WarmAll(DuckDbQueryBackend backend, IReadOnlyCollection<DatasetEntry> datasets, int previewLimit)
    {
        List<DatasetEntry> pending = [.. datasets.Where(entry =>
            !DatasetCache.IsFullyCached(entry.Name.ToLowerFast(), entry.FileHash, ColumnConfig.GetPromptColumn(entry.Name), previewLimit))];
        if (pending.Count == 0)
        {
            return 0;
        }
        List<Action<IDatasetReader>> jobs = [.. pending.Select(entry => (Action<IDatasetReader>)(reader => WarmOne(reader, entry, previewLimit)))];
        backend.RunPooled(jobs, Parallelism);
        DatasetCache.PersistIfDirty();
        Logs.Info($"Quarry: warmed {pending.Count} dataset(s).");
        return pending.Count;
    }

    public static void WarmFilteredCounts(DuckDbQueryBackend backend, IReadOnlyList<(DatasetEntry Entry, SqlFilter Filter)> requests)
    {
        List<(DatasetEntry Entry, SqlFilter Filter)> misses = [.. requests.Where(request =>
            !request.Filter.IsEmpty && !DatasetCache.TryGetFilteredCount(DatasetCache.FilteredCountKey(request.Entry, request.Filter), out _))];
        if (misses.Count == 0)
        {
            return;
        }
        string flightKey = string.Join("\n",
            misses.Select(miss => DatasetCache.FilteredCountKey(miss.Entry, miss.Filter)).OrderBy(key => key, StringComparer.Ordinal));
        _warmFlight.GetOrBuild(flightKey, () =>
        {
            RunWarm(backend, misses);
            return true;
        });
    }

    private static void RunWarm(DuckDbQueryBackend backend, List<(DatasetEntry Entry, SqlFilter Filter)> misses)
    {
        List<(DatasetEntry Entry, SqlFilter Filter)> pending = [.. misses.Where(miss =>
            !DatasetCache.TryGetFilteredCount(DatasetCache.FilteredCountKey(miss.Entry, miss.Filter), out _))];
        if (pending.Count == 0)
        {
            return;
        }
        if (pending.Count == 1)
        {
            try
            {
                DatasetManager.CountRowsFiltered(pending[0].Entry, pending[0].Filter);
            }
            catch (Exception ex)
            {
                Logs.Debug($"Quarry: filtered count failed for '{pending[0].Entry.Name}': {ex.Message}");
            }
            return;
        }
        List<Action<IDatasetReader>> jobs = [];
        foreach ((DatasetEntry entry, SqlFilter filter) in pending)
        {
            jobs.Add(reader =>
            {
                try
                {
                    DatasetManager.CountRowsFiltered(entry, filter, reader);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"Quarry: filtered count failed for '{entry.Name}': {ex.Message}");
                }
            });
        }
        try
        {
            backend.RunPooled(jobs, Parallelism);
        }
        catch (Exception ex)
        {
            Logs.Debug($"Quarry: filtered count fan-out failed: {ex.Message}");
        }
        DatasetCache.PersistIfDirty();
    }

    private static void WarmOne(IDatasetReader reader, DatasetEntry entry, int limit)
    {
        string key = entry.Name.ToLowerFast();
        try
        {
            ColumnSchema schema = reader.GetSchema(entry.Path);
            DatasetCache.StoreSchema(key, entry.FileHash, schema);
            string resolved = PromptColumnResolver.Resolve(ColumnConfig.GetPromptColumn(entry.Name), schema) ?? "";
            long count = reader.CountRows(entry.Path, SqlFilter.None);
            DatasetCache.StoreRowCount(key, entry.FileHash, resolved, count);
            (List<string> columns, List<List<string>> rows) = reader.GetSampleRows(entry.Path, limit);
            DatasetCache.StorePreview(key, entry.FileHash, new DatasetCache.PreviewData(limit, columns, rows));
        }
        catch (Exception ex)
        {
            Logs.Debug($"Quarry: warm failed for '{entry.Name}': {ex.Message}");
        }
    }
}
