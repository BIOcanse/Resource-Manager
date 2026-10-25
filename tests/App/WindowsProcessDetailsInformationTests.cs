using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsProcessDetailsInformationTests
{
    [Fact]
    public void NativeProcessHeaderReadDoesNotAllocateUnusedReservedArrays()
    {
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers
            .IsReferenceOrContainsReferences<WindowsProcessSystemThreadParser.SystemProcessInformationHeader>());
        var rows = Enumerable.Range(100, 512).Select(pid => ThreadTable(pid, [])).ToArray();
        for (var i = 0; i < rows.Length - 1; i++) WriteUInt32(rows[i], 0, (uint)rows[i].Length);
        var bytes = rows.SelectMany(static row => row).ToArray();
        var selected = new HashSet<int>();
        for (var i = 0; i < 32; i++)
            Assert.True(WindowsProcessSystemThreadParser.TryParse(bytes, selected, WindowsProcessDetailsOptions.Default, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = true;
        for (var i = 0; i < 32; i++)
            valid &= WindowsProcessSystemThreadParser.TryParse(bytes, selected, WindowsProcessDetailsOptions.Default, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        // Only the per-operation result and PID validation set remain. No header
        // object, reserved arrays, or unmanaged copy is needed per native row.
        Assert.InRange(allocated, 1, 32 * 512 * 64);
    }

    private static readonly ProcessInstanceKey Key = new(321, 123456789);
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UnixEpoch.AddSeconds(10);

    [Fact]
    public void PlacementInventoryParsesAllTargetProcessesAndSkipsSyntheticIdleThreadsWithinExistingBounds()
    {
        var records = new[]
        {
            ThreadTable(0, [new(0, 0, 0)]), ThreadTable(4, [new(0, 0, 0)]),
            ThreadTable(100, [new(77, 1, 0), new(78, 2, 1)]), ThreadTable(101, [new(79, 1, 0)])
        };
        for (var index = 0; index < records.Length - 1; index++)
            WriteUInt32(records[index], 0, (uint)records[index].Length);
        var bytes = records.SelectMany(static row => row).ToArray();
        Assert.True(WindowsProcessSystemThreadParser.TryParsePlacementInventory(bytes, WindowsProcessDetailsOptions.Default, out var parsed));
        Assert.Equal(new[] { 100, 101 }, parsed.Keys.Order().ToArray());
        Assert.Equal(new uint[] { 77, 78 }, parsed[100].Select(static row => row.ThreadId));
        Assert.Equal(79U, Assert.Single(parsed[101]).ThreadId);
        Assert.False(WindowsProcessSystemThreadParser.TryParsePlacementInventory(bytes,
            new WindowsProcessDetailsOptions(maximumProcesses: 3), out _));
        Assert.False(WindowsProcessSystemThreadParser.TryParsePlacementInventory(bytes,
            new WindowsProcessDetailsOptions(initialSystemInformationBufferBytes: 1, maximumSystemInformationBufferBytes: bytes.Length - 1), out _));
    }

    [Fact]
    public void OptionsExposeDefaultsAndRejectUnrepresentableOrInconsistentLimits()
    {
        Assert.Equal(131072, WindowsProcessDetailsOptions.Default.MaximumProcesses);
        Assert.Equal(8192, WindowsProcessDetailsOptions.Default.MaximumModulesPerProcess);
        Assert.Equal(8, WindowsProcessDetailsOptions.Default.MaximumModuleEnumerationAttempts);
        Assert.Equal(64 * 1024 * 1024, WindowsProcessDetailsOptions.Default.MaximumSystemInformationBufferBytes);
        Assert.Equal(9, WindowsProcessDetailsOptions.Default.MaximumSystemInformationAttempts);
        Assert.Equal(262144, WindowsProcessDetailsOptions.Default.MaximumThreadsPerProcess);

        var configured = new WindowsProcessDetailsOptions(maximumProcesses: 4,
            initialModulesPerProcess: 1, maximumModulesPerProcess: 8, maximumModuleEnumerationAttempts: 3,
            initialSystemInformationBufferBytes: 1024, maximumSystemInformationBufferBytes: 4096,
            maximumSystemInformationAttempts: 4, maximumThreadsPerProcess: 16, maximumImagePathCharacters: 256);
        Assert.Equal(4, configured.MaximumProcesses);
        Assert.Equal(8, configured.MaximumModulesPerProcess);
        Assert.Equal(4096, configured.MaximumSystemInformationBufferBytes);
        Assert.Equal(16, configured.MaximumThreadsPerProcess);

        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(maximumProcesses: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            initialModulesPerProcess: 2, maximumModulesPerProcess: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            initialModulesPerProcess: 1, maximumModulesPerProcess: Array.MaxLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            maximumSystemInformationBufferBytes: Array.MaxLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            initialSystemInformationBufferBytes: 4096, maximumSystemInformationBufferBytes: 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            maximumThreadsPerProcess: Array.MaxLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessDetailsOptions(
            maximumImagePathCharacters: Array.MaxLength));
    }

    [Fact]
    public async Task ConfiguredProcessLimitRejectsOversizedRecordedInventoryBeforeDetailRead()
    {
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        var source = new OversizedRecordedSource();
        owner.ConfigureProcessDetails(new WindowsProcessDetailsOptions(maximumProcesses: 1), source);

        var catalog = await owner.ProcessDetailsBackend.DiscoverAsync(CancellationToken.None);

        Assert.Empty(catalog.Devices);
        Assert.Equal(1, source.InventoryReads);
        Assert.Equal(0, source.DetailReads);
        Assert.Empty(owner.ReadProcessDetails().Devices);
    }

    [Fact]
    public void PartialNativeInventoryKeepsKnownRowsWhenIdleProcessWasExcluded()
    {
        var batch = new ProcessCaptureBatch<WindowsProcessFieldOwner.ProcessDetailsInventoryRow>(
            SamplingObservationStatus.Current, 2, 1, 0,
            [new(321, Key)], ObservedAt,
            new(ObservedAt, null, new Dictionary<ProcessInstanceKey, double>()));

        var inventory = WindowsProcessFieldOwner.MapProcessDetailsInventory(batch, maximumProcessCount: 8);

        Assert.False(inventory.Succeeded);
        Assert.Equal(new[] { Key }, inventory.ProcessIdentities.ToArray());
    }

    [Fact]
    public async Task MalformedOrDuplicateInventoryIdentitiesAreNotReadAsProvenRows()
    {
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        var source = new MalformedRecordedSource();
        owner.ConfigureProcessDetails(WindowsProcessDetailsOptions.Default, source);

        var catalog = await owner.ProcessDetailsBackend.DiscoverAsync(CancellationToken.None);

        Assert.Empty(catalog.Devices);
        Assert.Equal(0, source.DetailReads);
        Assert.Empty(owner.ReadProcessDetails().Devices);
    }

    [Fact]
    public async Task PartialInventoryReadsProvenRowsAndClearsUnknownAbsenceWithoutRetirement()
    {
        var missing = new ProcessInstanceKey(322, Key.StartKey + 1);
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        var source = new PidZeroExcludedRecordedSource();
        owner.ConfigureProcessDetails(WindowsProcessDetailsOptions.Default, source);
        owner.Current.SetProcessDetails(WindowsProcessDetailsInformationProjection.Apply(
            DeviceInformationSnapshot.Empty, true, [Key, missing], true,
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>(),
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>
            {
                [Key] = new(true, 0, true, [new(0x1000, "prior-current.dll")], true, [new(77, 1, 0)]),
                [missing] = new(true, 0, true, [new(0x2000, "prior-unknown.dll")], true, [new(88, 1, 0)])
            }, ObservedAt));

        _ = await owner.ProcessDetailsBackend.DiscoverAsync(CancellationToken.None);

        var snapshot = owner.ReadProcessDetails();
        Assert.Equal(1, source.DetailReads);
        Assert.NotNull(snapshot.FindField(new(WindowsProcessDetailsIdentity.Process(Key),
            DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name))?.Value);
        Assert.Equal("current.dll", snapshot.FindField(new(WindowsProcessDetailsIdentity.Module(Key, 0x1000),
            DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name))?.Value?.GetString());
        Assert.Equal<uint?>(5u, snapshot.FindField(new(WindowsProcessDetailsIdentity.Thread(Key, 77),
            DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name))?.Value?.GetProperty("state").GetUInt32());

        Assert.NotNull(snapshot.FindField(new(WindowsProcessDetailsIdentity.Process(missing),
            DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name)));
        Assert.Null(snapshot.FindField(new(WindowsProcessDetailsIdentity.Process(missing),
            DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name))?.Value);
        Assert.NotNull(snapshot.FindField(new(WindowsProcessDetailsIdentity.Module(missing, 0x2000),
            DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name)));
        Assert.Null(snapshot.FindField(new(WindowsProcessDetailsIdentity.Module(missing, 0x2000),
            DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name))?.Value);
        Assert.NotNull(snapshot.FindField(new(WindowsProcessDetailsIdentity.Thread(missing, 88),
            DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name)));
        Assert.Null(snapshot.FindField(new(WindowsProcessDetailsIdentity.Thread(missing, 88),
            DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name))?.Value);
    }

    [Fact]
    public void CanonicalDetailsProjectRawSignatureAndThreadCodesToTheirOwnObjectTypes()
    {
        Assert.Equal(("process_all_image_signature_reported", "enum", "process"),
            (DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name,
                DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Unit,
                DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Resource));
        Assert.Equal(("process_all_loaded_module_actual", "text", "process_module"),
            (DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name,
                DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Unit,
                DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Resource));
        Assert.Equal(("process_all_thread_state_actual", "enum", "process_thread"),
            (DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name,
                DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Unit,
                DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Resource));

        var expected = new WindowsProcessDetailsReadResult(true, 0x800B0100, true,
            [new(0x7FF612340000, @"C:\Recorded\sample.dll")], true, [new(77, 99, 123)]);
        var snapshot = WindowsProcessDetailsInformationProjection.Apply(DeviceInformationSnapshot.Empty, true,
            [Key], true, new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>(),
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> { [Key] = expected }, ObservedAt);

        var process = Assert.Single(snapshot.Devices.Where(static device => device.ObjectType == "process"));
        var module = Assert.Single(snapshot.Devices.Where(static device => device.ObjectType == "process_module"));
        var thread = Assert.Single(snapshot.Devices.Where(static device => device.ObjectType == "process_thread"));
        var signature = Assert.Single(process.Fields);
        var signatureReading = InformationFieldProjection.Read(process.DeviceIdentity, signature, snapshot.ObservedAt);
        Assert.Equal("unsigned128", InformationFieldProjection.Describe(signature.Definition).ValueKind);
        Assert.Equal(MetricInteger.Parse("2148204800"), signatureReading.Value?.Integer);
        Assert.Equal("C:\\Recorded\\sample.dll", InformationFieldProjection.Read(module.DeviceIdentity,
            Assert.Single(module.Fields), snapshot.ObservedAt).Value?.Text);
        var threadReading = InformationFieldProjection.Read(thread.DeviceIdentity, Assert.Single(thread.Fields), snapshot.ObservedAt);
        Assert.Equal("structured", InformationFieldProjection.Describe(DeviceInformationFieldRegistry.ProcessAllThreadStateActual).ValueKind);
        Assert.Equal<uint?>(99u, threadReading.Value?.Structure?.GetProperty("state").GetUInt32());
        Assert.Equal<uint?>(123u, threadReading.Value?.Structure?.GetProperty("waitReason").GetUInt32());
    }

    [Fact]
    public void SelectedFailureClearsOnlySelectedDetailAndSuccessfulEmptyRetiresChild()
    {
        var initial = WindowsProcessDetailsInformationProjection.Apply(DeviceInformationSnapshot.Empty, true,
            [Key], true, new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>(),
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>
            {
                [Key] = new(true, 0, true, [new(0x1000, "recorded.dll")], true, [new(77, 5, 2)])
            }, ObservedAt);
        var selected = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>
        {
            [Key] = WindowsProcessDetailsFields.LoadedModules
        };
        var failed = WindowsProcessDetailsInformationProjection.Apply(initial, true, [Key], false, selected,
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>
            {
                [Key] = new(false, null, false, [], false, [])
            }, ObservedAt.AddSeconds(1));

        var moduleField = Assert.Single(failed.Devices.Single(static device => device.ObjectType == "process_module").Fields);
        Assert.Null(moduleField.Value);
        Assert.NotNull(failed.FindField(new(WindowsProcessDetailsIdentity.Process(Key),
            DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name))?.Value);
        Assert.NotNull(failed.FindField(new(WindowsProcessDetailsIdentity.Thread(Key, 77),
            DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name))?.Value);

        var empty = WindowsProcessDetailsInformationProjection.Apply(initial, true, [Key], false, selected,
            new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>
            {
                [Key] = new(false, null, true, [], false, [])
            }, ObservedAt.AddSeconds(2));
        Assert.DoesNotContain(empty.Devices, static device => device.ObjectType == "process_module");
        Assert.Contains(empty.Devices, static device => device.ObjectType == "process_thread");
    }

    [Fact]
    public void SystemProcessThreadParserUsesDocumentedPointerLayoutsAndRejectsBadBounds()
    {
        Assert.Equal(IntPtr.Size == 8 ? 256 : 184, WindowsProcessSystemThreadParser.ProcessHeaderSize);
        Assert.Equal(IntPtr.Size == 8 ? 80 : 64, WindowsProcessSystemThreadParser.ThreadInformationSize);
        Assert.Equal(IntPtr.Size == 8 ? 80 : 68, WindowsProcessSystemThreadParser.ProcessIdOffset);
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32, WindowsProcessSystemThreadParser.ThreadProcessIdOffset);
        Assert.Equal(IntPtr.Size == 8 ? 48 : 36, WindowsProcessSystemThreadParser.ThreadIdOffset);
        Assert.Equal(IntPtr.Size == 8 ? 68 : 52, WindowsProcessSystemThreadParser.ThreadStateOffset);
        Assert.Equal(IntPtr.Size == 8 ? 72 : 56, WindowsProcessSystemThreadParser.ThreadWaitReasonOffset);

        var bytes = ThreadTable(321, [new(77, 99, 123)]);
        Assert.True(WindowsProcessSystemThreadParser.TryParse(bytes, new HashSet<int> { 321 },
            WindowsProcessDetailsOptions.Default, out var parsed));
        var state = Assert.Single(parsed[321]);
        Assert.Equal(new WindowsProcessThreadState(77, 99, 123), state);

        WriteUInt32(bytes, 4, uint.MaxValue); // NumberOfThreads cannot cross the record boundary.
        Assert.False(WindowsProcessSystemThreadParser.TryParse(bytes, new HashSet<int> { 321 },
            WindowsProcessDetailsOptions.Default, out _));

        var mismatched = ThreadTable(321, [new(77, 1, 0)], threadOwnerPid: 322);
        Assert.False(WindowsProcessSystemThreadParser.TryParse(mismatched, new HashSet<int> { 321 },
            WindowsProcessDetailsOptions.Default, out _));

        var duplicateTid = ThreadTable(321, [new(77, 1, 0), new(77, 2, 3)]);
        Assert.False(WindowsProcessSystemThreadParser.TryParse(duplicateTid, new HashSet<int> { 321 },
            WindowsProcessDetailsOptions.Default, out _));
    }

    [Fact]
    public void NativeReaderChecksExactBirthPreservesWinVerifyTrustBitsAndClosesHandle()
    {
        var api = new NativeApi();
        var reader = new WindowsProcessDetailsInformationReader(api);
        var selection = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>
        {
            [Key] = WindowsProcessDetailsFields.All
        };
        var result = reader.Read(selection, CancellationToken.None)[Key];

        Assert.True(result.SignatureSucceeded);
        Assert.Equal<uint?>(0x800B0100u, result.SignatureStatus);
        Assert.True(result.ModulesSucceeded);
        Assert.Equal("C:\\Recorded\\image.dll", Assert.Single(result.Modules).Path);
        Assert.True(result.ThreadsSucceeded);
        Assert.Equal(new WindowsProcessThreadState(77, 99, 123), Assert.Single(result.Threads));
        Assert.Equal(1, api.Opens);
        Assert.Equal(1, api.Closes);
        Assert.Equal("C:\\Recorded\\process.exe", api.VerifiedPath);
        Assert.Equal((WindowsProcessDetailsInformationReader.ProcessQueryLimitedInformation
            | WindowsProcessDetailsInformationReader.ProcessQueryInformation
            | WindowsProcessDetailsInformationReader.ProcessVmRead
            | WindowsProcessDetailsInformationReader.Synchronize), api.Access);

        api.Creation++;
        var wrongBirth = reader.Read(selection, CancellationToken.None)[Key];
        Assert.Equal(WindowsProcessDetailsReadResult.Unavailable, wrongBirth);
        Assert.Equal(2, api.Closes);
    }

    [Fact]
    public void NativeReaderRejectsOverLimitSelectionBeforeOpeningProcesses()
    {
        var api = new NativeApi();
        var options = new WindowsProcessDetailsOptions(maximumProcesses: 1);
        var reader = new WindowsProcessDetailsInformationReader(api, options);
        var selection = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>
        {
            [Key] = WindowsProcessDetailsFields.ImageSignature,
            [new(322, Key.StartKey + 1)] = WindowsProcessDetailsFields.ImageSignature
        };

        var result = reader.Read(selection, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, api.Opens);
    }

    [Fact]
    public void NativeReaderKeepsCategoryFailuresSeparateAndReleasesOnCancellationOrCloseFailure()
    {
        var api = new NativeApi { ModulesAvailable = false };
        var reader = new WindowsProcessDetailsInformationReader(api);
        var selection = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>
        {
            [Key] = WindowsProcessDetailsFields.All
        };
        var partial = reader.Read(selection, CancellationToken.None)[Key];
        Assert.True(partial.SignatureSucceeded);
        Assert.False(partial.ModulesSucceeded);
        Assert.True(partial.ThreadsSucceeded);

        api.ModulesAvailable = true;
        api.CloseSucceeds = false;
        var failedRelease = reader.Read(selection, CancellationToken.None)[Key];
        Assert.Equal(WindowsProcessDetailsReadResult.Unavailable, failedRelease);

        api.CloseSucceeds = true;
        using var cancellation = new CancellationTokenSource();
        api.AfterThreadSnapshot = cancellation.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(selection, cancellation.Token));
        Assert.Equal(3, api.Closes);
    }

    [Fact]
    public async Task OriginalOwnerCurrentBorrowPermitAndCancellationStayShared()
    {
        using var unconfigured = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        Assert.Throws<InvalidOperationException>(() => _ = unconfigured.ProcessDetailsBackend);

        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        var reader = new RecordedReader();
        var inventory = new UnexpectedProcessInventory();
        owner.Inventory = inventory;
        owner.ConfigureProcessDetails(WindowsProcessDetailsOptions.Default, reader);
        var backend = owner.ProcessDetailsBackend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(1, reader.InventoryReads);
        Assert.Equal(1, reader.DetailReads);
        Assert.Equal(0, inventory.Calls);
        var processPrefix = $"windows-process|{Key.ProcessId}:{Key.StartKey}";
        var signature = Assert.Single(catalog.Fields.Where(field => field.Field.FieldName
            == DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name
            && field.Field.DeviceIdentity.StartsWith(processPrefix, StringComparison.Ordinal))).Field;
        var module = Assert.Single(catalog.Fields.Where(field => field.Field.FieldName
            == DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name
            && field.Field.DeviceIdentity.StartsWith(processPrefix, StringComparison.Ordinal))).Field;
        var thread = Assert.Single(catalog.Fields.Where(field => field.Field.FieldName
            == DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name
            && field.Field.DeviceIdentity.StartsWith(processPrefix, StringComparison.Ordinal))).Field;
        Assert.Equal(WindowsProcessDetailsFields.All, reader.Requests.Single().Values.Single());
        var visible = owner.ReadProcessDetails();
        Assert.Equal(Assert.Single(await backend.CollectAsync([signature], CancellationToken.None)).Value,
            backend.ReadCached(signature).Value);
        visible = owner.ReadProcessDetails();
        Assert.Same(visible, owner.ReadProcessDetails());

        using var borrow = owner.BorrowProcessDetailsSubscriptionCapture();
        var rejectedFields = await borrow.CollectAsync([module], new RejectedPermit());
        Assert.False(rejectedFields.Committed);
        Assert.Same(visible, owner.ReadProcessDetails());
        Assert.Equal(WindowsProcessDetailsFields.LoadedModules, reader.Requests.Last().Values.Single());

        reader.BlockNextRead();
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(async () => await backend.CollectAsync([thread], cancellation.Token));
        await reader.ReadEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        reader.ReleaseRead.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(visible, owner.ReadProcessDetails());

        var beforeSourceFailure = owner.ReadProcessDetails();
        reader.NextFailure = new IOException("recorded source failure");
        var failedSourceReading = Assert.Single(await backend.CollectAsync([module], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, failedSourceReading.Status);
        Assert.Null(failedSourceReading.Value);
        Assert.NotSame(beforeSourceFailure, owner.ReadProcessDetails());
        Assert.Null(backend.ReadCached(module).Value);
        Assert.Equal(0, inventory.Calls);

        var prepared = owner.Current.PrepareCopy();
        prepared.SetProcessDetails(DeviceInformationSnapshot.Empty);
        owner.Current.CommitPrepared(prepared);
        Assert.Empty(owner.ReadProcessDetails().Devices);
    }

    private static byte[] ThreadTable(int pid, IReadOnlyList<WindowsProcessThreadState> threads, int? threadOwnerPid = null)
    {
        var headerSize = WindowsProcessSystemThreadParser.ProcessHeaderSize;
        var threadSize = WindowsProcessSystemThreadParser.ThreadInformationSize;
        var data = new byte[checked(headerSize + threads.Count * threadSize)];
        WriteUInt32(data, 4, checked((uint)threads.Count));
        WritePointer(data, WindowsProcessSystemThreadParser.ProcessIdOffset, checked((uint)pid));
        for (var index = 0; index < threads.Count; index++)
        {
            var start = checked(headerSize + index * threadSize);
            var thread = threads[index];
            WritePointer(data, start + WindowsProcessSystemThreadParser.ThreadProcessIdOffset,
                checked((uint)(threadOwnerPid ?? pid)));
            WritePointer(data, start + WindowsProcessSystemThreadParser.ThreadIdOffset, thread.ThreadId);
            WriteUInt32(data, start + WindowsProcessSystemThreadParser.ThreadStateOffset, thread.State);
            WriteUInt32(data, start + WindowsProcessSystemThreadParser.ThreadWaitReasonOffset, thread.WaitReason);
        }
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);

    private static void WritePointer(byte[] data, int offset, uint value)
    {
        if (IntPtr.Size == 8) BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);
    }

    private sealed class NativeApi : IWindowsProcessDetailsNativeApi
    {
        internal int Opens, Closes;
        internal ulong Creation = (ulong)Key.StartKey;
        internal uint Access;
        internal string? VerifiedPath;
        internal bool ModulesAvailable = true;
        internal bool CloseSucceeds = true;
        internal Action? AfterThreadSnapshot;
        public IntPtr OpenProcess(uint desiredAccess, int processId) { Opens++; Access = desiredAccess; return new(12); }
        public uint GetProcessId(IntPtr process) => (uint)Key.ProcessId;
        public bool TryGetCreationFileTime(IntPtr process, out ulong creationFileTime) { creationFileTime = Creation; return true; }
        public bool IsRunning(IntPtr process) => true;
        public bool TryReadImagePath(IntPtr process, out string path) { path = @"C:\Recorded\process.exe"; return true; }
        public bool TryReadModules(IntPtr process, out IReadOnlyList<WindowsProcessLoadedModule> modules)
        {
            modules = ModulesAvailable
                ? new[] { new WindowsProcessLoadedModule(0x1000, @"C:\Recorded\image.dll") }
                : Array.Empty<WindowsProcessLoadedModule>();
            return ModulesAvailable;
        }
        public bool TryVerifyImageSignature(string path, out int status)
        { VerifiedPath = path; status = unchecked((int)0x800B0100); return true; }
        public bool TryReadSystemProcessInformation(out byte[] data)
        { data = ThreadTable(Key.ProcessId, [new(77, 99, 123)]); AfterThreadSnapshot?.Invoke(); return true; }
        public bool CloseHandle(IntPtr handle) { Closes++; return CloseSucceeds; }
    }

    private sealed class UnexpectedProcessInventory : IWindowsProcessInventoryReader
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public Process[] GetProcesses()
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("The explicitly supplied recorded source must own inventory reads.");
        }
    }

    private sealed class OversizedRecordedSource : IWindowsProcessDetailsCaptureSource
    {
        internal int InventoryReads;
        internal int DetailReads;

        public WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref InventoryReads);
            return new(true, [Key, new(322, Key.StartKey + 1)], ObservedAt);
        }

        public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DetailReads);
            throw new InvalidOperationException("An over-limit inventory must not start detail reads.");
        }
    }

    private sealed class PidZeroExcludedRecordedSource : IWindowsProcessDetailsCaptureSource
    {
        internal int DetailReads;

        public WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Records the existing capture batch outcome: PID 0 was excluded, while Key is proven current.
            return new(false, [Key], ObservedAt);
        }

        public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DetailReads);
            return selection.ToDictionary(static pair => pair.Key, static pair => new WindowsProcessDetailsReadResult(
                (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0,
                (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0 ? 0u : null,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0
                    ? [new WindowsProcessLoadedModule(0x1000, "current.dll")] : [],
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0,
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0
                    ? [new WindowsProcessThreadState(77, 5, 2)] : []));
        }
    }

    private sealed class MalformedRecordedSource : IWindowsProcessDetailsCaptureSource
    {
        internal int DetailReads;

        public WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, [Key, new(Key.ProcessId, Key.StartKey + 1), new(322, 0)], ObservedAt);
        }

        public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DetailReads);
            return selection.ToDictionary(static pair => pair.Key, static _ => WindowsProcessDetailsReadResult.Unavailable);
        }
    }

    private sealed class RecordedReader : IWindowsProcessDetailsCaptureSource
    {
        private int blockNext;
        private int inventoryReads;
        private int detailReads;
        internal Exception? NextFailure;
        internal readonly List<IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields>> Requests = [];
        internal int InventoryReads => Volatile.Read(ref inventoryReads);
        internal int DetailReads => Volatile.Read(ref detailReads);
        private TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task ReadEntered => readEntered.Task;
        internal readonly ManualResetEventSlim ReleaseRead = new();

        public WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref inventoryReads);
            return new(true, [Key], new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        }

        public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref detailReads);
            Requests.Add(new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>(selection));
            if (Interlocked.Exchange(ref NextFailure, null) is { } failure) throw failure;
            if (Interlocked.Exchange(ref blockNext, 0) != 0)
            {
                readEntered.TrySetResult();
                if (!ReleaseRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Recorded reader release timed out.");
                cancellationToken.ThrowIfCancellationRequested();
            }
            return selection.ToDictionary(static pair => pair.Key, pair => new WindowsProcessDetailsReadResult(
                (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0, 0,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0 ? [new(0x1000, "recorded.dll")] : [],
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0,
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0 ? [new(77, 5, 2)] : []));
        }

        internal void BlockNextRead()
        {
            readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseRead.Reset();
            Interlocked.Exchange(ref blockNext, 1);
        }
    }

    private sealed class RejectedPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}
