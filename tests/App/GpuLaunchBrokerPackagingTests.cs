using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Resource_Manager_APP.Tests;

public sealed class GpuLaunchBrokerPackagingTests
{
    [Theory]
    [InlineData("ResourceManager.GpuLaunchBroker.exe")]
    [InlineData("GpuLaunchBrokerProbe/ResourceManager.GpuLaunchTargetProbe.exe")]
    public void DeliveredStandaloneEntriesHaveOnlyWindowsRuntimeImports(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        Assert.True(File.Exists(path), "Missing delivered native entry: " + path);
        using var file = File.OpenRead(path);
        using var image = new PEReader(file);
        Assert.False(image.PEHeaders.IsDll);
        Assert.Equal(Subsystem.WindowsGui, image.PEHeaders.PEHeader!.Subsystem);
        var names = ReadImports(image);
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            // Windows API sets resolve through the OS, not a product sidecar DLL.
            Assert.True(name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(Environment.SystemDirectory, name)),
                $"{Path.GetFileName(path)} requires an undeclared non-Windows runtime: {name}");
        }
    }

    private static IReadOnlyList<string> ReadImports(PEReader image)
    {
        var imports = image.PEHeaders.PEHeader!.ImportTableDirectory;
        Assert.True(imports.RelativeVirtualAddress > 0);
        var table = image.GetSectionData(imports.RelativeVirtualAddress).GetContent().ToArray();
        var names = new List<string>();
        for (var index = 0; index < 64 && index * 20 + 20 <= table.Length; index++)
        {
            var row = table.AsSpan(index * 20, 20);
            var nameRva = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(12, 4));
            if (nameRva == 0) return names;
            var name = image.GetSectionData(nameRva).GetContent().Take(260).ToArray();
            var end = Array.IndexOf(name, (byte)0);
            Assert.True(end > 0, "Import name is not bounded or terminated.");
            names.Add(Encoding.ASCII.GetString(name.AsSpan(0, end)));
        }
        Assert.Fail("Import directory exceeds the standalone entry's bounded table.");
        return names;
    }
}
