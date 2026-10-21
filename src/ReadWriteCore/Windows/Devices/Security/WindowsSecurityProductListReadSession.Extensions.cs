using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsSecurityProductListReadSession
{
    internal static readonly Guid Product2Id = new("F896CA54-FE09-4403-86D4-23CB488D81D8");
    internal static readonly Guid Product3Id = new("55536524-D1D1-4726-8C7C-04996A1904E7");
    internal const int GuidSlot = 12, DefaultSlot = 13, ScanSlot = 14, SettingsSlot = 15,
        ProtectionUpdateSlot = 16, DomainProfileSlot = 17, PrivateProfileSlot = 18, PublicProfileSlot = 19, DaysSlot = 20;

    private DeviceTopologySecurityProductInformation ReadExtendedProduct(IntPtr product,
        DeviceTopologySecurityProductInformation value, ProductTextBudget budget)
    {
        var pointer = QueryProduct(product, Product3Id);
        var hasDays = pointer != IntPtr.Zero;
        if (!hasDays) pointer = QueryProduct(product, Product2Id);
        if (pointer == IntPtr.Zero) return value;
        var releaseProduct = Bind<SecurityProductRelease>(pointer, 2)
            ?? throw new COMException("WSC extended interface has no release method.");
        try
        {
            return value with
            {
                ProductGuidReported = ReadString(pointer, GuidSlot, budget),
                ProductIsDefaultReported = ReadBoolean(pointer),
                AntivirusScanSubstatusReported = ReadNumber(pointer, ScanSlot, 3),
                AntivirusSettingsSubstatusReported = ReadNumber(pointer, SettingsSlot, 3),
                AntivirusProtectionUpdateSubstatusReported = ReadNumber(pointer, ProtectionUpdateSlot, 3),
                FirewallDomainProfileSubstatusReported = ReadNumber(pointer, DomainProfileSlot, 3),
                FirewallPrivateProfileSubstatusReported = ReadNumber(pointer, PrivateProfileSlot, 3),
                FirewallPublicProfileSubstatusReported = ReadNumber(pointer, PublicProfileSlot, 3),
                AntivirusDaysUntilExpiredReported = hasDays ? ReadNumber(pointer, DaysSlot, uint.MaxValue) : null
            };
        }
        finally { releaseProduct(pointer); }
    }

    private static IntPtr QueryProduct(IntPtr pointer, Guid id)
    {
        try
        {
            var query = Bind<SecurityProductQueryInterface>(pointer, 0);
            return query is not null && query(pointer, in id, out var result) == 0 ? result : IntPtr.Zero;
        }
        catch (Exception error) when (WindowsSecurityInformationReader.IsSourceFailure(error)) { return IntPtr.Zero; }
    }

    private static bool? ReadBoolean(IntPtr pointer)
    {
        try
        {
            var read = Bind<SecurityProductGetBoolean>(pointer, DefaultSlot);
            return read is not null && read(pointer, out var value) == 0 ? value != 0 : null;
        }
        catch (Exception error) when (WindowsSecurityInformationReader.IsSourceFailure(error)) { return null; }
    }
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SecurityProductQueryInterface(IntPtr self, in Guid id, out IntPtr pointer);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SecurityProductGetBoolean(IntPtr self, out int value);
