using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;

namespace Resource_Manager_APP.Tests;

public sealed partial class PciResourceInformationCollectorTests
{
    internal const string Id = @"PCI\VEN_1414&DEV_0000\TEST", Identity = "windows-pnp|" + Id;
    internal static JsonDocument Sdk()
    {
        var assembly = typeof(PciResourceInformationCollectorTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Storage.windows-sdk-pci-resources.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; return JsonDocument.Parse(stream);
    }
    internal static List<(uint Type, byte[] Bytes)> Frames()
    {
        using var sdk = Sdk(); return sdk.RootElement.GetProperty("frames").EnumerateArray()
            .Select(frame => (frame.GetProperty("type").GetUInt32(), Convert.FromHexString(frame.GetProperty("hex").GetString()!))).ToList();
    }
    internal static IReadOnlyList<DeviceTopologyPciResourceRange>? Read(Api api) => new WindowsPciResourceInformationReader(api).Read(42, Id).Ranges;
    internal static DeviceTopologySnapshot Snapshot(Api api) => DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port(Id) with { PciResourceRanges = Read(api) });
    internal static DeviceTopologyNativeDevice Native(string id, IReadOnlyList<DeviceTopologyPciResourceRange>? ranges)
        => new(id, null, null, null, null, null, null, null, "PCI", null, [], [], [], null, null, null) { PciResources = new(ranges, null, null) };

    internal sealed class Api : IWindowsPciResourceInformationApi
    {
        public int DescriptorPointerBytes { get; set; } = 8;
        internal List<(uint Type, byte[] Bytes)> Descriptors = Frames();
        internal int ConfigurationCount = 1, IdentityCalls, Opens, SizeCalls, DataCalls;
        internal string Fault = "";
        internal uint? SizeOverride;
        internal nuint? FaultResource;
        internal readonly List<(bool Configuration, nuint Handle)> Released = [];
        internal readonly List<nuint> Requested = [];
        public string? GetIdentity(uint devnode) { Assert.Equal(42U, devnode); IdentityCalls++; return Fault == "identity-before" || Fault == "identity-after" && IdentityCalls % 2 == 0 ? @"PCI\OTHER" : Id; }
        public uint FirstConfiguration(uint devnode, out nuint configuration) { Assert.Equal(42U, devnode); Opens++; configuration = 10; return Fault == "first" ? 5U : ConfigurationCount == 0 ? 14U : 0U; }
        public uint NextConfiguration(nuint previous, out nuint configuration)
        {
            configuration = Fault == "loop-config" ? 10U : previous + 1;
            return Fault == "next-config" ? 5U : Fault == "loop-config" || (int)previous - 9 < ConfigurationCount ? 0U : 14U;
        }
        public uint NextResource(nuint previous, out nuint resource, out uint type)
        {
            Requested.Add(previous); var index = previous == 10 ? 0 : previous >= 1000 ? (int)(previous - 1000) + 1 : Descriptors.Count;
            if (Fault == "loop-resource" && previous >= 1000) index = 0;
            resource = (nuint)(1000 + index); type = index < Descriptors.Count ? Descriptors[index].Type : uint.MaxValue;
            if (Fault == "next-resource" && previous >= 1000) return 5;
            return index < Descriptors.Count ? 0U : 15U;
        }
        public uint DataSize(nuint resource, out uint bytes)
        {
            SizeCalls++; var fail = FaultResource is null || FaultResource == resource;
            if (fail && Fault == "throw-size") throw new COMException("source failed");
            bytes = fail && SizeOverride is { } size ? size : (uint)Descriptors[(int)(resource - 1000)].Bytes.Length;
            return fail && Fault == "size" ? 5U : 0U;
        }
        public uint Data(nuint resource, byte[] buffer)
        {
            DataCalls++; var fail = FaultResource is null || FaultResource == resource;
            if (fail && Fault == "throw-data") throw new COMException("source failed");
            Descriptors[(int)(resource - 1000)].Bytes.CopyTo(buffer, 0); return fail && Fault == "data" ? 5U : 0U;
        }
        public uint ReleaseConfiguration(nuint configuration) { Released.Add((true, configuration)); return Fault == "release-config" ? 5U : 0U; }
        public uint ReleaseResource(nuint resource)
        {
            Released.Add((false, resource)); if (Fault == "throw-release") throw new COMException("release failed");
            return Fault == "release-resource" ? 5U : 0U;
        }
    }
}
