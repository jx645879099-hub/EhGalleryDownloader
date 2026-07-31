using System.Text;

namespace EhGalleryDownloader;

public static class NodeScreeningLogic
{
    public const int DefaultSmartLimit = 15;

    public static IReadOnlyList<NodeProbeResult> SelectForRealTest(
        IEnumerable<NodeProbeResult> results,
        bool testAllReachable,
        int smartLimit = DefaultSmartLimit)
    {
        var reachable = results
            .Where(result => result.ClashDelayMs > 0)
            .OrderBy(result => result.ClashDelayMs)
            .ThenBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (testAllReachable || reachable.Count <= smartLimit)
            return reachable;

        smartLimit = Math.Max(1, smartLimit);
        var selected = new List<NodeProbeResult>(smartLimit);
        var regionTarget = Math.Min(
            smartLimit,
            Math.Max(5, smartLimit / 3));
        foreach (var candidate in reachable
                     .GroupBy(result => GetRegionKey(result.Name))
                     .Select(group => group.First())
                     .Take(regionTarget))
        {
            selected.Add(candidate);
        }

        var fastestTarget = Math.Min(
            smartLimit,
            Math.Max(selected.Count, smartLimit / 2));
        foreach (var candidate in reachable)
        {
            if (selected.Count >= fastestTarget) break;
            if (!selected.Contains(candidate)) selected.Add(candidate);
        }

        var remaining = reachable.Where(result => !selected.Contains(result)).ToList();
        var latencySpreadCount = Math.Min(
            remaining.Count,
            Math.Max(1, (smartLimit - selected.Count) * 2 / 3));
        AddEvenlySpaced(
            selected,
            remaining,
            latencySpreadCount,
            smartLimit);

        var nameOrdered = reachable
            .Where(result => !selected.Contains(result))
            .OrderBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AddEvenlySpaced(
            selected,
            nameOrdered,
            smartLimit - selected.Count,
            smartLimit);

        foreach (var candidate in reachable)
        {
            if (selected.Count >= smartLimit) break;
            if (!selected.Contains(candidate)) selected.Add(candidate);
        }

        return selected;
    }

    public static string GetRegionKey(string name)
    {
        var runes = name.EnumerateRunes().Take(2).ToArray();
        if (runes.Length == 2
            && IsRegionalIndicator(runes[0])
            && IsRegionalIndicator(runes[1]))
        {
            return runes[0].ToString() + runes[1];
        }

        var prefix = new string(name
            .TakeWhile(char.IsLetter)
            .Take(6)
            .ToArray());
        if (!string.IsNullOrWhiteSpace(prefix))
            return prefix.ToUpperInvariant();

        return string.Concat(name.EnumerateRunes().Take(4));
    }

    private static bool IsRegionalIndicator(Rune rune) =>
        rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    private static void AddEvenlySpaced(
        List<NodeProbeResult> selected,
        IReadOnlyList<NodeProbeResult> source,
        int count,
        int limit)
    {
        if (count <= 0 || source.Count == 0 || selected.Count >= limit) return;

        for (var index = 0; index < count && selected.Count < limit; index++)
        {
            var position = count == 1
                ? source.Count / 2
                : (int)Math.Round(index * (source.Count - 1d) / (count - 1d));
            var candidate = source[Math.Clamp(position, 0, source.Count - 1)];
            if (!selected.Contains(candidate))
                selected.Add(candidate);
        }
    }
}
