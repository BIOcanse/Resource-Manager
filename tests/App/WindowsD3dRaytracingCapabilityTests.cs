using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsD3dRaytracingCapabilityTests
{
    private static readonly WindowsD3dAdapterIdentity First = new(0x10000002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity Second = new(0x20000003B, 0x10DE, 0x2684);

    [Fact]
    public void ExplicitFlagPreservesOriginalAllAndPositionalRecordShape()
    {
        Assert.Equal(3, (int)WindowsD3dCapabilityFields.All);
        Assert.Equal(0, (int)(WindowsD3dCapabilityFields.All & WindowsD3dCapabilityFields.RaytracingTier));
        Assert.Throws<ArgumentException>(() => new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.RaytracingTier, WindowsD3dApiVersion.Direct3D11, [First]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsD3dCapabilityScope(
            (WindowsD3dCapabilityFields)256, WindowsD3dApiVersion.Direct3D12, [First]));
        var scope = Scope();
        Assert.Empty(scope.Formats);
        var definition = Assert.Single(scope.Definitions);
        Assert.Same(DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier, definition);
        Assert.Equal("enum", definition.Unit);
        var row = new WindowsD3dAdapterCapabilityRead(First, WindowsD3dApiVersion.Direct3D12, null, []);
        var (adapter, api, level, formats, complete, profiles, configurations, caps, rates) = row;
        Assert.Equal(First, adapter);
        Assert.Equal(WindowsD3dApiVersion.Direct3D12, api);
        Assert.Null(level);
        Assert.Empty(formats);
        Assert.Null(complete);
        Assert.True(profiles.IsDefault && configurations.IsDefault && caps.IsDefault && rates.IsDefault);
        var serializable = row with
        {
            DecoderProfiles = [], DecoderConfigurations = [],
            VideoProcessorCaps = [], VideoProcessorRateConversionCaps = []
        };
        Assert.False(JsonSerializer.SerializeToElement(serializable).TryGetProperty("RaytracingTier", out _));
        Assert.Equal(0U, JsonSerializer.SerializeToElement(serializable with { RaytracingTier = 0 })
            .GetProperty("RaytracingTier").GetUInt32());
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(10U)]
    [InlineData(11U)]
    [InlineData(12U)]
    [InlineData(uint.MaxValue)]
    public void RealCheckFeatureSupportSlotReadsRawTierAtTheSdkOffset(uint tier)
    {
        using var call = new Options5Call { Tier = tier };
        Assert.Equal((12, 8), WindowsD3dCapabilityNativeReader.D3d12Options5Layout);
        Assert.Equal<uint?>(tier, WindowsD3dCapabilityNativeReader.TryReadD3d12RaytracingTier(call.Device, default));
        Assert.Equal(1, call.Count);
        Assert.Equal(27U, call.Feature);
        Assert.Equal(12U, call.Size);
        Assert.Equal(call.Device, call.ObservedDevice);
    }

    [Fact]
    public void NativeFailureAndCancellationNeverTurnAZeroBufferIntoReportedTierZero()
    {
        using var failed = new Options5Call { Status = unchecked((int)0x80070057), Tier = 0 };
        Assert.Null(WindowsD3dCapabilityNativeReader.TryReadD3d12RaytracingTier(failed.Device, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var before = new Options5Call();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            WindowsD3dCapabilityNativeReader.TryReadD3d12RaytracingTier(before.Device, canceled.Token));
        Assert.Equal(0, before.Count);
        using var during = new CancellationTokenSource();
        using var after = new Options5Call { AfterCall = during.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            WindowsD3dCapabilityNativeReader.TryReadD3d12RaytracingTier(after.Device, during.Token));
        Assert.Equal(1, after.Count);
    }

    [Fact]
    public async Task SelectedTierUsesExactAdaptersAndPreservesUnselectedFeatureLevels()
    {
        using var owner = EmptyOwner();
        var reader = new RecordedReader();
        var scope = Scope(WindowsD3dCapabilityFields.RaytracingTier | WindowsD3dCapabilityFields.FeatureLevel);
        var access = owner.CreateD3dCapabilityInformation(scope, reader);
        Assert.Equal(0, reader.Count);
        var catalog = await access.Backend.DiscoverAsync(default);
        var first = catalog.Fields.Single(x => x.Field.DeviceIdentity.Contains($"luid:{First.Luid:X16}")
            && x.Field.FieldName == DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier.Name).Field;
        var second = catalog.Fields.Single(x => x.Field.DeviceIdentity.Contains($"luid:{Second.Luid:X16}")
            && x.Field.FieldName == DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier.Name).Field;
        Assert.Equal("0", access.Backend.ReadCached(first).Value?.Integer);
        Assert.Equal("4294967295", access.Backend.ReadCached(second).Value?.Integer);
        var levels = catalog.Fields.Where(x => x.Field.FieldName == DeviceInformationFieldRegistry.GpuAllD3dFeatureLevel.Name)
            .Select(x => x.Field).ToArray();
        var preserved = levels.Select(access.Backend.ReadCached).ToArray();
        var reads = reader.Count;
        _ = access.Backend.ReadCached(first);
        _ = owner.ReadState().InformationFields;
        Assert.Equal(reads, reader.Count);
        reader.FirstTier = 10;
        reader.SecondTier = null;
        _ = await access.Backend.CollectAsync([first, second], default);
        Assert.Equal(WindowsD3dCapabilityFields.RaytracingTier, reader.LastFields);
        Assert.Equal("10", access.Backend.ReadCached(first).Value?.Integer);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(second).Status);
        Assert.Null(access.Backend.ReadCached(second).Value);
        Assert.Null(access.Backend.ReadCached(second).ObservedAt);
        Assert.Equal(preserved, levels.Select(access.Backend.ReadCached).ToArray());
        reader.WrongIdentity = true;
        _ = await access.Backend.CollectAsync([first, second], default);
        Assert.Null(access.Backend.ReadCached(first).Value);
        Assert.Null(access.Backend.ReadCached(second).Value);
        Assert.Equal(preserved, levels.Select(access.Backend.ReadCached).ToArray());
        reader.WrongIdentity = false;
        reader.SecondTier = 12;
        _ = await access.Backend.CollectAsync([first, second], default);
        Assert.Equal("12", access.Backend.ReadCached(second).Value?.Integer);
    }

    [Fact]
    public async Task CancellationAndRejectedPublicationLeaveOriginalCurrentUntouched()
    {
        using var owner = EmptyOwner();
        var reader = new RecordedReader();
        var access = owner.CreateD3dCapabilityInformation(Scope(), reader);
        var field = (await access.Backend.DiscoverAsync(default)).Fields.First().Field;
        var before = owner.ReadState();
        using var canceled = new CancellationTokenSource();
        reader.FirstTier = 11;
        reader.AfterRead = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            access.Backend.CollectAsync([field], canceled.Token).AsTask());
        Assert.Same(before, owner.ReadState());
        reader.AfterRead = null;
        using var borrow = access.BorrowSubscriptionCapture();
        var rejected = await borrow.CollectAsync([field], new Permit(false), default);
        Assert.False(rejected.Committed);
        Assert.Same(before, owner.ReadState());
        var accepted = await borrow.CollectAsync([field], new Permit(true), default);
        Assert.True(accepted.Committed);
        Assert.Equal("11", access.Backend.ReadCached(field).Value?.Integer);
        Assert.Equal(access.Backend.ReadCached(field), Assert.Single(accepted.Readings));
        reader.FirstTier = null;
        _ = await borrow.CollectAsync([field], new Permit(true), default);
        Assert.Null(access.Backend.ReadCached(field).Value);
        Assert.Null(access.Backend.ReadCached(field).ObservedAt);
    }

    [Fact]
    public void AnUnrequestedTierIsRejectedInsteadOfLeakingIntoOldAll()
    {
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.FeatureLevel,
            WindowsD3dApiVersion.Direct3D12, [First]);
        var read = new WindowsD3dCapabilityReadResult([new(First, scope.ApiVersion,
            WindowsD3dFeatureLevel.Level12_1, []) { RaytracingTier = 0 }]);
        Assert.False(WindowsInformationFieldProjection.IsValidD3dRead(scope, scope.Fields, read, DateTimeOffset.UtcNow));
    }

    private static WindowsD3dCapabilityScope Scope(WindowsD3dCapabilityFields fields = WindowsD3dCapabilityFields.RaytracingTier)
        => new(fields, WindowsD3dApiVersion.Direct3D12, [First, Second]);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private sealed class RecordedReader : IWindowsD3dCapabilityReader
    {
        public uint? FirstTier { get; set; } = 0;
        public uint? SecondTier { get; set; } = uint.MaxValue;
        public int Count { get; private set; }
        public WindowsD3dCapabilityFields LastFields { get; private set; }
        public bool WrongIdentity { get; set; }
        public Action? AfterRead { get; set; }
        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            LastFields = selectedFields;
            var rows = scope.Adapters.Select((adapter, index) => new WindowsD3dAdapterCapabilityRead(
                WrongIdentity && index == 0 ? new(adapter.Luid, adapter.PciVendorId, 0x2685) : adapter,
                scope.ApiVersion, (selectedFields & WindowsD3dCapabilityFields.FeatureLevel) != 0
                    ? WindowsD3dFeatureLevel.Level12_1 : null, [])
            { RaytracingTier = (selectedFields & WindowsD3dCapabilityFields.RaytracingTier) != 0
                    ? index == 0 ? FirstTier : SecondTier : null }).ToImmutableArray();
            AfterRead?.Invoke();
            return new(rows);
        }
    }

    private sealed class Permit(bool accept) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { if (!accept) return false; commit(); return true; }
    }

    private sealed class Options5Call : IDisposable
    {
        private readonly WindowsD3dCapabilityNativeReader.D3d12CheckFeatureSupportDelegate callback;
        private readonly IntPtr table;
        public IntPtr Device { get; }
        public int Count { get; private set; }
        public uint Feature { get; private set; }
        public uint Size { get; private set; }
        public IntPtr ObservedDevice { get; private set; }
        public int Status { get; init; }
        public uint Tier { get; init; }
        public Action? AfterCall { get; init; }
        public Options5Call()
        {
            callback = Query;
            table = Marshal.AllocHGlobal(14 * IntPtr.Size);
            Device = Marshal.AllocHGlobal(IntPtr.Size);
            for (var i = 0; i < 14; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(table, 13 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callback));
            Marshal.WriteIntPtr(Device, table);
        }
        private int Query(IntPtr device, uint feature, IntPtr data, uint size)
        {
            Count++; ObservedDevice = device; Feature = feature; Size = size;
            if (size != 12) return unchecked((int)0x80070057);
            // Distinct unrelated members detect an incorrect offset without a hardware call.
            Marshal.WriteInt32(data, 0, 17); Marshal.WriteInt32(data, 4, 19);
            Marshal.WriteInt32(data, 8, unchecked((int)Tier));
            AfterCall?.Invoke();
            return Status;
        }
        public void Dispose()
        {
            Marshal.FreeHGlobal(Device); Marshal.FreeHGlobal(table); GC.KeepAlive(callback);
        }
    }
}
