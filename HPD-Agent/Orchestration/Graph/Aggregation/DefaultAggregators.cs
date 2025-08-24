using System.Threading;

/// <summary>
/// An aggregator that sums long values.
/// </summary>
public class SumAggregator : IAggregator<long>
{
    private long _sum = 0;

    public void Add(long value)
    {
        Interlocked.Add(ref _sum, value);
    }

    public long GetResult() => _sum;

    public void Reset() => _sum = 0;
}

/// <summary>
/// An aggregator that finds the maximum of long values.
/// </summary>
public class MaxAggregator : IAggregator<long>
{
    private long _max = long.MinValue;

    public void Add(long value)
    {        // Simple spin lock for thread-safe max calculation
        long currentMax;
        do
        {
            currentMax = _max;
            if (value <= currentMax) break;
        } while (Interlocked.CompareExchange(ref _max, value, currentMax) != currentMax);
    }

    public long GetResult() => _max;

    public void Reset() => _max = long.MinValue;
}