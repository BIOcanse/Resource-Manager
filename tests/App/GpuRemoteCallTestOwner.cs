using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace Resource_Manager_APP.Tests;

internal static class GpuRemoteCallTestOwner
{
    internal static Task<PreparedOpenGlCallbacks?> RejectPreparation(GpuPlacementProcessInstance process, ulong adapter, CancellationToken token)
        => throw new InvalidOperationException("This fixture must not prepare OpenGL callbacks.");

    internal static async Task<GpuRemoteCallSnapshot> ExecuteCompletedAsync(GpuRemoteCallExecution call, CancellationToken token)
    {
        using (call)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            stop.CancelAfter(TimeSpan.FromSeconds(10));
            call.Start();
            await call.WaitAsync(stop.Token);
            var result = call.Observe();
            Assert.True(result.ResourcesReleased, "This positive fixture requires actual completion before closing its owner.");
            return result;
        }
    }

    internal static Task<GpuRemoteCallSnapshot> RejectUnexpected(GpuRemoteCallExecution call, CancellationToken token)
    {
        call.Dispose();
        throw new InvalidOperationException("A window-only fixture must not submit a remote call.");
    }
}
