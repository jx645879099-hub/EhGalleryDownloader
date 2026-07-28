namespace EhGalleryDownloader;

public static class NodeScreeningLogic
{
    public const int DefaultSmartLimit = 25;

    public static IReadOnlyList<NodeProbeResult> SelectForRealTest(
        IEnumerable<NodeProbeResult> results,
        bool testAllReachable,
        int smartLimit = DefaultSmartLimit)
    {
        var reachable = results
            .Where(result => result.ClashDelayMs > 0 && result.GalleryDelayMs > 0)
            .OrderBy(result => result.GalleryDelayMs)
            .ThenBy(result => result.ClashDelayMs)
            .ThenBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (testAllReachable || reachable.Count <= smartLimit)
            return reachable;

        smartLimit = Math.Max(1, smartLimit);
        var broadSampleCount = Math.Min(5, Math.Max(1, smartLimit / 5));
        var fastestCount = Math.Max(0, smartLimit - broadSampleCount);
        var selected = reachable.Take(fastestCount).ToList();
        var remaining = reachable.Skip(fastestCount).ToList();

        for (var index = 0; index < broadSampleCount && remaining.Count > 0; index++)
        {
            var position = (int)Math.Round(
                (index + 1d) * (remaining.Count - 1) / (broadSampleCount + 1d));
            var candidate = remaining[Math.Clamp(position, 0, remaining.Count - 1)];
            if (!selected.Contains(candidate))
                selected.Add(candidate);
        }

        foreach (var candidate in remaining)
        {
            if (selected.Count >= smartLimit) break;
            if (!selected.Contains(candidate))
                selected.Add(candidate);
        }

        return selected;
    }
}
