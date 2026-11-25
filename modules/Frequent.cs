using System;
using System.Collections.Generic;
using System.Linq;

// =====================================================================
// "SIK KULLANILANLAR": the most used profiles of the last 60 days, from the connection history
// (history.csv). Pure helpers: no file access, no UI; the list builds its group from the result.
// =====================================================================

internal sealed class FrequentStat
{
    public string Id;
    public int Count;              // sessions in the window (short sessions and duplicate rows not counted)
    public DateTime LastStartUtc;  // newest counted session start
}

internal static class FrequentProfiles
{
    public const int WindowDays = 60;      // only sessions started in the last 60 days count
    public const int MinCount = 3;         // a profile needs at least 3 connections to be "frequent"
    public const int MaxShown = 5;         // the group lists at most 5 profiles
    public const int MinSessionSeconds = 3; // shorter sessions (failed / instantly dropped) do not count

    // Connection counts per profile id within the window. Rows with the same profile id and the
    // same start second count once (history.csv can hold such duplicates). Bounded: one pass.
    public static Dictionary<string, FrequentStat> Count(IEnumerable<HistoryEntry> entries, DateTime nowUtc)
    {
        var result = new Dictionary<string, FrequentStat>(StringComparer.Ordinal);
        if (entries == null) return result;
        var from = nowUtc.AddDays(-WindowDays);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrEmpty(e.ProfileId)) continue;
            if (e.StartUtc < from || e.StartUtc > nowUtc.AddDays(1)) continue;
            if ((e.EndUtc - e.StartUtc).TotalSeconds < MinSessionSeconds) continue;
            var second = e.StartUtc.Ticks / TimeSpan.TicksPerSecond;
            if (!seen.Add(e.ProfileId + "\u0001" + second)) continue;
            FrequentStat s;
            if (!result.TryGetValue(e.ProfileId, out s)) { s = new FrequentStat { Id = e.ProfileId }; result[e.ProfileId] = s; }
            s.Count++;
            if (e.StartUtc > s.LastStartUtc) s.LastStartUtc = e.StartUtc;
        }
        return result;
    }

    // Connection count of one profile (0 = none in the window); for tooltips and search.
    public static int CountOf(IDictionary<string, FrequentStat> stats, string id)
    {
        FrequentStat s;
        return stats != null && id != null && stats.TryGetValue(id, out s) ? s.Count : 0;
    }

    // The group: profiles of the (already segment-filtered) pool with at least MinCount connections
    // that are not shown in a group above (connected, pinned, recent), most used first, then the
    // most recently used; at most MaxShown. A deleted profile is not in the pool, so it never shows.
    public static List<VpnProfile> Pick(IEnumerable<VpnProfile> pool, IDictionary<string, FrequentStat> stats, ICollection<string> shownAbove)
    {
        if (pool == null || stats == null || stats.Count == 0) return new List<VpnProfile>();
        return pool
            .Where(p => p != null && p.Id != null && (shownAbove == null || !shownAbove.Contains(p.Id)) && CountOf(stats, p.Id) >= MinCount)
            .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(p => stats[p.Id].Count)
            .ThenByDescending(p => stats[p.Id].LastStartUtc)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(MaxShown)
            .ToList();
    }
}
