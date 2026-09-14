using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

public sealed class SqlitePortableSoftwareRegistry(
    ResourceManagerDatabase database,
    RuntimePlanProvider runtimePlanProvider,
    HostManagerPortableSoftwareRegistryRuntime deployment,
    ILogger<SqlitePortableSoftwareRegistry> logger) : IPortableSoftwareRegistry, IHostedService, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Channel<bool> writerSignals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private RegistryContext? context;
    private CancellationTokenSource? writerCts;
    private Task? writerTask;
    private bool started;
    private bool disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (started)
        {
            return;
        }

        await database.EnsureInitializedAsync(cancellationToken);
        await ApplyPlanAsync(
            runtimePlanProvider.Current.HostManager.RequirePublished(),
            cancellationToken,
            throwOnFailure: true);
        writerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writerTask = Task.Run(() => RunWriterAsync(writerCts.Token), CancellationToken.None);
        runtimePlanProvider.Published += OnPublished;
        started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            return;
        }

        runtimePlanProvider.Published -= OnPublished;
        started = false;
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            if (context is not null)
            {
                await FlushAllDirtyCoreAsync(context, cancellationToken);
            }
        }
        finally
        {
            operationGate.Release();
        }

        writerSignals.Writer.TryComplete();
        writerCts?.Cancel();
        if (writerTask is { } task)
        {
            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Observe(PortableSoftwareObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ThrowIfDisposed();
        operationGate.Wait();
        try
        {
            var current = RequireContext();
            var now = DateTimeOffset.UtcNow;
            var envelope = current.Projection.ProjectObserve(
                observation,
                current.Session.ConfigurationGeneration,
                NextEpoch(ref current.OperationEpoch, "operation"),
                now,
                now);
            var input = envelope.Input;
            RequireStatus(
                current.Session.Observe(in input, envelope.KeyBytes.Span),
                "observe");
        }
        finally
        {
            operationGate.Release();
        }

        SignalWriter();
    }

    public IReadOnlyList<PortableSoftwareRegistration> GetSnapshot()
    {
        ThrowIfDisposed();
        operationGate.Wait();
        try
        {
            return CaptureSnapshot(RequireContext()).Registrations;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<PortableSoftwareRegistration>> RefreshAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireContext();
            var before = CaptureSnapshot(current);
            foreach (var path in before.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path.ExecutablePath))
                {
                    continue;
                }

                var input = current.Projection.ProjectMarkMissing(
                    path.SoftwareId,
                    path.ExecutablePath,
                    current.Session.ConfigurationGeneration,
                    NextEpoch(ref current.OperationEpoch, "operation"),
                    DateTimeOffset.UtcNow);
                RequireStatus(current.Session.MarkMissing(in input), "mark-missing");
            }

            await FlushAllDirtyCoreAsync(current, cancellationToken);
            return CaptureSnapshot(current).Registrations;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
        PortableSoftwareRootConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rootPath = NormalizeExistingDirectory(request.RootPath)
            ?? throw new ArgumentException("请选择真实存在的软件根目录。", nameof(request));

        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireContext();
            var envelope = current.Projection.ProjectConfirmRoot(
                request.SoftwareId,
                rootPath,
                current.Session.ConfigurationGeneration,
                NextEpoch(ref current.OperationEpoch, "operation"),
                DateTimeOffset.UtcNow);
            var input = envelope.Input;
            var status = current.Session.ConfirmRoot(
                in input,
                envelope.KeyBytes.Span,
                out var confirmedCount);
            if (status == NativePortableSoftwareRegistryStatus.NoData)
            {
                throw new KeyNotFoundException(
                    $"未找到便携软件登记或该目录下的程序入口：{request.SoftwareId}");
            }
            RequireStatus(status, "confirm-root");
            await FlushAllDirtyCoreAsync(current, cancellationToken);

            var snapshot = CaptureSnapshot(current);
            var registration = snapshot.Registrations.Single(
                item => item.SoftwareId.Equals(request.SoftwareId, StringComparison.OrdinalIgnoreCase));
            var canonicalRoot = NativePortableSoftwareRegistryProjection.CanonicalPath(
                rootPath,
                nameof(request.RootPath));
            return new PortableSoftwareRootConfirmationResult(
                registration.SoftwareId,
                canonicalRoot,
                checked((int)confirmedCount),
                registration.RequiresRootPathConfirmation,
                "Confirmed",
                registration.RequiresRootPathConfirmation
                    ? "已确认所选目录，仍有其他程序入口需要确认。"
                    : "软件根目录已确认。");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (started)
        {
            runtimePlanProvider.Published -= OnPublished;
            started = false;
        }
        writerCts?.Cancel();
        writerCts?.Dispose();
        operationGate.Wait();
        try
        {
            context?.Dispose();
            context = null;
            disposed = true;
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
        }
    }

    private void OnPublished(CompiledRuntimePlan plan)
    {
        try
        {
            ApplyPlanAsync(
                plan.HostManager.RequirePublished(),
                CancellationToken.None,
                throwOnFailure: false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Host Manager portable-software-registry plan apply failed; the prior native session remains active.");
        }
    }

    private async Task ApplyPlanAsync(
        CompiledHostManagerPlan hostPlan,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var desired = hostPlan.PortableSoftwareRegistry;
            if (context?.Plan.ConfigurationSha256 == desired.ConfigurationSha256
                && context.Plan.Build == desired.Build
                && context.Plan.Recreate == desired.Recreate)
            {
                return;
            }

            var initial = context is null;
            var recreate = !initial
                && (context!.Plan.Build != desired.Build || context.Plan.Recreate != desired.Recreate);
            var token = initial
                ? deployment.BeginInitialCreate(hostPlan)
                : recreate
                    ? deployment.BeginHostRecreateAndHotPublish(hostPlan)
                    : deployment.BeginHotPublish(hostPlan);
            RegistryContext? replacement = null;
            try
            {
                if (context is not null)
                {
                    await FlushAllDirtyCoreAsync(context, cancellationToken);
                }

                var persisted = await LoadPersistedRowsAsync(cancellationToken);
                replacement = CreateContext(desired, persisted, DateTimeOffset.UtcNow);
                deployment.CompleteSucceeded(token);
                var previous = context;
                context = replacement;
                replacement = null;
                previous?.Dispose();
            }
            catch (Exception ex)
            {
                replacement?.Dispose();
                try
                {
                    deployment.CompleteFailed(token, "portable-software-registry-apply-failed");
                }
                catch (Exception settlement)
                {
                    throw new AggregateException(ex, settlement);
                }

                throw;
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager portable-software-registry plan apply failed; the prior native session remains active.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    private RegistryContext CreateContext(
        CompiledHostManagerPortableSoftwareRegistryPlan plan,
        IReadOnlyList<PersistedPath> persisted,
        DateTimeOffset commandUtc)
    {
        var payloads = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var workspace = new NativePortableSoftwareRegistryWorkspace(plan);
        try
        {
            var capacity = workspace.Session.Capacity;
            var projection = new NativePortableSoftwareRegistryProjection(
                payloads,
                capacity.ExecutablePathByteCapacityPerPath,
                capacity.RootPathByteCapacityPerPath,
                checked((uint)plan.HotPublish.MaximumFutureSkewMilliseconds));
            var import = BuildImport(
                persisted,
                payloads,
                workspace.Session.ConfigurationGeneration,
                commandUtc);
            var importInput = import.Input;
            RequireStatus(
                workspace.Session.Import(
                    in importInput,
                    import.Rows,
                    import.KeyBytes),
                "import");
            return new RegistryContext(
                plan,
                workspace,
                payloads,
                projection,
                new NativePortableSoftwareRegistryPersistenceStore(database, payloads),
                operationEpoch: import.Input.OperationEpoch,
                importGeneration: import.Input.ImportGeneration);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await writerSignals.Reader.WaitToReadAsync(cancellationToken))
            {
                while (writerSignals.Reader.TryRead(out _))
                {
                }

                await operationGate.WaitAsync(cancellationToken);
                try
                {
                    if (context is not null)
                    {
                        await FlushAllDirtyCoreAsync(context, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (
                    ex is SqliteException
                        or IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException)
                {
                    logger.LogWarning(
                        ex,
                        "Portable software persistence is unavailable; Zig retains the exact dirty state for the next explicit write signal.");
                }
                finally
                {
                    operationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task FlushAllDirtyCoreAsync(
        RegistryContext current,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Array.Clear(current.PersistenceOperations);
            var input = new NativePortableSoftwarePlanPersistenceInput
            {
                AbiVersion = NativePortableSoftwareRegistryAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativePortableSoftwarePlanPersistenceInput>()),
                ConfigurationGeneration = current.Session.ConfigurationGeneration,
                PlanEpoch = NextEpoch(ref current.PlanEpoch, "persistence plan"),
                MaximumOperationCount = checked((uint)current.PersistenceOperations.Length),
                ValidMask = (ulong)NativePortableSoftwarePlanValidity.Required
            };
            RequireStatus(
                current.Session.PlanPersistence(
                    in input,
                    current.PersistenceOperations,
                    out var output),
                "plan-persistence");
            ValidatePlanOutput(current, in input, in output);
            if (output.OperationCount == 0)
            {
                return;
            }

            var operations = current.PersistenceOperations
                .AsSpan(0, checked((int)output.OperationCount))
                .ToArray();
            var feedback = await current.PersistenceStore.PersistAsync(
                operations,
                cancellationToken);
            var feedbackInput = new NativePortableSoftwarePersistenceFeedbackInput
            {
                AbiVersion = NativePortableSoftwareRegistryAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativePortableSoftwarePersistenceFeedbackInput>()),
                ConfigurationGeneration = current.Session.ConfigurationGeneration,
                FeedbackEpoch = NextEpoch(ref current.FeedbackEpoch, "persistence feedback"),
                FeedbackCount = checked((uint)feedback.Length),
                ValidMask = (ulong)NativePortableSoftwareFeedbackValidity.Required
            };
            RequireStatus(
                current.Session.ApplyFeedback(in feedbackInput, feedback),
                "apply-persistence-feedback");
        }
    }

    private RegistrySnapshot CaptureSnapshot(RegistryContext current)
    {
        var registrations = new List<NativePortableSoftwareRegistrationSnapshot>();
        var paths = new List<NativePortableSoftwarePathSnapshot>();
        uint registrationCursor = 0;
        uint pathCursor = 0;
        uint expectedRegistrationCount = 0;
        uint expectedPathCount = 0;
        var firstPage = true;
        while (true)
        {
            Array.Clear(current.RegistrationSnapshots);
            Array.Clear(current.PathSnapshots);
            var input = new NativePortableSoftwareSnapshotInput
            {
                AbiVersion = NativePortableSoftwareRegistryAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativePortableSoftwareSnapshotInput>()),
                ConfigurationGeneration = current.Session.ConfigurationGeneration,
                SnapshotEpoch = NextEpoch(ref current.SnapshotEpoch, "snapshot"),
                RegistrationCursor = registrationCursor,
                PathCursor = pathCursor,
                MaximumRegistrationCount = checked((uint)current.RegistrationSnapshots.Length),
                MaximumPathCount = checked((uint)current.PathSnapshots.Length),
                ValidMask = (ulong)NativePortableSoftwareSnapshotValidity.Required
            };
            RequireStatus(
                current.Session.Snapshot(
                    in input,
                    current.RegistrationSnapshots,
                    current.PathSnapshots,
                    out var output),
                "snapshot");
            ValidateSnapshotOutput(current, in input, in output);
            if (firstPage)
            {
                expectedRegistrationCount = output.RegistrationCount;
                expectedPathCount = output.PathCount;
                firstPage = false;
            }
            else if (output.RegistrationCount != expectedRegistrationCount
                || output.PathCount != expectedPathCount)
            {
                throw new InvalidOperationException(
                    "Native portable software snapshot totals changed while the serialized capture was in progress.");
            }

            registrations.AddRange(
                current.RegistrationSnapshots.AsSpan(
                    0,
                    checked((int)output.RegistrationOutputCount)).ToArray());
            paths.AddRange(
                current.PathSnapshots.AsSpan(
                    0,
                    checked((int)output.PathOutputCount)).ToArray());
            registrationCursor = output.NextRegistrationCursor;
            pathCursor = output.NextPathCursor;
            var registrationMore = (
                output.Flags
                & (uint)NativePortableSoftwareSnapshotOutputFlags.RegistrationHasMore) != 0;
            var pathMore = (
                output.Flags
                & (uint)NativePortableSoftwareSnapshotOutputFlags.PathHasMore) != 0;
            if (!registrationMore && !pathMore)
            {
                break;
            }
        }

        if (registrations.Count != expectedRegistrationCount || paths.Count != expectedPathCount)
        {
            throw new InvalidOperationException(
                "Native portable software snapshot pages do not represent the exact published totals.");
        }

        return ProjectSnapshot(current.Payloads, registrations, paths);
    }

    private static RegistrySnapshot ProjectSnapshot(
        NativePortableSoftwareRegistryPayloadCatalog payloads,
        IReadOnlyList<NativePortableSoftwareRegistrationSnapshot> registrations,
        IReadOnlyList<NativePortableSoftwarePathSnapshot> paths)
    {
        var pathViews = paths
            .Select(path =>
            {
                ValidatePathSnapshot(path);
                return new PathView(
                    path.SoftwareHandle,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.SoftwareId,
                        path.SoftwareHandle).Text,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.ExecutablePath,
                        path.PathHandle).Text,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.RootPath,
                        path.RootHandle).Text,
                    DateTimeOffset.FromUnixTimeMilliseconds(path.FirstObservedUtcMilliseconds),
                    (path.Flags & (uint)NativePortableSoftwareSnapshotFlags.IdentityConfirmed) != 0,
                    (path.Flags & (uint)NativePortableSoftwareSnapshotFlags.RootConfirmed) != 0);
            })
            .ToArray();
        var projected = registrations
            .Select(registration =>
            {
                ValidateRegistrationSnapshot(registration);
                var registrationPaths = pathViews
                    .Where(path => path.SoftwareHandle == registration.SoftwareHandle)
                    .OrderBy(static path => path.ExecutablePath, StringComparer.Ordinal)
                    .ToArray();
                if (registrationPaths.Length != registration.ActivePathCount
                    || registrationPaths.Count(static path => path.RootConfirmed)
                        != registration.ConfirmedRootCount)
                {
                    throw new InvalidOperationException(
                        "Native portable software registration aggregate does not match its exact path rows.");
                }

                return new PortableSoftwareRegistration(
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.SoftwareId,
                        registration.SoftwareHandle).Text,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.CatalogEntryId,
                        registration.CatalogEntryHandle).Text,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.DisplayName,
                        registration.DisplayNameHandle).Text,
                    payloads.ResolveRequired(
                        NativePortableSoftwarePayloadKind.SoftwareKind,
                        registration.SoftwareKindHandle).Text,
                    registrationPaths
                        .Select(static path => path.ExecutablePath)
                        .ToArray(),
                    registrationPaths
                        .Where(static path => path.RootConfirmed)
                        .Select(static path => path.RootPath)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        registration.FirstObservedUtcMilliseconds),
                    registrationPaths
                        .Where(static path => !path.RootConfirmed)
                        .Select(static path => path.RootPath)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    (registration.Flags
                        & (uint)NativePortableSoftwareSnapshotFlags.IdentityConfirmed) != 0,
                    (registration.Flags
                        & (uint)NativePortableSoftwareSnapshotFlags.RequiresRootConfirmation) != 0);
            })
            .OrderBy(static registration => registration.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RegistrySnapshot(projected, pathViews);
    }

    private async Task<IReadOnlyList<PersistedPath>> LoadPersistedRowsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<PersistedPath>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken);
        var requiresCanonicalRewrite = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT software_id, catalog_entry_id, display_name, kind,
                       executable_path, root_path, first_observed_utc_ticks,
                       identity_confirmed, root_confirmed
                FROM portable_software_executables
                ORDER BY software_id, executable_path;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawSoftwareId = reader.GetString(0);
                var rawCatalogEntryId = reader.GetString(1);
                var rawDisplayName = reader.GetString(2);
                var rawKind = reader.GetString(3);
                var rawExecutablePath = reader.GetString(4);
                var rawRootPath = reader.GetString(5);
                var rawFirstObservedTicks = reader.GetInt64(6);
                var rawIdentityConfirmed = reader.GetInt32(7);
                var rawRootConfirmed = reader.GetInt32(8);
                if (rawIdentityConfirmed is not 0 and not 1
                    || rawRootConfirmed is not 0 and not 1)
                {
                    throw new InvalidDataException(
                        "Persisted portable software confirmation fields must be exact booleans.");
                }
                if (rawFirstObservedTicks % TimeSpan.TicksPerMillisecond != 0)
                {
                    throw new InvalidDataException(
                        "Persisted portable software observation time cannot be represented losslessly by the millisecond ABI.");
                }

                var softwareId = CanonicalToken(rawSoftwareId, "software_id");
                var catalogEntryId = CanonicalToken(
                    rawCatalogEntryId,
                    "catalog_entry_id");
                if (!softwareId.Equals($"catalog:{catalogEntryId}", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Persisted portable software id does not exactly identify its catalog entry.");
                }
                var displayName = RequiredText(rawDisplayName, "display_name");
                var kind = CanonicalToken(rawKind, "kind");
                var executablePath = NativePortableSoftwareRegistryProjection.CanonicalPath(
                    rawExecutablePath,
                    "executable_path");
                var rootPath = NativePortableSoftwareRegistryProjection.CanonicalPath(
                    rawRootPath,
                    "root_path");
                if (!IsSameOrUnder(executablePath, rootPath))
                {
                    throw new InvalidDataException(
                        "Persisted portable software executable is outside its recorded root.");
                }

                DateTimeOffset firstObservedAt;
                try
                {
                    firstObservedAt = new DateTimeOffset(
                        rawFirstObservedTicks,
                        TimeSpan.Zero);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new InvalidDataException(
                        "Persisted portable software observation timestamp is invalid.",
                        ex);
                }
                if (firstObservedAt.ToUnixTimeMilliseconds() < 0)
                {
                    throw new InvalidDataException(
                        "Persisted portable software observation predates the published UTC ABI.");
                }
                var identityConfirmed = rawIdentityConfirmed != 0;
                var rootConfirmed = rawRootConfirmed != 0;
                if (rootConfirmed && !identityConfirmed)
                {
                    throw new InvalidDataException(
                        "Persisted portable software root confirmation lacks identity confirmation.");
                }
                requiresCanonicalRewrite |= !rawSoftwareId.Equals(
                        softwareId,
                        StringComparison.Ordinal)
                    || !rawCatalogEntryId.Equals(catalogEntryId, StringComparison.Ordinal)
                    || !rawDisplayName.Equals(displayName, StringComparison.Ordinal)
                    || !rawKind.Equals(kind, StringComparison.Ordinal)
                    || !rawExecutablePath.Equals(executablePath, StringComparison.Ordinal)
                    || !rawRootPath.Equals(rootPath, StringComparison.Ordinal);
                result.Add(new PersistedPath(
                    softwareId,
                    catalogEntryId,
                    displayName,
                    kind,
                    executablePath,
                    rootPath,
                    firstObservedAt,
                    identityConfirmed,
                    rootConfirmed));
            }
        }

        var ordered = result
            .OrderBy(static row => row.SoftwareId, StringComparer.Ordinal)
            .ThenBy(static row => row.ExecutablePath, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].SoftwareId == ordered[index].SoftwareId
                && ordered[index - 1].ExecutablePath == ordered[index].ExecutablePath)
            {
                throw new InvalidDataException(
                    "Persisted portable software rows collide after canonicalization.");
            }
        }

        if (requiresCanonicalRewrite)
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM portable_software_executables;";
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var row in ordered)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO portable_software_executables(
                        software_id, catalog_entry_id, display_name, kind,
                        executable_path, root_path, first_observed_utc_ticks,
                        identity_confirmed, root_confirmed)
                    VALUES (
                        $softwareId, $catalogEntryId, $displayName, $kind,
                        $executablePath, $rootPath, $firstObservedAt,
                        $identityConfirmed, $rootConfirmed);
                    """;
                insert.Parameters.AddWithValue("$softwareId", row.SoftwareId);
                insert.Parameters.AddWithValue("$catalogEntryId", row.CatalogEntryId);
                insert.Parameters.AddWithValue("$displayName", row.DisplayName);
                insert.Parameters.AddWithValue("$kind", row.Kind);
                insert.Parameters.AddWithValue("$executablePath", row.ExecutablePath);
                insert.Parameters.AddWithValue("$rootPath", row.RootPath);
                insert.Parameters.AddWithValue(
                    "$firstObservedAt",
                    row.FirstObservedAt.UtcTicks);
                insert.Parameters.AddWithValue(
                    "$identityConfirmed",
                    row.IdentityConfirmed ? 1 : 0);
                insert.Parameters.AddWithValue(
                    "$rootConfirmed",
                    row.RootConfirmed ? 1 : 0);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ordered;
    }

    private static ImportEnvelope BuildImport(
        IReadOnlyList<PersistedPath> persisted,
        NativePortableSoftwareRegistryPayloadCatalog payloads,
        ulong configurationGeneration,
        DateTimeOffset commandUtc)
    {
        var rows = new NativePortableSoftwarePersistedPathInput[persisted.Count];
        var keys = new ArrayBufferWriter<byte>();
        for (var index = 0; index < persisted.Count; index++)
        {
            var persistedPath = persisted[index];
            var handles = payloads.GetOrAddBatch(
            [
                new(NativePortableSoftwarePayloadKind.SoftwareId, persistedPath.SoftwareId),
                new(NativePortableSoftwarePayloadKind.CatalogEntryId, persistedPath.CatalogEntryId),
                new(NativePortableSoftwarePayloadKind.DisplayName, persistedPath.DisplayName),
                new(NativePortableSoftwarePayloadKind.SoftwareKind, persistedPath.Kind),
                new(NativePortableSoftwarePayloadKind.ExecutablePath, persistedPath.ExecutablePath),
                new(NativePortableSoftwarePayloadKind.RootPath, persistedPath.RootPath)
            ]);
            var executable = StrictUtf8.GetBytes(persistedPath.ExecutablePath);
            var root = StrictUtf8.GetBytes(persistedPath.RootPath);
            var executableOffset = checked((uint)keys.WrittenCount);
            keys.Write(executable);
            var rootOffset = checked((uint)keys.WrittenCount);
            keys.Write(root);
            rows[index] = new NativePortableSoftwarePersistedPathInput
            {
                StructSize = checked((uint)Marshal.SizeOf<NativePortableSoftwarePersistedPathInput>()),
                Flags = (uint)(
                    (persistedPath.IdentityConfirmed
                        ? NativePortableSoftwarePathFlags.IdentityConfirmed
                        : 0)
                    | (persistedPath.RootConfirmed
                        ? NativePortableSoftwarePathFlags.RootConfirmed
                        : 0)),
                SoftwareHandle = handles[0],
                CatalogEntryHandle = handles[1],
                DisplayNameHandle = handles[2],
                SoftwareKindHandle = handles[3],
                PathHandle = handles[4],
                RootHandle = handles[5],
                FirstObservedUtcMilliseconds = persistedPath.FirstObservedAt.ToUnixTimeMilliseconds(),
                ExecutablePathOffset = executableOffset,
                ExecutablePathLength = checked((uint)executable.Length),
                RootPathOffset = rootOffset,
                RootPathLength = checked((uint)root.Length)
            };
        }

        var keyBytes = keys.WrittenMemory.ToArray();
        var input = new NativePortableSoftwareImportInput
        {
            AbiVersion = NativePortableSoftwareRegistryAbi.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativePortableSoftwareImportInput>()),
            ConfigurationGeneration = configurationGeneration,
            ImportGeneration = 1,
            OperationEpoch = 1,
            CommandUtcMilliseconds = commandUtc.ToUnixTimeMilliseconds(),
            RowCount = checked((uint)rows.Length),
            KeyByteCount = checked((uint)keyBytes.Length),
            ValidMask = (ulong)NativePortableSoftwareImportValidity.Required
        };
        return new ImportEnvelope(input, rows, keyBytes);
    }

    private static void ValidatePlanOutput(
        RegistryContext current,
        in NativePortableSoftwarePlanPersistenceInput input,
        in NativePortableSoftwarePersistencePlanOutput output)
    {
        if (output.AbiVersion != NativePortableSoftwareRegistryAbi.Version
            || output.StructSize != Marshal.SizeOf<NativePortableSoftwarePersistencePlanOutput>()
            || output.ConfigurationGeneration != current.Session.ConfigurationGeneration
            || output.PlanEpoch != input.PlanEpoch
            || output.OperationCount > input.MaximumOperationCount
            || output.TotalDirtyCount < output.OperationCount
            || (output.Flags & ~(uint)NativePortableSoftwarePlanOutputFlags.Known) != 0
            || ((output.Flags & (uint)NativePortableSoftwarePlanOutputFlags.HasMore) != 0
                ? output.OperationCount >= output.TotalDirtyCount
                : output.OperationCount != output.TotalDirtyCount)
            || output.ReservedU32 != 0
            || output.FirstMutationVersion
                != (output.OperationCount == 0
                    ? 0
                    : current.PersistenceOperations[0].MutationVersion)
            || output.LastMutationVersion
                != (output.OperationCount == 0
                    ? 0
                    : current.PersistenceOperations[output.OperationCount - 1].MutationVersion))
        {
            throw new InvalidOperationException(
                "Native portable software persistence plan violates the fixed output contract.");
        }
    }

    private static void ValidateSnapshotOutput(
        RegistryContext current,
        in NativePortableSoftwareSnapshotInput input,
        in NativePortableSoftwareSnapshotOutput output)
    {
        if (output.AbiVersion != NativePortableSoftwareRegistryAbi.Version
            || output.StructSize != Marshal.SizeOf<NativePortableSoftwareSnapshotOutput>()
            || output.ConfigurationGeneration != current.Session.ConfigurationGeneration
            || output.LastSnapshotEpoch != input.SnapshotEpoch
            || output.RegistrationOutputCount > input.MaximumRegistrationCount
            || output.PathOutputCount > input.MaximumPathCount
            || output.NextRegistrationCursor < input.RegistrationCursor
            || output.NextPathCursor < input.PathCursor
            || (output.Flags & ~(uint)NativePortableSoftwareSnapshotOutputFlags.Known) != 0
            || output.ResidentByteCount > checked((ulong)current.Plan.HotPublish.ResidentByteBudget))
        {
            throw new InvalidOperationException(
                "Native portable software snapshot violates the fixed output contract.");
        }
    }

    private static unsafe void ValidateRegistrationSnapshot(
        NativePortableSoftwareRegistrationSnapshot value)
    {
        if (value.StructSize != Marshal.SizeOf<NativePortableSoftwareRegistrationSnapshot>()
            || (value.Flags & ~(uint)NativePortableSoftwareSnapshotFlags.Known) != 0
            || value.SoftwareHandle == 0
            || value.CatalogEntryHandle == 0
            || value.DisplayNameHandle == 0
            || value.SoftwareKindHandle == 0
            || value.FirstObservedUtcMilliseconds < 0
            || value.ActivePathCount == 0
            || value.ConfirmedRootCount > value.ActivePathCount
            || value.DirtyPathCount > value.ActivePathCount
            || value.ReservedU32 != 0
            || value.Reserved[0] != 0
            || value.Reserved[1] != 0)
        {
            throw new InvalidOperationException(
                "Native portable software registration snapshot violates the fixed row contract.");
        }
    }

    private static unsafe void ValidatePathSnapshot(NativePortableSoftwarePathSnapshot value)
    {
        if (value.StructSize != Marshal.SizeOf<NativePortableSoftwarePathSnapshot>()
            || (value.Flags & ~(uint)NativePortableSoftwareSnapshotFlags.Known) != 0
            || ((value.Flags & (uint)NativePortableSoftwareSnapshotFlags.Dirty) != 0
                && value.MutationVersion == 0)
            || ((value.Flags & (uint)NativePortableSoftwareSnapshotFlags.RootConfirmed) != 0
                && (value.Flags & (uint)NativePortableSoftwareSnapshotFlags.IdentityConfirmed) == 0)
            || value.SoftwareHandle == 0
            || value.PathHandle == 0
            || value.RootHandle == 0
            || value.FirstObservedUtcMilliseconds < 0
            || value.ExecutablePathLength == 0
            || value.RootPathLength == 0
            || value.Reserved[0] != 0
            || value.Reserved[1] != 0
            || value.Reserved[2] != 0)
        {
            throw new InvalidOperationException(
                "Native portable software path snapshot violates the fixed row contract.");
        }
    }

    private static void RequireStatus(
        NativePortableSoftwareRegistryStatus status,
        string operation)
    {
        if (status != NativePortableSoftwareRegistryStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native portable software registry {operation} failed with {status}.");
        }
    }

    private RegistryContext RequireContext()
        => context
            ?? throw new InvalidOperationException(
                "The Host Manager portable-software-registry session is not ready.");

    private void SignalWriter()
    {
        if (!writerSignals.Writer.TryWrite(true)
            && writerSignals.Reader.Completion.IsCompleted)
        {
            logger.LogWarning(
                "Portable software persistence writer is closed; Zig retains the exact dirty state.");
        }
    }

    private static ulong NextEpoch(ref ulong value, string name)
    {
        if (value == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                $"Portable software {name} epoch is exhausted.");
        }
        return ++value;
    }

    private static string? NormalizeExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }

    private static string CanonicalToken(string value, string field)
        => RequiredText(value, field).ToLowerInvariant();

    private static string RequiredText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Persisted portable software {field} is empty.");
        }
        var result = value.Trim();
        if (result.AsSpan().Contains('\0'))
        {
            throw new InvalidDataException(
                $"Persisted portable software {field} contains NUL.");
        }
        try
        {
            _ = StrictUtf8.GetByteCount(result);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Persisted portable software {field} is not valid Unicode.",
                ex);
        }
        return result;
    }

    private static bool IsSameOrUnder(string path, string root)
        => path.Equals(root, StringComparison.Ordinal)
            || path.StartsWith(
                root.EndsWith('/') ? root : root + '/',
                StringComparison.Ordinal);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class RegistryContext(
        CompiledHostManagerPortableSoftwareRegistryPlan plan,
        NativePortableSoftwareRegistryWorkspace workspace,
        NativePortableSoftwareRegistryPayloadCatalog payloads,
        NativePortableSoftwareRegistryProjection projection,
        NativePortableSoftwareRegistryPersistenceStore persistenceStore,
        ulong operationEpoch,
        ulong importGeneration) : IDisposable
    {
        public CompiledHostManagerPortableSoftwareRegistryPlan Plan { get; } = plan;
        public NativePortableSoftwareRegistryWorkspace Workspace { get; } = workspace;
        public NativePortableSoftwareRegistrySession Session => Workspace.Session;
        public NativePortableSoftwareRegistryPayloadCatalog Payloads { get; } = payloads;
        public NativePortableSoftwareRegistryProjection Projection { get; } = projection;
        public NativePortableSoftwareRegistryPersistenceStore PersistenceStore { get; } = persistenceStore;
        public NativePortableSoftwarePersistenceOperation[] PersistenceOperations { get; } =
            new NativePortableSoftwarePersistenceOperation[
                checked((int)workspace.Session.Capacity.PersistenceOperationCapacity)];
        public NativePortableSoftwareRegistrationSnapshot[] RegistrationSnapshots { get; } =
            new NativePortableSoftwareRegistrationSnapshot[
                checked((int)workspace.Session.Capacity.RegistrationSnapshotCapacity)];
        public NativePortableSoftwarePathSnapshot[] PathSnapshots { get; } =
            new NativePortableSoftwarePathSnapshot[
                checked((int)workspace.Session.Capacity.PathSnapshotCapacity)];
        public ulong OperationEpoch = operationEpoch;
        public ulong ImportGeneration = importGeneration;
        public ulong PlanEpoch;
        public ulong FeedbackEpoch;
        public ulong SnapshotEpoch;

        public void Dispose() => Workspace.Dispose();
    }

    private sealed record PersistedPath(
        string SoftwareId,
        string CatalogEntryId,
        string DisplayName,
        string Kind,
        string ExecutablePath,
        string RootPath,
        DateTimeOffset FirstObservedAt,
        bool IdentityConfirmed,
        bool RootConfirmed);

    private sealed record ImportEnvelope(
        NativePortableSoftwareImportInput Input,
        NativePortableSoftwarePersistedPathInput[] Rows,
        byte[] KeyBytes);

    private sealed record PathView(
        ulong SoftwareHandle,
        string SoftwareId,
        string ExecutablePath,
        string RootPath,
        DateTimeOffset FirstObservedAt,
        bool IdentityConfirmed,
        bool RootConfirmed);

    private sealed record RegistrySnapshot(
        IReadOnlyList<PortableSoftwareRegistration> Registrations,
        IReadOnlyList<PathView> Paths);
}
