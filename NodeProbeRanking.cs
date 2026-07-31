namespace EhGalleryDownloader;

public static class NodeProbeRanking
{
    public static IReadOnlyList<NodeProbeResult> Order(
        IEnumerable<NodeProbeResult> results) =>
        results
            .OrderBy(GetBucket)
            .ThenByDescending(result =>
                (double)result.SuccessfulSamples / Math.Max(result.SampleCount, 1))
            .ThenBy(result => result.Interruptions)
            .ThenByDescending(result => result.SpeedMbPerSecond)
            .ThenByDescending(result => result.EligibleForRealTest)
            .ThenBy(result =>
                result.ClashDelayMs > 0 ? result.ClashDelayMs : int.MaxValue)
            .ThenBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static int GetBucket(NodeProbeResult result)
    {
        if (result.SuccessfulSamples > 0) return 0;

        var explicitlyUnavailable = result.SampleCount > 0
                                    || result.ClashScreened && result.ClashDelayMs <= 0
                                    || result.Rating is "不可用" or "已淘汰"
                                    || result.Status.Contains("失败", StringComparison.Ordinal)
                                    || result.Status.Contains("Error", StringComparison.OrdinalIgnoreCase);
        return explicitlyUnavailable ? 2 : 1;
    }
}
