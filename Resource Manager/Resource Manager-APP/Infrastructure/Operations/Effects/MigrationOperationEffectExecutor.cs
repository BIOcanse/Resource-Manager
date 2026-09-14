using ResourceManager.App.Application.Migration;
using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class MigrationOperationEffectExecutor(
    ISoftwareDataMigrationManager migrationManager,
    bool restore) : IHostManagerOperationEffectExecutor
{
    public string Kind => restore
        ? HostManagerOperationKinds.MigrationRestore
        : HostManagerOperationKinds.MigrationExecute;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        _ = progress;
        try
        {
            if (restore)
            {
                var (id, confirm) =
                    HostManagerOperationRequestCodec.DecodeBooleanRequest(ticket.Request.Payload);
                var result = await migrationManager.RestoreAsync(
                    new SoftwareDataRestoreRequest(id, confirm),
                    cancellationToken).ConfigureAwait(false);
                return new(HostManagerOperationEffectOutcome.Succeeded, result.Message);
            }

            var request =
                HostManagerOperationRequestCodec.DecodeMigrationExecute(ticket.Request.Payload);
            var migration = await migrationManager.ExecuteAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            var message = migration.Results.Count == 0
                ? migration.Plan.Summary
                : string.Join(
                    "；",
                    migration.Results.Select(static result => result.Message));
            return new(HostManagerOperationEffectOutcome.Succeeded, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, "操作已取消。");
        }
        catch (IOException error)
        {
            return new(HostManagerOperationEffectOutcome.RetryableFailure, error.Message);
        }
        catch (Exception error)
        {
            return new(HostManagerOperationEffectOutcome.TerminalFailure, error.Message);
        }
    }

    public ValueTask<HostManagerOperationEffectCompletion> ReadbackAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationEffectReceipt receipt,
        CancellationToken cancellationToken)
    {
        _ = ticket;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(receipt.State == HostManagerOperationEffectReceiptState.Prepared
            ? new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.RetryableFailure,
                "尚无迁移动作执行证据，可重新排队。")
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                "迁移可能已修改文件系统，需要人工核验。"));
    }
}
