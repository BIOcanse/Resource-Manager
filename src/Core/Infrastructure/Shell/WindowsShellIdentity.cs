using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Shell;

public static class WindowsShellIdentity
{
    public static void TrySetCurrentProcessAppUserModelId(string appUserModelId)
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(appUserModelId);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
