using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
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
            IReadOnlyList<AcpiFirmwareTable>? tables;
            try { sourceQueryStarted?.Invoke(); tables = acpiReader.ReadTables(); }
            catch (Exception error)
            {
                logAcpiFailure?.Invoke(error);
                tables = null;
            }
            var next = tables is null
                ? WindowsInformationFieldProjection.ClearAcpiValues(ReadState().AcpiInformationFields)
                : WindowsInformationFieldProjection.CreateAcpi(tables, DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPublish(permission, () =>
            {
                Volatile.Write(ref state, ReadState() with { AcpiInformationFields = next });
                acpiPublicationSignal.Publish();
            })) return DeviceTopologySampleCompletion.Canceled;
            return tables is null ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        finally { sampleGate.Release(); }
    }
}

