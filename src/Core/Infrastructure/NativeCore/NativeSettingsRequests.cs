using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.CrashReports;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Protocol-neutral S-request binding for HTTP and the future pipe settings family.
// S-apply removes coordinator calls; S-store removes the store/object-field bridge.
// H3 removes this managed binding. Original objects stay in this async call scope;
// only scalar facts / synchronously pinned UTF-16 cross the ABI. No native pointer
// or lease survives an await, and no Task is synchronously blocked by native code.
internal static partial class NativeSettingsRequests
{
    internal enum Operation : uint { Read, Write, Patch, Reapply, Defaults }
    private enum Action : uint { Begin, Load, ProjectRuntime, Apply, ApplyPatch, Rebuild, LoadReapplied, ProjectPublication, Diagnostics, Defaults, Done }
    internal sealed record Response(int Status, object? Value, string? ProblemTitle = null, string? ProblemDetail = null);

    internal static string CapabilityId(Operation operation) => Text(0, (uint)operation);

    internal static async Task<Response> InvokeAsync(
        Operation operation, IAppSettingsStore store, IRuntimeSpecializationCoordinator coordinator,
        ServiceRecoverySettings recovery, CrashDumpSettings crashDumps, CrashDumpKeySettings crashDumpKeys, CancellationToken cancellationToken,
        AppSettings? settings = null, AppSettingsPatchRequest? patch = null)
    {
        AppSettingsUpdateResult? result = null;
        AppSettings? defaults = null;
        RuntimePlanPublicationResult? publication = null;
        AppSettingsPatchException? invalidPatch = null;
        var completed = Action.Begin;
        while (true)
        {
            var next = Next(operation, completed, result?.RuntimeApplicationDisposition, invalidPatch is not null,
                publication?.HasDeliveryFailures == true, publication?.Plan.RuntimeCapabilityConstrainedPaths.Count > 0);
            completed = next.Action;
            switch (next.Action)
            {
                case Action.Load:
                case Action.LoadReapplied:
                    result = await store.LoadReadOnlyAsync(cancellationToken);
                    break;
                case Action.ProjectRuntime:
                    result = coordinator.ProjectAppSettingsRuntimeState(result!);
                    break;
                case Action.Apply:
                    result = await coordinator.ApplyAppSettingsAsync(settings ?? throw new ArgumentNullException(nameof(settings)),
                        Text(1, (uint)operation), cancellationToken);
                    break;
                case Action.ApplyPatch:
                    try
                    {
                        result = await coordinator.ApplyAppSettingsPatchAsync(patch ?? throw new ArgumentNullException(nameof(patch)),
                            Text(1, (uint)operation), cancellationToken);
                    }
                    catch (AppSettingsPatchException error) { invalidPatch = error; }
                    break;
                case Action.Rebuild:
                    publication = await coordinator.RebuildAsync(Text(1, (uint)operation), cancellationToken);
                    break;
                case Action.ProjectPublication:
                    // Representation-only writes to the original managed result. Native
                    // owns the publication disposition and which fields are projected.
                    result = result! with
                    {
                        RuntimeApplicationDisposition = Text(2, next.Disposition),
                        RuntimePlanVersion = publication!.Plan.Version,
                        RuntimePublicationSequence = publication.PublicationSequence,
                        RuntimeDeliveryFailureCount = publication.DeliveryFailures.Count,
                        RuntimeCapabilityConstrainedPaths = publication.Plan.RuntimeCapabilityConstrainedPaths
                    };
                    break;
                case Action.Diagnostics:
                    result = result! with
                    {
                        ServiceRecovery = recovery.Diagnostics, CrashDumps = crashDumps.Diagnostics,
                        CrashDumpKeys = next.InspectCrashDumpKeys != 0
                            ? crashDumpKeys.Describe(result.Settings.SystemIntegration.CrashDumpKeys)
                            : result.CrashDumpKeys
                    };
                    break;
                case Action.Defaults:
                    defaults = AppSettingsDefaults.Create();
                    break;
                case Action.Done:
                    return new(checked((int)next.Status),
                        (object?)defaults ?? result,
                        invalidPatch is null ? null : Text(3, 0), invalidPatch?.Message);
                default:
                    throw new InvalidOperationException("Invalid native settings continuation.");
            }
        }
    }

    private static unsafe NativeResult Next(Operation operation, Action completed, string? disposition,
        bool invalidPatch, bool deliveryFailures, bool constrained)
    {
        fixed (char* pointer = disposition)
        {
            var facts = new Facts
            {
                InvalidPatch = invalidPatch ? 1U : 0U, DeliveryFailures = deliveryFailures ? 1U : 0U,
                Constrained = constrained ? 1U : 0U, Disposition = (ushort*)pointer,
                DispositionLength = checked((nuint)(disposition?.Length ?? 0))
            };
            var code = NativeMethods.Next((uint)operation, (uint)completed, in facts, out var result);
            if (code != 0) throw new InvalidOperationException($"Native settings request failed ({code}).");
            return result;
        }
    }

    private static string Text(uint kind, uint value) => Marshal.PtrToStringUni(NativeMethods.Text(kind, value))
        ?? throw new ArgumentOutOfRangeException(nameof(value));

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Facts
    {
        internal uint InvalidPatch, DeliveryFailures, Constrained;
        internal ushort* Disposition;
        internal nuint DispositionLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult { internal Action Action; internal uint Status, Disposition, InspectCrashDumpKeys; }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_settings_request_next")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Next(uint operation, uint completed, in Facts facts, out NativeResult result);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_settings_request_text")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nint Text(uint kind, uint value);
    }
}
