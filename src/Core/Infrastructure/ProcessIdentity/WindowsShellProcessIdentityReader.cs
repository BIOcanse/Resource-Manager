using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsShellProcessIdentityReader
{
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdProperty = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public IReadOnlyDictionary<int, ShellProcessIdentity> ReadWindowIdentities()
    {
        var identities = new Dictionary<int, ShellProcessIdentity>();
        NativeMethods.EnumWindows((window, _) =>
        {
            AddWindowIdentity(window, identities);
            return true;
        }, IntPtr.Zero);

        return identities;
    }

    public string? TryReadProcessApplicationUserModelId(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            uint length = 0;
            var result = NativeMethods.GetApplicationUserModelId(handle, ref length, null);
            if (result != NativeMethods.ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)length);
            result = NativeMethods.GetApplicationUserModelId(handle, ref length, builder);
            return result == NativeMethods.ErrorSuccess ? Clean(builder.ToString()) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static void AddWindowIdentity(
        IntPtr window,
        Dictionary<int, ShellProcessIdentity> identities)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window))
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processIdValue);
        var processId = (int)processIdValue;
        if (processId <= 0)
        {
            return;
        }

        var appUserModelId = TryReadWindowApplicationUserModelId(window);
        var title = TryReadWindowTitle(window);
        if (appUserModelId is null && title is null)
        {
            return;
        }

        if (!identities.TryGetValue(processId, out var existing))
        {
            identities[processId] = new ShellProcessIdentity(appUserModelId, title);
            return;
        }

        identities[processId] = existing with
        {
            WindowApplicationUserModelId = existing.WindowApplicationUserModelId ?? appUserModelId,
            WindowTitle = existing.WindowTitle ?? title
        };
    }

    private static string? TryReadWindowTitle(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, builder, builder.Capacity) > 0
            ? Clean(builder.ToString())
            : null;
    }

    private static string? TryReadWindowApplicationUserModelId(IntPtr window)
    {
        IPropertyStore? propertyStore = null;
        try
        {
            var interfaceId = PropertyStoreInterfaceId;
            var result = NativeMethods.SHGetPropertyStoreForWindow(window, ref interfaceId, out propertyStore);
            if (result < 0 || propertyStore is null)
            {
                return null;
            }

            var propertyKey = AppUserModelIdProperty;
            result = propertyStore.GetValue(ref propertyKey, out var propVariant);
            if (result < 0)
            {
                return null;
            }

            try
            {
                return Clean(ReadPropVariantString(propVariant));
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref propVariant);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or AccessViolationException)
        {
            return null;
        }
        finally
        {
            if (propertyStore is not null)
            {
                Marshal.ReleaseComObject(propertyStore);
            }
        }
    }

    private static string? ReadPropVariantString(PropVariant propVariant)
    {
        if (propVariant.PointerValue == IntPtr.Zero)
        {
            return null;
        }

        return propVariant.VariantType switch
        {
            NativeMethods.VariantTypeLpwStr => Marshal.PtrToStringUni(propVariant.PointerValue),
            NativeMethods.VariantTypeBStr => Marshal.PtrToStringBSTR(propVariant.PointerValue),
            _ => null
        };
    }

    private static string? Clean(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}

public sealed record ShellProcessIdentity(
    string? WindowApplicationUserModelId,
    string? WindowTitle);
