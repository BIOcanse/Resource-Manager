using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class HostD3dVideoDecoderCapabilityIntegrationTests
{
    private static readonly WindowsD3dAdapterIdentity Adapter =
        new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly Guid Profile = new("1B81BE68-A0C7-11D3-B984-00C04F2E73C5");

    [Fact]
    public async Task ExplicitHostFactoryUsesProviderCurrentAndOriginalInformationBorrow()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = new DeviceTopologySnapshotProvider(new RecordedTopology(), new RecordedStore(), new(),
            sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance, new RecordedAcpi());
        var descriptor = new WindowsD3dVideoDecoderDescriptor(Profile, 1920, 1080, WindowsD3dFormat.Nv12);
        var scope = new WindowsD3dCapabilityScope(
            WindowsD3dCapabilityFields.DecoderProfiles | WindowsD3dCapabilityFields.DecoderOutputFormats
                | WindowsD3dCapabilityFields.DecoderConfigurations,
            WindowsD3dApiVersion.Direct3D11, [Adapter], [WindowsD3dFormat.Nv12],
            decoderConfigurationDescriptors: [descriptor]);
        var reader = new RecordedReader();
        var access = provider.CreateD3dCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var profileField = catalog.Fields.Single(static item =>
            item.Field.FieldName == "gpu_all_video_decoder_profile_guid").Field;
        var formatField = catalog.Fields.Single(static item =>
            item.Field.FieldName == "gpu_all_video_decoder_output_format").Field;
        var configurationField = catalog.Fields.Single(static item =>
            item.Field.FieldName == "gpu_all_video_decoder_configuration").Field;

        var current = access.Backend.ReadCached(profileField);
        Assert.Equal(FieldObservationStatus.Current, current.Status);
        Assert.Equal(Profile.ToString("D"), current.Value?.Text);
        var providerFact = provider.ReadState().InformationFields.FindField(profileField);
        Assert.NotNull(providerFact);
        Assert.Equal(Profile.ToString("D"), providerFact.Value?.GetString());
        var configurationCurrent = access.Backend.ReadCached(configurationField);
        Assert.Equal(FieldObservationStatus.Current, configurationCurrent.Status);
        using (var configuration = JsonDocument.Parse(configurationCurrent.Value!.Structure!.Value.GetRawText()))
        {
            Assert.Equal(1U, configuration.RootElement.GetProperty("configurationCount").GetUInt32());
            Assert.Equal(0U, configuration.RootElement.GetProperty("configurations")[0].GetProperty("index").GetUInt32());
        }

        using var borrow = access.BorrowSubscriptionCapture();
        Assert.Same(access.Backend, borrow.Backend);
        var bindings = borrow.Bind([formatField, configurationField]);
        Assert.Equal(2, bindings.Length);
        Assert.All(bindings, binding => Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(binding.CollectionItemIds)));
        Assert.All(bindings, binding => Assert.Equal(FieldCollectionSourceKind.SourceQuery, binding.SourceKind));

        var collected = await borrow.CollectAsync([formatField, configurationField], new AcceptingPermit(), default);
        var borrowed = Assert.Single(collected.Readings.Where(reading => reading.Field == configurationField));
        Assert.True(collected.Committed);
        Assert.Contains(formatField, collected.QueriedFields);
        Assert.Equal(FieldObservationStatus.Current, borrowed.Status);
        Assert.Equal(MetricInteger.Parse("103"), collected.Readings.Single(reading => reading.Field == formatField).Value?.Integer);
        Assert.Equal(borrowed, access.Backend.ReadCached(configurationField));
        Assert.NotNull(provider.ReadState().InformationFields.FindField(configurationField)?.Value);
        Assert.Equal(2, reader.ReadCount);
    }

    private sealed class RecordedReader : IWindowsD3dCapabilityReader
    {
        public int ReadCount { get; private set; }

        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return new([
                new(Adapter, WindowsD3dApiVersion.Direct3D11, null, [], true,
                [new(Profile, true, [new(WindowsD3dFormat.Nv12, true)])],
                [new(new(Profile, 1920, 1080, WindowsD3dFormat.Nv12), true, 1,
                    [new(0, new(Guid.Empty, Guid.Empty, Guid.Empty, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0))])])
            ]);
        }
    }

    private sealed class RecordedTopology : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot()
            => throw new InvalidOperationException("The explicit decoder query must not read topology.");
    }

    private sealed class RecordedAcpi : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables()
            => throw new InvalidOperationException("The explicit decoder query must not read ACPI.");
    }

    private sealed class RecordedStore : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);

        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The explicit decoder query must not persist topology.");
    }

    private sealed class AcceptingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit)
        {
            commit();
            return true;
        }
    }
}
