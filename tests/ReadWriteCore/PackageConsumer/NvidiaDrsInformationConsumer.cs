using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class NvidiaDrsInformationConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var limits = new NvidiaDrsReadLimits(2, 3, 4, 1, 100_000);
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7, 8, 9 })], limits);
        var calls = 0; var fail = false;
        var access = owner.CreateNvidiaDrsInformation(scope, (profiles, supplied, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            Require(supplied == limits && profiles.Count == 1 && profiles[0].ProfileName == "Game", "exact library scope and explicit limits");
            if (fail) { Require(profiles[0].SettingIds.SequenceEqual(new uint[] { 7 }), "failure selects only one setting"); return null; }
            return Values(profiles);
        });
        using var http = new HttpClient();
        var core = new ReadWriteCore(access.Backend, new DependencyActions(http, []));
        Require(calls == 0 && core.Catalog.Fields.Length == 3, "passive exact catalog");
        Require(core.Catalog.Devices.All(device => device.Resource == "gpu_driver_profile_setting"), "profiles do not masquerade as GPU sensors");
        Require(core.Query(scope.Fields[0]).Read.Dependencies.IsEmpty, "no SDK installation dependency");
        Require(core.ReadCached(scope.Fields[0]).Status == FieldObservationStatus.Unavailable && calls == 0, "passive Current");
        var readings = await core.CollectAsync(scope.Fields);
        Require(readings.All(reading => reading.Status == FieldObservationStatus.Current), "typed current database declarations");
        Require(Packet(readings[0]).GetProperty("currentValue").GetProperty("value").GetString() == "18446744073709551615", "lossless QWORD JSON");
        Require(Packet(readings[1]).GetProperty("currentValue").GetProperty("base64").GetString() == "AP8=", "binary bytes");
        Require(Packet(readings[2]).GetProperty("settingTypeRaw").GetUInt32() == 2
            && Packet(readings[2]).GetProperty("currentValue").ValueKind == JsonValueKind.Null, "unknown STRING preserves metadata");
        Require(Packet(readings[0]).GetProperty("settingLocationRaw").GetUInt32() == 0x80000003, "raw location flags");
        Require(!owner.CreateBackend().Catalog.Fields.Any(capability => capability.Field == scope.Fields[0]),
            "ordinary topology catalog excludes explicit DRS query fields");
        Require(core.ReadCached(scope.Fields[0]).Value!.Structure!.Value.GetRawText()
            == owner.ReadState().InformationFields.FindField(scope.Fields[0])!.Value!.Value.GetRawText(), "original owner Current");
        Require((await core.WriteAsync(scope.Fields[0], new(Integer: MetricInteger.Parse("1")))).Status == "unsupported", "readonly only");
        using (var borrow = access.BorrowSubscriptionCapture()) { borrow.SynchronizeDemand(scope.Fields); }
        fail = true; _ = await core.CollectAsync([scope.Fields[0]]);
        Require(core.ReadCached(scope.Fields[0]).Status == FieldObservationStatus.Unavailable
            && core.ReadCached(scope.Fields[1]).Status == FieldObservationStatus.Current, "failure clears selected key only");
        fail = false; _ = await core.CollectAsync([scope.Fields[0]]);
        Require(core.ReadCached(scope.Fields[0]).Status == FieldObservationStatus.Current, "direct works after borrow disposal");
        var path = NvidiaDrsSubscriptionPath.Create(new("Game", new uint[] { 7 }), limits, TimeSpan.FromMilliseconds(100));
        Require(path.StartsWith("/api/device-topology/nvidia-drs/fields/subscribe?", StringComparison.Ordinal), "public selector helper");
        Console.WriteLine("[NVIDIA-DRS-READWRITE-PACKAGE] exact readonly library settings/typed JSON/original Current; recorded source, no driver.");
    }

    private static NvidiaDrsSnapshot Values(IReadOnlyList<NvidiaDrsProfileRequest> profiles) => new([.. profiles.Select(profile =>
        new NvidiaDrsProfile(profile.ProfileName, 7, 0, 0, (uint)profile.SettingIds.Count,
        [.. profile.SettingIds.Select(id => new NvidiaDrsSetting(id, "setting", id == 7 ? 4U : id == 8 ? 1U : 2U,
            0x80000003, 0x80000001, 1, id == 7 ? new NvidiaDrsQwordValue(ulong.MaxValue)
                : id == 8 ? new NvidiaDrsBinaryValue([0, 255]) : null))]))]);
    private static JsonElement Packet(FieldReading reading) => reading.Value!.Structure!.Value;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("NVIDIA DRS public consumer: " + message); }
}
