using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsSecurityProductListReadSession
{
    internal const int NameSlot = 7, StateSlot = 8, SignatureSlot = 9, RemediationSlot = 10, TimestampSlot = 11;

    private DeviceTopologySecurityProductInformation? ReadProduct(IntPtr pointer, ProductTextBudget budget)
    {
        var productRelease = Bind<SecurityProductRelease>(pointer, 2);
        if (productRelease is null) return null;
        try
        {
            var result = new DeviceTopologySecurityProductInformation(ReadString(pointer, NameSlot, budget), ReadNumber(pointer, StateSlot, 3),
                ReadNumber(pointer, SignatureSlot, 1), ReadString(pointer, RemediationSlot, budget), ReadString(pointer, TimestampSlot, budget));
            if (!budget.Exceeded) result = ReadExtendedProduct(pointer, result, budget);
            return budget.Exceeded ? null : result;
        }
        finally { productRelease(pointer); }
    }

    private static uint? ReadNumber(IntPtr pointer, int slot, uint maximum)
    {
        try
        {
            var read = Bind<SecurityProductGetNumber>(pointer, slot);
            return read is not null && read(pointer, out var value) == 0 && value <= maximum ? value : null;
        }
        catch (Exception error) when (WindowsSecurityInformationReader.IsSourceFailure(error)) { return null; }
    }

    private string? ReadString(IntPtr pointer, int slot, ProductTextBudget budget)
    {
        try
        {
            if (budget.Exceeded) return null;
            var read = Bind<SecurityProductGetString>(pointer, slot);
            if (read is null || read(pointer, out var value) != 0 || value == IntPtr.Zero) return null;
            try
            {
                var length = functions.StringLength(value);
                if (length > MaximumStringCharacters) return null;
                if (length > budget.Remaining) { budget.Exceeded = true; return null; }
                budget.Remaining -= (int)length;
                return Marshal.PtrToStringUni(value, (int)length);
            }
            finally { functions.FreeString(value); }
        }
        catch (Exception error) when (WindowsSecurityInformationReader.IsSourceFailure(error)) { return null; }
    }

    // Per-read copy limit only; no product identity, cached values or sampling history.
    private sealed class ProductTextBudget
    {
        internal int Remaining = MaximumListCharacters;
        internal bool Exceeded;
    }
}
