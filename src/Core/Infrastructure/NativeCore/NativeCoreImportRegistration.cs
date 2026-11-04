using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeCoreImportRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => NativeCoreLibraryResolver.RegisterFor(typeof(NativeCoreImportRegistration).Assembly);
}
