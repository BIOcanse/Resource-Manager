using System.Runtime.CompilerServices;
using ResourceManager.Subscription.Native;

// Consume only the NuGet package and its actual native asset, without the host.
if (args.Contains("--current-backend-recorded-only", StringComparer.Ordinal))
{
    await AmdHardwareTopologyConsumer.RunRecordedAsync();
    await SecurityProductConsumer.RunRecordedAsync();
    await AmdHardwareTopologySubscriptionConsumer.RunRecordedAsync();
    await SecurityProductSubscriptionConsumer.RunRecordedAsync();
    await EdidModeSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--security-products-recorded-only", StringComparer.Ordinal))
{
    await SecurityProductConsumer.RunRecordedAsync();
    await SecurityProductSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--amd-hardware-topology-recorded-only", StringComparer.Ordinal))
{
    await AmdHardwareTopologyConsumer.RunRecordedAsync();
    await AmdHardwareTopologySubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--cpu-edid-d3d-capability-recorded-only", StringComparer.Ordinal))
{
    await CpuInstructionConsumer.RunRecordedAsync();
    await EdidModeSubscriptionConsumer.RunRecordedAsync();
    await D3dCapabilitySubscriptionConsumer.RunAsync();
    return;
}
if (args.Contains("--edid-d3d-capability-recorded-only", StringComparer.Ordinal))
{
    await EdidModeSubscriptionConsumer.RunRecordedAsync();
    await D3dCapabilitySubscriptionConsumer.RunAsync();
    return;
}
if (args.Contains("--edid-mode-recorded-only", StringComparer.Ordinal))
{
    await EdidModeSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--nvidia-drs-recorded-only", StringComparer.Ordinal))
{
    await NvidiaDrsInformationSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--process-io-uvc-recorded-only", StringComparer.Ordinal))
{
    await DefaultOwnerSubscriptionConsumer.RunIoRatesWindowsAsync();
    await UvcFrameIntervalSubscriptionConsumer.RunRecordedAsync();
    Console.WriteLine("[PROCESS-IO-UVC-COMBINATION-PASSED] process IO actual-readonly; UVC recorded; no-live-ETW/camera");
    return;
}
if (args.Contains("--uvc-interval-recorded-only", StringComparer.Ordinal))
{
    await UvcFrameIntervalSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--npu-engine-recorded-only", StringComparer.Ordinal))
{
    await NpuEngineSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--intel-fan-recorded-only", StringComparer.Ordinal))
{
    await IntelFanSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--npu-memory-recorded-only", StringComparer.Ordinal))
{
    await NpuMemorySubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--psys-recorded-only", StringComparer.Ordinal))
{
    await IntelPsysSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--nvidia-fan-recorded-only", StringComparer.Ordinal))
{
    await NvidiaFanSubscriptionConsumer.RunRecordedAsync();
    return;
}
if (args.Contains("--npu-recorded-only", StringComparer.Ordinal))
{
    await NpuInformationSubscriptionConsumer.RunRecordedAsync();
    return;
}
await SecurityProductConsumer.RunRecordedAsync();
await SecurityProductSubscriptionConsumer.RunRecordedAsync();

await AmdHardwareTopologyConsumer.RunRecordedAsync();
await AmdHardwareTopologySubscriptionConsumer.RunRecordedAsync();
await CpuInstructionConsumer.RunRecordedAsync();
await EdidModeSubscriptionConsumer.RunRecordedAsync();
await NvidiaDrsInformationSubscriptionConsumer.RunRecordedAsync();
await UvcFrameIntervalSubscriptionConsumer.RunRecordedAsync();
await IntelFanSubscriptionConsumer.RunRecordedAsync();
await NpuEngineSubscriptionConsumer.RunRecordedAsync();
await NpuMemorySubscriptionConsumer.RunRecordedAsync();
await NpuInformationSubscriptionConsumer.RunRecordedAsync();
await NvidiaFanSubscriptionConsumer.RunRecordedAsync();
await IntelPsysSubscriptionConsumer.RunRecordedAsync();
var configuration = new NativeSamplingSubscriptionConfiguration
{
    AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionConfiguration>(),
    Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
    MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
    DefaultIntervalMilliseconds = 1_000, MinimumIntervalMilliseconds = 1,
    ActiveTtlMilliseconds = 3_000, MaximumFutureSkewMilliseconds = 1_000
};
Require(NativeSamplingSubscriptionSession.GetAbiVersion() == configuration.AbiVersion, "packaged native ABI");
Require(!typeof(NativeSamplingSubscriptionSession).Assembly.GetReferencedAssemblies()
    .Any(static reference => reference.Name is "ResourceManager" or "ResourceManager.App"), "no product assembly dependency");
using var session = new NativeSamplingSubscriptionSession(in configuration);
Require(session.Capacity.ItemCapacity == 8, "real native capacity");
ulong operation = 0, plan = 0;
Track(1, 1_000, 1_000, true, [10, 20]);
Track(2, 1_000, 250, true, [10]);
var first = Plan(1_000);
Require(first.Header.ActiveSourceCount == 2 && first.Header.ActiveItemCount == 2, "two source demands");
Require(first.Due.Single(item => item.ItemHandle == 10).EffectiveIntervalMilliseconds == 250, "fastest shared item");
Require(first.Due.Single(item => item.ItemHandle == 20).EffectiveIntervalMilliseconds == 1_000, "independent slow item");
Complete(first, 1_000);
var faster = Plan(1_250);
Require(faster.Due.Length == 1 && faster.Due[0].ItemHandle == 10, "only the faster item is due");
Complete(faster, 1_250);
Remove(2, 1_251);
var remaining = Snapshot();
var shared = remaining.Items.Single(item => item.ItemHandle == 10);
Require(shared.EffectiveIntervalMilliseconds == 1_000 && shared.SourceCount == 1, "remove fastest demand recalculates rate");
Require(shared.LastSampledMilliseconds == 1_250, "rate change retains actual sample time");
var slow = Plan(2_000);
Require(slow.Due.Length == 1 && slow.Due[0].ItemHandle == 20
    && slow.Due[0].EffectiveIntervalMilliseconds == 1_000, "independent item's full interval");
Complete(slow, 2_000);
var sharedSlow = Plan(2_250);
Require(sharedSlow.Due.Length == 1 && sharedSlow.Due[0].ItemHandle == 10
    && sharedSlow.Due[0].EffectiveIntervalMilliseconds == 1_000, "remaining shared item uses last sample plus interval");
Complete(sharedSlow, 2_250);
Remove(1, 2_251);
var idle = Plan(2_251);
Require(idle.Header.ActiveSourceCount == 0 && idle.Header.ActiveItemCount == 0 && idle.Due.Length == 0
    && idle.Header.NextWakeMilliseconds == 0, "last source removal stops future work");
Track(3, 3_000, 500, false, [30]);
var transient = Plan(3_000); Complete(transient, 3_000);
var expired = Plan(6_001);
Require(expired.Expired.Length == 1 && expired.Expired[0].SourceHandle == 3
    && expired.Header.ActiveItemCount == 0, "transient source expiry");
Track(4, 7_000, 250, true, [40]);
var restarted = Plan(7_000);
Require(restarted.Due.Length == 1 && restarted.Due[0].ItemHandle == 40, "demand after expiry restarts");
Complete(restarted, 7_000);
session.Dispose(); session.Dispose();
try { _ = session.Capacity; throw new InvalidOperationException("A disposed native session accepted a query."); }
catch (ObjectDisposedException) { }
using (var replacement = new NativeSamplingSubscriptionSession(in configuration))
{
    var header = new NativeSamplingSubscriptionSnapshotHeader { AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = Size<NativeSamplingSubscriptionSnapshotHeader>() };
    Require(replacement.Snapshot(ref header, new NativeSamplingSubscriptionSourceView[8], new NativeSamplingSubscriptionItemState[8])
        == NativeSamplingSubscriptionStatus.Ok && header.ActiveSourceCount == 0, "fresh session after release");
}
Console.WriteLine("SubscriptionCore package consumer passed: actual native frequency merge, item selection, removal, expiry, restart and release.");
ManagedTrackerConsumer.Run();
await FieldFacadeConsumer.RunAsync();
await CompositeFieldsConsumer.RunAsync();
await WorkerConsumer.RunAsync();
await FrameFieldSubscriptionConsumer.RunAsync();
await SubscriptionSamplingModeConsumer.RunAsync();
await NativeFieldDeliveryConsumer.RunAsync();
await WindowsExplicitResultSubscriptionConsumer.RunAsync();
await WindowsRawInputSessionSubscriptionConsumer.RunRecordedAsync();
if (OperatingSystem.IsWindows())
{
    await DefaultOwnerSubscriptionConsumer.RunWindowsAsync();
    await PrinterInformationSubscriptionConsumer.RunAsync();
    await AudioCodecSubscriptionConsumer.RunAsync();
    await D3dCapabilitySubscriptionConsumer.RunAsync();
    await GpuApiCapabilitySubscriptionConsumer.RunAsync();
    await OpenClCapabilitySubscriptionConsumer.RunAsync();
await BluetoothClassicInformationSubscriptionConsumer.RunAsync();
await WpdInformationSubscriptionConsumer.RunAsync();
await AtaSmartInformationSubscriptionConsumer.RunAsync();
    await InformationOwnerSubscriptionConsumer.RunWindowsAsync();
    await ProcessDetailsInformationSubscriptionConsumer.RunWindowsAsync();
    await OperatingSystemUptimeSubscriptionConsumer.RunWindowsAsync();
    await OperatingSystemBuildBootSubscriptionConsumer.RunWindowsAsync();
    await OperatingSystemAcPowerSubscriptionConsumer.RunWindowsAsync();
    await OperatingSystemKeyboardScanCodeRemapSubscriptionConsumer.RunWindowsAsync();
    await WindowsFileLayoutSubscriptionConsumer.RunAsync();
    await WindowsComponentInformationSubscriptionConsumer.RunAsync();
    await DriverInformationSubscriptionConsumer.RunWindowsAsync();
    var wheaThrough = DateTimeOffset.UtcNow;
    await WindowsWheaHistorySubscriptionConsumer.RunWindowsAsync(new("System", wheaThrough - TimeSpan.FromDays(7),
        wheaThrough, 64, TimeSpan.FromSeconds(5)));
}

static uint Size<T>() where T : unmanaged => checked((uint)Unsafe.SizeOf<T>());
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static NativeSamplingSubscriptionItemReference[] References(ulong[] ids) => ids.Select(id =>
    new NativeSamplingSubscriptionItemReference { StructSize = Size<NativeSamplingSubscriptionItemReference>(), ItemHandle = id }).ToArray();

void Track(ulong source, ulong now, ulong interval, bool persistent, ulong[] items)
{
    var input = new NativeSamplingSubscriptionTrackInput
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionTrackInput>(),
        ConfigurationGeneration = 1, OperationEpoch = ++operation, CommandAtMilliseconds = now, ObservedAtMilliseconds = now,
        SourceHandle = source, ExplicitIntervalMilliseconds = interval, ItemCount = (uint)items.Length,
        ValidMask = (ulong)NativeSamplingSubscriptionTrackValidity.Known,
        Flags = (uint)(NativeSamplingSubscriptionSourceFlags.ExplicitInterval
            | (persistent ? NativeSamplingSubscriptionSourceFlags.Persistent : NativeSamplingSubscriptionSourceFlags.None))
    };
    Require(session.Track(in input, References(items)) == NativeSamplingSubscriptionStatus.Ok, "track actual demand");
}

void Remove(ulong source, ulong now)
{
    var input = new NativeSamplingSubscriptionRemoveInput
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionRemoveInput>(),
        ConfigurationGeneration = 1, OperationEpoch = ++operation, CommandAtMilliseconds = now, SourceHandle = source,
        ValidMask = (ulong)NativeSamplingSubscriptionRemoveValidity.Required
    };
    Require(session.Remove(in input) == NativeSamplingSubscriptionStatus.Ok, "remove actual demand");
}

(NativeSamplingSubscriptionPlanHeader Header, NativeSamplingSubscriptionDueItem[] Due,
    NativeSamplingSubscriptionExpiredSource[] Expired,
    NativeSamplingSubscriptionTrigger[] Triggers) Plan(ulong now)
{
    var due = new NativeSamplingSubscriptionDueItem[8]; var sources = new NativeSamplingSubscriptionSourceView[8];
    var expiredSources = new NativeSamplingSubscriptionExpiredSource[8];
    var triggers = new NativeSamplingSubscriptionTrigger[16];
    var header = new NativeSamplingSubscriptionPlanHeader
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionPlanHeader>(),
        ConfigurationGeneration = 1, OperationEpoch = ++operation, PlanEpoch = ++plan, CommandAtMilliseconds = now,
        ValidMask = (ulong)NativeSamplingSubscriptionPlanValidity.Required,
        DueItemCapacity = 8, SourceViewCapacity = 8, ExpiredSourceCapacity = 8, TriggerCapacity = 16
    };
    Require(session.Plan(ref header, due, sources, expiredSources, triggers) == NativeSamplingSubscriptionStatus.Ok, "plan actual demand");
    return (header, due[..(int)header.DueItemCount], expiredSources[..(int)header.ExpiredSourceCount],
        triggers[..(int)header.TriggerCount]);
}

void Complete((NativeSamplingSubscriptionPlanHeader Header, NativeSamplingSubscriptionDueItem[] Due,
    NativeSamplingSubscriptionExpiredSource[] Expired,
    NativeSamplingSubscriptionTrigger[] Triggers) sampled, ulong now)
{
    var input = new NativeSamplingSubscriptionCompletionInput
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionCompletionInput>(),
        ConfigurationGeneration = 1, OperationEpoch = ++operation, PlanEpoch = sampled.Header.PlanEpoch,
        CommandAtMilliseconds = now, CompletedAtMilliseconds = now, ItemCount = (uint)sampled.Due.Length,
        TriggerCount = (uint)sampled.Triggers.Length,
        ValidMask = (ulong)NativeSamplingSubscriptionCompletionValidity.Required, Status = (uint)NativeSamplingSubscriptionCompletionStatus.Sampled
    };
    Require(session.Complete(in input, References(sampled.Due.Select(static item => item.ItemHandle).ToArray()), sampled.Triggers, out _)
        == NativeSamplingSubscriptionStatus.Ok, "settle actual plan");
}

(NativeSamplingSubscriptionSnapshotHeader Header, NativeSamplingSubscriptionItemState[] Items) Snapshot()
{
    var items = new NativeSamplingSubscriptionItemState[8];
    var header = new NativeSamplingSubscriptionSnapshotHeader
    { AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = Size<NativeSamplingSubscriptionSnapshotHeader>() };
    Require(session.Snapshot(ref header, new NativeSamplingSubscriptionSourceView[8], items) == NativeSamplingSubscriptionStatus.Ok,
        "actual native snapshot");
    return (header, items[..(int)header.ItemOutputCount]);
}
