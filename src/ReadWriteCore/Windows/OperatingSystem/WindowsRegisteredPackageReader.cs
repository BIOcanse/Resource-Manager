using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Read-only PackageManager.FindPackagesForUser ABI. Retrieves actual
/// Package.Id.FullName and FamilyName; no process package lookup, manifest parser,
/// deployment operation, helper process or retained COM resource.</summary>
internal static unsafe class WindowsRegisteredPackageReader
{
    private static readonly Guid ManagerId = new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53");
    private static readonly Guid PackageId = new("163c792f-bd75-413c-bf23-b1fe7b95d825");
    private static readonly Guid IdentityId = new("1adb665e-37c7-4790-9980-dd7ae74e8bb2");
    // ABI slots verified against Microsoft's Windows metadata projections:
    // IPackageManager.FindPackagesByUserSecurityId=12; IPackage.Id=6;
    // IPackageId.FullName=12, FamilyName=13; iterable.First=6;
    // iterator.Current=6, HasCurrent=7, MoveNext=8. IInspectable occupies 0..5.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "PackageManager package registered or unregistered")]
    internal static IReadOnlyList<WindowsRegisteredPackageFamily> Read(string sid, CancellationToken token)
    {
        var initialized = RoInitialize(1); // MTA, or borrow the thread's existing apartment.
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Check(initialized);
        nint className = 0, user = 0, instance = 0, manager = 0, iterable = 0, iterator = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            className = String("Windows.Management.Deployment.PackageManager"); user = String(sid);
            Check(RoActivateInstance(className, out instance));
            manager = Interface(instance, ManagerId);
            var find = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(manager, 12);
            Check(find(manager, user, &iterable));
            iterator = Out(iterable, 6);
            var result = new List<WindowsRegisteredPackageFamily>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (Boolean(iterator, 7))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= 131072) throw new COMException("Package enumeration exceeded the bounded complete-read limit.");
                nint value = 0, package = 0, identity = 0, typedIdentity = 0;
                try
                {
                    value = Out(iterator, 6); package = Interface(value, PackageId);
                    identity = Out(package, 6); typedIdentity = Interface(identity, IdentityId);
                    var full = Text(typedIdentity, 12); var family = Text(typedIdentity, 13);
                    if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(family) || !names.Add(full))
                        throw new COMException("Incomplete or duplicate registered package identity.");
                    result.Add(new(sid, full, family));
                }
                finally { Release(typedIdentity); Release(identity); Release(package); Release(value); }
                _ = Boolean(iterator, 8);
            }
            token.ThrowIfCancellationRequested();
            return result.ToArray();
        }
        finally
        {
            Release(iterator); Release(iterable); Release(manager); Release(instance);
            if (user != 0) _ = WindowsDeleteString(user);
            if (className != 0) _ = WindowsDeleteString(className);
            if (initialized >= 0) RoUninitialize();
        }
    }
    private static nint Slot(nint value, int index)
    {
        if (value == 0) throw new COMException("Windows returned a null interface.");
        return (*(nint**)value)[index];
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "PackageManager package registered or unregistered")]
    private static nint Interface(nint value, Guid iid)
    {
        nint result = 0;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(value, 0);
        var hr = query(value, &iid, &result);
        if (hr < 0) { Release(result); Check(hr); }
        if (result == 0) throw new COMException("Windows returned a null typed interface.");
        return result;
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "PackageManager package registered or unregistered")]
    private static nint Out(nint value, int slot)
    {
        nint result = 0;
        var read = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(value, slot);
        var hr = read(value, &result);
        if (hr < 0) { Release(result); Check(hr); }
        return result;
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "PackageManager package registered or unregistered")]
    private static bool Boolean(nint value, int slot)
    {
        byte result = 0;
        var read = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(value, slot);
        Check(read(value, &result)); return result != 0;
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "PackageManager package registered or unregistered")]
    private static string Text(nint value, int slot)
    {
        nint text = 0;
        try
        {
            var read = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(value, slot);
            Check(read(value, &text));
            var buffer = WindowsGetStringRawBuffer(text, out var length);
            if (length > 65536) throw new COMException("Package identity exceeded the complete-read limit.");
            return length == 0 ? "" : new string((char*)buffer, 0, checked((int)length));
        }
        finally { if (text != 0) _ = WindowsDeleteString(text); }
    }
    private static nint String(string value)
    {
        Check(WindowsCreateString(value, (uint)value.Length, out var result)); return result;
    }
    private static void Release(nint value)
    {
        if (value != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value);
    }
    private static void Check(int hr) { if (hr < 0) throw new COMException("Read-only Windows package enumeration failed.", hr); }

    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoInitialize(uint initializationType);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern void RoUninitialize();
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoActivateInstance(nint className, out nint instance);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern nint WindowsGetStringRawBuffer(nint value, out uint length);
}
