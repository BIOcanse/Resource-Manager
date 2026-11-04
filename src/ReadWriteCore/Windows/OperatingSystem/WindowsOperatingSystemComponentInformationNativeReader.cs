using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Explicit read-only DISM inventory for the online image. DISM is
/// initialized lazily and held only while at least one owning reader is alive.</summary>
public sealed class WindowsOperatingSystemComponentInformationNativeReader :
    IWindowsOperatingSystemComponentInformationReader, IDisposable
{
    private const string OnlineImage = "DISM_ONLINE_IMAGE";
    internal const int DismLogErrors = 0;
    private const int DismPackageNone = 0;
    private readonly object gate = new();
    private WindowsDismApiLease? lease;
    private bool disposed;

    public WindowsOperatingSystemComponentInformationReadResult Read(
        WindowsOperatingSystemComponentInformationScope scope,
        WindowsOperatingSystemInformationFields selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        const WindowsOperatingSystemInformationFields componentFields =
            WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages;
        if ((selected & ~componentFields) != 0 || (selected & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selected));
        cancellationToken.ThrowIfCancellationRequested();
        if (selected == WindowsOperatingSystemInformationFields.None || !OperatingSystem.IsWindows())
            return Unavailable();

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                lease ??= WindowsDismApiRuntime.Acquire(cancellationToken);
                return lease.Execute(() => ReadOnlineImage(scope, selected, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                return Unavailable();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lease?.Dispose();
            lease = null;
        }
    }

    internal static IReadOnlyList<WindowsOperatingSystemFeatureState> ParseFeatures(
        IntPtr array, uint count, WindowsOperatingSystemComponentInformationScope scope,
        CancellationToken cancellationToken = default)
    {
        if (count > scope.MaximumFeatures || count > int.MaxValue || (count > 0 && array == IntPtr.Zero))
            throw new InvalidDataException("DismGetFeatures returned an invalid or over-limit array.");
        var result = new List<WindowsOperatingSystemFeatureState>(checked((int)count));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = 0;
        var structureSize = Marshal.SizeOf<NativeDismFeature>();
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((int)((long)index * structureSize));
            var item = Marshal.PtrToStructure<NativeDismFeature>(IntPtr.Add(array, offset));
            var name = ReadString(item.FeatureName, scope.MaximumStringCharacters);
            AddName(name, names, ref characters, scope);
            result.Add(new(name, ReadState(item.State)));
        }
        return result;
    }

    internal static IReadOnlyList<WindowsOperatingSystemPackageState> ParsePackages(
        IntPtr array, uint count, WindowsOperatingSystemComponentInformationScope scope,
        CancellationToken cancellationToken = default)
    {
        if (count > scope.MaximumPackages || count > int.MaxValue || (count > 0 && array == IntPtr.Zero))
            throw new InvalidDataException("DismGetPackages returned an invalid or over-limit array.");
        var result = new List<WindowsOperatingSystemPackageState>(checked((int)count));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = 0;
        var structureSize = Marshal.SizeOf<NativeDismPackage>();
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((int)((long)index * structureSize));
            var item = Marshal.PtrToStructure<NativeDismPackage>(IntPtr.Add(array, offset));
            var name = ReadString(item.PackageName, scope.MaximumStringCharacters);
            AddName(name, names, ref characters, scope);
            result.Add(new(name, ReadState(item.PackageState)));
        }
        return result;
    }

    internal static IReadOnlyList<WindowsOperatingSystemDriverPackageIdentity> ParseDriverPackages(
        IntPtr array, uint count, WindowsOperatingSystemComponentInformationScope scope,
        CancellationToken cancellationToken = default)
    {
        if (count > scope.MaximumDriverPackages || count > int.MaxValue || (count > 0 && array == IntPtr.Zero))
            throw new InvalidDataException("DismGetDrivers returned an invalid or over-limit array.");
        var result = new List<WindowsOperatingSystemDriverPackageIdentity>(checked((int)count));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = 0;
        var structureSize = Marshal.SizeOf<NativeDismDriverPackage>();
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((int)((long)index * structureSize));
            var item = Marshal.PtrToStructure<NativeDismDriverPackage>(IntPtr.Add(array, offset));
            var name = ReadString(item.PublishedName, scope.MaximumStringCharacters);
            AddName(name, names, ref characters, scope);
            result.Add(new(name));
        }
        return result;
    }

    private static WindowsOperatingSystemComponentInformationReadResult ReadOnlineImage(
        WindowsOperatingSystemComponentInformationScope scope,
        WindowsOperatingSystemInformationFields selected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(DismOpenSession(OnlineImage, null, null, out var session), "DismOpenSession");
        IReadOnlyList<WindowsOperatingSystemFeatureState>? features = null;
        IReadOnlyList<WindowsOperatingSystemPackageState>? packages = null;
        IReadOnlyList<WindowsOperatingSystemDriverPackageIdentity>? drivers = null;
        try
        {
            if ((selected & WindowsOperatingSystemInformationFields.Features) != 0)
                features = ReadFeatures(session, scope, cancellationToken);
            if ((selected & WindowsOperatingSystemInformationFields.ComponentPackages) != 0)
                packages = ReadPackages(session, scope, cancellationToken);
            if ((selected & WindowsOperatingSystemInformationFields.DriverPackages) != 0)
                drivers = ReadDriverPackages(session, scope, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            EnsureSuccess(DismCloseSession(session), "DismCloseSession");
        }
        return new(features, packages, drivers);
    }

    private static IReadOnlyList<WindowsOperatingSystemFeatureState>? ReadFeatures(uint session,
        WindowsOperatingSystemComponentInformationScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hresult = DismGetFeatures(session, null, DismPackageNone, out var array, out var count);
        if (hresult < 0) return null;
        try { return ParseFeatures(array, count, scope, token); }
        catch (InvalidDataException) { return null; }
        finally { DeleteReturnedArray(array); }
    }

    private static IReadOnlyList<WindowsOperatingSystemPackageState>? ReadPackages(uint session,
        WindowsOperatingSystemComponentInformationScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hresult = DismGetPackages(session, out var array, out var count);
        if (hresult < 0) return null;
        try { return ParsePackages(array, count, scope, token); }
        catch (InvalidDataException) { return null; }
        finally { DeleteReturnedArray(array); }
    }

    private static IReadOnlyList<WindowsOperatingSystemDriverPackageIdentity>? ReadDriverPackages(uint session,
        WindowsOperatingSystemComponentInformationScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hresult = DismGetDrivers(session, true, out var array, out var count);
        if (hresult < 0) return null;
        try { return ParseDriverPackages(array, count, scope, token); }
        catch (InvalidDataException) { return null; }
        finally { DeleteReturnedArray(array); }
    }

    private static void DeleteReturnedArray(IntPtr array)
    {
        if (array != IntPtr.Zero) EnsureSuccess(DismDelete(array), "DismDelete");
    }

    private static void AddName(string name, HashSet<string> names, ref int characters,
        WindowsOperatingSystemComponentInformationScope scope)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > scope.MaximumStringCharacters
            || !names.Add(name))
            throw new InvalidDataException("DISM returned a missing, oversized or duplicate component identity.");
        characters = checked(characters + name.Length);
        if (characters > scope.MaximumCharactersPerCategory)
            throw new InvalidDataException("DISM returned more text than the caller's category limit.");
    }

    private static string ReadString(IntPtr value, int maximumCharacters)
    {
        if (value == IntPtr.Zero) throw new InvalidDataException("DISM returned a null component identity.");
        for (var index = 0; index <= maximumCharacters; index++)
        {
            var character = (char)Marshal.ReadInt16(value, checked(index * sizeof(char)));
            if (character == '\0')
                return Marshal.PtrToStringUni(value, index)
                    ?? throw new InvalidDataException("DISM returned a missing component identity.");
            if (index == maximumCharacters)
                throw new InvalidDataException("DISM returned an unterminated or over-limit component identity.");
        }
        throw new InvalidDataException("DISM returned an unterminated component identity.");
    }

    private static void EnsureSuccess(int hresult, string function)
    {
        if (hresult < 0) throw new DismApiException(function, hresult);
    }

    internal static void EnsureDismInitialized(int logLevel)
        => EnsureSuccess(DismInitializeNative(logLevel, null, null), "DismInitialize");

    internal static int ShutdownDism() => DismShutdownNative();

    private static uint ReadState(int state)
        => state >= 0 ? (uint)state
            : throw new InvalidDataException("DISM returned a negative state enum value.");

    private static bool IsReadFailure(Exception exception)
        => exception is DismApiException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or PlatformNotSupportedException or UnauthorizedAccessException
            or System.Security.SecurityException or MarshalDirectiveException or InvalidDataException;

    private static WindowsOperatingSystemComponentInformationReadResult Unavailable() => new(null, null, null);

    private sealed class DismApiException(string function, int hresult)
        : Exception($"{function} failed with HRESULT 0x{unchecked((uint)hresult):X8}.") { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDismFeature
    {
        public IntPtr FeatureName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDismPackage
    {
        public IntPtr PackageName;
        public int PackageState;
        public int ReleaseType;
        public NativeSystemTime InstallTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDismDriverPackage
    {
        public IntPtr PublishedName;
        public IntPtr OriginalFileName;
        public int InBox;
        public IntPtr CatalogFile;
        public IntPtr ClassName;
        public IntPtr ClassGuid;
        public IntPtr ClassDescription;
        public int BootCritical;
        public int DriverSignature;
        public IntPtr ProviderName;
        public NativeSystemTime Date;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint Build;
        public uint Revision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("DismApi.dll", EntryPoint = "DismOpenSession", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int DismOpenSession(string imagePath, string? windowsDirectory,
        string? systemDrive, out uint session);

    [DllImport("DismApi.dll", EntryPoint = "DismGetFeatures", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int DismGetFeatures(uint session, string? identifier, int packageIdentifier,
        out IntPtr features, out uint count);

    [DllImport("DismApi.dll", EntryPoint = "DismGetPackages", ExactSpelling = true)]
    private static extern int DismGetPackages(uint session, out IntPtr packages, out uint count);

    [DllImport("DismApi.dll", EntryPoint = "DismGetDrivers", ExactSpelling = true)]
    private static extern int DismGetDrivers(uint session, [MarshalAs(UnmanagedType.Bool)] bool allDrivers,
        out IntPtr driverPackages, out uint count);

    [DllImport("DismApi.dll", EntryPoint = "DismCloseSession", ExactSpelling = true)]
    private static extern int DismCloseSession(uint session);

    [DllImport("DismApi.dll", EntryPoint = "DismDelete", ExactSpelling = true)]
    private static extern int DismDelete(IntPtr dismStructure);

    [DllImport("DismApi.dll", EntryPoint = "DismInitialize", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int DismInitializeNative(int logLevel, string? logFilePath, string? scratchDirectory);

    [DllImport("DismApi.dll", EntryPoint = "DismShutdown", ExactSpelling = true)]
    private static extern int DismShutdownNative();
}

internal sealed class WindowsDismApiLease : IDisposable
{
    private int disposed;

    internal T Execute<T>(Func<T> action, CancellationToken cancellationToken)
        => WindowsDismApiRuntime.Execute(this, action, cancellationToken);

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal bool MarkDisposed() => Interlocked.Exchange(ref disposed, 1) == 0;

    public void Dispose() => WindowsDismApiRuntime.Release(this);
}

/// <summary>Only the DISM API's required process-global initialization lifetime;
/// it owns no inventory/current state and serializes all native calls.</summary>
internal static class WindowsDismApiRuntime
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static int leaseCount;
    private static bool initialized;

    internal static WindowsDismApiLease Acquire(CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        try
        {
            if (!initialized)
            {
                WindowsOperatingSystemComponentInformationNativeReader.EnsureDismInitialized(
                    WindowsOperatingSystemComponentInformationNativeReader.DismLogErrors);
                initialized = true;
            }
            leaseCount = checked(leaseCount + 1);
            return new WindowsDismApiLease();
        }
        finally { gate.Release(); }
    }

    internal static T Execute<T>(WindowsDismApiLease lease, Func<T> action, CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialized || lease.IsDisposed) throw new ObjectDisposedException(nameof(WindowsDismApiLease));
            return action();
        }
        finally { gate.Release(); }
    }

    internal static void Release(WindowsDismApiLease lease)
    {
        if (!lease.MarkDisposed()) return;
        gate.Wait();
        try
        {
            if (leaseCount <= 0) return;
            leaseCount--;
            if (leaseCount == 0 && initialized)
            {
                try
                {
                    if (WindowsOperatingSystemComponentInformationNativeReader.ShutdownDism() >= 0)
                        initialized = false;
                }
                catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                    or BadImageFormatException or PlatformNotSupportedException)
                {
                    // Keep initialized set so a future owner can reuse and retry shutdown.
                }
            }
        }
        finally { gate.Release(); }
    }
}
