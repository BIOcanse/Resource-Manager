using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Mechrevo;

/// <summary>The official UWACPI ReadEC interface. Construction does not load a library or read the EC.</summary>
public sealed class MechrevoUwAcpiReader : IDisposable
{
    // Fixed addresses and PWM encoding belong to the OEM protocol.
    private const int CpuFanRpmHighAddress = 1124;
    private const int CpuFanRpmLowAddress = 1125;
    private const int GpuFanRpmHighAddress = 1132;
    private const int GpuFanRpmLowAddress = 1131;
    private const int CpuFanDutyAddress = 1883;
    private const int GpuFanDutyAddress = 1884;
    private readonly object gate = new();
    private readonly Func<string, IUwAcpiReadSession?> openSession;
    private IUwAcpiReadSession? session;
    private bool openAttempted;
    private bool disposed;

    public MechrevoUwAcpiReader(string libraryPath, Func<string, IUwAcpiReadSession?>? openSession = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        LibraryPath = libraryPath;
        this.openSession = openSession ?? TryOpenNativeSession;
    }

    public string LibraryPath { get; }

    /// <summary>Explicitly reads the original fan addresses. Loading and failure state are owned by this instance.</summary>
    public MechrevoUwAcpiReadResult Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!openAttempted)
            {
                session = openSession(LibraryPath);
                openAttempted = true;
            }
            if (session is null)
            {
                return new(MechrevoUwAcpiReadStatus.LibraryUnavailable, null, null, null, null);
            }

            try
            {
                var cpuRpm = ReadRpm(session, CpuFanRpmHighAddress, CpuFanRpmLowAddress);
                var gpuRpm = ReadRpm(session, GpuFanRpmHighAddress, GpuFanRpmLowAddress);
                var cpuPercent = DecodeDutyPercent(ReadByte(session, CpuFanDutyAddress));
                var gpuPercent = DecodeDutyPercent(ReadByte(session, GpuFanDutyAddress));
                var hasValue = cpuRpm is not null || gpuRpm is not null
                    || cpuPercent is not null || gpuPercent is not null;
                return new(hasValue ? MechrevoUwAcpiReadStatus.Active : MechrevoUwAcpiReadStatus.NoData,
                    cpuRpm, cpuPercent, gpuRpm, gpuPercent);
            }
            catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException
                or EntryPointNotFoundException or SEHException or AccessViolationException)
            {
                return new(MechrevoUwAcpiReadStatus.ReadFailed, null, null, null, null, ex.Message);
            }
        }
    }

    /// <summary>Loads the specified DLL, checks ReadEC, and releases it without calling ReadEC.</summary>
    public static bool ProbeLibrary(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        using var probe = TryOpenNativeSession(libraryPath);
        return probe is not null;
    }

    // OEM ReadEC reports PWM on a fixed 0..200 scale, including values below 100.
    public static double? DecodeDutyPercent(int? raw)
        => raw is >= 0 and <= 200 ? raw.Value / 2d : null;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            var ownedSession = session;
            session = null;
            ownedSession?.Dispose();
        }
    }

    private static double? ReadRpm(IUwAcpiReadSession session, int highAddress, int lowAddress)
    {
        var high = ReadByte(session, highAddress);
        var low = ReadByte(session, lowAddress);
        if (high is null || low is null) return null;
        var rpm = (high.Value << 8) | low.Value;
        return rpm is >= 0 and < 20000 ? rpm : null;
    }

    private static int? ReadByte(IUwAcpiReadSession session, int address)
    {
        var value = session.ReadEc(address);
        return value is >= 0 and <= 255 ? value : null;
    }

    private static IUwAcpiReadSession? TryOpenNativeSession(string libraryPath)
    {
        var handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(libraryPath, out handle)) return null;
            if (!NativeLibrary.TryGetExport(handle, "ReadEC", out var export)) return null;
            var reader = Marshal.GetDelegateForFunctionPointer<ReadEcDelegate>(export);
            var loadedSession = new NativeReadSession(handle, reader);
            handle = IntPtr.Zero;
            return loadedSession;
        }
        catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadEcDelegate(int address);

    private sealed class NativeReadSession(IntPtr libraryHandle, ReadEcDelegate reader) : IUwAcpiReadSession
    {
        private IntPtr handle = libraryHandle;
        public int ReadEc(int address) => reader(address);
        public void Dispose()
        {
            var ownedHandle = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (ownedHandle != IntPtr.Zero) NativeLibrary.Free(ownedHandle);
        }
    }
}
