using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Disk.ReadWrite;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayCoordinatorDiskCheckpointStoreTests
{
    private const ulong Generation = 36UL << 32;
    private static ReadOnlySpan<byte> Uid => "rm/display/checkpoint/v3"u8;
    private static string Manifest => Path.Combine(AppContext.BaseDirectory, "Native", "DiskCheckpoint", "native.json");

    [Fact]
    public void ActualSdkAlternatesTwoSlotsReopensAndKeepsLegacyUntouched()
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        var original = File.ReadAllBytes(files.Legacy);
        using (var store = Open(files))
        {
            for (ulong epoch = 1; epoch <= 12; epoch++)
            {
                store.Save(Generation, Image(epoch));
                Assert.Equal(epoch, store.Load(Generation)!.Header.OperationEpoch);
            }
        }
        Assert.Equal(original, File.ReadAllBytes(files.Legacy));
        using (var disk = DiskStore.Open(Manifest, files.Root))
        {
            var query = disk.QueryUid(Uid);
            Assert.Equal(DiskResultKind.Item, query.Result.Kind);
            Assert.Equal(12UL, query.Result.Sequence);
            var segment = Assert.Single(query.Segments);
            Assert.Equal("checkpoint-b.bin", segment.Template);
            Assert.Equal(0UL, segment.Offset);
            Assert.Equal(new[] { "checkpoint-a.bin", "checkpoint-b.bin" },
                Directory.GetFiles(Path.Combine(files.Root, "data")).Select(Path.GetFileName).Order().ToArray());
        }
        using var reopened = Open(files);
        Assert.Equal(12UL, reopened.Load(Generation)!.Header.OperationEpoch);
        Assert.Null(reopened.Load(Generation + (1UL << 32)));
    }

    [Fact]
    public void LegacyLoadDoesNotRegisterAndFirstRealSaveSwitchesAuthority()
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        var original = File.ReadAllBytes(files.Legacy);
        using (var store = Open(files))
            Assert.Equal(99UL, store.Load(Generation)!.Header.OperationEpoch);
        using (var disk = DiskStore.Open(Manifest, files.Root))
            Assert.Equal(DiskResultKind.Missing, disk.QueryUid(Uid).Result.Kind);
        using (var store = Open(files))
        {
            store.Save(Generation, Image(7));
            Assert.Equal(7UL, store.Load(Generation)!.Header.OperationEpoch);
        }
        Assert.Equal(original, File.ReadAllBytes(files.Legacy));
        using var reopened = Open(files);
        Assert.Equal(7UL, reopened.Load(Generation)!.Header.OperationEpoch);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("machine")]
    [InlineData("trailing")]
    public void OriginalEnvelopeRejectsInvalidLegacyBeforeAnyRegistration(string damage)
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(1), foreignMachine: damage == "machine");
        var bytes = File.ReadAllBytes(files.Legacy);
        if (damage == "checksum") bytes[32] ^= 0x5A;
        if (damage == "trailing") bytes = InsertTrailingByte(bytes);
        File.WriteAllBytes(files.Legacy, bytes);
        using (var store = Open(files))
            Assert.Throws<InvalidDataException>(() => store.Load(Generation));
        using var disk = DiskStore.Open(Manifest, files.Root);
        Assert.Equal(DiskResultKind.Missing, disk.QueryUid(Uid).Result.Kind);
    }

    [Theory]
    [InlineData("sdk-checksum")]
    [InlineData("envelope-checksum")]
    [InlineData("append")]
    [InlineData("missing")]
    public void RegisteredAuthorityNeverFallsBackToLegacyOnDamagedSlot(string damage)
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        using (var store = Open(files)) store.Save(Generation, Image(7));
        using (var disk = DiskStore.Open(Manifest, files.Root))
        {
            var path = disk.GetPath(0);
            if (damage == "missing") File.Delete(path);
            else if (damage == "append")
            {
                using var file = new FileStream(path, FileMode.Append);
                file.WriteByte(0xA5);
            }
            else
            {
                var bytes = File.ReadAllBytes(path);
                bytes[32] ^= 0x5A;
                File.WriteAllBytes(path, bytes);
                if (damage == "envelope-checksum")
                {
                    // Refresh its SDK registration so CRC passes; the original SHA must still reject.
                    Assert.Equal(DiskResultKind.Refreshed, disk.Refresh(0).Kind);
                }
            }
        }
        using var reopened = Open(files);
        Assert.Throws<InvalidDataException>(() => reopened.Load(Generation));
    }

    [Fact]
    public void RegisteredGenerationMismatchDoesNotReadMatchingLegacy()
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        using var store = Open(files);
        store.Save(Generation + (1UL << 32), Image(7));
        Assert.Null(store.Load(Generation));
        Assert.Equal(7UL, store.Load(Generation + (1UL << 32))!.Header.OperationEpoch);
    }

    [Fact]
    public void ActualSdkCasConflictRetainsThePreviouslyBoundImage()
    {
        using var files = new TestFiles();
        var advanceBeforeRegister = false;
        using var store = Open(files, (stage, disk) =>
        {
            if (stage != DisplayCoordinatorCheckpointStage.BeforeRegister || !advanceBeforeRegister) return;
            advanceBeforeRegister = false;
            var current = disk.QueryUid(Uid);
            var slot = Assert.Single(current.Segments).Template == "checkpoint-a.bin" ? 0U : 1U;
            Assert.Equal(DiskResultKind.Applied,
                disk.Register(Uid, slot, replace: true, expectedSequence: current.Result.Sequence).Kind);
        });
        store.Save(Generation, Image(1));
        advanceBeforeRegister = true;
        var failure = Assert.Throws<IOException>(() => store.Save(Generation, Image(2)));
        Assert.Contains("conflicted", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1UL, store.Load(Generation)!.Header.OperationEpoch);
    }

    [Theory]
    [InlineData("CatalogWriteFailed", true)]
    [InlineData("CatalogEffectUncertain", false)]
    public void InjectedRegistrationBoundaryFailureDistinguishesConfirmedAndUncertainLegacyUse(
        string errorName, bool legacyRemainsAllowed)
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        var registerAttempts = 0;
        using var store = Open(files, (stage, _) =>
        {
            if (stage != DisplayCoordinatorCheckpointStage.BeforeRegister) return;
            registerAttempts++;
            // Product classification fixture, not a real Native catalog rollback fault.
            throw new DiskNativeException(errorName, "injected_register_boundary");
        });
        Assert.Equal(errorName,
            Assert.Throws<DiskNativeException>(() => store.Save(Generation, Image(7))).ErrorName);
        var recovered = store.Load(Generation);
        if (legacyRemainsAllowed) Assert.Equal(99UL, recovered!.Header.OperationEpoch);
        else Assert.Null(recovered);
        Assert.Equal(1, registerAttempts);
        store.Dispose();
        using var disk = DiskStore.Open(Manifest, files.Root);
        Assert.Equal(DiskResultKind.Missing, disk.QueryUid(Uid).Result.Kind);
    }

    [Fact]
    public void InjectedFailureAfterActualAppliedRecoversAuthorityWithoutReplayingImage()
    {
        using var files = new TestFiles();
        var registerAttempts = 0;
        var fail = true;
        using var store = Open(files, (stage, _) =>
        {
            if (stage == DisplayCoordinatorCheckpointStage.BeforeRegister) registerAttempts++;
            if (stage == DisplayCoordinatorCheckpointStage.Registered && fail)
            {
                fail = false;
                throw new DiskNativeException("CatalogEffectUncertain", "injected_after_actual_applied");
            }
        });
        var failure = Assert.Throws<IOException>(() => store.Save(Generation, Image(7)));
        Assert.Contains("applied at sequence 1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(7UL, store.Load(Generation)!.Header.OperationEpoch);
        Assert.Equal(1, registerAttempts);
        store.Dispose();
        using var disk = DiskStore.Open(Manifest, files.Root);
        Assert.Equal(1UL, disk.QueryUid(Uid).Result.Sequence);
    }

    [Fact]
    public void ActualPostCommitReadbackDetectsBytesAppendedOutsideRegisteredSegment()
    {
        using var files = new TestFiles();
        WriteLegacy(files.Legacy, Image(99));
        var registerAttempts = 0;
        using var store = Open(files, (stage, disk) =>
        {
            if (stage == DisplayCoordinatorCheckpointStage.BeforeRegister) registerAttempts++;
            if (stage != DisplayCoordinatorCheckpointStage.Registered) return;
            using var file = new FileStream(disk.GetPath(0), FileMode.Append);
            file.WriteByte(0xA5);
        });
        var failure = Assert.Throws<IOException>(() => store.Save(Generation, Image(7)));
        Assert.Contains("applied at sequence 1", failure.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => store.Load(Generation));
        Assert.Equal(1, registerAttempts);
    }

    [Fact]
    public void InjectedCloseBoundaryFailureRetainsRealWriterForRetry()
    {
        using var files = new TestFiles();
        var closeAttempts = 0;
        using var store = Open(files, (stage, _) =>
        {
            if (stage == DisplayCoordinatorCheckpointStage.Closing && ++closeAttempts == 1)
                throw new IOException("injected_before_native_close");
        });
        store.Save(Generation, Image(7));
        Assert.Throws<IOException>(store.Dispose);
        Assert.ThrowsAny<IOException>(() => { using var unexpected = DiskStore.Open(Manifest, files.Root); });
        Assert.Equal(7UL, store.Load(Generation)!.Header.OperationEpoch);
        store.Dispose();
        store.Dispose();
        Assert.Equal(2, closeAttempts);
        using var reopened = DiskStore.Open(Manifest, files.Root);
        Assert.Equal(DiskResultKind.Item, reopened.QueryUid(Uid).Result.Kind);
        Assert.Throws<ObjectDisposedException>(() => store.Load(Generation));
    }

    [Fact]
    public void ConstructorWriterConflictDoesNotDisturbOriginalAndCanReopenAfterClose()
    {
        using var files = new TestFiles();
        using (var first = Open(files))
        {
            first.Save(Generation, Image(7));
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Open(files); });
            Assert.Equal(7UL, first.Load(Generation)!.Header.OperationEpoch);
        }
        using var second = Open(files);
        Assert.Equal(7UL, second.Load(Generation)!.Header.OperationEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualDisplayNativeRestoresOrRejectsItsCheckpointWithoutHardwareCapture(bool corruptNativeChecksum)
    {
        using var files = new TestFiles();
        using var store = Open(files);
        var configuration = HostManagerDisplayCoordinatorOwner.CreateConfiguration(
            HostManagerTestPlanFactory.CreatePlan().DisplayCoordinator);
        NativeDisplayCoordinatorReadView before;
        using (var owner = new NativeDisplayCoordinatorWorkspace(configuration,
                   new NativeDisplayCoordinatorSession(in configuration),
                   () => new([RecordedPath()], null), new(store.Load, store.Save)))
        {
            owner.Refresh([]);
            before = owner.ReadCurrent();
            Assert.Single(before.Current.DisplayPaths);
        }
        if (corruptNativeChecksum)
        {
            var image = store.Load(configuration.Generation)!;
            var header = image.Header;
            header.Checksum.Low ^= 1;
            store.Save(configuration.Generation, image with { Header = header });
        }
        var savedHeader = store.Load(configuration.Generation)!.Header;
        var captures = 0;
        NativeDisplayCoordinatorWorkspace Restore() => new(configuration,
            new NativeDisplayCoordinatorSession(in configuration), () =>
            {
                captures++;
                return new([RecordedPath() with { Width = 2560 }], null);
            }, new(store.Load, store.Save));
        if (corruptNativeChecksum)
            Assert.Throws<InvalidOperationException>(() => { using var unexpected = Restore(); });
        else
        {
            using var restored = Restore();
            var after = restored.ReadCurrent();
            Assert.Equal(before.Metadata.ContentGeneration, after.Metadata.ContentGeneration);
            Assert.Equal(before.Metadata.StateRevision, after.Metadata.StateRevision);
            Assert.Equal(before.Metadata.LastSuccessUtcMilliseconds, after.Metadata.LastSuccessUtcMilliseconds);
            Assert.Equal(RecordedPath().MonitorDevicePath, Assert.Single(after.Current.DisplayPaths).Path.MonitorDevicePath);
            Assert.Equal(0, captures);
            restored.Refresh([]);
            var nextHeader = store.Load(configuration.Generation)!.Header;
            Assert.Equal(savedHeader.LastRefreshEpoch + 1, nextHeader.LastRefreshEpoch);
            Assert.Equal(savedHeader.OperationEpoch + 1, nextHeader.OperationEpoch);
        }
        Assert.Equal(corruptNativeChecksum ? 0 : 1, captures);
    }

    private static NativeDisplayCoordinatorPersistenceStore Open(TestFiles files,
        Action<DisplayCoordinatorCheckpointStage, DiskStore>? observer = null)
    {
        Assert.True(File.Exists(Manifest), "The actual RM DiskCheckpoint native.json and adjacent DLL must be deployed into test output.");
        return new(files.Legacy, Manifest, observer);
    }

    private static NativeDisplayCoordinatorPersistenceImage Image(ulong epoch)
    {
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        _ = catalog.AddDisplayPath(RecordedPath());
        return new(new NativeDisplayPersistenceHeader
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeDisplayPersistenceHeader>()),
            ConfigurationGeneration = Generation,
            OperationEpoch = epoch,
            ContentGeneration = 7,
            StateRevision = 11,
            LastRefreshEpoch = 5,
            Phase = (uint)NativeDisplayCoordinatorPhase.Ready
        }, [], [], [], catalog.Export());
    }

    private static DeviceTopologyDisplayPath RecordedPath() => new(1, 2, 3, 4, 10, 0,
        "Recorded display", @"DISPLAY\CHECKPOINT\1", 1920, 1080, 60, 1, true, true, @"PCI\CHECKPOINT");

    // The exact pre-migration envelope, independently written as an input fixture.
    private static void WriteLegacy(string path, NativeDisplayCoordinatorPersistenceImage image, bool foreignMachine = false)
    {
        var identity = DeviceTopologyCacheIdentity.ReadCurrentDescriptor();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(0x33445349444D5248UL);
            writer.Write(1U);
            writer.Write(NativeDisplayCoordinatorAbi.Version);
            writer.Write(Generation);
            foreach (var text in new[] { identity.Source, foreignMachine ? new string('0', identity.Sha256.Length) : identity.Sha256 })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write(checked((uint)bytes.Length));
                writer.Write(bytes);
            }
            var header = image.Header;
            writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1)));
            writer.Write(MemoryMarshal.AsBytes(image.Facts.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(image.Texts.AsSpan()));
            writer.Write(image.TextBytes);
            writer.Write(checked((uint)image.Payloads.Count));
            foreach (var payload in image.Payloads)
            {
                writer.Write(payload.Handle.High);
                writer.Write(payload.Handle.Low);
                writer.Write((uint)payload.Kind);
                writer.Write(checked((uint)payload.Payload.Length));
                writer.Write(payload.Payload);
            }
        }
        var body = stream.ToArray();
        File.WriteAllBytes(path, [.. body, .. SHA256.HashData(body)]);
    }

    private static byte[] InsertTrailingByte(byte[] image)
    {
        var body = new byte[image.Length - SHA256.HashSizeInBytes + 1];
        image.AsSpan(0, image.Length - SHA256.HashSizeInBytes).CopyTo(body);
        body[^1] = 0xA5;
        return [.. body, .. SHA256.HashData(body)];
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"rm-display-disk-{Guid.NewGuid():N}");
        internal string Legacy => Path.Combine(directory, "display-v3.bin");
        internal string Root => NativeDisplayCoordinatorPersistenceStore.ResolveRootPath(Legacy);
        internal TestFiles() => Directory.CreateDirectory(directory);
        public void Dispose()
        {
            var pending = new Stack<(string Path, bool Visited)>();
            pending.Push((directory, false));
            var entries = 0;
            while (pending.TryPop(out var item))
            {
                if (++entries > 256) throw new IOException("Owned checkpoint test directory exceeded its cleanup bound.");
                if (item.Path != directory && !item.Path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Checkpoint fixture cleanup escaped its owned root.");
                if (item.Visited) { Directory.Delete(item.Path); continue; }
                pending.Push((item.Path, true));
                foreach (var file in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    if (++entries > 256) throw new IOException("Owned checkpoint test entries exceeded their cleanup bound.");
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Checkpoint fixture cleanup does not follow reparse points.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push((file, false));
                    else File.Delete(file);
                }
            }
        }
    }
}

