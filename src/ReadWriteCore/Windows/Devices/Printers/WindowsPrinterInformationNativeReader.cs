using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Printers;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPrinterInformationNativeReader : IWindowsPrinterInformationReader
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint PrinterInfo4Level = 4;
    private const uint PrinterInfo2Level = 2;
    private const uint DriverInfo6Level = 6;
    private const uint PrinterOptionNoCache = 0x00000001;
    private const string AllDriverEnvironments = "all";

    public WindowsPrinterInformationReadResult Read(WindowsPrinterInformationScope scope,
        WindowsPrinterInformationFields selectedFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (selectedFields == WindowsPrinterInformationFields.None
            || (selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ReadCore(scope, selectedFields, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return FailedDirectory(selectedFields, FailureCode(exception));
        }
    }

    private static WindowsPrinterInformationReadResult ReadCore(WindowsPrinterInformationScope scope,
        WindowsPrinterInformationFields selectedFields, CancellationToken cancellationToken)
    {
        var totalBufferBytes = 0;
        var identities = ReadPrinterDirectory(scope, ref totalBufferBytes, cancellationToken);
        var printers = new List<DeviceTopologyPrinterInformation>(identities.Count);
        var failures = new List<WindowsPrinterInformationReadFailure>();
        var driverCatalogs = new Dictionary<string, DriverCatalog>(StringComparer.Ordinal);

        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrinterDetails details;
            try
            {
                details = ReadPrinterDetails(identity.PrinterName, scope, ref totalBufferBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                printers.Add(new(identity.PrinterName, identity.ServerName, null, null, null));
                failures.Add(new(identity.PrinterName, identity.ServerName, selectedFields, FailureCode(exception)));
                continue;
            }

            string? portName = (selectedFields & WindowsPrinterInformationFields.PortDeclared) != 0
                ? details.PortName
                : null;
            WindowsPrinterQueueStatusFlags? queueState = (selectedFields & WindowsPrinterInformationFields.QueueStateActual) != 0
                ? (WindowsPrinterQueueStatusFlags)details.Status
                : null;
            ulong? driverVersion = null;

            if ((selectedFields & WindowsPrinterInformationFields.DriverVersionDeclared) != 0)
            {
                if (string.IsNullOrEmpty(details.DriverName))
                {
                    failures.Add(new(identity.PrinterName, identity.ServerName,
                        WindowsPrinterInformationFields.DriverVersionDeclared, "driver-name-not-declared"));
                }
                else
                {
                    var server = details.ServerName;
                    var serverKey = ServerKey(server);
                    if (!driverCatalogs.TryGetValue(serverKey, out var catalog))
                    {
                        catalog = ReadDriverCatalog(server, scope, ref totalBufferBytes, cancellationToken);
                        driverCatalogs.Add(serverKey, catalog);
                    }

                    if (!catalog.Succeeded)
                    {
                        failures.Add(new(identity.PrinterName, identity.ServerName,
                            WindowsPrinterInformationFields.DriverVersionDeclared, catalog.FailureCode));
                    }
                    else
                    {
                        var match = MatchDriverVersion(catalog, details.DriverName);
                        driverVersion = match.Version;
                        if (match.FailureCode is not null)
                            failures.Add(new(identity.PrinterName, identity.ServerName,
                                WindowsPrinterInformationFields.DriverVersionDeclared, match.FailureCode));
                    }
                }
            }

            printers.Add(new(identity.PrinterName, identity.ServerName, portName, queueState, driverVersion));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(true, printers, failures);
    }

    private static IReadOnlyList<PrinterIdentity> ReadPrinterDirectory(WindowsPrinterInformationScope scope,
        ref int totalBufferBytes, CancellationToken cancellationToken)
    {
        var flags = scope.EnumerationFlags;
        return ReadBoundedBuffer(
            (buffer, bytes) => QueryPrinters(flags, buffer, bytes),
            scope, ref totalBufferBytes, cancellationToken,
            (buffer, bytes, count) => ParsePrinterInfo4Array(buffer, bytes, count, scope, cancellationToken));
    }

    internal static IReadOnlyList<PrinterIdentity> ParsePrinterInfo4Array(IntPtr buffer, uint bufferBytes,
        uint count, WindowsPrinterInformationScope scope, CancellationToken cancellationToken)
    {
        if (count > scope.MaximumPrinters)
            throw new InvalidDataException("Printer count exceeds the caller's explicit limit.");
        var structureSize = Marshal.SizeOf<NativePrinterInfo4>();
        if ((count != 0 && buffer == IntPtr.Zero) || (ulong)count * (uint)structureSize > bufferBytes)
            throw new InvalidDataException("EnumPrintersW returned a truncated PRINTER_INFO_4 array.");
        var result = new List<PrinterIdentity>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((int)((long)index * structureSize));
            var item = Marshal.PtrToStructure<NativePrinterInfo4>(IntPtr.Add(buffer, offset));
            var printerName = ReadString(item.PrinterName, buffer, bufferBytes, scope.MaximumStringCharacters)
                ?? throw new InvalidDataException("EnumPrintersW returned no printer name.");
            if (printerName.Length == 0)
                throw new InvalidDataException("EnumPrintersW returned an empty printer name.");
            var serverName = ReadString(item.ServerName, buffer, bufferBytes, scope.MaximumStringCharacters);
            result.Add(new(printerName, serverName));
        }
        return result;
    }

    private static PrinterDetails ReadPrinterDetails(string printerName, WindowsPrinterInformationScope scope,
        ref int totalBufferBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new NativePrinterOptions
        {
            CbSize = checked((uint)Marshal.SizeOf<NativePrinterOptions>()),
            Flags = PrinterOptionNoCache
        };
        if (!OpenPrinter2W(printerName, out var printer, IntPtr.Zero, ref options))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter2W failed.");
        if (printer == IntPtr.Zero) throw new InvalidDataException("OpenPrinter2W returned no printer handle.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadBoundedBuffer(
                (buffer, bytes) => QueryPrinter(printer, buffer, bytes),
                scope, ref totalBufferBytes, cancellationToken,
                (buffer, bytes, count) =>
                {
                    if (count != 1) throw new InvalidDataException("GetPrinterW did not return one PRINTER_INFO_2.");
                    return ParsePrinterInfo2(buffer, bytes, scope.MaximumStringCharacters);
                });
        }
        finally
        {
            _ = ClosePrinter(printer);
        }
    }

    internal static PrinterDetails ParsePrinterInfo2(IntPtr buffer, uint bufferBytes, int maximumStringCharacters)
    {
        var requiredBytes = checked((uint)Marshal.SizeOf<NativePrinterInfo2>());
        if (buffer == IntPtr.Zero || bufferBytes < requiredBytes)
            throw new InvalidDataException("GetPrinterW returned a truncated PRINTER_INFO_2 structure.");
        var info = Marshal.PtrToStructure<NativePrinterInfo2>(buffer);
        return new PrinterDetails(
            ReadString(info.PortName, buffer, bufferBytes, maximumStringCharacters),
            ReadString(info.DriverName, buffer, bufferBytes, maximumStringCharacters),
            ReadString(info.ServerName, buffer, bufferBytes, maximumStringCharacters),
            info.Status);
    }

    private static DriverCatalog ReadDriverCatalog(string? serverName, WindowsPrinterInformationScope scope,
        ref int totalBufferBytes, CancellationToken cancellationToken)
    {
        try
        {
            var versions = ReadBoundedBuffer(
                (buffer, bytes) => QueryDrivers(serverName, buffer, bytes),
                scope, ref totalBufferBytes, cancellationToken,
                (buffer, bytes, count) =>
                {
                    if (count > scope.MaximumDriverEntriesPerServer)
                        throw new InvalidDataException("Installed printer driver count exceeds the caller's explicit limit.");
                    var structureSize = Marshal.SizeOf<NativeDriverInfo6>();
                    if ((ulong)count * (uint)structureSize > bytes)
                        throw new InvalidDataException("EnumPrinterDriversW returned a truncated DRIVER_INFO_6 array.");
                    var result = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
                    for (uint index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var offset = checked((int)((long)index * structureSize));
                        var item = Marshal.PtrToStructure<NativeDriverInfo6>(IntPtr.Add(buffer, offset));
                        var name = ReadString(item.Name, buffer, bytes, scope.MaximumStringCharacters);
                        if (string.IsNullOrEmpty(name))
                            throw new InvalidDataException("EnumPrinterDriversW returned a driver without a name.");
                        if (!result.TryGetValue(name, out var versionsForName))
                        {
                            versionsForName = [];
                            result.Add(name, versionsForName);
                        }
                        versionsForName.Add(item.DriverVersion);
                    }
                    return result;
                });
            return new(true, versions, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new(false, new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase),
                FailureCode(exception));
        }
    }

    private static DriverVersionMatch MatchDriverVersion(DriverCatalog catalog, string driverName)
    {
        if (!catalog.Versions.TryGetValue(driverName, out var versions) || versions.Count == 0)
            return new(null, "driver-name-not-found-on-print-server");
        if (versions.Count != 1)
            return new(null, "driver-version-ambiguous-across-environments");
        var version = versions.Single();
        return version == 0
            ? new(null, null)
            : new(version, null);
    }

    internal static T ReadBoundedBuffer<T>(BufferQuery query, WindowsPrinterInformationScope scope,
        ref int totalBufferBytes, CancellationToken cancellationToken,
        Func<IntPtr, uint, uint, T> read)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sizing = query(IntPtr.Zero, 0);
        if (sizing.Success && sizing.Needed == 0 && sizing.Returned == 0)
            return read(IntPtr.Zero, 0, 0);
        if (!sizing.Success && sizing.Error != ErrorInsufficientBuffer)
            throw new Win32Exception(sizing.Error, "Winspool buffer sizing call failed.");
        if (sizing.Needed == 0)
            throw new InvalidDataException("Winspool returned an empty or inconsistent buffer size.");
        if (sizing.Needed > int.MaxValue
            || sizing.Needed > (uint)(scope.MaximumNativeBufferBytes - totalBufferBytes))
            throw new InvalidDataException("Winspool result exceeds the caller's total native buffer limit.");

        var allocationBytes = checked((int)sizing.Needed);
        totalBufferBytes = checked(totalBufferBytes + allocationBytes);
        var buffer = Marshal.AllocHGlobal(allocationBytes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filled = query(buffer, sizing.Needed);
            if (!filled.Success) throw new Win32Exception(filled.Error, "Winspool data query failed.");
            if (filled.Needed > sizing.Needed)
                throw new InvalidDataException("Winspool result grew beyond the bounded sizing call.");
            cancellationToken.ThrowIfCancellationRequested();
            return read(buffer, filled.Needed, filled.Returned);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadString(IntPtr value, IntPtr buffer, uint bufferBytes, int maximumCharacters)
    {
        if (value == IntPtr.Zero) return null;
        var baseAddress = buffer.ToInt64();
        var address = value.ToInt64();
        var endAddress = checked(baseAddress + bufferBytes);
        if (buffer == IntPtr.Zero || address < baseAddress || address > endAddress - sizeof(char)
            || ((address - baseAddress) & 1) != 0)
            throw new InvalidDataException("Winspool returned a string pointer outside its result buffer.");

        var availableCharacters = checked((int)((endAddress - address) / sizeof(char)));
        var characters = new char[Math.Min(maximumCharacters, availableCharacters)];
        for (var index = 0; index <= maximumCharacters; index++)
        {
            if (index >= availableCharacters)
                throw new InvalidDataException("Winspool returned an unterminated string.");
            var character = (char)Marshal.ReadInt16(value, checked(index * sizeof(char)));
            if (character == '\0') return new string(characters, 0, index);
            if (index == maximumCharacters)
                throw new InvalidDataException("Winspool string exceeds the caller's character limit.");
            characters[index] = character;
        }
        throw new InvalidDataException("Winspool returned an unterminated string.");
    }

    private static WindowsPrinterInformationReadResult FailedDirectory(
        WindowsPrinterInformationFields selectedFields, string code)
        => new(false, [], [new(null, null, selectedFields, code)]);

    private static string ServerKey(string? serverName) => serverName is null ? "\0local" : "\u0001" + serverName;

    private static string FailureCode(Exception exception) => exception switch
    {
        Win32Exception => "winspool-call-failed",
        InvalidDataException => "winspool-result-invalid-or-over-limit",
        OverflowException => "winspool-result-overflow",
        DllNotFoundException => "winspool-library-unavailable",
        EntryPointNotFoundException => "winspool-entry-point-unavailable",
        PlatformNotSupportedException => "winspool-platform-unsupported",
        _ => "winspool-read-failed"
    };

    private static bool IsReadFailure(Exception exception)
        => exception is Win32Exception or InvalidDataException or OverflowException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException
            or PlatformNotSupportedException or NotSupportedException or MarshalDirectiveException;

    private static NativeQueryResult QueryPrinters(uint flags, IntPtr buffer, uint bytes)
    {
        var success = EnumPrintersW(flags, null, PrinterInfo4Level, buffer, bytes,
            out var needed, out var returned);
        return new(success, needed, returned, success ? 0 : Marshal.GetLastWin32Error());
    }

    private static NativeQueryResult QueryPrinter(IntPtr printer, IntPtr buffer, uint bytes)
    {
        var success = GetPrinterW(printer, PrinterInfo2Level, buffer, bytes, out var needed);
        return new(success, needed, success ? 1U : 0U, success ? 0 : Marshal.GetLastWin32Error());
    }

    private static NativeQueryResult QueryDrivers(string? serverName, IntPtr buffer, uint bytes)
    {
        var success = EnumPrinterDriversW(serverName, AllDriverEnvironments, DriverInfo6Level,
            buffer, bytes, out var needed, out var returned);
        return new(success, needed, returned, success ? 0 : Marshal.GetLastWin32Error());
    }

    internal delegate NativeQueryResult BufferQuery(IntPtr buffer, uint bytes);

    internal readonly record struct NativeQueryResult(bool Success, uint Needed, uint Returned, int Error);
    internal sealed record PrinterIdentity(string PrinterName, string? ServerName);
    internal sealed record PrinterDetails(string? PortName, string? DriverName, string? ServerName, uint Status);
    private sealed record DriverCatalog(bool Succeeded, Dictionary<string, HashSet<ulong>> Versions, string FailureCode);
    private readonly record struct DriverVersionMatch(ulong? Version, string? FailureCode);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SeparatorFile;
        public IntPtr PrintProcessor;
        public IntPtr DataType;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint JobCount;
        public uint AveragePagesPerMinute;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDriverInfo6
    {
        public uint Version;
        public IntPtr Name;
        public IntPtr Environment;
        public IntPtr DriverPath;
        public IntPtr DataFile;
        public IntPtr ConfigurationFile;
        public IntPtr HelpFile;
        public IntPtr DependentFiles;
        public IntPtr MonitorName;
        public IntPtr DefaultDataType;
        public IntPtr PreviousNames;
        public uint DriverDateLow;
        public uint DriverDateHigh;
        public ulong DriverVersion;
        public IntPtr ManufacturerName;
        public IntPtr OemUrl;
        public IntPtr HardwareId;
        public IntPtr Provider;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePrinterOptions
    {
        public uint CbSize;
        public uint Flags;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level, IntPtr printerEnum,
        uint bufferBytes, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinterW(IntPtr printer, uint level, IntPtr info, uint bufferBytes, out uint needed);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDriversW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinterDriversW(string? serverName, string environment, uint level,
        IntPtr driverInfo, uint bufferBytes, out uint needed, out uint returned);

    [DllImport("spoolss.dll", EntryPoint = "OpenPrinter2W", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter2W(string printerName, out IntPtr printer, IntPtr defaults,
        ref NativePrinterOptions options);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);
}
