using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsSecurityProductListReadSession : IDisposable
{
    internal static readonly Guid ClassId = new("17072F7B-9ABE-4A74-A261-1EB76B55107A");
    internal static readonly Guid InterfaceId = new("722A338C-6E8E-4E72-AC27-1417FB0C81C2");
    internal const int MaximumProducts = 4096, MaximumStringCharacters = 32767;
    internal const int MaximumListCharacters = 1048576;
    internal const int InitializeSlot = 7, CountSlot = 8, ItemSlot = 9;
    private readonly WindowsSecurityProductComFunctions functions;
    private readonly bool ownsInitialization;
    private readonly SecurityProductRelease release;
    private IntPtr handle;

    private WindowsSecurityProductListReadSession(IntPtr pointer, bool ownsInitialization, WindowsSecurityProductComFunctions functions)
    {
        handle = pointer; this.ownsInitialization = ownsInitialization; this.functions = functions;
        release = Bind<SecurityProductRelease>(pointer, 2) ?? throw new COMException("WSC interface has no release method.");
    }

    internal static WindowsSecurityProductListReadSession? Open(uint provider) => Open(provider, new(
        () => CoInitializeEx(IntPtr.Zero, 0),
        (Guid classId, Guid interfaceId, out IntPtr pointer) => CoCreateInstance(in classId, IntPtr.Zero, 1, in interfaceId, out pointer),
        CoUninitialize, SysStringLen, SysFreeString));

    internal static WindowsSecurityProductListReadSession? Open(uint provider, WindowsSecurityProductComFunctions functions)
    {
        if (provider is not (1 or 4)) throw new ArgumentOutOfRangeException(nameof(provider));
        var status = functions.Initialize();
        if (status is not (0 or 1 or WindowsFirewallPolicyReadSession.ChangedApartment)) return null;
        var ownsInitialization = status is 0 or 1;
        WindowsSecurityProductListReadSession? session = null;
        var returned = false;
        try
        {
            if (functions.Create(ClassId, InterfaceId, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            session = new(pointer, ownsInitialization, functions);
            var initialize = Bind<SecurityProductInitialize>(pointer, InitializeSlot);
            if (initialize is null || Bind<SecurityProductCount>(pointer, CountSlot) is null
                || Bind<SecurityProductItem>(pointer, ItemSlot) is null || initialize(pointer, provider) != 0) return null;
            returned = true;
            return session;
        }
        finally
        {
            if (!returned)
            {
                if (session is not null) session.Dispose();
                else if (ownsInitialization) functions.Uninitialize();
            }
        }
    }

    internal IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts()
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        var count = Bind<SecurityProductCount>(handle, CountSlot)!;
        var item = Bind<SecurityProductItem>(handle, ItemSlot)!;
        if (count(handle, out var length) != 0 || length < 0 || length > MaximumProducts) return null;
        var products = new List<DeviceTopologySecurityProductInformation>(length);
        var textBudget = new ProductTextBudget();
        for (uint index = 0; index < length; index++)
        {
            if (item(handle, index, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            var product = ReadProduct(pointer, textBudget);
            if (product is null) return null;
            products.Add(product);
        }
        return products.AsReadOnly();
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (pointer == IntPtr.Zero) return;
        try { release(pointer); }
        finally { if (ownsInitialization) functions.Uninitialize(); }
    }

    private static T? Bind<T>(IntPtr pointer, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(pointer);
        if (table == IntPtr.Zero) return null;
        var method = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return method == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr pointer);
    [DllImport("oleaut32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SysStringLen(IntPtr value);
    [DllImport("oleaut32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SysFreeString(IntPtr value);
}

internal delegate int SecurityProductCreate(Guid classId, Guid interfaceId, out IntPtr pointer);
internal sealed record WindowsSecurityProductComFunctions(Func<int> Initialize, SecurityProductCreate Create,
    Action Uninitialize, Func<IntPtr, uint> StringLength, Action<IntPtr> FreeString);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint SecurityProductRelease(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SecurityProductInitialize(IntPtr self, uint provider);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SecurityProductCount(IntPtr self, out int count);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SecurityProductItem(IntPtr self, uint index, out IntPtr product);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SecurityProductGetNumber(IntPtr self, out uint value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SecurityProductGetString(IntPtr self, out IntPtr value);
