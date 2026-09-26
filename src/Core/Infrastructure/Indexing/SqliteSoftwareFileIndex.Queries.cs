using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Indexing;

public sealed partial class SqliteSoftwareFileIndex
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private const string CandidateProjection = """
        SELECT
            entry.id,
            root.software_id,
            root.software_name,
            root.root_path,
            entry.relative_path,
            entry.file_name,
            entry.extension,
            entry.size_bytes,
            entry.last_write_utc_ticks
        """;

    private const string StableCandidateOrder = """
        ORDER BY length(entry.file_name), CAST(entry.file_name AS BLOB), entry.id
        LIMIT $candidateLimit;
        """;

    private static readonly string ShortScanSql = $"""
        {CandidateProjection}
        FROM software_index_entries entry
        JOIN software_index_roots root ON root.id = entry.root_id
        WHERE instr(lower(entry.file_name), lower($query)) > 0
           OR instr(lower(entry.relative_path), lower($query)) > 0
           OR instr(lower(root.software_name), lower($query)) > 0
        {StableCandidateOrder}
        """;

    private static readonly string FileNameTrigramSql = $"""
        {CandidateProjection}
        FROM software_file_name_fts
        JOIN software_index_entries entry ON entry.id = software_file_name_fts.rowid
        JOIN software_index_roots root ON root.id = entry.root_id
        WHERE software_file_name_fts MATCH $expression
          AND instr(lower(entry.file_name), lower($query)) > 0
        {StableCandidateOrder}
        """;

    private static readonly string RelativePathUnicodeSql = $"""
        {CandidateProjection}
        FROM software_file_path_fts
        JOIN software_index_entries entry ON entry.id = software_file_path_fts.rowid
        JOIN software_index_roots root ON root.id = entry.root_id
        WHERE software_file_path_fts MATCH $expression
          AND instr(lower(entry.relative_path), lower($query)) > 0
        {StableCandidateOrder}
        """;

    private static readonly string SoftwareNameTrigramSql = $"""
        {CandidateProjection}
        FROM software_root_search_fts
        JOIN software_index_roots root ON root.id = software_root_search_fts.rowid
        JOIN software_index_entries entry ON entry.root_id = root.id
        WHERE software_root_search_fts MATCH $expression
          AND instr(lower(root.software_name), lower($query)) > 0
        {StableCandidateOrder}
        """;

    public async Task<SoftwareFileIndexStatistics> GetStatisticsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(DISTINCT software_id),
                COUNT(*),
                COALESCE(SUM(file_count), 0),
                COALESCE(SUM(total_bytes), 0),
                MIN(last_indexed_utc_ticks),
                MAX(last_indexed_utc_ticks)
            FROM software_index_roots;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SoftwareFileIndexStatistics(0, 0, 0, 0, null, null);
        }

        return new SoftwareFileIndexStatistics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
            reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero));
    }

    public async Task<SoftwareFileIndexSnapshot?> GetSnapshotAsync(
        string softwareId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT software_name, root_path, root_kind, total_bytes, file_count, last_indexed_utc_ticks
            FROM software_index_roots
            WHERE software_id = $softwareId
            ORDER BY root_path;
            """;
        command.Parameters.AddWithValue("$softwareId", softwareId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roots = new List<SoftwareFileIndexRootSnapshot>();
        string? softwareName = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            softwareName ??= reader.GetString(0);
            roots.Add(new SoftwareFileIndexRootSnapshot(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
        }

        if (roots.Count == 0)
        {
            return null;
        }

        return new SoftwareFileIndexSnapshot(
            softwareId,
            softwareName ?? softwareId,
            roots,
            roots.Sum(static root => root.TotalBytes),
            roots.Sum(static root => root.FileCount),
            roots.Min(static root => root.LastIndexedAt));
    }

    public async Task<IReadOnlyList<SoftwareFileSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var lease = await fileQuery.AcquireAsync(cancellationToken);
        var session = lease.Session;
        var capacity = session.Capacity;
        var queryByteCount = StrictUtf8.GetByteCount(query);
        if (queryByteCount > capacity.QueryUtf8ByteCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "The query exceeds the explicitly compiled native query capacity.");
        }
        if (limit <= 0 || (uint)limit > capacity.ResultCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "The result limit is outside the explicitly compiled native result capacity.");
        }

        var queryBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(queryByteCount, 1));
        var sourceBuffer = ArrayPool<NativeFileQuerySourcePlan>.Shared.Rent(
            checked((int)capacity.SourcePlanCapacity));
        var planBuffer = ArrayPool<byte>.Shared.Rent(
            checked((int)capacity.PlanUtf8ByteCapacity));
        var operationEpoch = fileQuery.NextEpoch();
        Exception? bodyFailure = null;
        try
        {
            NativeFileQueryPlanOutput plan;
            string normalizedQuery;
            NativeFileQuerySourceExecution[] sourceExecutions;
            {
                var actualQueryByteCount = StrictUtf8.GetBytes(query, queryBuffer);
                var queryBytes = queryBuffer.AsSpan(0, actualQueryByteCount);
                var sourceSpan = sourceBuffer.AsSpan(0, checked((int)capacity.SourcePlanCapacity));
                var planSpan = planBuffer.AsSpan(0, checked((int)capacity.PlanUtf8ByteCapacity));
                var beginInput = new NativeFileQueryBeginInput
                {
                    AbiVersion = NativeFileQueryAbi.Version,
                    StructSize = SizeOf<NativeFileQueryBeginInput>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = operationEpoch,
                    QueryEpoch = operationEpoch,
                    QueryByteCount = checked((uint)actualQueryByteCount),
                    RequestedResultCount = checked((uint)limit),
                    ValidMask = (ulong)NativeFileQueryBeginValidity.Required
                };
                RequireStatus(
                    session.Begin(
                        in beginInput,
                        queryBytes,
                        sourceSpan,
                        planSpan,
                        out plan),
                    "begin");
                ValidatePlan(
                    in plan,
                    sourceSpan,
                    planSpan,
                    session.ConfigurationGeneration,
                    operationEpoch,
                    checked((uint)limit));

                var sourceCount = checked((int)plan.SourcePlanCount);
                var planByteCount = checked((int)plan.PlanUtf8ByteCount);
                normalizedQuery = DecodePlanText(
                    planSpan[..planByteCount],
                    plan.NormalizedQueryOffset,
                    plan.NormalizedQueryLength,
                    "normalized query");
                sourceExecutions = new NativeFileQuerySourceExecution[sourceCount];
                for (var index = 0; index < sourceCount; index++)
                {
                    var source = sourceSpan[index];
                    sourceExecutions[index] = new NativeFileQuerySourceExecution(
                        source,
                        DecodePlanText(
                            planSpan[..planByteCount],
                            source.ExpressionOffset,
                            source.ExpressionLength,
                            "source expression"));
                }
            }
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            using var submission = new NativeFileQuerySubmission(
                fileQuery,
                session,
                capacity,
                plan.QueryEpoch,
                plan.MaximumTotalCandidateCount);
            foreach (var execution in sourceExecutions)
            {
                await ExecuteSourcePrimitiveAsync(
                    connection,
                    submission,
                    execution.Plan,
                    normalizedQuery,
                    execution.Expression,
                    cancellationToken);
            }
            submission.Flush();

            var nativeResults = ArrayPool<NativeFileQueryResult>.Shared.Rent(limit);
            try
            {
                var resultSpan = nativeResults.AsSpan(0, limit);
                var finalizeEpoch = fileQuery.NextEpoch();
                var finalizeInput = new NativeFileQueryFinalizeInput
                {
                    AbiVersion = NativeFileQueryAbi.Version,
                    StructSize = SizeOf<NativeFileQueryFinalizeInput>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = finalizeEpoch,
                    QueryEpoch = plan.QueryEpoch,
                    ResultCapacity = checked((uint)limit),
                    ValidMask = (ulong)NativeFileQueryFinalizeValidity.Required
                };
                RequireStatus(
                    session.Finalize(in finalizeInput, resultSpan, out var output),
                    "finalize");
                ValidateFinalize(
                    in output,
                    resultSpan,
                    submission,
                    session.ConfigurationGeneration,
                    plan.QueryEpoch,
                    checked((uint)limit));
                var results = new SoftwareFileSearchResult[checked((int)output.ResultCount)];
                for (var index = 0; index < results.Length; index++)
                {
                    results[index] = submission.Resolve(in resultSpan[index]);
                }
                return results;
            }
            finally
            {
                ArrayPool<NativeFileQueryResult>.Shared.Return(nativeResults, clearArray: true);
            }
        }
        catch (Exception ex)
        {
            bodyFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                var resetInput = new NativeFileQueryResetInput
                {
                    AbiVersion = NativeFileQueryAbi.Version,
                    StructSize = SizeOf<NativeFileQueryResetInput>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = fileQuery.NextEpoch(),
                    ValidMask = (ulong)NativeFileQueryResetValidity.Required
                };
                var resetStatus = session.Reset(in resetInput);
                if (resetStatus == NativeFileQueryStatus.Ok)
                {
                    lease.MarkReusable();
                }
                else if (bodyFailure is null)
                {
                    throw new InvalidOperationException(
                        $"Native file-query reset failed with {resetStatus}.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(queryBuffer, clearArray: false);
                ArrayPool<NativeFileQuerySourcePlan>.Shared.Return(sourceBuffer, clearArray: true);
                ArrayPool<byte>.Shared.Return(planBuffer, clearArray: false);
            }
        }
    }

    private static async Task ExecuteSourcePrimitiveAsync(
        SqliteConnection connection,
        NativeFileQuerySubmission submission,
        NativeFileQuerySourcePlan source,
        string normalizedQuery,
        string expression,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = (NativeFileQueryPrimitive)source.PrimitiveKind switch
        {
            NativeFileQueryPrimitive.ShortSubstringOrderedScan => ShortScanSql,
            NativeFileQueryPrimitive.FileNameTrigramFts => FileNameTrigramSql,
            NativeFileQueryPrimitive.RelativePathUnicodeFts => RelativePathUnicodeSql,
            NativeFileQueryPrimitive.SoftwareNameTrigramFts => SoftwareNameTrigramSql,
            _ => throw new InvalidOperationException(
                $"Native file-query returned unknown primitive {source.PrimitiveKind}.")
        };
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.Parameters.AddWithValue("$candidateLimit", checked((long)source.CandidateLimit));
        if ((NativeFileQueryPrimitive)source.PrimitiveKind
            != NativeFileQueryPrimitive.ShortSubstringOrderedScan)
        {
            command.Parameters.AddWithValue("$expression", expression);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryId = reader.GetInt64(0);
            if (entryId <= 0)
            {
                throw new InvalidDataException(
                    "The SQLite file index returned a non-positive entry identity.");
            }
            var softwareName = reader.GetString(2);
            var rootPath = reader.GetString(3);
            var relativePath = reader.GetString(4);
            var fileName = reader.GetString(5);
            submission.Add(
                source.SourceId,
                checked((ulong)entryId),
                fileName,
                relativePath,
                softwareName,
                new SoftwareFileSearchResult(
                    entryId,
                    reader.GetString(1),
                    softwareName,
                    rootPath,
                    relativePath,
                    Path.Combine(rootPath, relativePath),
                    fileName,
                    reader.GetString(6),
                    reader.GetInt64(7),
                    new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero)));
        }
    }

    private static unsafe void ValidatePlan(
        in NativeFileQueryPlanOutput plan,
        ReadOnlySpan<NativeFileQuerySourcePlan> sources,
        ReadOnlySpan<byte> planBytes,
        ulong configurationGeneration,
        ulong queryEpoch,
        uint requestedResultCount)
    {
        var mode = (NativeFileQueryPlanMode)plan.Mode;
        var sourceCount = checked((int)plan.SourcePlanCount);
        var planByteCount = checked((int)plan.PlanUtf8ByteCount);
        var expectedSourceMask = mode switch
        {
            NativeFileQueryPlanMode.ShortScan => NativeFileQuerySourceMask.ShortScan,
            NativeFileQueryPlanMode.Fts => NativeFileQuerySourceMask.FileName
                | NativeFileQuerySourceMask.RelativePath
                | NativeFileQuerySourceMask.SoftwareName,
            _ => NativeFileQuerySourceMask.None
        };
        var expectedSourceCount = mode == NativeFileQueryPlanMode.ShortScan ? 1 : 3;
        if (plan.AbiVersion != NativeFileQueryAbi.Version
            || plan.StructSize != SizeOf<NativeFileQueryPlanOutput>()
            || plan.ConfigurationGeneration != configurationGeneration
            || plan.StateRevision == 0
            || plan.QueryEpoch != queryEpoch
            || sourceCount != expectedSourceCount
            || sourceCount > sources.Length
            || plan.QueryRuneCount == 0
            || plan.RequestedResultCount != requestedResultCount
            || planByteCount <= 0
            || planByteCount > planBytes.Length
            || plan.SourceMask != (uint)expectedSourceMask
            || plan.CandidateLimitPerSource == 0
            || plan.MaximumTotalCandidateCount
                != checked(plan.CandidateLimitPerSource * plan.SourcePlanCount)
            || !ContainsSpan(planByteCount, plan.NormalizedQueryOffset, plan.NormalizedQueryLength)
            || plan.UnicodeTokenizerVersion != NativeFileQueryAbi.UnicodeTokenizerVersion
            || plan.UnicodeRemoveDiacriticsMode != NativeFileQueryAbi.UnicodeRemoveDiacriticsMode
            || plan.TrigramTokenizerContractVersion
                != NativeFileQueryAbi.TrigramTokenizerContractVersion
            || plan.TextMatchingVersion != NativeFileQueryAbi.TextMatchingVersion
            || plan.Flags != 0
            || plan.Reserved[0] != 0
            || plan.Reserved[1] != 0
            || plan.Reserved[2] != 0)
        {
            throw new InvalidDataException("Native file-query returned an invalid plan.");
        }

        var seenMask = NativeFileQuerySourceMask.None;
        for (var index = 0; index < sourceCount; index++)
        {
            var source = sources[index];
            var sourceId = (NativeFileQuerySource)source.SourceId;
            var sourceBit = sourceId switch
            {
                NativeFileQuerySource.FileName => NativeFileQuerySourceMask.FileName,
                NativeFileQuerySource.RelativePath => NativeFileQuerySourceMask.RelativePath,
                NativeFileQuerySource.SoftwareName => NativeFileQuerySourceMask.SoftwareName,
                NativeFileQuerySource.ShortScan => NativeFileQuerySourceMask.ShortScan,
                _ => NativeFileQuerySourceMask.None
            };
            var expectedPrimitive = sourceId switch
            {
                NativeFileQuerySource.FileName => NativeFileQueryPrimitive.FileNameTrigramFts,
                NativeFileQuerySource.RelativePath => NativeFileQueryPrimitive.RelativePathUnicodeFts,
                NativeFileQuerySource.SoftwareName => NativeFileQueryPrimitive.SoftwareNameTrigramFts,
                NativeFileQuerySource.ShortScan => NativeFileQueryPrimitive.ShortSubstringOrderedScan,
                _ => (NativeFileQueryPrimitive)0
            };
            if (source.StructSize != SizeOf<NativeFileQuerySourcePlan>()
                || sourceBit == NativeFileQuerySourceMask.None
                || (seenMask & sourceBit) != 0
                || (expectedSourceMask & sourceBit) == 0
                || source.PrimitiveKind != (uint)expectedPrimitive
                || (sourceId == NativeFileQuerySource.ShortScan
                    ? source.Priority != 0
                    : source.Priority == 0)
                || !ContainsSpan(planByteCount, source.ExpressionOffset, source.ExpressionLength)
                || source.CandidateLimit != plan.CandidateLimitPerSource
                || source.Flags != 0
                || source.Reserved[0] != 0
                || source.Reserved[1] != 0)
            {
                throw new InvalidDataException(
                    "Native file-query returned an invalid source plan.");
            }
            seenMask |= sourceBit;
        }
        if (seenMask != expectedSourceMask)
        {
            throw new InvalidDataException(
                "Native file-query source plans do not represent the advertised source mask.");
        }
    }

    private static unsafe void ValidateFinalize(
        in NativeFileQueryFinalizeOutput output,
        ReadOnlySpan<NativeFileQueryResult> results,
        NativeFileQuerySubmission submission,
        ulong configurationGeneration,
        ulong queryEpoch,
        uint requestedResultCount)
    {
        if (output.AbiVersion != NativeFileQueryAbi.Version
            || output.StructSize != SizeOf<NativeFileQueryFinalizeOutput>()
            || output.ConfigurationGeneration != configurationGeneration
            || output.StateRevision == 0
            || output.QueryEpoch != queryEpoch
            || output.ResultCount > requestedResultCount
            || output.ResultCount > (uint)results.Length
            || output.MatchedCandidateCount < output.ResultCount
            || output.MatchedCandidateCount > output.UniqueCandidateCount
            || output.SubmittedCandidateCount != submission.SubmittedCandidateCount
            || output.UniqueCandidateCount != submission.UniqueCandidateCount
            || output.Flags != 0
            || output.Reserved[0] != 0
            || output.Reserved[1] != 0
            || output.Reserved[2] != 0)
        {
            throw new InvalidDataException(
                "Native file-query returned an invalid finalization result.");
        }

        var resultCount = checked((int)output.ResultCount);
        for (var index = 0; index < resultCount; index++)
        {
            var result = results[index];
            if (result.StructSize != SizeOf<NativeFileQueryResult>()
                || result.SourcePriority > 3
                || result.EntryHandle == 0
                || result.CandidateOrdinal == 0
                || result.CandidateOrdinal > submission.CandidateOrdinalCount
                || result.FileNameRuneCount == 0
                || result.OrderIndex != (uint)index
                || result.Flags != 0
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0)
            {
                throw new InvalidDataException(
                    "Native file-query returned an invalid ranked result.");
            }
        }
    }

    private static string DecodePlanText(
        ReadOnlySpan<byte> planBytes,
        uint offset,
        uint length,
        string field)
    {
        if (!ContainsSpan(planBytes.Length, offset, length))
        {
            throw new InvalidDataException(
                $"Native file-query returned an invalid {field} span.");
        }
        return StrictUtf8.GetString(
            planBytes.Slice(checked((int)offset), checked((int)length)));
    }

    private static bool ContainsSpan(int capacity, uint offset, uint length)
    {
        var end = (ulong)offset + length;
        return length > 0 && end <= checked((ulong)capacity);
    }

    private static void RequireStatus(NativeFileQueryStatus status, string operation)
    {
        if (status != NativeFileQueryStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native file-query {operation} failed with {status}.");
        }
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private readonly record struct NativeFileQuerySourceExecution(
        NativeFileQuerySourcePlan Plan,
        string Expression);

    private sealed class NativeFileQuerySubmission : IDisposable
    {
        private readonly INativeFileQueryLeaseSource epochSource;
        private readonly NativeFileQuerySession session;
        private readonly NativeFileQueryCapacity capacity;
        private readonly ulong queryEpoch;
        private readonly uint maximumTotalCandidateCount;
        private readonly NativeFileQueryCandidateInput[] candidates;
        private readonly byte[] candidateBytes;
        private readonly List<SoftwareFileSearchResult> ordinalMap;
        private int candidateCount;
        private int candidateByteCount;
        private bool disposed;

        internal NativeFileQuerySubmission(
            INativeFileQueryLeaseSource epochSource,
            NativeFileQuerySession session,
            NativeFileQueryCapacity capacity,
            ulong queryEpoch,
            uint maximumTotalCandidateCount)
        {
            this.epochSource = epochSource;
            this.session = session;
            this.capacity = capacity;
            this.queryEpoch = queryEpoch;
            this.maximumTotalCandidateCount = maximumTotalCandidateCount;
            candidates = ArrayPool<NativeFileQueryCandidateInput>.Shared.Rent(
                checked((int)capacity.CandidateSubmitBatchCapacity));
            candidateBytes = ArrayPool<byte>.Shared.Rent(
                checked((int)capacity.CandidateSubmitUtf8ByteCapacity));
            ordinalMap = new List<SoftwareFileSearchResult>(
                checked((int)maximumTotalCandidateCount));
        }

        internal uint SubmittedCandidateCount { get; private set; }

        internal uint UniqueCandidateCount { get; private set; }

        internal uint CandidateOrdinalCount => checked((uint)ordinalMap.Count);

        internal void Add(
            uint sourceId,
            ulong entryHandle,
            string fileName,
            string relativePath,
            string softwareName,
            SoftwareFileSearchResult result)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var fileNameByteCount = StrictUtf8.GetByteCount(fileName);
            var relativePathByteCount = StrictUtf8.GetByteCount(relativePath);
            var softwareNameByteCount = StrictUtf8.GetByteCount(softwareName);
            var requiredByteCount = checked(
                fileNameByteCount + relativePathByteCount + softwareNameByteCount);
            if (fileNameByteCount == 0
                || relativePathByteCount == 0
                || softwareNameByteCount == 0
                || fileNameByteCount > capacity.FileNameUtf8ByteCapacity
                || requiredByteCount > capacity.CandidateSubmitUtf8ByteCapacity)
            {
                throw new InvalidDataException(
                    "A SQLite file-query candidate exceeds the compiled native text shape.");
            }
            if (candidateCount == checked((int)capacity.CandidateSubmitBatchCapacity)
                || candidateByteCount + requiredByteCount
                    > capacity.CandidateSubmitUtf8ByteCapacity)
            {
                Flush();
            }
            if (ordinalMap.Count >= maximumTotalCandidateCount)
            {
                throw new InvalidDataException(
                    "SQLite returned more file-query candidates than the native plan allowed.");
            }

            var fileNameOffset = candidateByteCount;
            candidateByteCount += StrictUtf8.GetBytes(
                fileName,
                candidateBytes.AsSpan(candidateByteCount, fileNameByteCount));
            var relativePathOffset = candidateByteCount;
            candidateByteCount += StrictUtf8.GetBytes(
                relativePath,
                candidateBytes.AsSpan(candidateByteCount, relativePathByteCount));
            var softwareNameOffset = candidateByteCount;
            candidateByteCount += StrictUtf8.GetBytes(
                softwareName,
                candidateBytes.AsSpan(candidateByteCount, softwareNameByteCount));
            var ordinal = checked((uint)ordinalMap.Count + 1U);
            candidates[candidateCount++] = new NativeFileQueryCandidateInput
            {
                StructSize = SizeOf<NativeFileQueryCandidateInput>(),
                SourceId = sourceId,
                EntryHandle = entryHandle,
                CandidateOrdinal = ordinal,
                FileNameOffset = checked((uint)fileNameOffset),
                FileNameLength = checked((uint)fileNameByteCount),
                RelativePathOffset = checked((uint)relativePathOffset),
                RelativePathLength = checked((uint)relativePathByteCount),
                SoftwareNameOffset = checked((uint)softwareNameOffset),
                SoftwareNameLength = checked((uint)softwareNameByteCount)
            };
            ordinalMap.Add(result);
        }

        internal unsafe void Flush()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (candidateCount == 0)
            {
                return;
            }
            var operationEpoch = epochSource.NextEpoch();
            var input = new NativeFileQuerySubmitInput
            {
                AbiVersion = NativeFileQueryAbi.Version,
                StructSize = SizeOf<NativeFileQuerySubmitInput>(),
                ConfigurationGeneration = session.ConfigurationGeneration,
                OperationEpoch = operationEpoch,
                QueryEpoch = queryEpoch,
                BatchEpoch = operationEpoch,
                CandidateCount = checked((uint)candidateCount),
                CandidateByteCount = checked((uint)candidateByteCount),
                ValidMask = (ulong)NativeFileQuerySubmitValidity.Required
            };
            RequireStatus(
                session.SubmitCandidates(
                    in input,
                    candidates.AsSpan(0, candidateCount),
                    candidateBytes.AsSpan(0, candidateByteCount),
                    out var output),
                "candidate submission");
            var expectedSubmitted = checked(SubmittedCandidateCount + (uint)candidateCount);
            var expectedUnique = checked(
                UniqueCandidateCount + (uint)candidateCount - output.DuplicateCandidateCount);
            if (output.AbiVersion != NativeFileQueryAbi.Version
                || output.StructSize != SizeOf<NativeFileQuerySubmitOutput>()
                || output.ConfigurationGeneration != session.ConfigurationGeneration
                || output.StateRevision == 0
                || output.QueryEpoch != queryEpoch
                || output.BatchEpoch != operationEpoch
                || output.AcceptedCandidateCount != (uint)candidateCount
                || output.DuplicateCandidateCount > output.AcceptedCandidateCount
                || output.TotalSubmittedCandidateCount != expectedSubmitted
                || output.TotalUniqueCandidateCount != expectedUnique
                || output.CandidateTextByteCount < CandidateTextByteCount
                || output.CandidateTextByteCount > capacity.CandidateTextArenaByteCapacity
                || output.Flags != 0
                || output.Reserved[0] != 0
                || output.Reserved[1] != 0
                || output.Reserved[2] != 0)
            {
                throw new InvalidDataException(
                    "Native file-query returned an invalid candidate-submission result.");
            }
            SubmittedCandidateCount = output.TotalSubmittedCandidateCount;
            UniqueCandidateCount = output.TotalUniqueCandidateCount;
            CandidateTextByteCount = output.CandidateTextByteCount;
            Array.Clear(candidates, 0, candidateCount);
            candidateCount = 0;
            candidateByteCount = 0;
        }

        internal SoftwareFileSearchResult Resolve(in NativeFileQueryResult result)
        {
            var ordinalIndex = checked((int)result.CandidateOrdinal - 1);
            var resolved = ordinalMap[ordinalIndex];
            if (checked((ulong)resolved.EntryId) != result.EntryHandle)
            {
                throw new InvalidDataException(
                    "Native file-query result identity does not match its submitted ordinal.");
            }
            return resolved;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            ArrayPool<NativeFileQueryCandidateInput>.Shared.Return(candidates, clearArray: true);
            ArrayPool<byte>.Shared.Return(candidateBytes, clearArray: false);
        }

        private uint CandidateTextByteCount { get; set; }
    }
}
