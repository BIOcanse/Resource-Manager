using System.Runtime.CompilerServices;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

internal static class NativeTestProcessStartup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
            WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
    }
}
