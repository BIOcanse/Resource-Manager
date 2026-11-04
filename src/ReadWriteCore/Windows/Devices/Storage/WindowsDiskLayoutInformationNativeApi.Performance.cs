using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsDiskLayoutInformationNativeApi
{
    private const int DiskPerformanceBufferBytes = 88;
    private const int DiskPerformanceMembersBytes = 84;

    public async Task<WindowsDiskPerformanceNativeRead?> ReadPerformanceAsync(
        WindowsDiskInterface device,
        IReadOnlyDictionary<(StorageIdentifyProtocol Protocol, string Serial), DeviceTopologyStorageInformation[]> candidates,
        IReadOnlySet<string> selectedIdentityKeys,
        TimeSpan sampleWindow,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle handle;
        try { handle = calls.Open(device.DevicePath); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { return null; }
        using (handle)
        {
            if (handle.IsInvalid || handle.IsClosed) return null;
            var numberBefore = Number(handle);
            if (numberBefore is null) return null;

            string? identityKey;
            try
            {
                var descriptor = ReadDeviceDescriptor(handle);
                var actual = ReadHardwareIdentification(handle, descriptor);
                identityKey = WindowsInformationFieldProjection.PhysicalDiskRelation(actual, candidates);
            }
            catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { return null; }
            if (identityKey is null || !selectedIdentityKeys.Contains(identityKey)) return null;

            cancellationToken.ThrowIfCancellationRequested();
            var metrics = await ReadPerformanceWindowAsync(handle, sampleWindow, clock, cancellationToken).ConfigureAwait(false);
            // The number brackets only verify the opened handle remained stable. It is never joined to a physical identity.
            var numberAfter = Number(handle);
            return numberAfter == numberBefore ? new(identityKey, metrics) : new(identityKey, null);
        }
    }

    private async Task<WindowsDiskPerformanceMetrics?> ReadPerformanceWindowAsync(
        SafeFileHandle handle, TimeSpan sampleWindow, TimeProvider clock, CancellationToken cancellationToken)
    {
        var successfulEnableCalls = 0;
        var offCallsSucceeded = true;
        WindowsDiskPerformanceMetrics? metrics = null;
        try
        {
            var firstBuffer = new byte[DiskPerformanceBufferBytes];
            if (!calls.Performance(handle, firstBuffer, out var firstReturned)) return null;
            successfulEnableCalls++;
            var firstTimestamp = clock.GetTimestamp();
            var first = DecodePerformance(firstBuffer, firstReturned);
            if (first is null) return null;

            await Task.Delay(sampleWindow, clock, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var secondBuffer = new byte[DiskPerformanceBufferBytes];
            if (!calls.Performance(handle, secondBuffer, out var secondReturned)) return null;
            successfulEnableCalls++;
            var secondTimestamp = clock.GetTimestamp();
            var second = DecodePerformance(secondBuffer, secondReturned);
            if (second is null) return null;
            var secondObservedAt = DecodeObservedAt(second.QueryTime);
            if (secondObservedAt is null) return null;

            var elapsed = clock.GetElapsedTime(firstTimestamp, secondTimestamp);
            metrics = WindowsDiskPerformanceProjection.Project(first, second, elapsed) is { } projected
                ? projected with { ObservedAt = secondObservedAt.Value }
                : null;
        }
        finally
        {
            for (var remaining = successfulEnableCalls; remaining > 0; remaining--)
            {
                try { offCallsSucceeded &= calls.PerformanceOff(handle); }
                catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { offCallsSucceeded = false; }
            }
            if (!offCallsSucceeded) metrics = null;
        }
        return metrics;
    }

    private static WindowsDiskPerformanceCounters? DecodePerformance(byte[] buffer, uint returned)
    {
        if (returned > buffer.Length || returned < DiskPerformanceMembersBytes) return null;
        // DISK_PERFORMANCE: five LARGE_INTEGERs, four DWORDs, QueryTime, StorageDeviceNumber, WCHAR[8].
        var manager = Encoding.Unicode.GetString(buffer, 68, 16).TrimEnd('\0', ' ');
        var queryTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(56, 8));
        _ = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64, 4)); // Provider-local number is not an identity join.
        return new(
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(40, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(44, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(48, 4)),
            manager,
            queryTime);
    }

    private static DateTimeOffset? DecodeObservedAt(long queryTime)
    {
        try { return new(DateTime.FromFileTimeUtc(queryTime)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
