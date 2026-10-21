using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

internal static class JournalRuntimeTestStorage
{
    // Pure recovery/algorithm fixtures explicitly own ordinary storage.
    internal static async Task<HostManagerTransactionJournalRuntime> OpenAsync(
        string root,
        CompiledHostManagerTransactionJournalRecreatePlan recreate,
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublish,
        CancellationToken cancellationToken)
    {
        var runtime = HostManagerTransactionJournalRuntime.Create(
            root, recreate, hotPublish, NativeAppliedOwnershipTestStorage.Create);
        try
        {
            await runtime.InitializeAsync(cancellationToken);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }
}
