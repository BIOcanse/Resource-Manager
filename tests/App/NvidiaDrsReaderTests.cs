using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaDrsReaderTests
{
    private static readonly NvidiaDrsReadLimits Limits = new(8, 16, 64, 2, 1_000_000);
    private static readonly NvidiaDrsProfileRequest[] Selected = [new("Game", new uint[] { 7 })];

    [Fact]
    public void OfficialAbiAndCallingConventionArePreserved()
    {
        Assert.Equal(4100, Marshal.SizeOf<NvDrsBinary>());
        Assert.Equal(4100, Marshal.SizeOf<NvDrsValue>());
        Assert.Equal(12320, Marshal.SizeOf<NvDrsSetting>());
        Assert.Equal(4116, Marshal.SizeOf<NvDrsProfile>());
        Assert.Equal((nint)4100, Marshal.OffsetOf<NvDrsSetting>(nameof(NvDrsSetting.Id)));
        Assert.Equal((nint)4120, Marshal.OffsetOf<NvDrsSetting>(nameof(NvDrsSetting.Predefined)));
        Assert.Equal((nint)8220, Marshal.OffsetOf<NvDrsSetting>(nameof(NvDrsSetting.Current)));
        Assert.Equal((nint)4100, Marshal.OffsetOf<NvDrsProfile>(nameof(NvDrsProfile.GpuSupport)));
        Assert.Equal(0x00013020U, NvDrsSetting.Create().Version);
        Assert.Equal(0x00011014U, NvDrsProfile.Create().Version);
        var delegates = typeof(NvidiaNvapiReader).GetNestedTypes(BindingFlags.NonPublic)
            .Where(type => typeof(MulticastDelegate).IsAssignableFrom(type));
        Assert.All(delegates, type => Assert.Equal(CallingConvention.Cdecl,
            type.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()!.CallingConvention));
        Assert.DoesNotContain(typeof(INvidiaNvapiDrsApi).GetMethods(), method =>
            method.Name.StartsWith("Set", StringComparison.Ordinal) || method.Name.Contains("Save", StringComparison.Ordinal));
    }

    [Fact]
    public void NoDemandDoesNotInitializeOrCreateAndSuccessfulEmptyReallyEnumerates()
    {
        var api = new RecordingApi();
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(new(0, 0, 0, 0, 0)));
        Assert.Empty(reader.ReadDrs([], Limits)!.Profiles);
        Assert.Empty(api.Calls);
        Assert.Empty(reader.DiscoverDrs(Limits with { MaximumPayloadBytes = 0 })!.Profiles);
        Assert.Equal(new[] { "initialize", "create:1", "load:1", "count", "profile:0", "destroy:1" }, api.Calls);
    }

    [Fact]
    public void RequestCopiesUseOnlyBudgetedIndexesAndNeverAnInconsistentEnumerator()
    {
        var ids = new InconsistentList<uint>([7, 8], 1);
        var requests = new InconsistentList<NvidiaDrsProfileRequest>(
            [new("Game", ids), new("Unselected", new uint[] { 9 })], 1);
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 0), Dword(8, 8)]));
        using var reader = new NvidiaNvapiReader(api);
        var limits = new NvidiaDrsReadLimits(1, 1, 1, 1, 4116 + 12320);
        var profile = Assert.Single(reader.ReadDrs(requests, limits)!.Profiles);
        Assert.Equal(7U, Assert.Single(profile.Settings).SettingId);
        Assert.Equal(1, requests.IndexReads); Assert.Equal(1, ids.IndexReads);
        Assert.Equal(0, requests.EnumeratorCalls); Assert.Equal(0, ids.EnumeratorCalls);
        Assert.DoesNotContain("get:8", api.Calls);
        Assert.DoesNotContain("find:Unselected", api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CountChangingDuringAnIndexedCopyIsRejectedBeforeAnyNativeCall(bool profileCountChanges)
    {
        var ids = new InconsistentList<uint>([7], 1) { ChangeCountAfterFirstRead = !profileCountChanges };
        var requests = new InconsistentList<NvidiaDrsProfileRequest>([new("Game", ids)], 1)
            { ChangeCountAfterFirstRead = profileCountChanges };
        var api = new RecordingApi();
        using var reader = new NvidiaNvapiReader(api);
        Assert.Throws<ArgumentException>(() => reader.ReadDrs(requests, Limits));
        Assert.Empty(api.Calls);
        Assert.Equal(0, requests.EnumeratorCalls); Assert.Equal(0, ids.EnumeratorCalls);
    }

    [Fact]
    public void TotalBudgetIsProvedBeforeCopyingAnySettingElement()
    {
        var ids = new InconsistentList<uint>([7, 8], 2);
        var api = new RecordingApi();
        using var reader = new NvidiaNvapiReader(api);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadDrs([new("Game", ids)],
            Limits with { MaximumTotalSettingCount = 1 }));
        Assert.Equal(0, ids.IndexReads); Assert.Equal(0, ids.EnumeratorCalls);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void PagesAreCompleteAndProfileNamesAndSettingIdsAreTheObjectKeys()
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(1, 0), Dword(2, 2), Dword(3, 3)]),
            new RecordedProfile("game", [Dword(1, 4)]));
        using var reader = new NvidiaNvapiReader(api);
        var profiles = reader.DiscoverDrs(Limits)!.Profiles;
        Assert.Equal(2, profiles.Length);
        Assert.Equal(new uint[] { 1, 2, 3 }, profiles[0].Settings.Select(value => value.SettingId));
        Assert.NotEqual(profiles[0].DeviceIdentity, profiles[1].DeviceIdentity);
        Assert.EndsWith("|setting|00000001", profiles[0].Settings[0].IdentityOf(profiles[0]));
        Assert.NotEqual(profiles[0].Settings[0].IdentityOf(profiles[0]), profiles[1].Settings[0].IdentityOf(profiles[1]));
        Assert.Contains("settings:1:0:2", api.Calls);
        Assert.Contains("settings:1:2:1", api.Calls);
        Assert.Contains("settings:1:3:1", api.Calls);
        Assert.Equal(0U, Assert.IsType<NvidiaDrsDwordValue>(profiles[0].Settings[0].CurrentValue).Value);
    }

    [Fact]
    public void TypedCurrentValuesPreserveAllBitsAndUnknownStringIsNotGuessed()
    {
        var binary = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
        var wide = Row(3, 3); WriteText(wide, 8220, "配置 𐐷");
        var qword = Row(4, 4); U64(qword, 8220, ulong.MaxValue);
        var unknownString = Row(5, 2); WriteText(unknownString, 8220, "do not reinterpret");
        var unknownFuture = Row(6, 99);
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(1, 0), Binary(2, binary), wide, qword,
            unknownString, unknownFuture, Binary(7, []), Text(8, "")]));
        using var reader = new NvidiaNvapiReader(api);
        var values = reader.DiscoverDrs(Limits)!.Profiles[0].Settings;
        Assert.Equal(0U, Assert.IsType<NvidiaDrsDwordValue>(values[0].CurrentValue).Value);
        Assert.Equal<byte>(binary, Assert.IsType<NvidiaDrsBinaryValue>(values[1].CurrentValue).Value);
        Assert.Equal("配置 𐐷", Assert.IsType<NvidiaDrsUnicodeValue>(values[2].CurrentValue).Value);
        Assert.Equal(ulong.MaxValue, Assert.IsType<NvidiaDrsQwordValue>(values[3].CurrentValue).Value);
        Assert.Equal(2U, values[4].TypeRaw); Assert.Null(values[4].CurrentValue);
        Assert.Equal(99U, values[5].TypeRaw); Assert.Null(values[5].CurrentValue);
        Assert.Empty(Assert.IsType<NvidiaDrsBinaryValue>(values[6].CurrentValue).Value);
        Assert.Equal("", Assert.IsType<NvidiaDrsUnicodeValue>(values[7].CurrentValue).Value);
        Assert.All(values, value =>
        {
            Assert.Equal(0x80000003U, value.LocationRaw);
            Assert.Equal(0x80000007U, value.IsCurrentPredefinedRaw);
            Assert.Equal(0x80000009U, value.IsPredefinedValidRaw);
        });
    }

    [Fact]
    public void InvalidBinaryAndUnicodeHaveUnknownValuesWithoutReadingPastTheUnion()
    {
        var tooLong = Binary(1, []); U32(tooLong, 8220, 4097);
        var noTerminator = Row(2, 3);
        for (var index = 0; index < 2048; index++) BinaryPrimitives.WriteUInt16LittleEndian(noTerminator.AsSpan(8220 + index * 2), 65);
        var invalidSurrogate = Row(3, 3); BinaryPrimitives.WriteUInt16LittleEndian(invalidSurrogate.AsSpan(8220), 0xD800);
        var maximum = Text(4, new string('a', 2047));
        var invalidOptionalName = Dword(5, 9); BinaryPrimitives.WriteUInt16LittleEndian(invalidOptionalName.AsSpan(4), 0xD800);
        using var reader = new NvidiaNvapiReader(new RecordingApi(new RecordedProfile("Game", [tooLong, noTerminator, invalidSurrogate, maximum, invalidOptionalName])));
        var settings = reader.DiscoverDrs(Limits)!.Profiles[0].Settings;
        Assert.All(settings.Take(3), value => Assert.Null(value.CurrentValue));
        Assert.Equal(2047, Assert.IsType<NvidiaDrsUnicodeValue>(settings[3].CurrentValue).Value.Length);
        Assert.Null(settings[4].NameDeclared);
        Assert.Equal(9U, Assert.IsType<NvidiaDrsDwordValue>(settings[4].CurrentValue).Value);
    }

    [Fact]
    public void MalformedProfileNamesAreRejectedWithoutReplacementOrImplicitFullDatabaseReads()
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 1)]));
        using var reader = new NvidiaNvapiReader(api);
        Assert.Throws<EncoderFallbackException>(() => reader.ReadDrs([new("bad\uD800", new uint[] { 7 })], Limits));
        Assert.Throws<ArgumentException>(() => reader.ReadDrs([new("bad\0name", new uint[] { 7 })], Limits));
        Assert.Throws<ArgumentNullException>(() => reader.ReadDrs(null!, Limits));
        Assert.Empty(reader.ReadDrs([], Limits)!.Profiles);
        Assert.Empty(api.Calls);
        api.ReturnedProfileName = "\uD800";
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(1, api.DestroyCount);
    }

    [Fact]
    public void ExactConfiguredCapacityIsAcceptedAndAnUntrustedHugeCountNeverAllocatesItsSize()
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(1, 0), Dword(2, 0), Dword(3, 0)]));
        using var reader = new NvidiaNvapiReader(api);
        var limits = new NvidiaDrsReadLimits(1, 3, 3, 2, 4116 + 3 * 12320);
        Assert.Equal(3, reader.DiscoverDrs(limits)!.Profiles[0].Settings.Length);
        api.ReportedProfileCount = uint.MaxValue;
        var before = api.Calls.Count;
        Assert.Null(reader.DiscoverDrs(limits));
        Assert.DoesNotContain(api.Calls.Skip(before), call => call.StartsWith("profile:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("profiles")]
    [InlineData("settings")]
    [InlineData("total")]
    [InlineData("bytes")]
    [InlineData("extraTail")]
    [InlineData("overrunCount")]
    [InlineData("zeroProgress")]
    [InlineData("duplicateId")]
    [InlineData("duplicateName")]
    public void IncompleteOrOverBudgetDirectoryNeverReturnsATruncatedPrefix(string fault)
    {
        var profile = new RecordedProfile("Game", [Dword(1, 1), Dword(2, 2), Dword(3, 3)]);
        var api = new RecordingApi(profile);
        var limits = Limits;
        switch (fault)
        {
            case "profiles": api.ReportedProfileCount = 9; break;
            case "settings": limits = limits with { MaximumSettingsPerProfile = 2 }; break;
            case "total": limits = limits with { MaximumTotalSettingCount = 2 }; break;
            case "bytes": limits = limits with { MaximumPayloadBytes = 4116 + 2 * 12320 }; break;
            case "extraTail": profile.ReportedSettingCount = 2; break;
            case "overrunCount": api.OverrunCount = true; break;
            case "zeroProgress": api.ZeroProgress = true; break;
            case "duplicateId": profile.Settings[2] = Dword(1, 3); break;
            case "duplicateName": api.Profiles.Add(new("Game", [])); break;
        }
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(limits));
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(1, api.DestroyCount);
    }

    [Fact]
    public void SelectedReadDoesNotEnumerateOrInventAnOverrideLocation()
    {
        var row = Dword(7, 0); U32(row, 4108, 2);
        var api = new RecordingApi(new RecordedProfile("Game", [row, Dword(9, 9)]));
        using var reader = new NvidiaNvapiReader(api);
        var profile = Assert.Single(reader.ReadDrs(Selected, Limits)!.Profiles);
        var setting = Assert.Single(profile.Settings);
        Assert.Equal(7U, setting.SettingId);
        Assert.Equal(2U, setting.LocationRaw); // Base-profile origin remains base, not an explicit per-profile override.
        Assert.DoesNotContain(api.Calls, call => call == "count" || call.StartsWith("profile:", StringComparison.Ordinal)
            || call.StartsWith("settings:", StringComparison.Ordinal));
        Assert.Contains("find:Game", api.Calls);
        Assert.Contains("get:7", api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSettingOrProfileNameRoundTripFailureDoesNotReuseOldData(bool wrongName)
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 1)]));
        using var reader = new NvidiaNvapiReader(api);
        Assert.NotNull(reader.ReadDrs(Selected, Limits)!.Profiles[0].Settings[0].CurrentValue);
        if (wrongName) api.ReturnedProfileName = "game";
        else api.SettingStatus = -160;
        var setting = reader.ReadDrs(Selected, Limits)!.Profiles[0].Settings[0];
        Assert.Null(setting.CurrentValue);
        Assert.Null(setting.TypeRaw);
        Assert.Equal(2, api.DestroyCount);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("load")]
    [InlineData("exception")]
    public void UnsupportedAndGlobalFailuresAreUnavailableRatherThanSuccessfulEmpty(string fault)
    {
        var api = new RecordingApi();
        if (fault == "unsupported") api.Supported = false;
        if (fault == "load") api.LoadStatus = -1;
        if (fault == "exception") api.OnLoad = () => throw new InvalidOperationException("recorded native error");
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(fault == "unsupported" ? 0 : 1, api.CreateCount);
        Assert.Equal(api.CreateCount, api.DestroyCount);
    }

    [Fact]
    public void CancellationDestroysSessionAndTheNextCallLoadsANewSession()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 1)])) { OnLoad = cancellation.Cancel };
        using var reader = new NvidiaNvapiReader(api);
        Assert.Throws<OperationCanceledException>(() => reader.ReadDrs(Selected, Limits, cancellation.Token));
        Assert.Equal(1, api.DestroyCount);
        api.OnLoad = null;
        Assert.NotNull(reader.ReadDrs(Selected, Limits));
        Assert.Equal(2, api.CreateCount);
        Assert.Equal(2, api.LoadCount);
        Assert.Equal(2, api.DestroyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFailureOrCancellationPlusDestroyFailurePreservesTheOriginalOutcome(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        var api = new RecordingApi { ThrowDestroyOnce = true, LoadStatus = -1 };
        api.DestroyStatuses.Enqueue(-1);
        if (canceled) { api.LoadStatus = 0; api.OnLoad = cancellation.Cancel; }
        var reader = new NvidiaNvapiReader(api);
        if (canceled)
        {
            var error = Assert.Throws<OperationCanceledException>(() => reader.DiscoverDrs(Limits, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(new nint[] { 1 }, api.LiveSessions.Order());
        api.OnLoad = null;
        Assert.Null(reader.DiscoverDrs(Limits)); // Pending cleanup still fails; there must be no second Create/Load.
        Assert.Equal(1, api.CreateCount); Assert.Equal(1, api.LoadCount);
        Assert.Equal(0, api.UnloadCount); Assert.Equal(0, api.FreeCount);
        reader.Dispose();
        Assert.Empty(api.LiveSessions); Assert.Equal(1, api.UnloadCount); Assert.Equal(1, api.FreeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonzeroSessionReturnedByAFailedOrThrowingCreateStillHasACleanupObligation(bool throws)
    {
        var api = new RecordingApi { CreateStatus = -1, ThrowCreate = throws };
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(1, api.CreateCount); Assert.Equal(1, api.DestroyCount);
        Assert.Equal(0, api.LoadCount); Assert.Empty(api.LiveSessions);
    }

    [Fact]
    public void ThrowingDestroyAndUnloadDoNotLoseTheOutstandingNativeObligation()
    {
        var api = new RecordingApi { ThrowDestroyOnce = true, ThrowUnloadOnce = true };
        var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(new nint[] { 1 }, api.LiveSessions.Order());
        Assert.Throws<InvalidOperationException>(reader.Dispose);
        Assert.Empty(api.LiveSessions); Assert.Equal(0, api.FreeCount);
        reader.Dispose();
        Assert.Equal(1, api.CreateCount); Assert.Equal(2, api.DestroyCount);
        Assert.Equal(2, api.UnloadCount); Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public void DestroyFailureRetainsExactlyOneObligationAndBlocksNewSessionsUntilCleanup()
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 1)]));
        api.DestroyStatuses.Enqueue(-1); api.DestroyStatuses.Enqueue(-1);
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.ReadDrs(Selected, Limits));
        Assert.Null(reader.ReadDrs(Selected, Limits));
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(1, api.LoadCount);
        Assert.Equal(new nint[] { 1 }, api.LiveSessions.Order());
        Assert.NotNull(reader.ReadDrs(Selected, Limits));
        Assert.Equal(2, api.CreateCount);
        Assert.Empty(api.LiveSessions);
        Assert.Equal(new[] { "destroy:1", "destroy:1", "destroy:1", "destroy:2" }, api.Calls.Where(call => call.StartsWith("destroy:", StringComparison.Ordinal)));
    }

    [Fact]
    public void DisposeFailureRetainsSessionOrUnloadObligationAndNeverFreesTheModuleEarly()
    {
        var api = new RecordingApi(new RecordedProfile("Game", [Dword(7, 1)]));
        api.DestroyStatuses.Enqueue(-1); api.DestroyStatuses.Enqueue(-1);
        api.UnloadStatuses.Enqueue(-1);
        var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.ReadDrs(Selected, Limits));
        Assert.Throws<InvalidOperationException>(reader.Dispose);
        Assert.Equal(0, api.UnloadCount); Assert.Equal(0, api.FreeCount);
        Assert.Throws<ObjectDisposedException>(() => reader.DiscoverDrs(Limits));
        Assert.Throws<InvalidOperationException>(reader.Dispose);
        Assert.Empty(api.LiveSessions);
        Assert.Equal(1, api.UnloadCount); Assert.Equal(0, api.FreeCount);
        reader.Dispose(); reader.Dispose();
        Assert.Equal(2, api.UnloadCount); Assert.Equal(1, api.FreeCount);
        Assert.Equal(0, api.Module.References);
    }

    [Fact]
    public void InitializationFailureFreesOnlyTheUninitializedLibraryAndDoesNotCallUnload()
    {
        var api = new RecordingApi { InitializeStatus = -4 };
        using var reader = new NvidiaNvapiReader(api);
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(0, api.UnloadCount);
        Assert.Equal(0, api.CreateCount);
        Assert.Null(reader.DiscoverDrs(Limits));
        Assert.Equal(1, api.InitializeCount);
    }

    [Fact]
    public void TwoReadersPairTheirOwnReferencesAndDisposingOneDoesNotUnloadTheOther()
    {
        var module = new RecordedModule();
        var first = new RecordingApi { Module = module };
        var second = new RecordingApi { Module = module };
        using var a = new NvidiaNvapiReader(first);
        using var b = new NvidiaNvapiReader(second);
        Assert.NotNull(a.DiscoverDrs(Limits)); Assert.NotNull(b.DiscoverDrs(Limits));
        Assert.Equal(2, module.References);
        a.Dispose();
        Assert.Equal(1, module.References);
        Assert.NotNull(b.DiscoverDrs(Limits));
        Assert.Equal(1, second.InitializeCount);
        Assert.Equal(0, second.UnloadCount); Assert.Equal(0, second.FreeCount);
        b.Dispose(); Assert.Equal(0, module.References);
        Assert.Equal(1, first.UnloadCount); Assert.Equal(1, second.UnloadCount);
    }

    [Fact]
    public async Task DisposeWaitsForNativeOperationAndThenPreventsAllNewReads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var api = new RecordingApi { OnLoad = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(3))); } };
        var reader = new NvidiaNvapiReader(api);
        var read = Task.Run(() => reader.DiscoverDrs(Limits));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        Task dispose;
        try
        {
            dispose = Task.Run(() => { disposeStarted.Set(); reader.Dispose(); });
            Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(3)));
            Assert.False(dispose.Wait(TimeSpan.FromMilliseconds(30)));
            Assert.Equal(0, api.UnloadCount); Assert.Equal(0, api.FreeCount);
        }
        finally { release.Set(); }
        await read; await dispose;
        Assert.Equal(1, api.DestroyCount); Assert.Equal(1, api.UnloadCount); Assert.Equal(1, api.FreeCount);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadDrs([], Limits));
        Assert.Throws<ObjectDisposedException>(() => { _ = reader.ReadAsync(new(null, false, false, false), null, CancellationToken.None); });
    }

    private static byte[] Row(uint id, uint type)
    {
        var row = new byte[12320]; U32(row, 0, 0x00013020); WriteText(row, 4, "setting");
        U32(row, 4100, id); U32(row, 4104, type); U32(row, 4108, 0x80000003);
        U32(row, 4112, 0x80000007); U32(row, 4116, 0x80000009);
        return row;
    }
    private static byte[] Dword(uint id, uint value) { var row = Row(id, 0); U32(row, 8220, value); return row; }
    private static byte[] Binary(uint id, byte[] value)
    {
        var row = Row(id, 1); U32(row, 8220, (uint)value.Length); value.CopyTo(row, 8224); return row;
    }
    private static byte[] Text(uint id, string value) { var row = Row(id, 3); WriteText(row, 8220, value); return row; }
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void WriteText(byte[] bytes, int offset, string value)
    {
        // Preserve even deliberately malformed test UTF-16; Encoding.Unicode's replacement fallback would hide the bug.
        for (var index = 0; index < value.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + index * 2), value[index]);
    }
    private static T Structure<T>(byte[] bytes) where T : struct
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    private sealed class RecordedModule { internal int References; }
    private sealed class InconsistentList<T>(T[] values, int declaredCount) : IReadOnlyList<T>
    {
        internal int IndexReads, EnumeratorCalls;
        internal bool ChangeCountAfterFirstRead;
        private int countReads;
        public int Count => ChangeCountAfterFirstRead && ++countReads > 1 ? declaredCount + 1 : declaredCount;
        public T this[int index] { get { IndexReads++; return values[index]; } }
        public IEnumerator<T> GetEnumerator() { EnumeratorCalls++; return ((IEnumerable<T>)values).GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class RecordedProfile(string name, List<byte[]> settings)
    {
        internal string Name = name;
        internal List<byte[]> Settings = settings;
        internal uint? ReportedSettingCount;
    }

    private sealed class RecordingApi(params RecordedProfile[] profiles) : INvidiaNvapiDrsApi
    {
        internal readonly List<RecordedProfile> Profiles = [.. profiles];
        internal readonly List<string> Calls = [];
        internal readonly HashSet<nint> LiveSessions = [];
        internal readonly Queue<int> DestroyStatuses = new();
        internal readonly Queue<int> UnloadStatuses = new();
        internal RecordedModule Module = new();
        internal bool Supported = true, OverrunCount, ZeroProgress, ThrowCreate, ThrowDestroyOnce, ThrowUnloadOnce;
        internal uint? ReportedProfileCount;
        internal string? ReturnedProfileName;
        internal int InitializeStatus, LoadStatus, SettingStatus, CreateStatus;
        internal int InitializeCount, UnloadCount, FreeCount, CreateCount, DestroyCount, LoadCount;
        internal Action? OnLoad;
        private bool initialized;
        public bool IsSupported => Supported;
        public int Initialize()
        {
            Calls.Add("initialize"); InitializeCount++;
            if (InitializeStatus == 0) { initialized = true; Module.References++; }
            return InitializeStatus;
        }
        public int Unload()
        {
            Calls.Add("unload"); UnloadCount++; Assert.True(initialized); Assert.Empty(LiveSessions);
            if (ThrowUnloadOnce) { ThrowUnloadOnce = false; throw new InvalidOperationException("recorded unload failure"); }
            var status = UnloadStatuses.TryDequeue(out var next) ? next : 0;
            if (status == 0) { initialized = false; Module.References--; }
            return status;
        }
        public void FreeLibrary() { Assert.False(initialized); Assert.Empty(LiveSessions); FreeCount++; Calls.Add("free"); }
        public int CreateSession(out nint session)
        {
            Assert.True(initialized); Assert.Empty(LiveSessions); session = ++CreateCount;
            LiveSessions.Add(session); Calls.Add("create:" + session);
            if (ThrowCreate) throw new InvalidOperationException("recorded create failure after nonzero output");
            return CreateStatus;
        }
        public int DestroySession(nint session)
        {
            Assert.Contains(session, LiveSessions); DestroyCount++; Calls.Add("destroy:" + session);
            if (ThrowDestroyOnce) { ThrowDestroyOnce = false; throw new InvalidOperationException("recorded destroy failure"); }
            var status = DestroyStatuses.TryDequeue(out var next) ? next : 0;
            if (status == 0) LiveSessions.Remove(session);
            return status;
        }
        public int LoadSettings(nint session) { Assert.Contains(session, LiveSessions); LoadCount++; Calls.Add("load:" + session); OnLoad?.Invoke(); return LoadStatus; }
        public int GetNumProfiles(nint session, out uint count) { count = ReportedProfileCount ?? (uint)Profiles.Count; Calls.Add("count"); return 0; }
        public int EnumProfiles(nint session, uint index, out nint profile)
        {
            Calls.Add("profile:" + index); profile = index < Profiles.Count ? checked((nint)(index + 1)) : 0;
            return profile == 0 ? -7 : 0;
        }
        public int GetProfileInfo(nint session, nint profile, ref NvDrsProfile info)
        {
            Assert.Equal(0x00011014U, info.Version); Calls.Add("info:" + profile);
            var data = new byte[4116]; var item = Profiles[checked((int)profile) - 1];
            U32(data, 0, 0x00011014); WriteText(data, 4, ReturnedProfileName ?? item.Name);
            U32(data, 4100, 0x80000007); U32(data, 4104, 0x80000001);
            U32(data, 4108, 5); U32(data, 4112, item.ReportedSettingCount ?? (uint)item.Settings.Count);
            info = Structure<NvDrsProfile>(data); return 0;
        }
        public int FindProfileByName(nint session, string name, out nint profile)
        {
            Calls.Add("find:" + name); var index = Profiles.FindIndex(item => item.Name == name);
            profile = index < 0 ? 0 : index + 1; return index < 0 ? -163 : 0;
        }
        public int EnumSettings(nint session, nint profile, uint start, ref uint count, nint settings)
        {
            Calls.Add($"settings:{profile}:{start}:{count}");
            for (var index = 0; index < count; index++)
            {
                var initial = Marshal.PtrToStructure<NvDrsSetting>(settings + checked(index * 12320));
                Assert.Equal(0x00013020U, initial.Version);
                Assert.Equal(4096U, initial.Predefined.Binary.Length);
                Assert.Equal(4096U, initial.Current.Binary.Length);
            }
            var rows = Profiles[checked((int)profile) - 1].Settings;
            if (start >= rows.Count) { count = 0; return -7; }
            if (OverrunCount) { count++; return 0; }
            if (ZeroProgress) { count = 0; return 0; }
            count = Math.Min(count, checked((uint)rows.Count - start));
            for (var index = 0; index < count; index++) Marshal.Copy(rows[checked((int)start + index)], 0, settings + checked(index * 12320), 12320);
            return 0;
        }
        public int GetSetting(nint session, nint profile, uint id, ref NvDrsSetting setting)
        {
            Calls.Add("get:" + id); Assert.Equal(0x00013020U, setting.Version);
            Assert.Equal(4096U, setting.Predefined.Binary.Length); Assert.Equal(4096U, setting.Current.Binary.Length);
            var row = Profiles[checked((int)profile) - 1].Settings.Find(item => BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan(4100)) == id);
            if (SettingStatus != 0) return SettingStatus;
            if (row is null) return -160;
            setting = Structure<NvDrsSetting>(row); return 0;
        }
    }
}
