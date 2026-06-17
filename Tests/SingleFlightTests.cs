using System.Collections.Concurrent;
using Xunit;

namespace Quarry.Tests;

public class SingleFlightTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>String comparer that records the managed thread id of every dictionary probe via
    /// <see cref="GetHashCode(string)"/>. ConcurrentDictionary.GetOrAdd hashes the key as part of its lookup,
    /// so once a thread has been recorded here for a key that already exists, that thread's GetOrAdd has found
    /// (and will return) the existing entry — i.e. it has attached to the in-flight build and can no longer
    /// start a second one. This lets the concurrency tests release the gated build only after every caller has
    /// provably attached, making "built exactly once" deterministic rather than timing-dependent.</summary>
    private sealed class ProbeRecordingComparer : IEqualityComparer<string>
    {
        private readonly ConcurrentDictionary<int, byte> _threads;

        public ProbeRecordingComparer(ConcurrentDictionary<int, byte> threads) => _threads = threads;

        public bool Equals(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj)
        {
            _threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            return StringComparer.Ordinal.GetHashCode(obj);
        }
    }

    private sealed record CoalesceResult(int Builds, int InFlightAfter, IReadOnlyList<int> Values, IReadOnlyList<Exception> Errors);

    /// <summary>Runs one winner that parks inside the build, then <paramref name="followerCount"/> followers that
    /// all attach to the same key, releasing the build only once every caller has provably probed the dictionary.
    /// Captures each caller's value (or exception) so assertions never touch a blocking Task operation.</summary>
    private static CoalesceResult RunCoalesced(int followerCount, Func<int> body)
    {
        ConcurrentDictionary<int, byte> probed = new();
        SingleFlight<string, int> flight = new(new ProbeRecordingComparer(probed));
        int builds = 0;
        ManualResetEventSlim buildStarted = new(false);
        ManualResetEventSlim proceed = new(false);
        ConcurrentQueue<int> values = new();
        ConcurrentQueue<Exception> errors = new();
        Func<int> build = () =>
        {
            Interlocked.Increment(ref builds);
            buildStarted.Set();
            Assert.True(proceed.Wait(Timeout));
            return body();
        };
        void Caller()
        {
            try
            {
                values.Enqueue(flight.GetOrBuild("k", build));
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        }
        int total = followerCount + 1;
        Task[] tasks = new Task[total];
        // Winner first: its GetOrAdd creates the entry, so by the time followers run the entry is already present
        // and their probes are guaranteed to find it (the entry can't be removed until we release `proceed`).
        tasks[0] = Task.Factory.StartNew(Caller, TaskCreationOptions.LongRunning);
        Assert.True(buildStarted.Wait(Timeout));
        for (int i = 1; i < total; i++)
        {
            tasks[i] = Task.Factory.StartNew(Caller, TaskCreationOptions.LongRunning);
        }
        Assert.True(SpinWait.SpinUntil(() => probed.Count >= total, Timeout), "not all callers attached to the in-flight build");
        proceed.Set();
        Assert.True(SpinWait.SpinUntil(() => tasks.All(t => t.IsCompleted), Timeout), "callers did not all complete");
        return new CoalesceResult(builds, flight.InFlightCount, values.ToArray(), errors.ToArray());
    }

    [Fact]
    public void EightConcurrentSameKey_BuildsOnce_AllShareResult()
    {
        CoalesceResult r = RunCoalesced(7, () => 42);
        Assert.Equal(1, r.Builds);            // 8 concurrent callers => exactly one underlying build
        Assert.Equal(8, r.Values.Count);
        Assert.All(r.Values, v => Assert.Equal(42, v));
        Assert.Empty(r.Errors);
        Assert.Equal(0, r.InFlightAfter);     // no leak: every settled build removed its entry
    }

    [Fact]
    public void EightConcurrentSameKey_BuildThrows_BuildsOnce_AllObserveFailure()
    {
        CoalesceResult r = RunCoalesced(7, () => throw new InvalidOperationException("boom"));
        Assert.Equal(1, r.Builds);            // even a failing build is coalesced (no thundering herd of failing queries)
        Assert.Equal(8, r.Errors.Count);
        Assert.All(r.Errors, e => Assert.IsType<InvalidOperationException>(e));
        Assert.Empty(r.Values);
        Assert.Equal(0, r.InFlightAfter);     // faulted entry evicted, not cached
    }

    [Fact]
    public void DistinctKeys_RunConcurrently_NotSerialized()
    {
        SingleFlight<string, int> flight = new();
        CountdownEvent bothInside = new(2);
        // Each build announces itself then waits for the other; if distinct keys serialized on a shared lock,
        // the second build could never start and the first would time out and fault.
        Func<int> build = () =>
        {
            bothInside.Signal();
            Assert.True(bothInside.Wait(Timeout));
            return 1;
        };
        Task a = Task.Factory.StartNew(() => flight.GetOrBuild("a", build), TaskCreationOptions.LongRunning);
        Task b = Task.Factory.StartNew(() => flight.GetOrBuild("b", build), TaskCreationOptions.LongRunning);
        Assert.True(SpinWait.SpinUntil(() => a.IsCompleted && b.IsCompleted, Timeout));
        Assert.True(a.IsCompletedSuccessfully, "key 'a' did not complete — distinct keys appear to be serialized");
        Assert.True(b.IsCompletedSuccessfully, "key 'b' did not complete — distinct keys appear to be serialized");
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public void BuildThrows_EntryRemoved_LaterCallRetries()
    {
        SingleFlight<string, int> flight = new();
        Assert.Throws<InvalidOperationException>(() => flight.GetOrBuild("k", () => throw new InvalidOperationException("boom")));
        Assert.Equal(0, flight.InFlightCount); // finally ran before the throw surfaced, so the faulted Lazy is gone
        int built = 0;
        int value = flight.GetOrBuild("k", () => { built++; return 7; });
        Assert.Equal(7, value);
        Assert.Equal(1, built); // the failed build was NOT cached — the retry actually ran and succeeded
    }

    [Fact]
    public void SettledEntryRemoved_FreshCallRebuilds()
    {
        // After a build settles its entry is removed, so a fresh call rebuilds. (In production the persistent
        // cache check before GetOrBuild — not the in-flight gate — is what suppresses repeat work; see next test.)
        SingleFlight<string, int> flight = new();
        int builds = 0;
        Assert.Equal(5, flight.GetOrBuild("k", () => { builds++; return 5; }));
        Assert.Equal(5, flight.GetOrBuild("k", () => { builds++; return 5; }));
        Assert.Equal(2, builds);
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public void PreCheckHit_ShortCircuits_DoesNotEnterBuild()
    {
        // Mirrors every DatasetManager call site: the persistent-cache check happens BEFORE GetOrBuild, so a warm
        // hit never allocates a Lazy or runs a build.
        Dictionary<string, int> store = new() { ["k"] = 5 };
        SingleFlight<string, int> flight = new();
        int built = 0;
        int Get(string key) => store.TryGetValue(key, out int hit) ? hit : flight.GetOrBuild(key, () => { built++; return -1; });
        Assert.Equal(5, Get("k"));
        Assert.Equal(0, built);
        Assert.Equal(0, flight.InFlightCount);
    }
}
