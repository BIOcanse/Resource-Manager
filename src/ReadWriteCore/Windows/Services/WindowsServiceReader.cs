using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Services;

[Flags]
public enum WindowsServiceScope : uint { Win32 = 0x30, Drivers = 0x0B, Win32AndDrivers = 0x3B }

/// <summary>SCM enumeration facts, not process-instance verification. Zero PID
/// means no reported process; configuration is null unless explicitly queried.</summary>
public sealed record WindowsServiceRecord(string ServiceName, string DisplayName,
    uint ServiceType, uint State, uint ProcessId, uint? StartType);
public sealed record WindowsServiceCapture(bool Available, IReadOnlyList<WindowsServiceRecord> Services, int? ErrorCode = null);

/// <summary>The original two-call SCM enumeration and per-call native resources.
/// No session/cache, service control, installation or hidden retries.</summary>
public sealed class WindowsServiceReader
{
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private readonly IWindowsServiceNativeApi native;
    public WindowsServiceReader() : this(new WindowsServiceNativeApi()) { }
    internal WindowsServiceReader(IWindowsServiceNativeApi native) => this.native = native;

    public WindowsServiceCapture Read(WindowsServiceScope scope = WindowsServiceScope.Win32,
        bool includeConfiguration = false, CancellationToken cancellationToken = default,
        IReadOnlySet<string>? configurationServiceNames = null)
    {
        if (scope is not WindowsServiceScope.Win32 and not WindowsServiceScope.Drivers and not WindowsServiceScope.Win32AndDrivers)
            throw new ArgumentOutOfRangeException(nameof(scope));
        cancellationToken.ThrowIfCancellationRequested();
        var configurationNames = configurationServiceNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manager = native.OpenManager(includeConfiguration ? 0x0005u : 0x0004u);
        if (Invalid(manager)) return new(false, [], native.LastError);
        try
        {
            uint resume = 0;
            var sized = native.Enumerate(manager, (uint)scope, IntPtr.Zero, 0, out var bytes, out var count, ref resume);
            if (sized && count == 0 && bytes == 0) return new(true, []);
            if (bytes == 0 || native.LastError != ErrorMoreData) return new(false, [], native.LastError);
            var buffer = native.Allocate(checked((int)bytes));
            try
            {
                resume = 0;
                if (!native.Enumerate(manager, (uint)scope, buffer, bytes, out _, out count, ref resume))
                    return new(false, [], native.LastError);
                var records = new List<WindowsServiceRecord>();
                var entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();
                if ((ulong)count * (ulong)entrySize > bytes) return new(false, [], 13);
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(IntPtr.Add(buffer, checked(index * entrySize)));
                    if (string.IsNullOrWhiteSpace(entry.ServiceName)) continue;
                    records.Add(new(entry.ServiceName, entry.DisplayName, entry.Status.ServiceType,
                        entry.Status.CurrentState, entry.Status.ProcessId,
                        includeConfiguration && (configurationNames is null || configurationNames.Contains(entry.ServiceName))
                            ? ReadStartType(manager, entry.ServiceName, cancellationToken) : null));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(true, records);
            }
            finally { native.Free(buffer); }
        }
        catch (Exception error) when (error is Win32Exception or OutOfMemoryException or ArgumentException or OverflowException)
        { return new(false, [], native.LastError); }
        finally { native.Close(manager); }
    }

    private uint? ReadStartType(IntPtr manager, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = native.OpenService(manager, name, 0x0001);
        if (Invalid(service)) return null;
        try
        {
            _ = native.QueryConfiguration(service, IntPtr.Zero, 0, out var bytes);
            if (bytes < (uint)Marshal.SizeOf<QueryServiceConfig>() || native.LastError != ErrorInsufficientBuffer) return null;
            var buffer = native.Allocate(checked((int)bytes));
            try
            {
                if (!native.QueryConfiguration(service, buffer, bytes, out _)) return null;
                cancellationToken.ThrowIfCancellationRequested();
                var start = Marshal.PtrToStructure<QueryServiceConfig>(buffer).StartType;
                return start <= 4 ? start : null;
            }
            finally { native.Free(buffer); }
        }
        catch (Exception error) when (error is Win32Exception or OutOfMemoryException or ArgumentException or OverflowException)
        { return null; }
        finally { native.Close(service); }
    }
    private static bool Invalid(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1);
}

internal interface IWindowsServiceNativeApi
{
    int LastError { get; }
    IntPtr OpenManager(uint access);
    bool Enumerate(IntPtr manager, uint type, IntPtr buffer, uint size, out uint bytes, out uint count, ref uint resume);
    IntPtr OpenService(IntPtr manager, string name, uint access);
    bool QueryConfiguration(IntPtr service, IntPtr buffer, uint size, out uint bytes);
    IntPtr Allocate(int size);
    void Free(IntPtr buffer);
    void Close(IntPtr handle);
}

