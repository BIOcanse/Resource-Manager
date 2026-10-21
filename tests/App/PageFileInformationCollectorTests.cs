using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class PageFileInformationCollectorTests
{
    internal const string PathA = @"C:\pagefile.sys";
    internal const string PathB = @"D:\页面文件.sys";
    internal const string IdA = "windows-pagefile|" + PathA;
    internal const string IdB = "windows-pagefile|" + PathB;

    [Fact]
    public void ActualPageFileUsageKeepsItsFileAndQuantitySeparateFromSystemCommit()
    {
        var fields = Project(Sample());
        var file = Assert.Single(fields.Devices, item => item.DeviceIdentity == IdA);
        Assert.Equal("page_file", file.ObjectType); Assert.Equal(8, file.Fields.Count);
        Assert.Equal(PathA, Field(fields, "memory_all_pagefile_path_reported").Value!.Value.GetString());
        Assert.Equal("8192000", Field(fields, "memory_all_pagefile_capacity").Value!.Value.GetString());
        Assert.Equal("1638400", Field(fields, "memory_all_pagefile_used").Value!.Value.GetString());
        Assert.Equal("2457600", Field(fields, "memory_all_pagefile_peak_used_reported_bytes").Value!.Value.GetString());
        Assert.All(file.Fields, field => { Assert.Equal("windows.pagefile", field.Definition.SourceId);
            Assert.Equal("memory", field.Definition.Resource); Assert.Equal("all", field.Definition.Vendor); });
        Assert.DoesNotContain(file.Fields, field => field.Definition.Name.Contains("commit", StringComparison.Ordinal));
        Assert.Equal("memory.pagefile.used_reported", Field(fields, "memory_all_pagefile_used").Definition.Quantity);
    }

    [Fact]
    public void ExactPageAndByteValuesCanExceedDoubleAndUInt64WithoutTruncation()
    {
        var file = Sample() with { CapacityPages = ulong.MaxValue, UsedPages = 9007199254740993UL,
            PeakUsedPages = ulong.MaxValue - 1, PageSizeBytes = uint.MaxValue };
        var fields = Project(file);
        Assert.Equal("18446744073709551615", Field(fields, "memory_all_pagefile_capacity_reported_pages").Value!.Value.GetString());
        Assert.Equal("9007199254740993", Field(fields, "memory_all_pagefile_used_reported_pages").Value!.Value.GetString());
        Assert.Equal(((UInt128)ulong.MaxValue * uint.MaxValue).ToString(CultureInfo.InvariantCulture),
            Field(fields, "memory_all_pagefile_capacity").Value!.Value.GetString());
        Assert.Equal(((UInt128)9007199254740993UL * uint.MaxValue).ToString(CultureInfo.InvariantCulture),
            Field(fields, "memory_all_pagefile_used").Value!.Value.GetString());
        Assert.Equal("unsigned-integer", Field(fields, "memory_all_pagefile_used").Definition.ValueKind);
    }

    [Theory]
    [InlineData(null)] [InlineData(0U)]
    public void UnknownPageSizeClearsBytesAndUnitWhileRetainingOriginalPageCounts(uint? size)
    {
        var fields = Project(Sample() with { PageSizeBytes = size });
        foreach (var name in new[] { "memory_all_pagefile_capacity", "memory_all_pagefile_used",
            "memory_all_pagefile_peak_used_reported_bytes", "memory_all_pagefile_page_size_reported_bytes" })
            Assert.Null(Field(fields, name).Value);
        Assert.Equal("200", Field(fields, "memory_all_pagefile_used_reported_pages").Value!.Value.GetString());
        Assert.Equal(PathA, Field(fields, "memory_all_pagefile_path_reported").Value!.Value.GetString());
    }

    [Fact]
    public void SuccessfulZeroAndIndependentOsPeakReportArePreserved()
    {
        var fields = Project(Sample() with { CapacityPages = 0, UsedPages = 0, PeakUsedPages = 500 });
        Assert.Equal("0", Field(fields, "memory_all_pagefile_capacity").Value!.Value.GetString());
        Assert.Equal("0", Field(fields, "memory_all_pagefile_used").Value!.Value.GetString());
        Assert.Equal("4096000", Field(fields, "memory_all_pagefile_peak_used_reported_bytes").Value!.Value.GetString());
    }

    [Fact]
    public void NativeFileNameIsNotTrimmedTranslatedOrTreatedAsAStorageHardwareId()
    {
        const string path = @"\Device\HarddiskVolume8\  页面 文件.sys ";
        var file = Sample() with { DeviceIdentity = "windows-pagefile|" + path, PathReported = path };
        var fields = Project(file);
        Assert.Equal(path, Field(fields, "memory_all_pagefile_path_reported", file.DeviceIdentity).Value!.Value.GetString());
        Assert.DoesNotContain(fields.Devices, device => device.ObjectType == "physical_disk");
    }

    [Fact]
    public void RawSnapshotRoundTripAndSemanticHashIncludeUsageButIgnoreEnumerationOrder()
    {
        var a = Sample(); var b = Sample() with { DeviceIdentity = IdB, PathReported = PathB, UsedPages = 0 };
        var raw = DeviceInformationTestData.Snapshot() with { PageFileInformation = [a, b] };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var decoded = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw, options), options)!;
        Assert.Equal(a, decoded.PageFileInformation![0]); Assert.Equal(b, decoded.PageFileInformation[1]);
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { PageFileInformation = [b, a], CapturedAt = DateTimeOffset.UtcNow.AddDays(1) }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { PageFileInformation = [a with { UsedPages = 201 }, b] }));
        var old = JsonSerializer.Deserialize<DeviceTopologySnapshot>("{\"capturedAt\":\"2026-10-06T00:00:00Z\",\"system\":{\"manufacturer\":\"\",\"model\":\"\",\"brandDisplayName\":\"\",\"brandLogoText\":\"\"},\"ports\":[],\"notes\":[]}", options)!;
        Assert.Empty(old.PageFileInformation!);
    }

    [Fact]
    public void MissingCountEmptiesOnlyItsDependentByteValue()
    {
        var fields = Project(Sample() with { UsedPages = null });
        Assert.Null(Field(fields, "memory_all_pagefile_used").Value);
        Assert.Null(Field(fields, "memory_all_pagefile_used_reported_pages").Value);
        Assert.Equal("8192000", Field(fields, "memory_all_pagefile_capacity").Value!.Value.GetString());
        Assert.Equal("2457600", Field(fields, "memory_all_pagefile_peak_used_reported_bytes").Value!.Value.GetString());
    }

    internal static DeviceTopologyPageFileInformation Sample() => new(IdA)
        { PathReported = PathA, CapacityPages = 1000, UsedPages = 200, PeakUsedPages = 300, PageSizeBytes = 8192 };
    internal static DeviceInformationSnapshot Project(DeviceTopologyPageFileInformation file)
        => DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot() with { PageFileInformation = [file] }, DateTimeOffset.UtcNow);
    private static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name, string identity = IdA)
        => Assert.Single(Assert.Single(snapshot.Devices, device => device.DeviceIdentity == identity).Fields, field => field.Definition.Name == name);
}
