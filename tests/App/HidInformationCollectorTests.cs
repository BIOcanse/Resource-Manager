using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Hid;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class HidInformationCollectorTests
{
    internal const string PathA = @"\\?\HID#VID_1234&PID_5678&COL01#A#{FIXTURE}";
    internal const string PathB = @"\\?\HID#VID_1234&PID_5678&COL02#B#{FIXTURE}";
    internal const string PnpA = @"HID\VID_1234&PID_5678&COL01\A";
    internal const string IdentityA = "windows-hid-collection|" + PathA;
    internal static IReadOnlyList<DeviceTopologyHidInformation> Declarations() => new WindowsHidInformationReader(new Api()).Read()!;

    [Fact]
    public void CompiledOfficialSdkBytesAgreeWithManagedAbiAndActualDecoder()
    {
        using var fixture = Fixture(); var data = fixture.RootElement;
        Assert.Equal(data.GetProperty("capsSize").GetInt32(), Marshal.SizeOf<WindowsHidCaps>());
        Assert.Equal(data.GetProperty("buttonSize").GetInt32(), Marshal.SizeOf<WindowsHidButtonCaps>());
        Assert.Equal(data.GetProperty("valueSize").GetInt32(), Marshal.SizeOf<WindowsHidValueCaps>());
        Assert.Equal(data.GetProperty("unitsOffset").GetInt32(), (int)Marshal.OffsetOf<WindowsHidValueCaps>(nameof(WindowsHidValueCaps.UnitsExponentRaw)));
        Assert.Equal(data.GetProperty("selectionOffset").GetInt32(), (int)Marshal.OffsetOf<WindowsHidValueCaps>(nameof(WindowsHidValueCaps.Selection)));
        Assert.Equal(data.GetProperty("buttonCountOffset").GetInt32(), (int)Marshal.OffsetOf<WindowsHidButtonCaps>(nameof(WindowsHidButtonCaps.ReportCount)));
        Assert.Equal(WindowsHidInformationReader.HidStatusSuccess, data.GetProperty("success").GetInt32());
        var hid = Assert.Single(Declarations());
        Assert.Equal(new HidCollectionUsage(1, 4), hid.Usage);
        Assert.Equal(new HidReportLengths(65, 33, 17), hid.ReportLengths);
        var button = hid.Buttons![0]; var value = hid.Values![0];
        Assert.Equal((ushort)9, button.Control.UsagePage); Assert.Equal((byte)250, button.Control.ReportId);
        Assert.True(button.Control.IsAlias); Assert.True(button.Control.IsAbsolute); Assert.True(button.Control.IsStringRange);
        Assert.True(button.Control.IsDesignatorRange); Assert.Equal((ushort)0x8002, button.Control.BitField);
        Assert.Equal((ushort)35, button.Control.LinkCollection); Assert.Equal((ushort)1, button.Control.LinkUsagePage);
        Assert.Equal((ushort)4, button.Control.LinkUsage); Assert.Equal((ushort)37, button.ReportCount);
        Assert.Equal(new HidCapabilitySelection(true, 1, 16, 4, 7, 11, 15, 255, 270), button.Control.Selection);
        Assert.Equal((ushort)0xFF01, value.Control.UsagePage); Assert.Equal((byte)255, value.Control.ReportId);
        Assert.False(value.Control.IsAlias); Assert.False(value.Control.IsAbsolute);
        Assert.Equal((ushort)73, value.Control.LinkCollection); Assert.Equal((ushort)0xFF02, value.Control.LinkUsagePage);
        Assert.Equal((ushort)0xFF03, value.Control.LinkUsage); Assert.True(value.HasNull);
        Assert.Equal((ushort)32, value.BitSize); Assert.Equal((ushort)5, value.ReportCount);
        Assert.Equal(0xFFFFFFFEU, value.UnitsExponentRaw); Assert.Equal(0xFFFEEFFEU, value.UnitsRaw);
        Assert.Equal(int.MinValue, value.LogicalMinimum); Assert.Equal(int.MaxValue, value.LogicalMaximum);
        Assert.Equal(-120, value.PhysicalMinimum); Assert.Equal(65537, value.PhysicalMaximum);
        Assert.Equal(new HidCapabilitySelection(false, 48, 48, 55, 55, 66, 66, 77, 77), value.Control.Selection);
    }

    [Fact]
    public void AllReportKindsKeepAliasesAndRawVendorUsagesWithoutRangeExpansion()
    {
        var api = new Api(); var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.Equal(IdentityA, hid.DeviceIdentity); Assert.Equal(PnpA, hid.PnpInstanceId);
        Assert.Equal(["input","output","feature"], hid.Buttons!.Select(b => b.Control.ReportType).ToArray());
        Assert.Equal(["input","output","feature"], hid.Values!.Select(v => v.Control.ReportType).ToArray());
        Assert.Equal(1, api.VersionReads); Assert.Equal(1, api.EnumerationReads); Assert.Equal([PathA], api.Opened);
        Assert.Equal(3, api.Sessions[PathA].ButtonCalls.Count); Assert.Equal(3, api.Sessions[PathA].ValueCalls.Count);
        Assert.True(api.Sessions[PathA].Disposed);
        Assert.All(hid.Buttons!, static b => Assert.True(b.Control.IsAlias));
        Assert.All(hid.Values!, static v => Assert.Equal((ushort)0xFF01, v.Control.UsagePage));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(2, 37)]
    [InlineData(3, 37)]
    public void ButtonCountRequiresActualParserVersionWhileValueCountRemainsIndependent(int? version, int? expected)
    {
        var api = new Api { Version = (uint?)version };
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.All(hid.Buttons!, button => Assert.Equal((ushort?)expected, button.ReportCount));
        Assert.All(hid.Values!, static value => Assert.Equal((ushort)5, value.ReportCount));
    }

    [Fact]
    public void OfficialHeaderVersionFallbackDistinguishesMissingExportFromQueryFailure()
    {
        Assert.Equal(1U, WindowsHidNativeApi.ReadVersion(static () => throw new EntryPointNotFoundException()));
        Assert.Equal(2U, WindowsHidNativeApi.ReadVersion(static () => (0, 2U)));
        Assert.Equal(2U, WindowsHidNativeApi.ReadVersion(static () => (WindowsHidInformationReader.HidStatusSuccess, 2U)));
        Assert.Null(WindowsHidNativeApi.ReadVersion(static () => (-1, 2U)));
        Assert.Null(WindowsHidNativeApi.ReadVersion(static () => (0, 0U)));
        var api = new Api { VersionFailure = true };
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.All(hid.Buttons!, static b => Assert.Null(b.ReportCount)); Assert.NotNull(hid.Usage); Assert.NotNull(hid.Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidOrAmbiguousEnumerationNeverOpensDevicesOrQueriesVersion(int mode)
    {
        var api = new Api();
        api.Collections = mode switch {
            0 => null,
            1 => [new(PathA, PnpA), new(PathA.ToLowerInvariant(), PnpA)],
            2 => [new(PathA, "")],
            3 => [new(PathA+"\n", PnpA)],
            4 => Enumerable.Range(0,257).Select(i => new WindowsHidCollection(PathA+i, PnpA)).ToArray(),
            _ => [new(new string('x',8193), PnpA)]
        };
        Assert.Null(new WindowsHidInformationReader(api).Read());
        Assert.Empty(api.Opened); Assert.Equal(0, api.VersionReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OpenAndHeaderFailureKeepKnownPnpAndDisposeExistingSession(int failure)
    {
        var api = new Api { OpenFailure = failure == 0 };
        api.Sessions[PathA].CapsStatus = failure == 1 ? 0 : WindowsHidInformationReader.HidStatusSuccess;
        api.Sessions[PathA].CapsThrows = failure == 2;
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.Equal(PnpA, hid.PnpInstanceId); Assert.Null(hid.Usage); Assert.Null(hid.ReportLengths);
        Assert.Null(hid.Buttons); Assert.Null(hid.Values);
        Assert.Empty(api.Sessions[PathA].ButtonCalls); Assert.Empty(api.Sessions[PathA].ValueCalls);
        Assert.Equal(failure != 0, api.Sessions[PathA].Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CapabilityGroupFailureNeverPublishesTruncatedDataOrClearsOtherGroup(int mode)
    {
        var api = new Api(); var session = api.Sessions[PathA];
        session.ButtonFailure = mode is 0 or 1 or 2 ? mode : -1;
        session.ValueFailure = mode is 3 or 4 or 5 ? mode-3 : -1;
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.NotNull(hid.Usage); Assert.NotNull(hid.ReportLengths);
        if (mode < 3) { Assert.Null(hid.Buttons); Assert.Equal(3, hid.Values!.Count); }
        else { Assert.Null(hid.Values); Assert.Equal(3, hid.Buttons!.Count); }
        Assert.True(session.Disposed);
    }

    [Fact]
    public void TwoCollectionsOfSameDeviceStaySeparateAndFailedCollectionDoesNotAffectAnother()
    {
        var api = new Api { Collections = [new(PathB, PnpA), new(PathA, PnpA)] };
        api.Sessions[PathB] = new Session { Header = Header() with { UsagePage = 0xFF01, Usage = 0x31 } };
        api.Sessions[PathA].CapsStatus = 0;
        var result = new WindowsHidInformationReader(api).Read()!;
        Assert.Equal(2, result.Count); Assert.Equal(2, result.Select(h => h.DeviceIdentity).Distinct().Count());
        var first = Assert.Single(result, h => h.DeviceIdentity == IdentityA);
        var second = Assert.Single(result, h => h.DeviceIdentity != IdentityA);
        Assert.Null(first.Usage); Assert.Equal(new HidCollectionUsage(0xFF01,0x31), second.Usage);
        Assert.All(result, static h => Assert.Equal(PnpA, h.PnpInstanceId));
        Assert.All(api.Sessions.Values, static s => Assert.True(s.Disposed));
    }

    [Fact]
    public void ZeroCapabilitiesAreSuccessfulEmptyArraysAndDoNotCallEitherCapabilityApi()
    {
        var api = new Api(); api.Sessions[PathA].Header = new() { UsagePage = 1, Usage = 6 };
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.Empty(hid.Buttons!); Assert.Empty(hid.Values!); Assert.Equal(new HidReportLengths(0,0,0), hid.ReportLengths);
        Assert.Empty(api.Sessions[PathA].ButtonCalls); Assert.Empty(api.Sessions[PathA].ValueCalls);
        api.Collections = [];
        api.VersionReads = 0; api.Opened.Clear();
        Assert.Empty(new WindowsHidInformationReader(api).Read()!); Assert.Equal(0, api.VersionReads); Assert.Empty(api.Opened);
    }

    [Fact]
    public void CapabilityLimitsAreIndependentAndNeverTurnPartialDataIntoACompleteField()
    {
        var api = new Api();
        api.Sessions[PathA].Header = Header() with { NumberInputButtonCaps = 2049 };
        var hid = Assert.Single(new WindowsHidInformationReader(api).Read()!);
        Assert.Null(hid.Buttons); Assert.Equal(3, hid.Values!.Count); Assert.Empty(api.Sessions[PathA].ButtonCalls);
        api = new Api { Collections = Enumerable.Range(0,9).Select(i => new WindowsHidCollection(PathA+i, PnpA)).ToArray() };
        api.Sessions.Clear();
        foreach (var collection in api.Collections!) api.Sessions[collection.DevicePath] = new Session {
            Header = new() { UsagePage = 1, Usage = 4, NumberInputButtonCaps = 2048 } };
        var records = new WindowsHidInformationReader(api).Read()!;
        Assert.Equal(9, records.Count);
        Assert.All(records.Take(8), static h => Assert.Equal(2048,h.Buttons!.Count));
        Assert.Null(records[8].Buttons); Assert.NotNull(records[8].Usage); Assert.Empty(records[8].Values!);
        Assert.Empty(api.Sessions[PathA+8].ButtonCalls);
    }

    [Fact]
    public void OriginalInformationProjectionPublishesTypedValuesAndClearsAllPersistedFields()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with { HidInformation = Declarations() };
        var hid = Assert.Single(DeviceTopologyInformationProjection.Create(snapshot,DateTimeOffset.UtcNow).Devices,
            static d => d.DeviceIdentity == IdentityA);
        Assert.Equal("hid_collection",hid.ObjectType); Assert.Equal(5,hid.Fields.Count);
        Assert.All(hid.Fields, static field => { Assert.Equal("hid",field.Definition.Resource); Assert.Equal("all",field.Definition.Vendor);
            Assert.Equal("windows.hid.capabilities",field.Definition.SourceId); Assert.NotNull(field.Value); });
        var report = Assert.Single(hid.Fields,static f=>f.Definition.Name=="hid_all_report_length_declared");
        Assert.Equal((ushort)65, report.Value!.Value.GetProperty("inputBytes").GetUInt16());
        var value = Assert.Single(hid.Fields,static f=>f.Definition.Name=="hid_all_value_capability").Value!.Value[0];
        Assert.Equal(int.MinValue,value.GetProperty("logicalMinimum").GetInt32());
        Assert.Equal(0xFFFFFFFEU,value.GetProperty("unitsExponentRaw").GetUInt32());
        var persisted=Assert.Single(DeviceTopologyInformationProjection.Create(snapshot,null).Devices,static d=>d.DeviceIdentity==IdentityA);
        Assert.All(persisted.Fields,static f=>Assert.Null(f.Value));
    }

    [Fact]
    public void CollectionEnumerationOrderDoesNotChangeTopologyIdentity()
    {
        var declarations = Declarations(); var snapshot=DeviceInformationTestData.Snapshot() with {
            HidInformation = [declarations[0],declarations[0] with { DeviceIdentity="second" }] };
        var comparer=new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(snapshot),comparer.ComputeHash(snapshot with { HidInformation=snapshot.HidInformation!.Reverse().ToArray() }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InterfaceDetailDecoderRequiresCompleteUtf16PathAndIgnoresOnlyPaddingAfterNull(int mode)
    {
        byte[] bytes = [8,0,0,0,..Encoding.Unicode.GetBytes(PathA+"\0")];
        if (mode == 1) bytes = bytes[..^2];
        if (mode == 2) bytes = [..bytes,0];
        if (mode == 3) bytes = [8,0,0,0,0,0xD8,0,0];
        if (mode == 4) bytes = [..bytes,0,0xD8];
        Assert.Equal(mode is 0 or 4?PathA:null,WindowsHidNativeApi.DecodeInterfacePath(bytes));
    }

    private static JsonDocument Fixture()
    {
        var assembly=Assembly.GetExecutingAssembly();
        var name=Assert.Single(assembly.GetManifestResourceNames(),static n=>n.EndsWith("Fixtures.Hid.windows-sdk-layout.json",StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }
    private static T Sdk<T>(string key) where T:struct
    {
        using var fixture=Fixture(); var bytes=Convert.FromHexString(fixture.RootElement.GetProperty(key).GetString()!);
        Assert.Equal(Marshal.SizeOf<T>(),bytes.Length); var pointer=Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes,0,pointer,bytes.Length); return Marshal.PtrToStructure<T>(pointer); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static WindowsHidCaps Header() => Sdk<WindowsHidCaps>("capsHex");
    internal sealed class RepeatedApi : IWindowsHidCollectionApi
    {
        private int enumerations, opens;
        internal int Enumerations => Volatile.Read(ref enumerations);
        internal int Opens => Volatile.Read(ref opens);
        public IReadOnlyList<WindowsHidCollection>? Enumerate() { Interlocked.Increment(ref enumerations); return [new(PathA,PnpA)]; }
        public uint? ReadVersion() => 2;
        public IWindowsHidCollectionSession? Open(WindowsHidCollection collection) { Interlocked.Increment(ref opens); return new Session(); }
    }
    private sealed class Api : IWindowsHidCollectionApi
    {
        internal IReadOnlyList<WindowsHidCollection>? Collections = [new(PathA,PnpA)];
        internal uint? Version=2;
        internal int VersionReads,EnumerationReads;
        internal bool OpenFailure,VersionFailure;
        internal List<string> Opened=[];
        internal Dictionary<string,Session> Sessions=new(StringComparer.OrdinalIgnoreCase) { [PathA]=new() };
        public IReadOnlyList<WindowsHidCollection>? Enumerate() { EnumerationReads++; return Collections; }
        public uint? ReadVersion() { VersionReads++; if (VersionFailure) throw new InvalidOperationException("version failed"); return Version; }
        public IWindowsHidCollectionSession? Open(WindowsHidCollection collection) { Opened.Add(collection.DevicePath); return OpenFailure?null:Sessions[collection.DevicePath]; }
    }
    private sealed class Session : IWindowsHidCollectionSession
    {
        internal WindowsHidCaps Header=HidInformationCollectorTests.Header();
        internal WindowsHidButtonCaps Button=Sdk<WindowsHidButtonCaps>("buttonHex");
        internal WindowsHidValueCaps Value=Sdk<WindowsHidValueCaps>("valueHex");
        internal int CapsStatus=WindowsHidInformationReader.HidStatusSuccess,ButtonFailure=-1,ValueFailure=-1;
        internal bool CapsThrows,Disposed;
        internal List<WindowsHidReportType> ButtonCalls=[],ValueCalls=[];
        public int ReadCaps(out WindowsHidCaps caps) { caps=Header; if (CapsThrows) throw new InvalidOperationException("caps failed"); return CapsStatus; }
        public int ReadButtons(WindowsHidReportType report,WindowsHidButtonCaps[] caps,ref ushort count)
        {
            ButtonCalls.Add(report); Assert.False(Disposed); Array.Fill(caps,Button);
            if (ButtonFailure==0) return 0; if (ButtonFailure==1) count--; if (ButtonFailure==2) throw new InvalidOperationException("button failed");
            return WindowsHidInformationReader.HidStatusSuccess;
        }
        public int ReadValues(WindowsHidReportType report,WindowsHidValueCaps[] caps,ref ushort count)
        {
            ValueCalls.Add(report); Assert.False(Disposed); Array.Fill(caps,Value);
            if (ValueFailure==0) return 0; if (ValueFailure==1) count++; if (ValueFailure==2) throw new InvalidOperationException("value failed");
            return WindowsHidInformationReader.HidStatusSuccess;
        }
        public void Dispose() { Assert.False(Disposed); Disposed=true; }
    }
}
