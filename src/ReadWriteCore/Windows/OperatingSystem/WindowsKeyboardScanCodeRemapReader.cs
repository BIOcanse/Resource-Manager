using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>One stored scan-code remap record, preserving both 16-bit words verbatim.</summary>
public readonly record struct WindowsKeyboardScanCodeRemapEntry(ushort SourceScanCode, ushort TargetScanCode);

/// <summary>The stored registry map only; it does not indicate that Windows or another hook applied it.</summary>
public sealed record WindowsKeyboardScanCodeRemapReading(
    bool Configured, ImmutableArray<WindowsKeyboardScanCodeRemapEntry> Entries);

/// <summary>Typed boundary for the selected machine Scancode Map registry read.</summary>
public interface IWindowsKeyboardScanCodeRemapReader
{
    WindowsKeyboardScanCodeRemapReading? Read();
}

/// <summary>Reads only HKLM Keyboard Layout\Scancode Map through the native registry view.</summary>
public sealed class WindowsKeyboardScanCodeRemapReader : IWindowsKeyboardScanCodeRemapReader
{
    private const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
    private const string RegistryValueName = "Scancode Map";
    private const int RecordStartOffset = 12;
    private const int MinimumValueLength = RecordStartOffset + sizeof(uint);
    private const uint RegistryTypeBinary = 3; // REG_BINARY
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;

    /// <summary>Default allocation policy for this reader; callers may choose a different bounded value size.</summary>
    public const int DefaultMaximumValueLength = 262_156;

    private readonly int maximumValueLength;

    public WindowsKeyboardScanCodeRemapReader(int maximumValueLength = DefaultMaximumValueLength)
    {
        if (maximumValueLength < MinimumValueLength || maximumValueLength > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maximumValueLength));
        this.maximumValueLength = maximumValueLength;
    }

    public WindowsKeyboardScanCodeRemapReading? Read()
        => OperatingSystem.IsWindows() ? ReadWindows(maximumValueLength) : null;

    [SupportedOSPlatform("windows")]
    private static WindowsKeyboardScanCodeRemapReading? ReadWindows(int maximumValueLength)
    {
        try
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(RegistrySubKey, writable: false);
            if (key is null) return NotConfigured();

            RegistryValueQuery query = (byte[]? buffer, out uint type, ref uint byteCount) =>
                RegQueryValueExW(key.Handle, RegistryValueName, IntPtr.Zero, out type, buffer, ref byteCount);
            var result = ReadBoundedBinaryValue(query, maximumValueLength, out var bytes);
            if (result == BoundedValueReadStatus.NotFound) return NotConfigured();
            if (result != BoundedValueReadStatus.Success || bytes is null) return null;
            return TryParse(bytes, maximumValueLength, out var reading) ? reading : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Parses the bounded little-endian value without consulting live keyboard state.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out WindowsKeyboardScanCodeRemapReading? reading)
        => TryParse(bytes, DefaultMaximumValueLength, out reading);

    internal static bool TryParse(ReadOnlySpan<byte> bytes, int maximumValueLength,
        out WindowsKeyboardScanCodeRemapReading? reading)
    {
        reading = null;
        if (maximumValueLength < MinimumValueLength || bytes.Length < MinimumValueLength
            || bytes.Length > maximumValueLength)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[..sizeof(uint)]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(sizeof(uint), sizeof(uint))) != 0)
            return false;

        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(2 * sizeof(uint), sizeof(uint)));
        if (recordCount == 0 || RecordStartOffset + (long)recordCount * sizeof(uint) != bytes.Length) return false;

        var mappingCount = checked((int)recordCount - 1);
        var endingRecordOffset = RecordStartOffset + mappingCount * sizeof(uint);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(endingRecordOffset, sizeof(uint))) != 0) return false;

        var entries = ImmutableArray.CreateBuilder<WindowsKeyboardScanCodeRemapEntry>(mappingCount);
        for (var index = 0; index < mappingCount; index++)
        {
            var offset = RecordStartOffset + index * sizeof(uint);
            var targetScanCode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, sizeof(ushort)));
            var sourceScanCode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + sizeof(ushort), sizeof(ushort)));
            entries.Add(new(sourceScanCode, targetScanCode));
        }

        reading = new(true, entries.MoveToImmutable());
        return true;
    }

    internal enum BoundedValueReadStatus { Success, NotFound, Unavailable }

    internal delegate int RegistryValueQuery(byte[]? buffer, out uint type, ref uint byteCount);

    /// <summary>Queries size/type before allocation, then accepts only an exact-size, same-type fill.</summary>
    internal static BoundedValueReadStatus ReadBoundedBinaryValue(RegistryValueQuery query,
        int maximumValueLength, out byte[]? bytes)
    {
        bytes = null;
        if (maximumValueLength < MinimumValueLength || maximumValueLength > Array.MaxLength)
            return BoundedValueReadStatus.Unavailable;

        uint requiredLength = 0;
        var status = query(null, out var type, ref requiredLength);
        if (status == ErrorFileNotFound) return BoundedValueReadStatus.NotFound;
        if (status != ErrorSuccess || type != RegistryTypeBinary || requiredLength < MinimumValueLength
            || requiredLength > (uint)maximumValueLength)
            return BoundedValueReadStatus.Unavailable;

        var data = new byte[checked((int)requiredLength)];
        var actualLength = requiredLength;
        status = query(data, out var actualType, ref actualLength);
        if (status != ErrorSuccess || actualType != RegistryTypeBinary || actualLength != requiredLength)
            return BoundedValueReadStatus.Unavailable;

        bytes = data;
        return BoundedValueReadStatus.Success;
    }

    private static WindowsKeyboardScanCodeRemapReading NotConfigured()
        => new(false, ImmutableArray<WindowsKeyboardScanCodeRemapEntry>.Empty);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string valueName, IntPtr reserved,
        out uint type, [Out] byte[]? data, ref uint dataLength);
}
