using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Printers;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    [SupportedOSPlatform("windows")]
    public WindowsPrinterInformationAccess CreatePrinterInformation(WindowsPrinterInformationScope scope)
        => CreatePrinterInformation(scope, new WindowsPrinterInformationNativeReader());

    /// <summary>Creates a scoped printer view on this owner's existing current and capture gate.
    /// No enumeration occurs until discovery or selected collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsPrinterInformationAccess CreatePrinterInformation(
        WindowsPrinterInformationScope scope, IWindowsPrinterInformationReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsPrinterInformationAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CapturePrinterInformationAsync(
        WindowsPrinterInformationScope scope,
        WindowsPrinterInformationFields selectedFields,
        IWindowsPrinterInformationReader reader,
        DeviceTopologyCapturePermission? permission,
        bool requireSuccessfulRead,
        CancellationToken cancellationToken,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsPrinterInformationFields.None)
            return WindowsInformationFieldProjection.PrinterCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.PrinterCurrent(ReadState().InformationFields, scope);

            WindowsPrinterInformationReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, selectedFields, cancellationToken)
                    ?? new(false, [], [new(null, null, selectedFields, "reader-returned-null")]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsPrinterReadFailure(exception))
            {
                read = new(false, [], [new(null, null, selectedFields, "reader-threw")]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            var committed = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergePrinters(current.InformationFields,
                    scope, selectedFields, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });

            if (requireSuccessfulRead && committed && (!read.DirectorySucceeded || read.Failures.Count != 0))
                throw new InvalidOperationException($"Printer discovery failed for the {scope.EnumerationCode} scope; "
                    + $"directorySucceeded={read.DirectorySucceeded}, failedReads={read.Failures.Count}.");

            return WindowsInformationFieldProjection.PrinterCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }

    private static bool IsPrinterReadFailure(Exception exception)
        => exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidDataException or OverflowException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
            or NotSupportedException or System.Runtime.InteropServices.ExternalException
            or System.Runtime.InteropServices.MarshalDirectiveException;
}
