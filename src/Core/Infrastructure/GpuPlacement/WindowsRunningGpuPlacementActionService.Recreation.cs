using System.Buffers.Binary;
using System.Globalization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsRunningGpuPlacementActionService
{
    private sealed record RecreationBatch(WindowBatchResult Windows,
        Dictionary<int, GpuRecreationResult> Results, List<RunningGpuPlacementActionRecord> Records);

    private static async Task<RecreationBatch> ExecuteRecreationRequestsAsync(RunningGpuPlacementActionPlan plan,
        IReadOnlyDictionary<int, WindowsGpuPlacementInjector.ProviderProcess> processes, GpuWindowActionMethod method,
        RunningGpuPlacementExecution execution, CancellationToken token)
    {
        var armed = new Dictionary<int, byte[]>();
        var results = new Dictionary<int, GpuRecreationResult>();
        var records = new List<RunningGpuPlacementActionRecord>();
        var batch = new WindowBatchResult([], false, false);
        if (execution.RecreationDeadlineMilliseconds <= (ulong)Environment.TickCount64) return new(batch, results, records);
        try
        {
            foreach (var pair in processes)
            {
                if (token.IsCancellationRequested) break;
                var api = plan.GraphicsApis[pair.Key];
                if (api is not (GpuGraphicsApi.D3D11 or GpuGraphicsApi.D3D12 or (GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12))) continue;
                var request = WindowsGpuPlacementInjector.CreateRecreationRequest(api, method == GpuWindowActionMethod.Resize,
                    execution.RecreationDeadlineMilliseconds, plan.Request.TargetAdapterKey);
                var result = await pair.Value.RecreateAsync(GpuRemoteCallKind.ArmRecreation, request, token).ConfigureAwait(false);
                results[pair.Key] = result;
                if (result.State == GpuRecreationState.Armed) armed.Add(pair.Key, request);
                if (pair.Value.CallsStopped) break;
            }
            if (method == GpuWindowActionMethod.Resize && !processes.Values.Any(static process => process.CallsStopped))
                batch = await ExecuteWindowRequestsAsync(EnumerateWindows(
                    processes.Where(pair => armed.ContainsKey(pair.Key)).ToDictionary(), method,
                    execution.MaximumWindowCount, token), execution, token).ConfigureAwait(false);
            foreach (var pair in armed.ToArray())
            {
                if (batch.Stopped || token.IsCancellationRequested || processes.Values.Any(static process => process.CallsStopped)) break;
                var result = await processes[pair.Key].RecreateAsync(GpuRemoteCallKind.FinishRecreation, pair.Value, token).ConfigureAwait(false);
                results[pair.Key] = result;
                if (result.State != GpuRecreationState.None) armed.Remove(pair.Key);
                AddSignalRecord(pair.Key, pair.Value, result);
            }
        }
        finally
        {
            // The original call owner retains any unresolved remote call. Native expiry
            // prevents a late signal even when that owner cannot yet issue cancellation.
            foreach (var pair in armed)
            {
                var process = processes[pair.Key];
                var result = await process.RecreateAsync(GpuRemoteCallKind.CancelRecreation, pair.Value, CancellationToken.None).ConfigureAwait(false);
                results[pair.Key] = result;
                AddSignalRecord(pair.Key, pair.Value, result);
            }
        }
        return new(batch, results, records);

        void AddSignalRecord(int pid, byte[] request, GpuRecreationResult result)
        {
            if (!result.Signalled) return;
            var identity = processes[pid].Identity;
            records.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24)).ToString("x16", CultureInfo.InvariantCulture),
                method == GpuWindowActionMethod.Resize ? GpuPlacementRuntimeSwitchMethods.WindowRerender : GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["processId"] = pid.ToString(CultureInfo.InvariantCulture),
                    ["processStartKey"] = identity.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
                    ["nativeRecreationState"] = "signalled",
                    ["sourceAdapterKey"] = result.SourceAdapterKey.ToString("x16", CultureInfo.InvariantCulture),
                    ["targetAdapterKey"] = plan.Request.TargetAdapterKey.ToString("x16", CultureInfo.InvariantCulture)
                }));
        }
    }
}
