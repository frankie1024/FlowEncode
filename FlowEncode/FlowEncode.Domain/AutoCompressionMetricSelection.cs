namespace FlowEncode.Domain;

internal static class AutoCompressionMetricSelection
{
    public static AutoCompressionMetric ResolvePreferredMetric(
        AutoCompressionMetric currentMetric,
        IEnumerable<AutoCompressionMetric> supportedMetrics)
    {
        ArgumentNullException.ThrowIfNull(supportedMetrics);

        var supported = supportedMetrics
            .Distinct()
            .ToArray();
        if (supported.Length == 0)
        {
            return currentMetric;
        }

        return supported.Contains(currentMetric)
            ? currentMetric
            : supported[0];
    }
}
