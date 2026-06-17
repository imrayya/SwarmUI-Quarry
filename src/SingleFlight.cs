namespace Quarry;

internal sealed class SingleFlight<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _inFlight;
    internal SingleFlight() => _inFlight = new();
    internal SingleFlight(IEqualityComparer<TKey> comparer) => _inFlight = new(comparer);

    internal TValue GetOrBuild(TKey key, Func<TValue> build)
    {
        Lazy<TValue> lazy = _inFlight.GetOrAdd(key, _ => new Lazy<TValue>(build, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        finally
        {
            _inFlight.TryRemove(KeyValuePair.Create(key, lazy));
        }
    }

    internal int InFlightCount => _inFlight.Count;
}
