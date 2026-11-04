using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    [SupportedOSPlatform("windows")]
    public WindowsWpdInformationAccess CreatePortableDeviceInformation(WindowsWpdInformationScope scope)
        => CreatePortableDeviceInformation(scope, new WindowsWpdInformationNativeReader());

    /// <summary>Creates a passive WPD view over this owner's current and capture gate.
    /// The injected reader is called only by explicit discovery, selected collection,
    /// or the existing role-2 borrow.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsWpdInformationAccess CreatePortableDeviceInformation(
        WindowsWpdInformationScope scope, IWindowsWpdInformationReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsWpdInformationAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureWpdInformationAsync(
        WindowsWpdInformationScope scope,
        WindowsWpdInformationCaptureRequest request,
        IWindowsWpdInformationReader reader,
        DeviceTopologyCapturePermission? permission,
        bool requireSuccessfulRead,
        CancellationToken cancellationToken,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((request.Fields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Fields == WindowsWpdInformationFields.None)
            return WindowsInformationFieldProjection.WpdCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.WpdCurrent(ReadState().InformationFields, scope);

            WindowsWpdInformationReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, request, cancellationToken)
                    ?? new(false, [], [], "reader-returned-null");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsWpdReadFailure(exception))
            {
                read = new(false, [], [], $"reader-threw-{exception.GetType().Name}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            var committed = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeWpd(current.InformationFields,
                    scope, request, read, observedAt, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });

            if (requireSuccessfulRead && committed && (!read.DeviceDirectorySucceeded || read.Failures.Count != 0))
                throw new InvalidOperationException("WPD discovery failed for the explicitly selected scope; "
                    + $"directorySucceeded={read.DeviceDirectorySucceeded}, failedReads={read.Failures.Count}, "
                    + $"directoryFailure={read.DeviceDirectoryFailureCode ?? "none"}.");

            return WindowsInformationFieldProjection.WpdCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }

    private static bool IsWpdReadFailure(Exception exception)
        => exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidDataException or OverflowException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
            or NotSupportedException or System.Runtime.InteropServices.COMException
            or System.Runtime.InteropServices.MarshalDirectiveException;
}
