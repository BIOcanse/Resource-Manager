using System.Collections;
using System.Reflection;
using ResourceManager.App.StateItems;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.Shared.BusinessBlocks;
using BusinessBlockScheduler = ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private sealed record NoDemandOwnerEvidence(bool ResidencyInUse, bool InterruptsInUse, bool PortableDiscoveryEnabled,
        bool PortableDiscoveryPermitted, bool ReportsPermitted, bool ReportsDomainPermitted, bool SmartPermitted,
        BusinessBlocksSnapshot Controller, ActualUseHolders Holders);
    private static NoDemandOwnerEvidence ReadNoDemandOwners(IServiceProvider services, ActualUseHolders holders)
    {
        var scheduler = services.GetRequiredService<BusinessBlockScheduler>();
        bool Fact(int uid, int? block = null)
        {
            var value = scheduler.Authority.Read<bool>(new(uid, block));
            if (!value.HasValue) throw new InvalidDataException($"No-demand input {uid} is unpublished.");
            return value.Value;
        }
        return new(Fact(StateItemUids.SamplingCpuResidencyInUse), Fact(StateItemUids.SamplingInterruptsInUse),
            Fact(StateItemUids.PortableDiscoveryEnabled), Fact(StateItemUids.HostStartPermitted, BlockUids.PortableSoftwareDiscoveryService),
            Fact(StateItemUids.HostStartPermitted, BlockUids.HostManagerReportCoordinatorOwner),
            Fact(StateItemUids.HostStartPermitted, BlockUids.Reports),
            Fact(StateItemUids.HostStartPermitted, BlockUids.HostManagerSmartCoordinator), scheduler.Read(), holders);
    }
    private static void VerifyNoDemandOwners(NoDemandOwnerEvidence evidence)
    {
        foreach (var id in new[] { BlockUids.EtwCpuCoreResidencyReader, BlockUids.EtwSystemInterruptSnapshotSource,
            BlockUids.PortableSoftwareDiscoveryService, BlockUids.HostManagerReportCoordinatorOwner, BlockUids.HostManagerSmartCoordinator, BlockUids.Reports, BlockUids.KernelLeakObservation, BlockUids.GpuPlacementProcessHistoryObserver })
            if (evidence.Controller.Blocks.Single(value => value.Id == id).State != BusinessBlockState.Stopped)
                throw new InvalidDataException($"No-user block {id} has not stopped: {DescribeHolders(evidence.Holders)}");
        if (evidence.PortableDiscoveryPermitted || evidence.ReportsPermitted || evidence.ReportsDomainPermitted || evidence.SmartPermitted
            || evidence.Holders.Kernel.Length != 0 || evidence.Holders.Activity.Any(value => value.InUse)
            || evidence.Holders.Present.Length != 0)
            throw new InvalidDataException($"No-user profile retained a consumer: {DescribeHolders(evidence.Holders)}");
    }

    private sealed record SamplingHolderEvidence(string Id, bool InUse, bool Persistent, TimeSpan? Interval,
        string[] Items, string[] RequestedFields, int[] FieldUids);
    private sealed record ActivityHolderEvidence(int Uid, string Name, bool InUse, string[] HolderIds);
    private sealed record ActualUseHolders(KernelRegistrationEvidence[] Kernel, ActivityHolderEvidence[] Activity,
        SamplingHolderEvidence[] Interrupts, SamplingHolderEvidence[] Residency,
        SamplingHolderEvidence[] Resources, SamplingHolderEvidence[] Hardware, object[] Present);
    private static ActualUseHolders ReadActualUseHolders(IServiceProvider services, IKernelEtwSessionBroker broker)
    {
        var irq = ReadSamplingHolders(services.GetRequiredService<EtwSystemInterruptSnapshotSource>());
        var cpu = ReadSamplingHolders(services.GetRequiredService<EtwCpuCoreResidencyReader>());
        var resources = ReadSamplingHolders(services.GetRequiredService<WindowsResourceBreakdownSampler>());
        var hardware = ReadSamplingHolders(services.GetRequiredService<WindowsHardwareMetricSampler>());
        var present = ReadPresentHolders(services.GetRequiredService<PresentFrameEtwSource>());
        var authority = services.GetRequiredService<BusinessBlockScheduler>().Authority;
        ActivityHolderEvidence Read(int uid, string name, IEnumerable<string> ids)
        {
            var fact = authority.Read<bool>(uid);
            if (!fact.HasValue) throw new InvalidDataException($"Activity fact {uid}/{name} is unpublished.");
            return new(uid, name, fact.Value, ids.ToArray());
        }
        string[] Ids(SamplingHolderEvidence[] sources) => sources.Where(value => value.InUse).Select(value => value.Id).ToArray();
        var fields = new[] {
            Read(StateItemUids.SamplingInterruptsInUse, "interrupts", Ids(irq)),
            Read(StateItemUids.SamplingCpuResidencyInUse, "residency", Ids(cpu)),
            Read(StateItemUids.SamplingHardwareMetricsInUse, "hardware", Ids(hardware)),
            Read(StateItemUids.SamplingResourceBreakdownInUse, "resources", Ids(resources)),
            Read(StateItemUids.SamplingGpuTelemetryInUse, "gpu-telemetry", ReadOptionalSamplingIds(services, "GpuTelemetryWorkerSupervisor")),
            Read(StateItemUids.SamplingCpuTopologyInUse, "cpu-topology", ReadOptionalSamplingIds(services, "CpuTopologySnapshotProvider")),
            Read(StateItemUids.SamplingDeviceTopologyInUse, "device-topology", ReadOptionalSamplingIds(services, "DeviceTopologySnapshotProvider")),
            Read(StateItemUids.GraphicsApiObservationInUse, "graphics-api", present.Where(NeedsPresentEvents).Select(value => (string)Member(value, "Id")!)),
            Read(StateItemUids.ProcessDedicatedGpuMemoryInUse, "dedicated-gpu-memory", resources.Where(value => value.InUse && value.FieldUids.Contains(113)).Select(value => value.Id)),
            Read(StateItemUids.VramResidualInUse, "vram-residual", resources.Where(value => value.InUse && value.FieldUids.Contains(114)).Select(value => value.Id)),
            Read(StateItemUids.DiskAttributionInUse, "disk-attribution", Ids(resources)),
            Read(StateItemUids.NetworkAttributionInUse, "network-attribution", Ids(resources)) };
        return new(ReadKernelRegistrations(broker), fields, irq, cpu, resources, hardware, present);
    }
    private static readonly Type[] ActualUseCoreTypes = typeof(WindowsHardwareMetricSampler).Assembly.GetTypes();
    private static string[] ReadOptionalSamplingIds(IServiceProvider services, string typeName)
    {
        // These roles are recorded too so no-user cannot hide an unrelated active sampling fact.
        var type = ActualUseCoreTypes.SingleOrDefault(value => value.Name == typeName);
        var instance = type is null ? null : services.GetService(type);
        return instance is null ? [] : ReadSamplingHolders(instance).Where(value => value.InUse).Select(value => value.Id).ToArray();
    }
    private static object[] ReadPresentHolders(PresentFrameEtwSource present)
        => ((IEnumerable)(present.GetType().GetMethod("ReadHolders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(present, null)!)).Cast<object>().ToArray();
    private static bool NeedsPresentEvents(object holder) => (bool)Member(holder, "NeedsEvents")!;
    private static string DescribeHolders(ActualUseHolders holders)
        => $"kernel=[{string.Join(",", holders.Kernel.Select(value => $"{value.Id}:0x{(ulong)value.Keywords:X}"))}]; "
            + $"activity=[{string.Join(";", holders.Activity.Where(value => value.InUse).Select(value => $"{value.Uid}/{value.Name}:[{string.Join(",", value.HolderIds)}]"))}]; "
            + $"present=[{string.Join(",", holders.Present.Select(value => $"{Member(value, "Id")}:needsEvents={NeedsPresentEvents(value)}"))}]";

    private static SamplingHolderEvidence[] ReadSamplingHolders(object source)
    {
        var tracker = Member(source, "subscriptions");
        if (tracker is null) return [];
        var type = tracker.GetType();
        for (var depth = 0; type is not null && depth < 16 && type.GetField("activitySnapshot", BindingFlags.Instance | BindingFlags.NonPublic) is null; depth++) type = type.BaseType;
        if (type is null) throw new InvalidDataException($"Unknown subscription tracker for {source.GetType().Name}.");
        var snapshot = type.GetField("activitySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        var clock = (TimeProvider)type.GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        var settings = type.GetMethod("CurrentSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracker, null);
        var raw = (ulong)type.GetMethod("RawTimestamp", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [clock.GetUtcNow()])!;
        var elapsed = clock.GetElapsedTime((long)Member(snapshot, "CommandTimestamp")!, clock.GetTimestamp());
        var now = Math.Max(raw, checked((ulong)Member(snapshot, "CommandMilliseconds")! + (ulong)(elapsed.Ticks / TimeSpan.TicksPerMillisecond)));
        var sources = (IEnumerable)Member(snapshot, "Sources")!;
        var result = new List<SamplingHolderEvidence>();
        foreach (var payload in sources)
        {
            var request = Member(payload, "Request")!;
            var inUse = settings is not null && (bool)type.GetMethod("IsInUse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [payload, now, settings])!;
            var fields = new List<string>();
            foreach (var property in new[] { "MetricIds", "PublicationDatasetIds" })
                if (Member(request, property) is IEnumerable values) fields.AddRange(values.Cast<object>().Select(value => value.ToString()!));
            if (Member(request, "SchedulingMetricMask") is { } mask) fields.Add("SchedulingMetricMask:" + mask);
            var uids = new List<int>();
            if (request is ResourceBreakdownSampleRequest resource)
            {
                if ((bool)typeof(WindowsResourceBreakdownSampler).GetMethod("NeedsGpuAllocations", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [resource])!) uids.Add(113);
                foreach (var id in resource.MetricIds)
                {
                    object?[] arguments = [id, 0, ""];
                    if ((bool)typeof(WindowsResourceBreakdownSampler).GetMethod("TryParseGpuMetric", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)! && (string)arguments[2]! == "vram") { uids.Add(114); break; }
                }
            }
            result.Add(new((string)Member(payload, "SourceKey")!, inUse, (bool)Member(payload, "Persistent")!,
                (TimeSpan?)Member(payload, "ExplicitInterval"), ((IEnumerable)Member(payload, "ItemIds")!).Cast<string>().ToArray(), fields.ToArray(), uids.ToArray()));
        }
        return result.ToArray();
    }
    private static object? Member(object instance, string name)
    {
        var type = instance.GetType();
        for (var depth = 0; type is not null && depth < 16; depth++, type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (type.GetField(name, flags) is { } field) return field.GetValue(instance);
            if (type.GetProperty(name, flags) is { } property) return property.GetValue(instance);
        }
        return null;
    }
    private sealed record ResidencyWindowEvidence(DateTimeOffset MeasuredFrom, DateTimeOffset MeasuredThrough,
        long SessionGeneration, int ProcessRows);
}
