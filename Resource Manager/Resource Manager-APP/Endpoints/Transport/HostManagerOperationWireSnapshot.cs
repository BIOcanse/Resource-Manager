using System.Globalization;
using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record HostManagerOperationsStateWireSnapshot(
    string Schema,
    DateTimeOffset CapturedAt,
    string PublicationRevision,
    string ConfigurationGeneration,
    bool Ready,
    bool PersistenceFaulted,
    string? FaultStage,
    string? FaultMessage,
    IReadOnlyList<HostManagerOperationWireSnapshot> Operations);

internal sealed record HostManagerOperationWireSnapshot(
    string Id,
    string Kind,
    string? DomainKey,
    string? Title,
    string State,
    string ConfigurationGeneration,
    string StateRevision,
    uint AttemptNumber,
    uint MaximumAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool CancelRequested,
    HostManagerOperationProgressWireSnapshot? Progress,
    string? Result,
    string? Error);

internal sealed record HostManagerOperationProgressWireSnapshot(
    string Sequence,
    double? Percent,
    string? BytesDone,
    string? BytesTotal,
    string? SpeedBytesPerSecond,
    string? Stage,
    string? Message);

internal static class HostManagerOperationWireProjection
{
    internal const string Schema = "host-manager.operations.state.v1";

    internal static HostManagerOperationsStateWireSnapshot Project(
        HostManagerOperationsPublishedState state)
        => new(
            Schema,
            state.CapturedAt,
            Decimal(state.PublicationRevision),
            Decimal(state.ConfigurationGeneration),
            state.Health.Ready,
            state.Health.PersistenceFaulted,
            state.Health.FaultStage,
            state.Health.FaultMessage,
            state.Operations.Select(Project).ToArray());

    internal static HostManagerOperationWireSnapshot Project(
        HostManagerOperationSnapshot operation)
        => new(
            operation.Id,
            operation.Kind,
            operation.DomainKey,
            operation.Title,
            operation.State,
            Decimal(operation.ConfigurationGeneration),
            Decimal(operation.StateRevision),
            operation.AttemptNumber,
            operation.MaximumAttempts,
            operation.CreatedAt,
            operation.UpdatedAt,
            operation.CompletedAt,
            operation.CancelRequested,
            operation.Progress is null ? null : Project(operation.Progress),
            operation.Result,
            operation.Error);

    private static HostManagerOperationProgressWireSnapshot Project(
        HostManagerOperationProgress progress)
        => new(
            Decimal(progress.Sequence),
            progress.Percent,
            NullableDecimal(progress.BytesDone),
            NullableDecimal(progress.BytesTotal),
            NullableDecimal(progress.SpeedBytesPerSecond),
            progress.Stage,
            progress.Message);

    private static string? NullableDecimal(ulong? value)
        => value.HasValue ? Decimal(value.Value) : null;

    private static string Decimal(ulong value)
        => value.ToString(CultureInfo.InvariantCulture);
}
