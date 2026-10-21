using System.Buffers.Binary;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsKeyboardScanCodeRemapOwnerTests
{
    private static readonly FieldReference KeyboardRemapField = new("machine", "keyboard_all_scan_code_remap_configured");
    private static readonly FieldReference AcPowerField = new("machine", "machine_all_ac_power_actual");

    [Fact]
    public void ParserPreservesOrderedSourceAndTargetCodesAndAcceptsDisabledTargets()
    {
        var bytes = RegistryValue(
            new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D),
            new WindowsKeyboardScanCodeRemapEntry(0xE052, 0xE053),
            new WindowsKeyboardScanCodeRemapEntry(0x0001, 0x0000));

        Assert.True(WindowsKeyboardScanCodeRemapReader.TryParse(bytes, out var reading));
        Assert.True(reading!.Configured);
        Assert.Equal(new[]
        {
            new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D),
            new WindowsKeyboardScanCodeRemapEntry(0xE052, 0xE053),
            new WindowsKeyboardScanCodeRemapEntry(0x0001, 0x0000)
        }, reading.Entries);

        Assert.True(WindowsKeyboardScanCodeRemapReader.TryParse(RegistryValue(), out var empty));
        Assert.True(empty!.Configured);
        Assert.Empty(empty.Entries);
    }

    [Fact]
    public void ParserPreservesSourceZeroAndDuplicateRecordsExactlyAsStored()
    {
        var entries = new[]
        {
            new WindowsKeyboardScanCodeRemapEntry(0, 0),
            new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D),
            new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001E)
        };

        Assert.True(WindowsKeyboardScanCodeRemapReader.TryParse(RegistryValue(entries), out var reading));
        Assert.Equal(entries, reading!.Entries);
    }

    [Fact]
    public void ParserRejectsBadHeadersCountsLengthsAndEndRecords()
    {
        var valid = RegistryValue(new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D));
        var malformed = new List<byte[]>();

        var badVersion = valid.ToArray(); badVersion[0] = 1; malformed.Add(badVersion);
        var badFlags = valid.ToArray(); badFlags[4] = 1; malformed.Add(badFlags);
        var zeroCount = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(zeroCount.AsSpan(8, 4), 0); malformed.Add(zeroCount);
        var impossibleCount = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(impossibleCount.AsSpan(8, 4), uint.MaxValue); malformed.Add(impossibleCount);
        malformed.Add(valid[..^1]);
        malformed.Add([.. valid, 0]);
        var badEnd = valid.ToArray(); badEnd[^1] = 1; malformed.Add(badEnd);

        Assert.All(malformed, bytes =>
        {
            Assert.False(WindowsKeyboardScanCodeRemapReader.TryParse(bytes, out var reading));
            Assert.Null(reading);
        });
    }

    [Fact]
    public void BoundedValueReaderQueriesSizeBeforeAllocatingAndAcceptsExactTypedFill()
    {
        var expected = RegistryValue(new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D));
        var calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery query =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 3;
                if (buffer is null)
                {
                    Assert.Equal(0u, byteCount);
                    byteCount = checked((uint)expected.Length);
                    return 0;
                }
                Assert.Equal(expected.Length, buffer.Length);
                expected.CopyTo(buffer, 0);
                byteCount = checked((uint)expected.Length);
                return 0;
            };

        var status = WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(
            query, expected.Length, out var actual);

        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Success, status);
        Assert.Equal(2, calls);
        Assert.Equal(expected, actual);
        Assert.True(WindowsKeyboardScanCodeRemapReader.TryParse(actual!, expected.Length, out _));
        Assert.False(WindowsKeyboardScanCodeRemapReader.TryParse(expected, expected.Length - 1, out _));
    }

    [Fact]
    public void BoundedValueReaderRejectsOversizeBeforeFillAndRejectsTypeOrSizeRaces()
    {
        var calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery tooLarge =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 3;
                byteCount = 65;
                return 0;
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(tooLarge, 64, out var noBytes));
        Assert.Null(noBytes);
        Assert.Equal(1, calls); // No allocation/fill call after an oversized size query.

        calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery wrongInitialType =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 1;
                byteCount = 20;
                return 0;
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(wrongInitialType, 64, out noBytes));
        Assert.Null(noBytes);
        Assert.Equal(1, calls); // Type is checked before allocating/filling.

        var expected = RegistryValue(new WindowsKeyboardScanCodeRemapEntry(0x003A, 0x001D));
        calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery typeChanged =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = buffer is null ? 3u : 1u;
                if (buffer is not null) expected.CopyTo(buffer, 0);
                byteCount = checked((uint)expected.Length);
                return 0;
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(typeChanged, expected.Length, out noBytes));
        Assert.Null(noBytes);
        Assert.Equal(2, calls);

        calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery resized =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 3;
                if (buffer is null) byteCount = checked((uint)expected.Length);
                else
                {
                    expected.AsSpan(0, expected.Length - sizeof(uint)).CopyTo(buffer);
                    byteCount = checked((uint)(expected.Length - sizeof(uint)));
                }
                return 0;
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(resized, expected.Length, out noBytes));
        Assert.Null(noBytes);
        Assert.Equal(2, calls);

        calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery grew =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 3;
                if (buffer is null)
                {
                    byteCount = checked((uint)expected.Length);
                    return 0;
                }
                byteCount = checked((uint)(expected.Length + sizeof(uint)));
                return 234; // ERROR_MORE_DATA after the registry value grew.
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(grew, expected.Length, out noBytes));
        Assert.Null(noBytes);
        Assert.Equal(2, calls);

        calls = 0;
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery disappeared =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                calls++;
                type = 3;
                if (buffer is null)
                {
                    byteCount = checked((uint)expected.Length);
                    return 0;
                }
                byteCount = 0;
                return 2; // ERROR_FILE_NOT_FOUND after the size query is a race, not known-unconfigured.
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.Unavailable,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(disappeared, expected.Length, out noBytes));
        Assert.Null(noBytes);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void BoundedValueReaderDistinguishesMissingValueFromReadFailure()
    {
        WindowsKeyboardScanCodeRemapReader.RegistryValueQuery missing =
            (byte[]? buffer, out uint type, ref uint byteCount) =>
            {
                type = 3;
                byteCount = 0;
                return 2;
            };
        Assert.Equal(WindowsKeyboardScanCodeRemapReader.BoundedValueReadStatus.NotFound,
            WindowsKeyboardScanCodeRemapReader.ReadBoundedBinaryValue(missing,
                WindowsKeyboardScanCodeRemapReader.DefaultMaximumValueLength, out var noBytes));
        Assert.Null(noBytes);
    }

    [Fact]
    public async Task ExplicitCapturePublishesOnlyTheSelectedMapToTheOriginalMachineCurrent()
    {
        var reader = new RecordedReader(new(true,
        [
            new(0x003A, 0x001D),
            new(0xE052, 0x0000)
        ]));
        var queries = new List<string>(); var platformReads = new List<WindowsOperatingSystemInformationFields>(); var uptimeReads = 0;
        using var owner = CreateOwner(reader, queries, platformReads,
            () => { uptimeReads++; return new(TimeSpan.FromSeconds(30), 30); });
        var backend = owner.CreateBackend();

        Assert.Empty(backend.Catalog.Fields);
        Assert.Equal(0, reader.Reads); Assert.Equal(0, uptimeReads); Assert.Empty(queries); Assert.Empty(platformReads);
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);

        var capability = Assert.Single(backend.Catalog.Fields.Where(field => field.Field == KeyboardRemapField));
        Assert.Equal("machine", backend.Catalog.Devices.Single(device => device.Identity == "machine").Resource);
        Assert.Equal("structured", capability.Definition.ValueKind);
        Assert.Equal("map", capability.Definition.Unit);
        Assert.Equal("windows.registry.keyboard-layout.scancode-map", capability.ReadSource);
        Assert.True(capability.Read.Supported); Assert.False(capability.Write.Supported);
        Assert.Empty(queries); Assert.Empty(platformReads); Assert.Equal(0, uptimeReads); Assert.Equal(1, reader.Reads);

        var reading = backend.ReadCached(KeyboardRemapField);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.NotNull(reading.ObservedAt);
        var map = reading.Value!.Structure!.Value;
        Assert.True(map.GetProperty("configured").GetBoolean());
        var entries = map.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("0x003A", entries[0].GetProperty("sourceScanCode").GetString());
        Assert.Equal("0x001D", entries[0].GetProperty("targetScanCode").GetString());
        Assert.Equal("0xE052", entries[1].GetProperty("sourceScanCode").GetString());
        Assert.Equal("0x0000", entries[1].GetProperty("targetScanCode").GetString());
        Assert.Equal(reading, backend.ReadCached(KeyboardRemapField));
        Assert.Equal(1, reader.Reads); // Catalog and cache reads stay passive.
    }

    [Fact]
    public async Task MissingScancodeMapIsACurrentUnconfiguredResult()
    {
        var reader = new RecordedReader(new(false, ImmutableArray<WindowsKeyboardScanCodeRemapEntry>.Empty));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);

        var reading = backend.ReadCached(KeyboardRemapField);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.NotNull(reading.ObservedAt);
        var map = reading.Value!.Structure!.Value;
        Assert.False(map.GetProperty("configured").GetBoolean());
        Assert.Empty(map.GetProperty("entries").EnumerateArray());
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task UnrelatedCapturePreservesTheExistingMachineObjectAndDoesNotResampleRegistry()
    {
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        var uptimeReads = 0;
        using var owner = CreateOwner(reader, uptime: () => { uptimeReads++; return new(TimeSpan.FromSeconds(30), 30); });
        var backend = owner.CreateBackend();

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Uptime);
        Assert.DoesNotContain(owner.ReadInformation().Devices,
            device => device.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineObjectIdentity);
        Assert.Equal(0, reader.Reads);

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);
        var before = backend.ReadCached(KeyboardRemapField);
        var machineBefore = Assert.Single(owner.ReadInformation().Devices,
            device => device.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineObjectIdentity);
        Assert.Equal(1, reader.Reads);

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Uptime);
        var after = backend.ReadCached(KeyboardRemapField);
        var machineAfter = Assert.Single(owner.ReadInformation().Devices,
            device => device.DeviceIdentity == WindowsOperatingSystemInformationOwner.MachineObjectIdentity);
        Assert.Same(machineBefore, machineAfter);
        Assert.Equal(before, after);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(2, uptimeReads);
    }

    [Fact]
    public async Task OriginalRecordedOwnerConstructorDoesNotFallThroughToTheRegistry()
    {
        var uptimeReads = 0; var wmiReads = 0; var platformReads = 0;
        var declarations = new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, _, _) =>
        {
            wmiReads++;
            return new([], null);
        });
        var platformDeclarations = new WindowsPlatformDeclarationReader((_, _) =>
        {
            platformReads++;
            return new([], [], [], []);
        });
        using var owner = new WindowsOperatingSystemInformationOwner(
            () => { uptimeReads++; return new(TimeSpan.FromSeconds(30), 30); }, declarations, platformDeclarations);

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);

        var reading = owner.CreateBackend().ReadCached(KeyboardRemapField);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value); Assert.Null(reading.ObservedAt);
        Assert.Equal(0, uptimeReads); Assert.Equal(0, wmiReads); Assert.Equal(0, platformReads);
    }

    [Fact]
    public async Task SelectedSourceFailureClearsOnlyKeyboardMapAndPreservesAcPowerReading()
    {
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        var acReader = new RecordedAcReader(new(1));
        var queries = new List<string>(); var platformReads = new List<WindowsOperatingSystemInformationFields>();
        using var owner = CreateOwner(reader, queries, platformReads, acPowerStatusReader: acReader);
        var backend = owner.CreateBackend();

        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower
            | WindowsOperatingSystemInformationFields.KeyboardRemap);
        var priorAc = backend.ReadCached(AcPowerField);
        var priorMap = backend.ReadCached(KeyboardRemapField);
        Assert.Equal(FieldObservationStatus.Current, priorAc.Status);
        Assert.True(priorAc.Value!.Boolean);
        Assert.Equal(FieldObservationStatus.Current, priorMap.Status);
        var acReads = acReader.Reads;
        var platformReadCount = platformReads.Count;

        reader.Value = null;
        var failed = Assert.Single(await backend.CollectAsync([KeyboardRemapField], default));

        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value); Assert.Null(failed.ObservedAt);
        Assert.Equal(failed, backend.ReadCached(KeyboardRemapField));
        Assert.Equal(priorAc, backend.ReadCached(AcPowerField));
        Assert.Equal(acReads, acReader.Reads);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(platformReadCount, platformReads.Count);
        Assert.Empty(queries);
    }

    [Fact]
    public async Task CancellationAndRejectedPermitLeaveTheOriginalCurrentUntouched()
    {
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);
        var prior = owner.ReadInformation();
        var priorReading = backend.ReadCached(KeyboardRemapField);
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([KeyboardRemapField]);

        reader.Value = new(true, [new(0x003A, 0x001E)]);
        using var canceled = new CancellationTokenSource();
        reader.AfterRead = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await borrow.CollectAsync([KeyboardRemapField], new Permit(true), canceled.Token));
        Assert.Same(prior, owner.ReadInformation());
        Assert.Equal(priorReading, backend.ReadCached(KeyboardRemapField));

        reader.AfterRead = null;
        var rejected = await borrow.CollectAsync([KeyboardRemapField], new Permit(false));
        Assert.False(rejected.Committed);
        Assert.Same(prior, owner.ReadInformation());
        Assert.Equal(priorReading, backend.ReadCached(KeyboardRemapField));
    }

    [Fact]
    public async Task RoleTwoSubscriptionUsesTheSameCurrentAndOriginalOwnerSurvivesStop()
    {
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        using var owner = CreateOwner(reader);
        var backend = owner.CreateBackend();
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap);
        var field = Assert.Single(backend.Catalog.Fields.Where(capability => capability.Field == KeyboardRemapField)).Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
            in configuration, failure => throw new InvalidOperationException("Unexpected role-2 keyboard-remap worker failure", failure.Error),
            TimeSpan.Zero);
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("machine-keyboard-remap", [field], TimeSpan.FromSeconds(1),
            new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var first = Assert.Single(stream.Current);
        Assert.Equal(FieldObservationStatus.Current, first.Status);
        Assert.Equal("0x001D", first.Value!.Structure!.Value.GetProperty("entries")[0].GetProperty("targetScanCode").GetString());
        Assert.Equal(first, backend.ReadCached(field));

        reader.Value = new(true, [new(0x003A, 0x001E)]);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var next = Assert.Single(stream.Current);
        Assert.Equal("0x001E", next.Value!.Structure!.Value.GetProperty("entries")[0].GetProperty("targetScanCode").GetString());
        Assert.Equal(next, backend.ReadCached(field));

        await composition.StopAsync();
        Assert.False(await stream.MoveNextAsync());
        var direct = Assert.Single(await backend.CollectAsync([field], timeout.Token));
        Assert.Equal(FieldObservationStatus.Current, direct.Status);
        Assert.Equal("0x001E", direct.Value!.Structure!.Value.GetProperty("entries")[0].GetProperty("targetScanCode").GetString());
        Assert.Equal(direct, backend.ReadCached(field));
        Assert.True(reader.Reads >= 3);
    }

    private static WindowsOperatingSystemInformationOwner CreateOwner(RecordedReader reader,
        ICollection<string>? queries = null,
        ICollection<WindowsOperatingSystemInformationFields>? platformReads = null,
        Func<WindowsMachineUptime?>? uptime = null,
        IWindowsAcPowerStatusReader? acPowerStatusReader = null)
    {
        var declarations = new WindowsOperatingSystemDeclarationReader(TimeSpan.FromSeconds(2), (_, query, _) =>
        {
            queries?.Add(query);
            return new([], null);
        });
        var platformDeclarations = new WindowsPlatformDeclarationReader((selected, _) =>
        {
            platformReads?.Add(selected);
            return new([], [], [], []);
        });
        return new(uptime ?? (() => new(TimeSpan.FromSeconds(30), 30)), declarations, platformDeclarations,
            acPowerStatusReader: acPowerStatusReader, keyboardScanCodeRemapReader: reader);
    }

    private static byte[] RegistryValue(params WindowsKeyboardScanCodeRemapEntry[] entries)
    {
        var bytes = new byte[12 + (entries.Length + 1) * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), checked((uint)entries.Length + 1));
        for (var index = 0; index < entries.Length; index++)
        {
            var offset = 12 + index * sizeof(uint);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)), entries[index].TargetScanCode);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + sizeof(ushort), sizeof(ushort)), entries[index].SourceScanCode);
        }
        return bytes; // The final DWORD is already zero.
    }

    private sealed class RecordedReader(WindowsKeyboardScanCodeRemapReading? value) : IWindowsKeyboardScanCodeRemapReader
    {
        private readonly object gate = new();
        private WindowsKeyboardScanCodeRemapReading? current = value;
        private int reads;
        internal WindowsKeyboardScanCodeRemapReading? Value
        {
            get { lock (gate) return current; }
            set { lock (gate) current = value; }
        }
        internal Action? AfterRead { get; set; }
        internal int Reads => Volatile.Read(ref reads);
        public WindowsKeyboardScanCodeRemapReading? Read()
        {
            Interlocked.Increment(ref reads);
            var result = Value;
            AfterRead?.Invoke();
            return result;
        }
    }

    private sealed class RecordedAcReader(WindowsAcPowerStatusReading? value) : IWindowsAcPowerStatusReader
    {
        private WindowsAcPowerStatusReading? current = value;
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        public WindowsAcPowerStatusReading? Read()
        {
            Interlocked.Increment(ref reads);
            return current;
        }
    }

    private sealed class Permit(bool active) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit)
        {
            if (!active) return false;
            commit();
            return true;
        }
    }
}
