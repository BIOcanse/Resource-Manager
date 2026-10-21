using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsSecurityInformationCollectorTests
{
    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(2U)] [InlineData(3U)]
    public void WscCategoriesRetainEachDefinedHealthCodeIncludingZero(uint value)
    {
        var api = new Api(); api.Health[4] = (0, value);
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Equal(value, result.AntivirusHealthReported); Assert.Equal(3U, result.FirewallHealthReported);
        Assert.Equal(new uint[] { 4, 1 }, api.Providers); Assert.Equal(1, api.Opens);
        Assert.Equal(new uint[] { 1, 2, 4 }, Assert.Single(api.Policies).Requested); Assert.Equal(1, api.Policies[0].Disposals);
    }

    [Theory]
    [InlineData(1, 2U)] [InlineData(2, 0U)] [InlineData(-2147467259, 0U)] [InlineData(0, 4U)] [InlineData(0, uint.MaxValue)]
    public void StoppedWscServiceFailedCallOrUnknownCodeDoesNotPublishDirtyHealth(int status, uint dirtyValue)
    {
        var api = new Api(); api.Health[4] = (status, dirtyValue);
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Null(result.AntivirusHealthReported); Assert.Equal(3U, result.FirewallHealthReported);
        Assert.False(result.DomainFirewallEnabledConfigured); Assert.True(result.PrivateFirewallEnabledConfigured);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NativeWscExceptionsFailOnlyTheirCategory(int failure)
    {
        var api = new Api { HealthError = failure switch { 0 => new COMException("failed"), 1 => new DllNotFoundException("missing"), _ => new EntryPointNotFoundException("missing") } };
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Null(result.AntivirusHealthReported); Assert.Equal(3U, result.FirewallHealthReported); Assert.Equal(6U, result.ActiveFirewallProfiles);
    }

    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(2U)] [InlineData(4U)] [InlineData(6U)] [InlineData(7U)]
    public void ActiveProfileMaskKeepsZeroAndConcurrentProfiles(uint mask)
    {
        var api = new Api { PolicyFactory = () => new Policy { Mask = mask } }; Assert.Equal(mask, new WindowsSecurityInformationReader(api).Read().ActiveFirewallProfiles);
    }

    [Theory]
    [InlineData(1, 6U)] [InlineData(-2147467259, 6U)] [InlineData(0, 8U)] [InlineData(0, uint.MaxValue)]
    public void InvalidProfileMaskIsEmptyWhileIndependentProfileSettingsSurvive(int status, uint mask)
    {
        var api = new Api { PolicyFactory = () => new Policy { Mask = mask, MaskStatus = status } };
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Null(result.ActiveFirewallProfiles); Assert.False(result.DomainFirewallEnabledConfigured); Assert.True(result.PrivateFirewallEnabledConfigured);
    }

    [Theory]
    [InlineData(1, -1)] [InlineData(-2147467259, -1)] [InlineData(0, 1)] [InlineData(0, 2)]
    public void FailedOrNoncanonicalVariantBoolOnlyEmptiesItsOwnProfile(int status, short value)
    {
        var api = new Api { PolicyFactory = () => new Policy { PrivateStatus = status, PrivateValue = value } };
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Null(result.PrivateFirewallEnabledConfigured); Assert.False(result.DomainFirewallEnabledConfigured); Assert.False(result.PublicFirewallEnabledConfigured);
        Assert.Equal(6U, result.ActiveFirewallProfiles); Assert.Equal(1, Assert.Single(api.Policies).Disposals);
    }

    [Fact]
    public void QueryExceptionAndOpenFailureKeepOtherIndependentSources()
    {
        var api = new Api { PolicyFactory = () => new Policy { PrivateError = new COMException("denied") } };
        Assert.Null(new WindowsSecurityInformationReader(api).Read().PrivateFirewallEnabledConfigured); Assert.Equal(1, api.Policies[0].Disposals);
        api.OpenError = new COMException("class unavailable"); var result = new WindowsSecurityInformationReader(api).Read();
        Assert.Equal(0U, result.AntivirusHealthReported); Assert.Equal(3U, result.FirewallHealthReported); Assert.Null(result.ActiveFirewallProfiles);
        Assert.Null(result.DomainFirewallEnabledConfigured); Assert.Null(result.PrivateFirewallEnabledConfigured); Assert.Null(result.PublicFirewallEnabledConfigured);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(-2147417850)]
    public void ReadOnlyComVtableUsesSingleProfileCallsAndBalancesOnlyOwnedInitialization(int initialized)
    {
        using var com = new Com(initialized); using var session = WindowsFirewallPolicyReadSession.Open(com.Functions); Assert.NotNull(session);
        Assert.Equal(0, session.GetCurrentProfiles(out var profiles)); Assert.Equal(6U, profiles);
        foreach (var profile in new uint[] { 1, 2, 4 }) { Assert.Equal(0, session.GetEnabled(profile, out var value)); Assert.Equal(profile == 2 ? -1 : 0, value); }
        Assert.Throws<ArgumentOutOfRangeException>(() => session.GetEnabled(7, out _)); Assert.Equal(new uint[] { 1, 2, 4 }, com.Requested);
        session.Dispose(); session.Dispose(); Assert.Equal(1, com.Releases); Assert.Equal(initialized == WindowsFirewallPolicyReadSession.ChangedApartment ? 0 : 1, com.Uninitializes);
        Assert.Throws<ObjectDisposedException>(() => session.GetCurrentProfiles(out _)); Assert.Throws<ObjectDisposedException>(() => session.GetEnabled(1, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(-2147417850)]
    public void FailedComCreationDiscardsPointerWithoutInvokingAndBalancesApartment(int initialized)
    {
        using var com = new Com(initialized) { CreateStatus = unchecked((int)0x80004005) };
        Assert.Null(WindowsFirewallPolicyReadSession.Open(com.Functions)); Assert.Equal(0, com.Releases); Assert.Empty(com.Requested);
        Assert.Equal(initialized == WindowsFirewallPolicyReadSession.ChangedApartment ? 0 : 1, com.Uninitializes);
    }

    [Fact]
    public void UnusableComInitializationNeverCreatesOrUninitializesAnything()
    {
        using var com = new Com(unchecked((int)0x80004005)); Assert.Null(WindowsFirewallPolicyReadSession.Open(com.Functions));
        Assert.Equal(0, com.Creates); Assert.Equal(0, com.Uninitializes); Assert.Equal(0, com.Releases);
    }

    [Fact]
    public void SuccessfulCreateWithoutPointerAndThrownCreateBothBalanceInitialization()
    {
        var closed = 0;
        int Empty(Guid _, Guid __, out IntPtr pointer) { pointer = IntPtr.Zero; return 0; }
        Assert.Null(WindowsFirewallPolicyReadSession.Open(new(() => 0, Empty, () => closed++))); Assert.Equal(1, closed);
        int Failed(Guid _, Guid __, out IntPtr pointer) { pointer = IntPtr.Zero; throw new COMException("failed"); }
        Assert.Throws<COMException>(() => WindowsFirewallPolicyReadSession.Open(new(() => 1, Failed, () => closed++))); Assert.Equal(2, closed);
    }

    [Fact]
    public void MissingReadSlotReleasesCreatedInterfaceAndUninitializesOnce()
    {
        using var com = new Com(0); Marshal.WriteIntPtr(com.Table, WindowsFirewallPolicyReadSession.EnabledSlot * IntPtr.Size, IntPtr.Zero);
        Assert.Throws<ArgumentNullException>(() => WindowsFirewallPolicyReadSession.Open(com.Functions)); Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes);
    }

    [Fact]
    public void SecurityProjectionUsesRawCodesExactSourcesAndStableProfileIdentities()
    {
        var report = new WindowsSecurityInformationReader(new Api()).Read(); var raw = DeviceInformationTestData.Snapshot() with { SecurityInformation = report };
        var facts = DeviceTopologyInformationProjection.Create(raw, DateTimeOffset.UtcNow);
        var antivirus = Field(facts, "machine", "security_all_antivirus_state_reported"); Assert.Equal("0", antivirus.Value!.Value.GetString()); Assert.Equal("windows.security-center", antivirus.Definition.SourceId);
        Assert.Equal("3", Field(facts, "machine", "security_all_firewall_state_reported").Value!.Value.GetString());
        Assert.Equal("6", Field(facts, "machine", "security_all_windows_firewall_profiles_active_mask").Value!.Value.GetString());
        foreach (var (name, code, enabled) in new[] { ("domain", "1", false), ("private", "2", true), ("public", "4", false) })
        {
            var identity = "windows-firewall-profile|" + name;
            Assert.Equal(code, Field(facts, identity, "security_all_windows_firewall_profile_type").Value!.Value.GetString());
            var field = Field(facts, identity, "security_all_windows_firewall_enabled_configured"); Assert.Equal(enabled, field.Value!.Value.GetBoolean()); Assert.Equal("windows.firewall-policy", field.Definition.SourceId);
            Assert.Equal("boolean", field.Definition.ValueKind);
        }
        var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { CapturedAt = raw.CapturedAt.AddSeconds(1) }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { SecurityInformation = report with { PrivateFirewallEnabledConfigured = false } }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { SecurityInformation = report with { AntivirusHealthReported = 2 } }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { SecurityInformation = report with { ActiveFirewallProfiles = 0 } }));
    }

    [Fact]
    public void PersistedOrMissingReportsKeepDeclarationsButNoHistoricalSecurityValues()
    {
        var raw = DeviceInformationTestData.Snapshot() with { SecurityInformation = new WindowsSecurityInformationReader(new Api()).Read() };
        var facts = DeviceTopologyInformationProjection.Create(raw, null); Assert.All(facts.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
        var empty = DeviceTopologyInformationProjection.Create(raw with { SecurityInformation = null! }, DateTimeOffset.UtcNow);
        Assert.Null(Field(empty, "machine", "security_all_antivirus_state_reported").Value); Assert.Null(Field(empty, "windows-firewall-profile|private", "security_all_windows_firewall_enabled_configured").Value);
        var json = JsonSerializer.Serialize(raw); var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(json)!; Assert.Equal(raw.SecurityInformation, restored.SecurityInformation);
    }

    [Fact]
    public void OfficialSdkConfirmsHealthEnumsProviderBitsAndReadOnlyVtableSlots()
    {
        var assembly = typeof(WindowsSecurityInformationCollectorTests).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".Fixtures.Security.windows-sdk-security.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var sdk = JsonDocument.Parse(stream);
        var value = sdk.RootElement; Assert.Equal(IntPtr.Size, value.GetProperty("pointerSize").GetInt32()); Assert.Equal(4, value.GetProperty("hresultSize").GetInt32());
        Assert.Equal(2, value.GetProperty("variantBoolSize").GetInt32()); Assert.Equal(16, Marshal.SizeOf<Guid>());
        Assert.Equal(WindowsFirewallPolicyReadSession.CurrentProfilesSlot, value.GetProperty("currentProfilesSlot").GetInt32()); Assert.Equal(WindowsFirewallPolicyReadSession.EnabledSlot, value.GetProperty("enabledSlot").GetInt32());
        Assert.Equal(WindowsFirewallPolicyReadSession.ReleaseSlot, value.GetProperty("releaseSlot").GetInt32());
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, value.GetProperty("health").EnumerateArray().Select(item => item.GetUInt32()));
        Assert.Equal(new uint[] { 4, 1 }, value.GetProperty("providers").EnumerateArray().Select(item => item.GetUInt32()));
        Assert.Equal(new uint[] { 1, 2, 4 }, value.GetProperty("profiles").EnumerateArray().Select(item => item.GetUInt32()));
        Assert.Equal(-1, value.GetProperty("variantTrue").GetInt32()); Assert.Equal(0, value.GetProperty("variantFalse").GetInt32());
        Assert.Equal(WindowsFirewallPolicyReadSession.ClassId, Guid.Parse(value.GetProperty("classId").GetString()!)); Assert.Equal(WindowsFirewallPolicyReadSession.InterfaceId, Guid.Parse(value.GetProperty("interfaceId").GetString()!));
    }

    private static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string identity, string name)
        => Assert.Single(Assert.Single(snapshot.Devices, device => device.DeviceIdentity == identity).Fields, field => field.Definition.Name == name);

    internal sealed class Api : IWindowsSecurityInformationApi
    {
        internal readonly Dictionary<uint, (int Status, uint Value)> Health = new() { [4] = (0, 0), [1] = (0, 3) };
        internal readonly List<uint> Providers = []; internal readonly List<Policy> Policies = [];
        internal Exception? HealthError, OpenError; internal Func<Policy> PolicyFactory = () => new(); internal int Opens;
        public int GetHealth(uint provider, out uint health) { Providers.Add(provider); health = Health[provider].Value; if (provider == 4 && HealthError is not null) throw HealthError; return Health[provider].Status; }
        public IWindowsFirewallPolicyRead? OpenFirewall() { Opens++; if (OpenError is not null) throw OpenError; var policy = PolicyFactory(); Policies.Add(policy); return policy; }
        public IReadOnlyList<DeviceTopologySecurityProductInformation>? ReadProducts(uint provider) => null;
    }
    internal sealed class Policy : IWindowsFirewallPolicyRead
    {
        internal uint Mask = 6; internal int MaskStatus, PrivateStatus, Disposals; internal short PrivateValue = -1; internal Exception? PrivateError;
        internal readonly List<uint> Requested = [];
        public int GetCurrentProfiles(out uint profiles) { profiles = Mask; return MaskStatus; }
        public int GetEnabled(uint profile, out short enabled) { Requested.Add(profile); enabled = profile == 2 ? PrivateValue : (short)0; if (profile == 2 && PrivateError is not null) throw PrivateError; return profile == 2 ? PrivateStatus : 0; }
        public void Dispose() => Disposals++;
    }
    private sealed class Com : IDisposable
    {
        private readonly FirewallRelease release; private readonly FirewallGetProfiles profiles; private readonly FirewallGetEnabled enabled;
        internal readonly IntPtr Table, Pointer; internal readonly List<uint> Requested = []; internal int Releases, Uninitializes, Creates, CreateStatus;
        internal WindowsFirewallComFunctions Functions { get; }
        internal Com(int initialized)
        {
            Table = Marshal.AllocHGlobal(9 * IntPtr.Size); Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (var i = 0; i < 9; i++) Marshal.WriteIntPtr(Table, i * IntPtr.Size, IntPtr.Zero); Marshal.WriteIntPtr(Pointer, Table);
            release = self => { Assert.Equal(Pointer, self); Releases++; return 0; };
            profiles = (IntPtr self, out uint value) => { Assert.Equal(Pointer, self); value = 6; return 0; };
            enabled = (IntPtr self, uint profile, out short value) => { Assert.Equal(Pointer, self); Requested.Add(profile); value = profile == 2 ? (short)-1 : (short)0; return 0; };
            Marshal.WriteIntPtr(Table, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(release)); Marshal.WriteIntPtr(Table, 7 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(profiles)); Marshal.WriteIntPtr(Table, 8 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(enabled));
            Functions = new(() => initialized, (Guid classId, Guid interfaceId, out IntPtr pointer) => { Creates++; Assert.Equal(WindowsFirewallPolicyReadSession.ClassId, classId); Assert.Equal(WindowsFirewallPolicyReadSession.InterfaceId, interfaceId); pointer = Pointer; return CreateStatus; }, () => Uninitializes++);
        }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(Table); GC.KeepAlive(release); GC.KeepAlive(profiles); GC.KeepAlive(enabled); }
    }
}
