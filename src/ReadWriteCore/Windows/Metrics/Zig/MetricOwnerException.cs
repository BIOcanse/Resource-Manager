using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public sealed class MetricOwnerException : Exception
{
    public MetricOwnerFailure Failure { get; }
    public MetricOwnerException(MetricOwnerFailure failure)
        : base($"Metric owner: {failure.Code}, {failure.Family}, required {failure.Required}, limit {failure.Limit}.")
        => Failure = failure;

    internal static void Check(uint code, MetricOwnerFailure failure)
    {
        if (code == 0) return;
        failure.Code = (MetricOwnerCode)code;
        throw new MetricOwnerException(failure);
    }
}
