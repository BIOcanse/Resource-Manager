using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal enum HostManagerOperationEffectOutcome
{
    Succeeded = 1,
    RetryableFailure = 2,
    TerminalFailure = 3,
    Canceled = 4,
    Uncertain = 5
}

internal sealed record HostManagerOperationEffectCompletion(
    HostManagerOperationEffectOutcome Outcome,
    string Message);

internal sealed record HostManagerOperationActionTicket(
    NativeOperationActionOutput Action,
    string Kind,
    HostManagerOperationPayloadEntry Request,
    NativeOperationHandle128 ReceiptId);

internal interface IHostManagerOperationProgressSink
{
    ValueTask ReportAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationProgressUpdate progress,
        CancellationToken cancellationToken);
}

internal sealed record HostManagerOperationProgressUpdate(
    uint? PercentMilli = null,
    ulong? BytesDone = null,
    ulong? BytesTotal = null,
    ulong? SpeedBytesPerSecond = null,
    string? Stage = null,
    string? Message = null,
    byte[]? Checkpoint = null);

internal interface IHostManagerOperationEffectExecutor
{
    string Kind { get; }

    ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken);

    ValueTask<HostManagerOperationEffectCompletion> ReadbackAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationEffectReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed class HostManagerOperationEffectRouter
{
    private readonly IReadOnlyDictionary<string, IHostManagerOperationEffectExecutor>
        executors;

    internal HostManagerOperationEffectRouter(
        IEnumerable<IHostManagerOperationEffectExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        var materialized = executors.ToArray();
        var duplicate = materialized
            .GroupBy(static executor => executor.Kind, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() != 1);
        if (duplicate is not null
            || materialized.Any(static executor =>
                string.IsNullOrWhiteSpace(executor.Kind)))
        {
            throw new InvalidOperationException(
                "Operation effect executor kinds must be unique and nonempty.");
        }
        this.executors = materialized.ToDictionary(
            static executor => executor.Kind,
            StringComparer.Ordinal);
    }

    internal IReadOnlySet<string> Kinds
        => executors.Keys.ToHashSet(StringComparer.Ordinal);

    internal IHostManagerOperationEffectExecutor Require(string kind)
        => executors.TryGetValue(kind, out var executor)
            ? executor
            : throw new InvalidOperationException(
                $"No operation effect executor is registered for '{kind}'.");
}
