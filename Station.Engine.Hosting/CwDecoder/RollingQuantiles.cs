// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
namespace Zeus.Server;

/// <summary>
/// Exact streaming quantiles over a bounded history. Insert and expire one
/// sample per update instead of copying and sorting the window each block.
/// Sequence numbers allow a shorter recent window to share the same ordering.
/// </summary>
internal sealed class RollingQuantiles(int capacity)
{
    private readonly double[] _history = new double[capacity];
    private readonly Entry[] _ordered = new Entry[capacity];
    private long _sequence;
    private int _cursor;
    public int Count { get; private set; }
    private readonly record struct Entry(double Value, long Sequence);

    public void Push(double value)
    {
        if (Count == _history.Length)
        {
            int remove = Find(new Entry(_history[_cursor], _sequence - Count), Count);
            Array.Copy(_ordered, remove + 1, _ordered, remove, Count - remove - 1);
            Count--;
        }
        _history[_cursor] = value;
        _cursor = (_cursor + 1) % _history.Length;
        var entry = new Entry(value, _sequence++);
        int insert = Find(entry, Count);
        Array.Copy(_ordered, insert, _ordered, insert + 1, Count - insert);
        _ordered[insert] = entry;
        Count++;
    }

    public double Percentile(double fraction, int recentCount = int.MaxValue, bool interpolate = true)
    {
        int count = Math.Min(recentCount, Count);
        if (count <= 0) return 0;
        double rank = (count - 1) * fraction;
        int low = (int)rank;
        int high = interpolate ? Math.Min(low + 1, count - 1) : low;
        double lower = 0, upper = 0;
        if (count == Count)
        {
            lower = _ordered[low].Value;
            upper = _ordered[high].Value;
        }
        else
        {
            long oldest = _sequence - count;
            int index = 0;
            for (int i = 0; i < Count; i++)
            {
                Entry entry = _ordered[i];
                if (entry.Sequence < oldest) continue;
                if (index == low) lower = entry.Value;
                if (index++ == high)
                {
                    upper = entry.Value;
                    break;
                }
            }
        }
        double weight = interpolate ? rank - low : 0;
        return lower * (1 - weight) + upper * weight;
    }

    private int Find(Entry entry, int count)
    {
        int low = 0, high = count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            Entry candidate = _ordered[mid];
            if (candidate.Value < entry.Value || (candidate.Value == entry.Value && candidate.Sequence < entry.Sequence)) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    public void Reset()
    {
        Count = 0;
        _sequence = 0;
        _cursor = 0;
    }
}
