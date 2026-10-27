using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    private Lazy<(DeviceInformationSnapshot Snapshot, bool Failed)>? fixedAcpi;

    [RuntimeReadCache]
    private (DeviceInformationSnapshot Snapshot, bool Failed) ReadAcpiOnce(Action? sourceQueryStarted)
        => (fixedAcpi ??= new(() => ReadAcpiSource(sourceQueryStarted))).Value;

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    private (DeviceInformationSnapshot Snapshot, bool Failed) ReadAcpiSource(Action? sourceQueryStarted)
    {
        IReadOnlyList<AcpiFirmwareTable>? tables;
        try { sourceQueryStarted?.Invoke(); tables = acpiReader.ReadTables(); }
        catch (Exception error)
        {
            logAcpiFailure?.Invoke(error);
            tables = null;
        }
        return (tables is null
            ? WindowsInformationFieldProjection.ClearAcpiValues(ReadState().AcpiInformationFields)
            : WindowsInformationFieldProjection.CreateAcpi(tables, DateTimeOffset.UtcNow), tables is null);
    }

    public async Task<DeviceTopologySampleCompletion> SampleAcpiAsync(
        DeviceTopologyCapturePermission? permission, CancellationToken cancellationToken, bool requireDemand = true,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        try
        {
            if (requireDemand && !await sampleGate.WaitAsync(0, cancellationToken)) return DeviceTopologySampleCompletion.SkippedBusy;
            if (!requireDemand) await sampleGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceTopologySampleCompletion.Canceled;
        }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (permission is not null && !permission.IsActive()) return DeviceTopologySampleCompletion.Canceled;
            // Settle retired demand in the existing plan without beginning a new firmware read.
            if (requireDemand && permission is not null && !permission.HasCurrentDemand())
                return DeviceTopologySampleCompletion.Sampled;
            var capture = ReadAcpiOnce(sourceQueryStarted);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPublish(permission, () =>
            {
                Volatile.Write(ref state, ReadState() with { AcpiInformationFields = capture.Snapshot });
                acpiPublicationSignal.Publish();
            })) return DeviceTopologySampleCompletion.Canceled;
            return capture.Failed ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        finally { sampleGate.Release(); }
    }
}

