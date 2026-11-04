using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Read-only desktop OpenGL queries against caller-borrowed WGL contexts. Never creates or changes contexts.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsOpenGlCapabilityNativeReader : IWindowsOpenGlCapabilityReader
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint GlVersion = 0x1F02;
    private const uint GlExtensions = 0x1F03;
    private const uint GlNumExtensions = 0x821D;
    private const uint GlDeviceLuidExt = 0x9599;
    private const uint GlDeviceNodeMaskExt = 0x959A;
    private const int GlLuidSizeExt = 8;
    private const string MemoryObjectExtension = "GL_EXT_memory_object";
    private const string MemoryObjectWin32Extension = "GL_EXT_memory_object_win32";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IWindowsOpenGlCurrentContextExecutor contextExecutor;

    internal WindowsOpenGlCapabilityNativeReader(IWindowsOpenGlCurrentContextExecutor contextExecutor)
        => this.contextExecutor = contextExecutor ?? throw new ArgumentNullException(nameof(contextExecutor));

    public WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
        WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (selectedFields == WindowsOpenGlCapabilityFields.None || (selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return WindowsOpenGlCapabilityReadResult.Unavailable;

        var module = LoadLibraryExW("opengl32.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero) return WindowsOpenGlCapabilityReadResult.Unavailable;
        try
        {
            var getString = Marshal.GetDelegateForFunctionPointer<GlGetStringDelegate>(
                NativeLibrary.GetExport(module, "glGetString"));
            var getIntegerAddress = NativeLibrary.GetExport(module, "glGetIntegerv");
            var getInteger = Marshal.GetDelegateForFunctionPointer<GlGetIntegervDelegate>(getIntegerAddress);
            var getIntegerForLimits = (selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0
                ? Marshal.GetDelegateForFunctionPointer<GlGetIntegervLimitDelegate>(getIntegerAddress)
                : null;
            var getFloat = Marshal.GetDelegateForFunctionPointer<GlGetFloatvDelegate>(
                NativeLibrary.GetExport(module, "glGetFloatv"));
            var getProcAddress = Marshal.GetDelegateForFunctionPointer<WglGetProcAddressDelegate>(
                NativeLibrary.GetExport(module, "wglGetProcAddress"));

            var rows = ImmutableArray.CreateBuilder<WindowsOpenGlAdapterCapabilityRead>(scope.Targets.Length);
            foreach (var target in scope.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WindowsOpenGlAdapterCapabilityRead row;
                try
                {
                    row = contextExecutor.Execute(target,
                        () => ReadCurrentContext(target, scope, selectedFields, getString, getInteger,
                            getIntegerForLimits, getFloat, getProcAddress, cancellationToken), cancellationToken)
                        ?? Unavailable(target, selectedFields);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    row = Unavailable(target, selectedFields);
                }
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(row);
            }
            return new(true, rows.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WindowsOpenGlCapabilityReadResult.Unavailable;
        }
        finally { _ = FreeLibrary(module); }
    }

    private static WindowsOpenGlAdapterCapabilityRead ReadCurrentContext(
        WindowsOpenGlAdapterTarget target, WindowsOpenGlCapabilityScope scope,
        WindowsOpenGlCapabilityFields selectedFields, GlGetStringDelegate getString,
        GlGetIntegervDelegate getInteger, GlGetIntegervLimitDelegate? getIntegerForLimits,
        GlGetFloatvDelegate getFloat,
        WglGetProcAddressDelegate getProcAddress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = ReadNativeText(getString(GlVersion), scope.MaximumTextBytes, out _);
        if (version is null || !TryParseVersion(version, out var major, out var minor))
            return Unavailable(target, selectedFields);

        ImmutableArray<string> extensions;
        try
        {
            extensions = ReadExtensions(major, scope, getString, getInteger, getProcAddress, cancellationToken);
        }
        catch (OpenGlDataException) { return Unavailable(target, selectedFields); }

        // The Win32 extension defines the exact current-context LUID/node query. It depends on memory objects,
        // whose extension defines glGetUnsignedBytevEXT. Without both advertised tokens identity is unproven.
        if (!extensions.Contains(MemoryObjectExtension, StringComparer.Ordinal)
            || !extensions.Contains(MemoryObjectWin32Extension, StringComparer.Ordinal))
            return Unavailable(target, selectedFields);

        cancellationToken.ThrowIfCancellationRequested();
        var getUnsignedByte = GetExtensionDelegate<GlGetUnsignedBytevDelegate>(
            getProcAddress, "glGetUnsignedBytevEXT");
        if (getUnsignedByte is null) return Unavailable(target, selectedFields);

        var luidBytes = new byte[GlLuidSizeExt];
        fixed (byte* pLuid = luidBytes) getUnsignedByte(GlDeviceLuidExt, pLuid);
        getInteger(GlDeviceNodeMaskExt, out var signedNodeMask);
        cancellationToken.ThrowIfCancellationRequested();

        var luid = BinaryPrimitives.ReadUInt64LittleEndian(luidBytes);
        var nodeMask = unchecked((uint)signedNodeMask);
        if (luid == 0 || nodeMask == 0 || luid != target.Adapter.Luid || nodeMask != target.NodeMask)
            return new(target, luid, nodeMask, null, null, UnavailableLimitValues(selectedFields));

        var limits = ReadLimits(selectedFields, major, minor, extensions, getIntegerForLimits, getFloat,
            getProcAddress, cancellationToken);
        return new(target, luid, nodeMask,
            (selectedFields & WindowsOpenGlCapabilityFields.Version) != 0 ? version : null,
            (selectedFields & WindowsOpenGlCapabilityFields.Extensions) != 0 ? extensions : null,
            limits.Count == 0 ? null : limits);
    }

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> ReadLimits(
        WindowsOpenGlCapabilityFields selectedFields, int major, int minor,
        ImmutableArray<string> extensions, GlGetIntegervLimitDelegate? getInteger,
        GlGetFloatvDelegate getFloat, WglGetProcAddressDelegate getProcAddress,
        CancellationToken cancellationToken)
    {
        var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
        GlGetInteger64vDelegate? getInteger64 = null;
        var attemptedInteger64Lookup = false;
        foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
        {
            if ((selectedFields & spec.Field) == 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsOpenGlCoreLimitsCatalog.IsSupported(spec.Limit, major, minor, extensions))
            {
                values.Add(spec.Limit, new(WindowsOpenGlLimitReadStatus.Unsupported));
                continue;
            }
            if (getInteger is null)
            {
                values.Add(spec.Limit, new(WindowsOpenGlLimitReadStatus.Unavailable));
                continue;
            }

            WindowsOpenGlLimitRead value;
            switch (spec.ValueKind)
            {
                case WindowsOpenGlLimitValueKind.Unsigned32:
                    var unsigned32 = -1;
                    getInteger(spec.Token, ref unsigned32);
                    value = unsigned32 < 0
                        ? new(WindowsOpenGlLimitReadStatus.Unavailable)
                        : new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: (uint)unsigned32);
                    break;
                case WindowsOpenGlLimitValueKind.Unsigned64:
                    if (!attemptedInteger64Lookup)
                    {
                        getInteger64 = GetExtensionDelegate<GlGetInteger64vDelegate>(getProcAddress, "glGetInteger64v");
                        attemptedInteger64Lookup = true;
                    }
                    if (getInteger64 is null)
                    {
                        value = new(WindowsOpenGlLimitReadStatus.Unavailable);
                        break;
                    }
                    var unsigned64 = -1L;
                    getInteger64(spec.Token, ref unsigned64);
                    value = unsigned64 < 0
                        ? new(WindowsOpenGlLimitReadStatus.Unavailable)
                        : new(WindowsOpenGlLimitReadStatus.Queried, Unsigned64Value: (ulong)unsigned64);
                    break;
                case WindowsOpenGlLimitValueKind.Number:
                    var number = float.NaN;
                    getFloat(spec.Token, ref number);
                    value = float.IsFinite(number) && number >= 0
                        ? new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: number)
                        : new(WindowsOpenGlLimitReadStatus.Unavailable);
                    break;
                default:
                    throw new InvalidOperationException("Unknown OpenGL limit value type.");
            }
            values.Add(spec.Limit, value);
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<string> ReadExtensions(int major, WindowsOpenGlCapabilityScope scope,
        GlGetStringDelegate getString, GlGetIntegervDelegate getInteger,
        WglGetProcAddressDelegate getProcAddress, CancellationToken cancellationToken)
    {
        var extensions = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var aggregateBytes = 0;
        if (major >= 3)
        {
            var getStringi = GetExtensionDelegate<GlGetStringiDelegate>(getProcAddress, "glGetStringi");
            if (getStringi is null) throw new OpenGlDataException();
            getInteger(GlNumExtensions, out var count);
            if (count < 0 || count > scope.MaximumExtensionCount) throw new OpenGlDataException();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ReadNativeText(getStringi(GlExtensions, checked((uint)index)),
                    scope.MaximumTextBytes, out var textBytes);
                aggregateBytes = checked(aggregateBytes + textBytes + 1);
                if (aggregateBytes > scope.MaximumAggregateTextBytes) throw new OpenGlDataException();
                if (!IsExtensionName(name) || !unique.Add(name!)) throw new OpenGlDataException();
                extensions.Add(name!);
            }
            return extensions.ToImmutable();
        }

        var extensionText = ReadNativeText(getString(GlExtensions), scope.MaximumTextBytes, out var legacyTextBytes);
        if (legacyTextBytes > scope.MaximumAggregateTextBytes) throw new OpenGlDataException();
        if (extensionText is null) throw new OpenGlDataException();
        foreach (var name in extensionText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (extensions.Count == scope.MaximumExtensionCount
                || !IsExtensionName(name) || !unique.Add(name)) throw new OpenGlDataException();
            extensions.Add(name);
        }
        return extensions.ToImmutable();
    }

    private static string? ReadNativeText(IntPtr pointer, int maximumBytes, out int byteCount)
    {
        byteCount = 0;
        if (pointer == IntPtr.Zero) return null;
        var length = 0;
        for (var index = 0; index <= maximumBytes; index++)
        {
            var value = Marshal.ReadByte(pointer, index);
            if (value == 0)
            {
                length = index;
                break;
            }
            if (index == maximumBytes) return null;
        }
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
        byteCount = length;
        try { return StrictUtf8.GetString(bytes, 0, length); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool TryParseVersion(string version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (version.StartsWith("OpenGL ES", StringComparison.Ordinal)) return false;
        var dot = version.IndexOf('.');
        if (dot < 1 || dot == version.Length - 1
            || !int.TryParse(version.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out major)) return false;
        var end = dot + 1;
        while (end < version.Length && char.IsAsciiDigit(version[end])) end++;
        if (end == dot + 1 || !int.TryParse(version.AsSpan(dot + 1, end - dot - 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out minor)) return false;
        if (end < version.Length && version[end] == '.')
        {
            var releaseStart = ++end;
            while (end < version.Length && char.IsAsciiDigit(version[end])) end++;
            if (end == releaseStart) return false;
        }
        return major > 0 && (end == version.Length || char.IsWhiteSpace(version[end]));
    }

    private static bool IsExtensionName(string? name)
        => name is { Length: > 0 } && name.All(static character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static T? GetExtensionDelegate<T>(WglGetProcAddressDelegate getProcAddress, string name)
        where T : Delegate
    {
        var pointer = getProcAddress(name);
        var value = pointer.ToInt64();
        if (value is 0 or 1 or 2 or 3 or -1 or uint.MaxValue) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static WindowsOpenGlAdapterCapabilityRead Unavailable(WindowsOpenGlAdapterTarget target,
        WindowsOpenGlCapabilityFields selectedFields)
        => new(target, null, null, null, null, UnavailableLimitValues(selectedFields));

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>? UnavailableLimitValues(
        WindowsOpenGlCapabilityFields selectedFields)
    {
        var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
        foreach (var spec in WindowsOpenGlCoreLimitsCatalog.Specs)
            if ((selectedFields & spec.Field) != 0)
                values.Add(spec.Limit, new(WindowsOpenGlLimitReadStatus.Unavailable));
        return values.Count == 0 ? null : values.ToImmutable();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr GlGetStringDelegate(uint name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGetIntegervDelegate(uint name, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGetIntegervLimitDelegate(uint name, ref int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGetFloatvDelegate(uint name, ref float value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlGetInteger64vDelegate(uint name, ref long value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr GlGetStringiDelegate(uint name, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void GlGetUnsignedBytevDelegate(uint name, byte* data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate IntPtr WglGetProcAddressDelegate([MarshalAs(UnmanagedType.LPStr)] string name);

    private sealed class OpenGlDataException : Exception { }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
