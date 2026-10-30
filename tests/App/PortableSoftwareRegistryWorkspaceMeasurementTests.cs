using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class PortableSoftwareRegistryWorkspaceMeasurementTests(ITestOutputHelper output) : IDisposable
{
    private const int RegistrationCount = 120;
    private const int PathCount = RegistrationCount * 2;
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-portable-measurement-{Guid.NewGuid():N}");
    private readonly ResourceManagerLedgerOwner ledgerOwner = new();

    [Fact]
    public async Task SQLiteReopenReportsWorkspaceAndReadCostsForRepresentativeRegistry()
    {
        var contentRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var database = new ResourceManagerDatabase(new TestEnvironment(contentRoot));
        await database.EnsureInitializedAsync(default);
        var plan = HostManagerTestPlanFactory.CreatePlan();

        // Warm the ABI, JIT, ledger and SQLite paths before measuring a populated reopen.
        await MeasureReopenAsync(database, plan, "empty-warmup", 0);
        await SeedAsync(database);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            measurement = "fixture", registrations = RegistrationCount, paths = PathCount,
            assumptions = "Synthetic workstation: 120 catalog identities, two executable paths each; 25% games, 25% high-performance, 50% other; 80% confirmed roots. No machine inventory or file probing.",
            plan = plan.PortableSoftwareRegistry.Recreate.Capacity,
            nativeByteMeaning = "ABI requested allocation lengths, excluding page-allocator rounding, DLL mappings, SQLite, and ledger metadata",
            managedByteMeaning = "Full-GC process heap delta; approximate retained workspace increment, not an exact object graph. Allocations span StartAsync only and are process-wide."
        }));
        var iteration = 0;
        while (iteration < 5)
        {
            await MeasureReopenAsync(database, plan, $"populated-{++iteration}", RegistrationCount);
        }
    }

    private async Task MeasureReopenAsync(ResourceManagerDatabase database, CompiledHostManagerPlan plan,
        string label, int expectedRegistrations)
    {
        // Each independently constructed owner starts with its own deployment history.
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = plan });
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        await registry.StartAsync(default);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var heapDelta = GC.GetTotalMemory(forceFullCollection: true) - heapBefore;
        try
        {
            // Output rows are created per snapshot call at the live rows and closed when it returns.
            var firstReadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var firstReadStart = Stopwatch.GetTimestamp();
            var firstCount = ReadCount(registry);
            var firstReadElapsed = Stopwatch.GetElapsedTime(firstReadStart);
            var firstReadAllocated = GC.GetAllocatedBytesForCurrentThread() - firstReadAllocatedBefore;
            var heapAfterFirstRead = GC.GetTotalMemory(forceFullCollection: true) - heapBefore;
            Assert.Equal(expectedRegistrations, firstCount);
            var context = PrivateField(registry, "context");
            var workspace = (NativePortableSoftwareRegistryWorkspace)context.GetType().GetProperty("Workspace")!.GetValue(context)!;
            var capacity = workspace.Session.Capacity;
            var live = workspace.Session.WorkspaceRows;
            var snapshotScratchBytes = checked((ulong)Math.Max(1U, live.RegistrationCount) * (ulong)Unsafe.SizeOf<NativePortableSoftwareRegistrationSnapshot>()
                + (ulong)Math.Max(1U, live.PathCount) * (ulong)Unsafe.SizeOf<NativePortableSoftwarePathSnapshot>());
            Assert.DoesNotContain(ledgerOwner.ReadWorkspaceCapacities(),
                row => row.WorkArea == NativePortableSoftwareRegistryWorkspace.ScratchWorkArea);
            var catalog = (NativePortableSoftwareRegistryPayloadCatalog)context.GetType().GetProperty("Payloads")!.GetValue(context)!;
            var payloads = ((IDictionary)PrivateField(catalog, "payloadByHandle")).Values.Cast<NativePortableSoftwarePayload>().ToArray();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                measurement = "sqlite-reopen", label, elapsedMilliseconds = elapsed.TotalMilliseconds,
                startManagedAllocatedBytes = allocated, fullGcManagedHeapDeltaBytes = heapDelta,
                afterFirstSnapshotFullGcManagedHeapDeltaBytes = heapAfterFirstRead,
                firstSnapshotElapsedMilliseconds = firstReadElapsed.TotalMilliseconds,
                firstSnapshotThreadManagedAllocatedBytes = firstReadAllocated,
                nativeSessionRequestedBytes = capacity.ResidentByteCount,
                snapshotScratchBytesPerCall = snapshotScratchBytes,
                nativeSessionPlusScratchRequestedBytes = checked(capacity.ResidentByteCount + snapshotScratchBytes),
                scratchPhase = "Created per snapshot call at the live rows and closed before it returns; none between calls",
                retainedPayloads = catalog.Count,
                retainedPayloadUtf8Bytes = payloads.Sum(static p => (long)p.Utf8Bytes.Length),
                retainedPayloadUtf16CharacterBytes = payloads.Sum(static p => (long)p.Text.Length * sizeof(char)),
                capacity.RegistrationCapacity, capacity.PathCapacity,
                capacity.ExecutablePathByteCapacityPerPath, capacity.RootPathByteCapacityPerPath
            }));
            // Read timing and allocation are measured separately from reopening/importing.
            // Keep only an integer checksum, so returned snapshots are not retained in heap evidence.
            Assert.Equal(expectedRegistrations, registry.GetSnapshot().Count);
            const int reads = 20;
            var readAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var readStart = Stopwatch.GetTimestamp();
            var checksum = 0;
            var read = 0;
            while (read++ < reads) checksum += registry.GetSnapshot().Count;
            var readElapsed = Stopwatch.GetElapsedTime(readStart);
            var readAllocated = GC.GetAllocatedBytesForCurrentThread() - readAllocatedBefore;
            Assert.Equal(expectedRegistrations * reads, checksum);
            var snapshot = registry.GetSnapshot();
            if (expectedRegistrations != 0)
            {
                Assert.Equal(PathCount, snapshot.Sum(static r => r.ExecutablePaths.Count));
                Assert.All(snapshot, static row => Assert.Equal(2, row.ExecutablePaths.Count));
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                measurement = "snapshot-reads", label, reads, totalElapsedMilliseconds = readElapsed.TotalMilliseconds,
                averageElapsedMilliseconds = readElapsed.TotalMilliseconds / reads,
                threadManagedAllocatedBytes = readAllocated, averageManagedAllocatedBytes = readAllocated / reads,
                checksum
            }));
            GC.KeepAlive(registry);
        }
        finally { await registry.StopAsync(default); }
    }

    private static object PrivateField(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadCount(SqlitePortableSoftwareRegistry registry) => registry.GetSnapshot().Count;

    private static async Task SeedAsync(ResourceManagerDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO portable_software_executables
            (software_id, catalog_entry_id, display_name, kind, executable_path, root_path,
             first_observed_utc_ticks, identity_confirmed, root_confirmed)
            VALUES ($id, $catalog, $name, $kind, $path, $root, $ticks, 1, $confirmed);
            """;
        foreach (var name in new[] { "$id", "$catalog", "$name", "$kind", "$path", "$root", "$ticks", "$confirmed" })
            command.Parameters.AddWithValue(name, "");
        var row = 0;
        while (row < PathCount)
        {
            var identity = row / 2;
            var entry = $"portable-workstation-{identity:D3}";
            var root = $"d:/portable/{entry}";
            command.Parameters["$id"].Value = $"catalog:{entry}";
            command.Parameters["$catalog"].Value = entry;
            command.Parameters["$name"].Value = $"Workstation Application {identity:D3}";
            command.Parameters["$kind"].Value = (identity % 4) switch { 0 => "game", 1 => "high_performance", _ => "other" };
            command.Parameters["$path"].Value = root + (row % 2 == 0 ? "/app.exe" : "/bin/helper.exe");
            command.Parameters["$root"].Value = root;
            command.Parameters["$ticks"].Value = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + identity).UtcTicks;
            command.Parameters["$confirmed"].Value = identity % 5 == 0 ? 0 : 1;
            await command.ExecuteNonQueryAsync();
            row++;
        }
        await transaction.CommitAsync();
    }

    public void Dispose()
    {
        ledgerOwner.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(testRoot, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
