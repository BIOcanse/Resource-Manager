using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;

internal static class DisplayOwnerConsumer
{
    // Actual native/read-only route. Explicit configuration matches the original native ABI fixture;
    // identities are exact OS monitor paths and field time is native last-success time.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var configuration = Configuration();
        using var owner = new NativeDisplayCoordinatorWorkspace(in configuration);
        using var client = new HttpClient();
        var backend = new InformationReadWriteBackend(() => owner.ReadFields(Identity), token => {
            token.ThrowIfCancellationRequested();
            owner.Refresh([]); // Explicitly supplied no OEM profiles or persistence; no true-machine write.
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.ReadFields(Identity));
        }, static _ => new(true, null, []));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var catalog = await core.DiscoverAsync();
        if (catalog.Fields.IsEmpty || owner.ReadCurrent().Current.State != HostManagerDisplayCoordinatorReadState.Ready)
        {
            Console.WriteLine("Actual native display owner produced empty/unavailable canonical data; no available-field claim.");
            return;
        }
        var field = catalog.Fields.First().Field;
        Require(core.Query(field).Read.Supported && !core.Query(field).Write.Supported, "original read-only capability");
        await core.CollectAsync([field]);
        var before = owner.ReadCurrent();
        _ = core.ReadCached(field);
        var after = owner.ReadCurrent();
        Require(before.Metadata.LastAttemptUtcMilliseconds == after.Metadata.LastAttemptUtcMilliseconds
            && before.Metadata.StateRevision == after.Metadata.StateRevision, "cached canonical read does not recapture");
        Require(typeof(NativeDisplayCoordinatorWorkspace).Assembly == typeof(ReadWriteCore).Assembly,
            "complete native display owner belongs to independent package");
        Console.WriteLine("Actual native display owner canonical consumer completed; caller monitor identities/native last-success time; persistence disabled.");
    }

    private static string Identity(DeviceTopologyDisplayPath path)
        => path.MonitorDevicePath!;

    private static NativeDisplayCoordinatorConfiguration Configuration() => new()
    {
        AbiVersion = NativeDisplayCoordinatorAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeDisplayCoordinatorConfiguration>(), Generation = 1,
        MaximumSourceCount = NativeDisplayCoordinatorAbi.SourceCount, MaximumObservationCount = 8,
        MaximumNodeCount = 16, MaximumEdgeCount = 8, MaximumCapabilityCount = 8, MaximumDiffEntryCount = 160,
        MaximumTextBindingCount = 32, MaximumTextByteCount = 4096, MaximumUnresolvedCount = 8,
        MaximumSourceBatchCount = NativeDisplayCoordinatorAbi.SourceCount, IdentityIndexCapacity = 64,
        TextIndexCapacity = 32, ResidentByteBudget = 4 * 1024 * 1024,
        RequiredSourceMask = (ulong)NativeDisplaySourceMask.DisplayConfig,
        OptionalSourceMask = (ulong)(NativeDisplaySourceMask.Dxgi | NativeDisplaySourceMask.Edid
            | NativeDisplaySourceMask.SetupApiMonitor | NativeDisplaySourceMask.OemConnectorProfile),
        IdentitySourcePriorityOrder = 0x0000_0005_0403_0201UL, FriendlyNameSourcePriorityOrder = 0x0000_0005_0403_0201UL,
        CapabilitySourcePriorityOrder = 0x0000_0005_0403_0201UL, ProjectionSourcePriorityOrder = 0x0000_0005_0403_0201UL,
        IdentityContractVersion = NativeDisplayCoordinatorAbi.IdentityContractVersion,
        CapabilityContractVersion = NativeDisplayCoordinatorAbi.CapabilityContractVersion,
        MaximumFutureSkewMilliseconds = 30_000
    };

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
