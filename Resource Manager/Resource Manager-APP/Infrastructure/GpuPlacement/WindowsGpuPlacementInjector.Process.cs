using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    internal sealed class ProviderProcess(
        GpuPlacementProcessInstance identity,
        RuntimeGpuProviderInjectionResult result,
        Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> execute,
        SafeKernelHandle? handle = null,
        IntPtr readObservationsAddress = default) : IDisposable
    {
        public GpuPlacementProcessInstance Identity { get; } = identity;
        public RuntimeGpuProviderInjectionResult Result { get; internal set; } = result;
        internal IntPtr ReadObservationsAddress { get; set; } = readObservationsAddress;
        private GpuRemoteCallSnapshot? stoppedCall;
        internal bool CallsStopped => stoppedCall is not null;
        internal IReadOnlyList<ApiObservationEntries> ApiObservations { get; set; } = [];

        public Task<GpuDeviceObservationReadResult> ReadDeviceObservationsAsync(CancellationToken token)
            => handle is { IsClosed: false, IsInvalid: false }
                && NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WaitTimeout
                    ? ReadObservationsAsync(this, token)
                    : Task.FromResult(new GpuDeviceObservationReadResult(null, "process-unavailable"));

        internal Task<GpuRemoteCallSnapshot> StartApiObservationAsync(ApiObservationEntries entries, int durationMilliseconds, CancellationToken token)
        {
            return InvokeAsync(GpuRemoteCallKind.StartApiObservation, entries.Start,
                GpuApiObservationProtocol.EncodeRequest(entries.Apis, durationMilliseconds), false, token);
        }

        internal Task<GpuApiObservationReadResult> ReadApiObservationAsync(ApiObservationEntries entries, CancellationToken token)
            => ReadApiObservationCoreAsync(this, entries, stop: false, token);

        internal Task<GpuApiObservationReadResult> StopApiObservationAsync(ApiObservationEntries entries, CancellationToken token)
            => ReadApiObservationCoreAsync(this, entries, stop: true, token);

        internal Task<GpuApiObservationReadResult> ObserveApiOnceAsync(int durationMilliseconds,
            Func<TimeSpan, CancellationToken, Task> waitAsync, CancellationToken token, CancellationToken cleanupToken)
            => ObserveApiOnceCoreAsync(this, durationMilliseconds, waitAsync, token, cleanupToken);

        internal async Task<GpuRemoteCallSnapshot> InvokeAsync(GpuRemoteCallKind kind, IntPtr function,
            byte[] payload, bool readResponse, CancellationToken token)
        {
            if ((CallsStopped && !(kind == GpuRemoteCallKind.StopApiObservation && stoppedCall!.ResourcesReleased))
                || token.IsCancellationRequested)
                return new(0, null, null, null, null, true, "remote-action-stopped", null);
            if (handle is null || handle.IsClosed || handle.IsInvalid)
                return new(0, null, null, null, null, true, "process-unavailable", null);
            var call = new RemoteCall(handle, Identity, kind, function, payload, readResponse);
            var snapshot = await execute(call, token).ConfigureAwait(false);
            if (!snapshot.Completed) stoppedCall ??= snapshot;
            return snapshot;
        }

        public bool OwnsWindow(IntPtr window)
            => window != IntPtr.Zero && handle is { IsClosed: false, IsInvalid: false }
                && NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WaitTimeout
                && NativeMethods.GetWindowThreadProcessId(window, out var processId) != 0
                && processId == Identity.ProcessId;

        public void Dispose() => handle?.Dispose();
    }
}
