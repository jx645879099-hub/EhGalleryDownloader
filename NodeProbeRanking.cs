namespace EhGalleryDownloader;

public static class NodeProbeRanking
{
    public static IReadOnlyList<NodeProbeResult> Order(
        IEnumerable<NodeProbeResult> results) =>
        results
            .OrderByDescending(result => result.SampleCount > 0)
            .ThenByDescending(result =>
                (double)result.SuccessfulSamples / Math.Max(result.SampleCount, 1))
            .ThenBy(result => result.Interruptions)
            .ThenByDescending(result => result.SpeedMbPerSecond)
            .ThenByDescending(result => result.EligibleForRealTest)
            .ThenBy(result =>
                result.ClashDelayMs > 0 ? result.ClashDelayMs : int.MaxValue)
            .ThenBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
