using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>Win32 calls for one explicit, read-only process detail request.</summary>
internal sealed partial class WindowsProcessDetailsNativeApi : IWindowsProcessDetailsNativeApi
{
    private const uint WaitTimeout = 0x102;
    private const uint ListModulesAll = 0x03;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private readonly WindowsProcessDetailsOptions options;

    internal WindowsProcessDetailsNativeApi() : this(WindowsProcessDetailsOptions.Default) { }

    internal WindowsProcessDetailsNativeApi(WindowsProcessDetailsOptions options)
        => this.options = options ?? throw new ArgumentNullException(nameof(options));

    public IntPtr OpenProcess(uint desiredAccess, int processId)
        => OpenProcessNative(desiredAccess, false, processId);

    public uint GetProcessId(IntPtr process) => GetProcessIdNative(process);

    public bool TryGetCreationFileTime(IntPtr process, out ulong creationFileTime)
    {
        creationFileTime = 0;
        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return false;
        creationFileTime = ((ulong)creation.High << 32) | creation.Low;
        return creationFileTime != 0;
    }

    public bool IsRunning(IntPtr process) => WaitForSingleObject(process, 0) == WaitTimeout;

    public bool TryReadImagePath(IntPtr process, out string path)
    {
        path = string.Empty;
        var buffer = new StringBuilder(options.MaximumImagePathCharacters);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)
            || length == 0 || length >= (uint)buffer.Capacity) return false;
        path = buffer.ToString();
        return !string.IsNullOrWhiteSpace(path);
    }

    public bool TryReadModules(IntPtr process, out IReadOnlyList<WindowsProcessLoadedModule> modules)
    {
        modules = [];
        var capacity = options.InitialModulesPerProcess;
        for (var attempt = 0; attempt < options.MaximumModuleEnumerationAttempts; attempt++)
        {
            var nativeModules = new IntPtr[capacity];
            var bytes = checked((uint)(nativeModules.Length * IntPtr.Size));
            var succeeded = EnumProcessModulesEx(process, nativeModules, bytes, out var needed, ListModulesAll);
            if (needed % (uint)IntPtr.Size != 0) return false;
            var count = needed / (uint)IntPtr.Size;
            if (count > options.MaximumModulesPerProcess) return false;
            if (needed > bytes)
            {
                if (attempt == options.MaximumModuleEnumerationAttempts - 1) return false;
                var requestedCapacity = Math.Max((long)capacity * 2, count);
                var nextCapacity = Math.Min(requestedCapacity, options.MaximumModulesPerProcess);
                if (nextCapacity <= capacity) return false;
                capacity = checked((int)nextCapacity);
                continue;
            }
            if (!succeeded) return false;

            var rows = new List<WindowsProcessLoadedModule>(checked((int)count));
            var bases = new HashSet<ulong>();
            for (var index = 0; index < count; index++)
            {
                var module = nativeModules[checked((int)index)];
                if (module == IntPtr.Zero) return false;
                var baseAddress = PointerValue(module);
                if (!bases.Add(baseAddress) || !TryReadModulePath(process, module, out var modulePath)) return false;
                rows.Add(new(baseAddress, modulePath) { ImageSizeBytes = GetModuleInformation(process, module, out var info,
                    checked((uint)Marshal.SizeOf<ModuleInformation>())) ? info.SizeOfImage : null });
            }
            modules = rows.AsReadOnly();
            return true;
        }
        return false;
    }

    public bool TryVerifyImageSignature(string path, out int status)
    {
        status = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var filePath = IntPtr.Zero;
        try
        {
            filePath = Marshal.StringToHGlobalUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = filePath
            };
            var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                var data = new WinTrustData
                {
                    StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    Info = fileInfoPointer,
                    StateAction = WtdStateActionIgnore,
                    ProviderFlags = WtdCacheOnlyUrlRetrieval
                };
                var action = GenericVerifyV2;
                status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return true; // WinVerifyTrust's LONG is the reported status; only zero means trusted.
            }
            finally { Marshal.FreeHGlobal(fileInfoPointer); }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (filePath != IntPtr.Zero) Marshal.FreeHGlobal(filePath);
        }
    }

    public bool TryReadSystemProcessInformation(out byte[] data)
    {
        data = [];
        var size = options.InitialSystemInformationBufferBytes;
        for (var attempt = 0; attempt < options.MaximumSystemInformationAttempts; attempt++)
        {
            if (size > options.MaximumSystemInformationBufferBytes) return false;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(5, buffer, checked((uint)size), out var returned);
                if (status == 0)
                {
                    if (returned == 0 || returned > (uint)size) return false;
                    var result = new byte[checked((int)returned)];
                    Marshal.Copy(buffer, result, 0, result.Length);
                    data = result;
                    return true;
                }
                if (status is not (StatusInfoLengthMismatch or StatusBufferTooSmall)) return false;
                var next = Math.Max((long)returned, (long)size * 2);
                if (next <= size || next > options.MaximumSystemInformationBufferBytes || next > Array.MaxLength) return false;
                size = checked((int)next);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    public bool CloseHandle(IntPtr handle) => CloseHandleNative(handle);

    private bool TryReadModulePath(IntPtr process, IntPtr module, out string path)
    {
        path = string.Empty;
        var buffer = new StringBuilder(options.MaximumImagePathCharacters);
        var length = GetModuleFileNameExW(process, module, buffer, checked((uint)buffer.Capacity));
        if (length == 0 || length >= (uint)buffer.Capacity) return false;
        path = buffer.ToString();
        return !string.IsNullOrWhiteSpace(path);
    }

    private static ulong PointerValue(IntPtr value) => unchecked((ulong)value.ToInt64());

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal IntPtr FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr Info;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcessNative(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessId", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessIdNative(IntPtr process);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleNative(IntPtr handle);

    [DllImport("psapi.dll", EntryPoint = "EnumProcessModulesEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint bytes, out uint needed, uint filterFlag);

    [DllImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder fileName, uint size);

    [DllImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQuerySystemInformation(uint informationClass, IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData data);
}
