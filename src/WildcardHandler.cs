using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Quarry;

/// <summary>Registers SwarmUI's <c>q</c> prompt-tag (<c>&lt;q:NAME[query]&gt;</c>) and serves it from our
/// datasets via DuckDB. The <c>q</c> prefix is Quarry's own — it does not piggyback on, capture, or chain to
/// core's <c>wc</c>/<c>wildcard</c> handlers. A <c>q</c> reference that doesn't match a dataset is dropped
/// (with a warning when the extension is active) rather than delegated anywhere.</summary>
public static class WildcardHandler
{
    /// <summary>The prompt-tag prefix this extension owns: <c>&lt;q:...&gt;</c>.</summary>
    public const string TagPrefix = "q";

    /// <summary>How many rows to probe forward from a picked index when that row's prompt is blank. Datasets
    /// are blank-filtered at ingest (scripts/to_lancedb.py), so this only matters for a dataset added without
    /// that cleanup; a small bound skips the occasional stray empty row without letting a pathological
    /// all-blank file loop.</summary>
    private const int BlankProbeLimit = 8;

    public static void Initialize()
    {
        T2IPromptHandling.PromptTagProcessors[TagPrefix] = Processor;
        T2IPromptHandling.PromptTagLengthEstimators[TagPrefix] = Estimator;
    }

    /// <summary>One file matched by a reference: where to read its prompt from, the WHERE filter built for this
    /// query against its schema, how many rows pass that filter (<paramref name="Count"/>), and the dataset's
    /// total row count (<paramref name="Total"/> — used to gauge filter selectivity when sampling a row).</summary>
    private sealed record MatchedDataset(DatasetEntry Entry, string PromptColumn, SqlFilter Filter, long Count, long Total);

    private static string Processor(string data, T2IPromptHandling.PromptTagContext context)
    {
        WildcardQuery query;
        try
        {
            query = WildcardQueryParser.Parse(data);
        }
        catch (WildcardQueryException ex)
        {
            // A malformed <q:...> is a user error, not something to pass along — drop it (warn only when active
            // so a disabled extension doesn't spam old prompts that still contain q-tags).
            if (DatasetManager.IsActive)
            {
                context.TrackWarning($"Quarry: invalid reference '{data}': {ex.Message}");
            }
            return "";
        }
        List<DatasetEntry> targets = ResolveTargets(query.Name);
        if (targets.Count == 0)
        {
            if (DatasetManager.IsActive)
            {
                context.TrackWarning($"Quarry: no dataset matches '{query.Name}'.");
            }
            return "";
        }
        bool multi = IsMultiReference(query.Name);
        try
        {
            return ProcessTargets(query, targets, multi, context);
        }
        catch (WildcardQueryException ex)
        {
            context.TrackWarning($"Quarry '{query.Name}': {ex.Message}");
            return "";
        }
        catch (Exception ex)
        {
            context.TrackWarning($"Quarry '{query.Name}' failed: {ex.Message}");
            Logs.Error($"Quarry: error processing '{data}': {ex.ReadableString()}");
            return "";
        }
    }

    /// <summary>Matches a <c>&lt;q:...&gt;</c> reference tag (optional <c>[n]</c> / <c>[n-m]</c> count, like core),
    /// capturing the inner <c>NAME[query]</c> data. Reserved chars <c>&lt; &gt;</c> can't appear in a value, so
    /// stopping at the first <c>&gt;</c> is safe.</summary>
    private static readonly Regex ReferenceTagRegex =
        new(@"<q(?:\[\d+(?:-\d+)?\])?:([^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns the distinct wildcard names of every dataset this extension would serve for the
    /// <c>q</c> references in <paramref name="prompt"/>, resolved with the exact same name
    /// handling as real expansion (comma lists, globs, fuzzy match) so the settings UI can flag precisely what
    /// would be used. Filters are ignored — only the NAME part matters. Never throws: an unparseable or
    /// non-matching tag is skipped.</summary>
    public static IReadOnlyList<string> ResolveReferencedDatasetNames(string prompt)
    {
        List<string> names = [];
        if (string.IsNullOrEmpty(prompt))
        {
            return names;
        }
        HashSet<string> seen = [];
        foreach (Match match in ReferenceTagRegex.Matches(prompt))
        {
            WildcardQuery query;
            try
            {
                query = WildcardQueryParser.Parse(match.Groups[1].Value);
            }
            catch (WildcardQueryException)
            {
                continue;
            }
            foreach (DatasetEntry entry in ResolveTargets(query.Name))
            {
                if (seen.Add(entry.WildcardName.ToLowerFast()))
                {
                    names.Add(entry.WildcardName);
                }
            }
        }
        return names;
    }

    /// <summary>Resolves a reference NAME to the datasets it targets. A NAME may be a comma-separated list and
    /// each part may be a glob (<c>quarry/*</c>): globs match every known dataset, a plain name resolves via
    /// the same fuzzy <see cref="T2IParamTypes.GetBestInList"/> SwarmUI uses for wildcards — but against our own
    /// dataset names, never the Wildcards folder. Results are de-duplicated by lowercased name, preserving
    /// discovery order.</summary>
    private static List<DatasetEntry> ResolveTargets(string name)
    {
        List<DatasetEntry> targets = [];
        HashSet<string> seen = [];
        foreach (string part in WildcardNameMatching.SplitNames(name))
        {
            if (WildcardNameMatching.IsGlob(part))
            {
                foreach (DatasetEntry entry in DatasetManager.AllDatasets.OrderBy(e => e.WildcardName, StringComparer.OrdinalIgnoreCase))
                {
                    if (WildcardNameMatching.GlobMatches(part, entry.WildcardName) && seen.Add(entry.WildcardName.ToLowerFast()))
                    {
                        targets.Add(entry);
                    }
                }
            }
            else
            {
                string card = T2IParamTypes.GetBestInList(part, DatasetManager.AllDatasetNames);
                if (card is not null && DatasetManager.Resolve(card) is DatasetEntry entry && seen.Add(entry.WildcardName.ToLowerFast()))
                {
                    targets.Add(entry);
                }
            }
        }
        return targets;
    }

    private static bool IsMultiReference(string name) => name.Contains(',') || WildcardNameMatching.IsGlob(name);

    private static string ProcessTargets(WildcardQuery query, List<DatasetEntry> targets, bool multi, T2IPromptHandling.PromptTagContext context)
    {
        // Pass 1 (cheap, no scanning): resolve each target's prompt column and build its filter from the
        // cached schema — just enough to know which datasets need a (possibly costly) filtered count.
        List<PlanDraft> drafts = [];
        foreach (DatasetEntry entry in targets)
        {
            PlanDraft draft;
            try
            {
                draft = DraftPlan(query, entry, context);
            }
            catch (Exception ex) when (multi)
            {
                // In a fan-out, one unusable file (missing column, unreadable schema, …) must not abort the
                // rest. A single explicit reference still surfaces the error via the caller's catch.
                Logs.Debug($"Quarry: skipping '{entry.WildcardName}' in '{query.Name}': {ex.Message}");
                continue;
            }
            if (draft is not null)
            {
                drafts.Add(draft);
            }
        }
        // A filtered count is a live scan (the filter defeats both the cached total and Lance's count
        // pushdown). For a fan-out, pre-compute those scans in parallel and cache them, so the whole query
        // costs about its slowest single dataset instead of the sum of every matched dataset's scan.
        if (multi)
        {
            DatasetManager.WarmFilteredCounts([.. drafts.Where(draft => !draft.Filter.IsEmpty).Select(draft => (draft.Entry, draft.Filter))]);
        }
        // Pass 2: size each dataset's pick pool from the (now warm) counts.
        List<MatchedDataset> matched = [];
        foreach (PlanDraft draft in drafts)
        {
            MatchedDataset plan;
            try
            {
                long count = ResolveCount(draft, multi);
                if (count <= 0)
                {
                    continue;
                }
                // The unfiltered total (a cached metadata read) lets the fetch gauge filter selectivity; with
                // no filter it equals the count, so there's nothing extra to read.
                long totalRows = draft.Filter.IsEmpty ? count : DatasetManager.GetRowCount(draft.Entry, draft.PromptColumn);
                plan = new MatchedDataset(draft.Entry, draft.PromptColumn, draft.Filter, count, totalRows);
            }
            catch (Exception ex) when (multi)
            {
                Logs.Debug($"Quarry: skipping '{draft.Entry.WildcardName}' in '{query.Name}': {ex.Message}");
                continue;
            }
            matched.Add(plan);
        }
        if (matched.Count == 0)
        {
            return "";
        }
        (int picks, string separator) = T2IPromptHandling.InterpretPredataForRandom("random", context.PreData, query.Name, context);
        if (separator is null)
        {
            return null;
        }
        // Quarry tracks its own contributions in `used_quarry` (kept separate from core's `used_wildcards`),
        // so a <q:...> datafile is reported distinctly from a real wildcard. Core serializes every ExtraMeta
        // key into the saved image metadata, so this surfaces there automatically.
        List<string> usedQuarry = context.Input.ExtraMeta.GetOrCreate("used_quarry", () => new List<string>()) as List<string>;

        bool indexBehavior = context.Input.Get(T2IParamTypes.WildcardSeedBehavior, "Random") == "Index";
        Func<long> nextIndex = indexBehavior
            ? () => context.Input.GetWildcardSeed()
            : () => context.Input.GetWildcardRandom().Next();

        // Pool every matching row across the matched files: a global index in [0, total) maps into whichever
        // file's row range contains it, so larger files contribute proportionally more picks.
        long total = 0;
        long[] offsets = new long[matched.Count];
        for (int i = 0; i < matched.Count; i++)
        {
            offsets[i] = total;
            total += matched[i].Count;
        }

        // Record a file in the used_quarry metadata only when a pick actually lands on it — not every
        // candidate of a comma/glob fan-out — so the saved metadata reports what truly contributed. First-hit order.
        List<string> hitOrdered = [];
        HashSet<string> hit = [];

        string result = WildcardSelection.Pick(
            total,
            picks,
            separator,
            nextIndex,
            globalIndex =>
            {
                int i = LocateDataset(offsets, globalIndex);
                MatchedDataset m = matched[i];
                long localIndex = globalIndex - offsets[i];
                // Fetch the picked row's prompt. A selective-enough filter samples a matching row by cheap
                // random O(1) seeks (rejection sampling) instead of a filtered OFFSET scan; an empty or sparse
                // filter uses the deterministic OFFSET fetch. Either path probes past a stray blank row (rare —
                // datasets are blank-filtered at ingest); the warning below is the last-resort backstop.
                string value = FetchPrompt(m, localIndex, globalIndex, context);
                if (value.Length == 0)
                {
                    Logs.Warning(
                        $"Quarry wildcard '{m.Entry.WildcardName}': blank result from prompt column '{m.PromptColumn}' near row {localIndex} (file '{m.Entry.Path}').");
                }
                else if (hit.Add(m.Entry.WildcardName))
                {
                    hitOrdered.Add(m.Entry.WildcardName);
                }
                return value;
            });

        foreach (string name in hitOrdered)
        {
            if (!usedQuarry.Contains(name))
            {
                usedQuarry.Add(name);
            }
        }
        return result;
    }

    /// <summary>One target resolved far enough to size its pick pool: which file, where its prompt is, and the
    /// WHERE filter built for this query against its (cached) schema. The row count is filled in separately
    /// (Pass 2) so a fan-out's filtered counts can be computed in parallel.</summary>
    private sealed record PlanDraft(DatasetEntry Entry, string PromptColumn, SqlFilter Filter);

    /// <summary>The cheap, scan-free half of planning: resolves a target's prompt column and builds its filter
    /// from the cached schema. Returns null (with a warning) when the file has no prompt column to read.</summary>
    private static PlanDraft DraftPlan(WildcardQuery query, DatasetEntry entry, T2IPromptHandling.PromptTagContext context)
    {
        ColumnSchema schema = DatasetManager.GetSchema(entry);
        string promptColumn = PromptColumnResolver.Resolve(DatasetManager.GetConfiguredPromptColumn(entry.WildcardName), schema);
        if (promptColumn is null)
        {
            context.TrackWarning($"Quarry wildcard '{entry.WildcardName}' has no columns to read.");
            return null;
        }
        // No configured tag columns (or a single-column prompt file) → the prompt column doubles as the tag
        // column, so `[tags=…]` still works without any per-file setup.
        List<ColumnInfo> tagColumns = TagColumnResolver.Resolve(DatasetManager.GetConfiguredTagColumns(entry.WildcardName), schema, promptColumn);
        SqlFilter filter = SqlFilterBuilder.Build(query, schema, tagColumns);
        return new PlanDraft(entry, promptColumn, filter);
    }

    /// <summary>Sizes a draft's pick pool. With no [query] filter (the common case) this is the dataset's
    /// invariant row total from the warm cache — and the matching GetPromptAt fetch is then a bare LIMIT/OFFSET
    /// that DuckDB's lance scan pushes down to a native O(1) row seek. A non-empty filter is dataset-specific:
    /// in a fan-out (<paramref name="multi"/>) the count was pre-computed in parallel by
    /// <see cref="DatasetManager.WarmFilteredCounts"/> and is read back here (a miss means that dataset's count
    /// failed → 0, so it is dropped); a single explicit reference counts it directly so any error surfaces to
    /// the user. Blank prompt rows are excluded at ingest, so the total is the usable-pick total;
    /// ProcessTargets still probes past any stray blank in a dataset that skipped that cleanup.</summary>
    private static long ResolveCount(PlanDraft draft, bool multi)
    {
        if (draft.Filter.IsEmpty)
        {
            return DatasetManager.GetRowCount(draft.Entry, draft.PromptColumn);
        }
        if (multi)
        {
            return DatasetManager.TryGetFilteredCount(draft.Entry, draft.Filter, out long count) ? count : 0;
        }
        return DatasetManager.CountRowsFiltered(draft.Entry, draft.Filter);
    }

    /// <summary>Finds the index of the file whose pooled row range contains <paramref name="globalIndex"/>.</summary>
    private static int LocateDataset(long[] offsets, long globalIndex)
    {
        for (int i = offsets.Length - 1; i >= 0; i--)
        {
            if (globalIndex >= offsets[i])
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>How many random unfiltered-row seeks the rejection sampler tries before falling back to the
    /// deterministic filtered OFFSET fetch. Each seek is a cheap native O(1) Lance lookup, so this doubles as
    /// the selectivity gate: a filter matching fewer than ~1/this of a dataset's rows is scanned directly
    /// (random seeks would mostly miss) rather than sampled.</summary>
    private const int RejectionMaxAttempts = 48;

    /// <summary>Reads a picked row's prompt. With no filter this is a direct LIMIT/OFFSET seek (Lance pushes it
    /// down to O(1)). With a selective-enough filter it samples a matching row by cheap random seeks
    /// (<see cref="TryRejectionSample"/>) rather than the filtered OFFSET scan that must skip
    /// <paramref name="localIndex"/> matches; a sparse filter, or sampling that finds no non-blank match, falls
    /// back to <see cref="FetchByOffset"/>. Returns "" only when the dataset is empty or every probed row is
    /// blank.</summary>
    private static string FetchPrompt(MatchedDataset m, long localIndex, long globalIndex, T2IPromptHandling.PromptTagContext context)
    {
        if (m.Count <= 0)
        {
            return "";
        }
        if (!m.Filter.IsEmpty)
        {
            string sampled = TryRejectionSample(m, globalIndex, context);
            if (sampled is not null)
            {
                return sampled;
            }
        }
        return FetchByOffset(m, localIndex, context);
    }

    /// <summary>The deterministic OFFSET fetch: the <paramref name="localIndex"/>-th row that passes the filter
    /// (or the localIndex-th row outright when there is no filter), probing a few rows forward past any stray
    /// blank. A pure function of localIndex, so it reproduces under a fixed seed (including the Index seed
    /// behavior). With a filter the seek scans to the offset (no pushdown); with none Lance pushes it to
    /// O(1).</summary>
    private static string FetchByOffset(MatchedDataset m, long localIndex, T2IPromptHandling.PromptTagContext context)
    {
        string value = "";
        for (int probe = 0; probe < BlankProbeLimit && m.Count > 0; probe++)
        {
            long row = (localIndex + probe) % m.Count;
            value = context.Parse(DatasetManager.Backend.GetPromptAt(m.Entry.Path, m.PromptColumn, m.Filter, row)).Trim();
            if (value.Length > 0)
            {
                break;
            }
        }
        return value;
    }

    /// <summary>Tries to pick a matching row by rejection sampling: draw random rows from the dataset's FULL
    /// (unfiltered) range, fetch each with a cheap native O(1) seek, and accept the first that passes the filter
    /// and is non-blank. Returns that prompt, or null to tell the caller to fall back to
    /// <see cref="FetchByOffset"/> — either because the filter is too sparse to sample efficiently (over
    /// <see cref="RejectionMaxAttempts"/> expected draws per hit) or because the bounded draws found no
    /// non-blank match. Determinism: the candidate stream comes from a seed derived solely from
    /// <paramref name="globalIndex"/> (itself a pure function of the wildcard seed) in its OWN RNG — so it
    /// reproduces under a fixed seed yet never consumes from, or perturbs, the shared wildcard RNG that other
    /// prompt tags draw from. It also yields a uniform pick over matching rows, matching the OFFSET fetch's
    /// distribution.</summary>
    private static string TryRejectionSample(MatchedDataset m, long globalIndex, T2IPromptHandling.PromptTagContext context)
    {
        // Selectivity gate: total/Count is the expected number of draws per match; sample only when a hit is
        // expected within the attempt budget. Too sparse → let the caller scan instead.
        if (m.Total <= 0 || m.Total > m.Count * (long)RejectionMaxAttempts)
        {
            return null;
        }
        Random random = new(RejectionSeed(globalIndex));
        for (int attempt = 0; attempt < RejectionMaxAttempts; attempt++)
        {
            long candidate = random.NextInt64(m.Total);
            (string raw, bool matches) = DatasetManager.Backend.GetCandidateAt(m.Entry.Path, m.PromptColumn, m.Filter, candidate);
            if (!matches)
            {
                continue;
            }
            string value = context.Parse(raw).Trim();
            if (value.Length > 0)
            {
                return value;
            }
            // A matching but blank row (rare — blanks are excluded at ingest); keep sampling.
        }
        return null;
    }

    /// <summary>A stable per-pick seed for the rejection sampler, derived only from the pick's pooled global
    /// index so it is a pure function of the wildcard seed (reproducible) and isolated from the shared wildcard
    /// RNG. A 64-bit mix (Fibonacci-hash multiply + xor-fold) decorrelates adjacent indices so neighboring
    /// picks don't sample near-identical sequences.</summary>
    private static int RejectionSeed(long globalIndex)
    {
        long mixed = unchecked(globalIndex * (long)0x9E3779B97F4A7C15UL);
        return unchecked((int)(mixed ^ (mixed >> 32)));
    }

    /// <summary>Length estimate for a <c>&lt;q:...&gt;</c> tag. A queried dataset row can't be estimated cheaply
    /// and an unmatched tag is dropped, so either way it contributes nothing — return empty.</summary>
    private static string Estimator(string data, T2IPromptHandling.PromptTagContext context) => "";
}
