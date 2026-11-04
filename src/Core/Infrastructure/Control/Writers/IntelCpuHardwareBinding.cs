using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Product paths and control capability IDs; the library owns only the selected native operations.</summary>
internal static class IntelCpuHardwareBinding
{
    internal static PawnIoIntelCpuHardware Open(string contentRoot)
        => new(Path.Combine(PackagePathResolver.ResolvePackageRoot(contentRoot), "Dependencies", "intel-msr-pawnio-provider", "IntelMSR.bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"));

    internal static int? VoltagePlane(string id) => IntelCpuControlResource.VoltagePlane(id);
}
