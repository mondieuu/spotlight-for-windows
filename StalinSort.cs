using System;
using System.Collections.Generic;

namespace Odak;

/// <summary>
/// Stalin sort: walk the list once, and eliminate every item that is out of order. O(n), and the result is always
/// sorted, but it is shorter than the input. It walks upward and keeps each new record, so the best item always
/// survives; ties survive too. The survivors are then returned best first.
/// </summary>
public static class StalinSort
{
    public static List<T> Descending<T>(IReadOnlyList<T> items, Func<T, int> score)
    {
        var survivors = new List<T>();
        int best = int.MinValue;
        foreach (var item in items)
        {
            int value = score(item);
            if (value < best) continue;
            best = value;
            survivors.Add(item);
        }
        survivors.Reverse();
        return survivors;
    }
}
