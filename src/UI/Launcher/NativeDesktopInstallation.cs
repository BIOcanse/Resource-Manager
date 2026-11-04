using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

// Temporary synchronous binding. W-launcher-entry removes the managed result
// projection; P-launcher/installation removes registry/path execution callbacks.
// DesktopInstallation.Read uses this entry point; equivalence tests also exercise
// the bridge directly to check borrowed regions and exception propagation.
public static class NativeDesktopInstallation
{
    public static DesktopInstallation Read(RegistryKey root, ToolText? text = null)
    {
        text ??= ToolText.For(AppLanguage.System);
        using var key = root.OpenSubKey(@"Software\ResourceManager")
            ?? throw new InvalidOperationException(text.NotInstalled);
        using var borrow = new CallBorrow(key);
        var context = GCHandle.Alloc(borrow);
        try
        {
            var host = new Host
            {
                Context = GCHandle.ToIntPtr(context),
                ExpectedService = borrow.Publish(WindowsServiceRegistration.ProductServiceName),
                ReadValue = ReadValueCallback,
                Operation = OperationCallback
            };
            var abiStatus = ReadNative(ref host, out var result);
            // Captured exceptions never cross the C ABI. Re-throw the original
            // instance only after Zig has stopped reading all borrowed regions.
            borrow.Failure?.Throw();
            if (abiStatus != 0) throw new InvalidOperationException("Invalid launcher installation ABI.");
            switch ((Status)result.Status)
            {
                case Status.Success:
                    return new(CallBorrow.String(result.Root), CallBorrow.String(result.Backend),
                        CallBorrow.String(result.NativeUi), CallBorrow.String(result.Launcher));
                case Status.InvalidRegistration:
                    throw new InvalidOperationException(text.LauncherRegistrationInvalid);
                case Status.MissingValue:
                    throw new InvalidOperationException(text.Format(text.LauncherValueMissingFormat,
                        ValueName((Value)result.MissingValue)));
                case Status.ProgramMissing:
                    throw new FileNotFoundException(text.LauncherProgramMissing, CallBorrow.String(result.MissingPath));
                default:
                    throw new InvalidOperationException("Launcher installation bridge failed without an exception.");
            }
        }
        finally
        {
            context.Free();
            // using disposes every pin after the last native reader and after
            // projection to original string references, on every failure too.
        }
    }

    private enum Value : uint { ServiceName, InstallRoot, Backend, NativeUi, Launcher, Desktop }
    private enum Operation : uint { FullPath, ResidentFor, RelativePath, IsPathRooted, EqualIgnoreCase, FileExists }
    private enum Status : uint { Success, InvalidRegistration, MissingValue, ProgramMissing, BridgeFailure }

    private static string ValueName(Value value) => value switch
    {
        Value.ServiceName => "ServiceName",
        Value.InstallRoot => "InstallRoot",
        Value.Backend => "BackendPath",
        Value.NativeUi => "NativeUiPath",
        Value.Launcher => "LauncherPath",
        Value.Desktop => "DesktopPath",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Text
    {
        public nint Data;
        public nuint Length;
        public nint Identity;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadValueDelegate(nint context, Value value, out Text output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint OperationDelegate(nint context, Operation operation, in Text first, in Text second,
        out Text output, out uint fact);

    [StructLayout(LayoutKind.Sequential)]
    private struct Host
    {
        public nint Context;
        public Text ExpectedService;
        [MarshalAs(UnmanagedType.FunctionPtr)] public ReadValueDelegate ReadValue;
        [MarshalAs(UnmanagedType.FunctionPtr)] public OperationDelegate Operation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public uint Status;
        public uint MissingValue;
        public Text MissingPath;
        public Text Root;
        public Text Backend;
        public Text NativeUi;
        public Text Launcher;
    }

    // Maintenance binds all Desktop leaf exports into this one shared temporary
    // DLL. This file does not build or load a private alternate product library.
    [DllImport("ResourceManager.Desktop.Native", EntryPoint = "rm_launcher_installation_read",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint ReadNative(ref Host host, out Result result);

    private static readonly ReadValueDelegate ReadValueCallback = ReadValue;
    private static readonly OperationDelegate OperationCallback = Execute;

    private static uint ReadValue(nint context, Value value, out Text output)
    {
        output = default;
        var borrow = (CallBorrow)GCHandle.FromIntPtr(context).Target!;
        try
        {
            output = borrow.Publish(borrow.Key.GetValue(ValueName(value)) as string);
            return 0;
        }
        catch (Exception error)
        {
            borrow.Failure = ExceptionDispatchInfo.Capture(error);
            return 1;
        }
    }

    private static uint Execute(nint context, Operation operation, in Text first, in Text second,
        out Text output, out uint fact)
    {
        output = default;
        fact = 0;
        var borrow = (CallBorrow)GCHandle.FromIntPtr(context).Target!;
        try
        {
            // Exact original APIs, complete operands, no product choices here.
            switch (operation)
            {
                case Operation.FullPath:
                    output = borrow.Publish(Path.GetFullPath(CallBorrow.String(first)));
                    break;
                case Operation.ResidentFor:
                    output = borrow.Publish(ProductDesktopLayout.ResidentFor(CallBorrow.String(first)));
                    break;
                case Operation.RelativePath:
                    output = borrow.Publish(Path.GetRelativePath(CallBorrow.String(first), CallBorrow.String(second)));
                    break;
                case Operation.IsPathRooted:
                    fact = Path.IsPathRooted(CallBorrow.String(first)) ? 1u : 0u;
                    break;
                case Operation.EqualIgnoreCase:
                    fact = string.Equals(CallBorrow.String(first), CallBorrow.String(second), StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
                    break;
                case Operation.FileExists:
                    fact = File.Exists(CallBorrow.String(first)) ? 1u : 0u;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
            return 0;
        }
        catch (Exception error)
        {
            borrow.Failure = ExceptionDispatchInfo.Capture(error);
            return 1;
        }
    }

    // Call-local ownership only. Pins reference the original registry/API
    // strings; no UTF-8 copies, installation DTO mirror, or retained cache.
    private sealed class CallBorrow(RegistryKey key) : IDisposable
    {
        public RegistryKey Key { get; } = key;
        public ExceptionDispatchInfo? Failure { get; set; }
        private readonly List<GCHandle> pins = [];

        public Text Publish(string? value)
        {
            if (value is null) return default;
            var index = 0;
            while (index < pins.Count)
            {
                var existing = pins[index++];
                if (ReferenceEquals(existing.Target, value))
                    return Describe(existing, value.Length);
            }
            var pin = GCHandle.Alloc(value, GCHandleType.Pinned);
            try { pins.Add(pin); }
            catch { pin.Free(); throw; }
            return Describe(pin, value.Length);
        }

        private static Text Describe(GCHandle pin, int length) =>
            new() { Data = pin.AddrOfPinnedObject(), Length = (nuint)length, Identity = GCHandle.ToIntPtr(pin) };

        public static string String(in Text value) => (string)GCHandle.FromIntPtr(value.Identity).Target!;

        public void Dispose()
        {
            var index = pins.Count;
            while (index > 0) pins[--index].Free();
            pins.Clear();
        }
    }
}
