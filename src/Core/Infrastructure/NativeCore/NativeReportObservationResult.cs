namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum ReportSourceFailureReason
{
    None,
    ObservationCapacityExceeded,
    NativeFailure
}

internal readonly record struct NativeReportObservationResult(
    NativeReportCoordinatorStatus Status, ReportSourceFailureReason Reason, uint RequiredObservationCount);

internal sealed class ReportSourceObservationException(
    ulong source, ulong coverage, NativeReportObservationResult result, uint limit)
    : InvalidOperationException($"Native report coordinator source observation (source={source}, coverage={coverage}) failed: {result.Reason}, status={result.Status}, requiredObservations={result.RequiredObservationCount}, limit={limit}.")
{
    internal NativeReportObservationResult Result { get; } = result;
}
