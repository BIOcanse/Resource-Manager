using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed record NativeOperationCanonicalImage(
    NativeOperationPersistenceHeader Header,
    NativeOperationOutput[] Records);

internal sealed record NativeOperationPlanBatch(
    NativeOperationPlanOutput Plan,
    NativeOperationActionOutput[] Actions);

internal sealed class NativeOperationCoordinatorWorkspace : IDisposable
{
    private NativeOperationCoordinatorSession session;
    private CompiledHostManagerOperationCoordinatorPlan plan;
    private readonly NativeOperationHandle128 sessionInstanceId;
    private readonly NativeOperationHandle128 clockInstanceId;
    private readonly NativeOperationOutput[] operationBuffer;
    private readonly NativeOperationActionOutput[] actionBuffer;
    private readonly NativeOperationOutput[] persistenceBuffer;
    private ulong nextSubmitEpoch;
    private ulong nextRequestEpoch;
    private ulong nextFeedbackEpoch;
    private ulong nextCompletionEpoch;
    private ulong nextPlanEpoch;
    private ulong nextPersistenceEpoch;
    private bool disposed;

    internal NativeOperationCoordinatorWorkspace(
        CompiledHostManagerOperationCoordinatorPlan plan,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 clockInstanceId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new ArgumentException(
                "The operation-coordinator plan must be published.",
                nameof(plan));
        }
        if (sessionInstanceId.IsZero || clockInstanceId.IsZero)
        {
            throw new ArgumentException(
                "Operation-coordinator session and clock identities must be nonzero.");
        }

        this.plan = plan;
        this.sessionInstanceId = sessionInstanceId;
        this.clockInstanceId = clockInstanceId;
        var configuration = CreateConfiguration(plan, sessionInstanceId, clockInstanceId);
        session = new NativeOperationCoordinatorSession(in configuration);
        var capacity = plan.Recreate.Capacity;
        operationBuffer = new NativeOperationOutput[checked((int)capacity.MaximumReadCount)];
        actionBuffer = new NativeOperationActionOutput[checked((int)capacity.MaximumActionCount)];
        persistenceBuffer =
            new NativeOperationOutput[checked((int)capacity.MaximumOperationCount)];
    }

    internal NativeOperationHandle128 SessionInstanceId => sessionInstanceId;

    internal NativeOperationHandle128 ClockInstanceId => clockInstanceId;

    internal ulong ConfigurationGeneration => plan.HotPublish.ConfigurationGeneration;

    internal NativeOperationSubmitOutput SubmitLocked(ref NativeOperationSubmitInput input)
    {
        ThrowIfDisposed();
        input.AbiVersion = NativeOperationCoordinatorAbi.Version;
        input.StructSize = SizeOf<NativeOperationSubmitInput>();
        input.ConfigurationGeneration = ConfigurationGeneration;
        input.SubmitEpoch = Next(ref nextSubmitEpoch, "submit epoch");
        input.Flags = 0;
        ZeroReserved(ref input);
        RequireOk(session.Submit(in input, out var output), "submit");
        if (output.StructSize != SizeOf<NativeOperationSubmitOutput>()
            || output.OperationId != input.OperationId
            || output.StateRevision == 0
            || output.SubmitSequence == 0
            || !Enum.IsDefined((NativeOperationState)output.State)
            || (output.Flags & ~(uint)NativeOperationSubmitOutputFlags.Known) != 0
            || output.ReservedU32 != 0
            || !ReservedZero(in output))
        {
            throw new InvalidDataException(
                "Native operation-coordinator submit output is non-canonical.");
        }
        return output;
    }

    internal NativeOperationCancelOutput CancelLocked(ref NativeOperationCancelInput input)
    {
        ThrowIfDisposed();
        input.AbiVersion = NativeOperationCoordinatorAbi.Version;
        input.StructSize = SizeOf<NativeOperationCancelInput>();
        input.ConfigurationGeneration = ConfigurationGeneration;
        input.RequestEpoch = Next(ref nextRequestEpoch, "request epoch");
        input.Flags = 0;
        ZeroReserved(ref input);
        RequireOk(session.Cancel(in input, out var output), "cancel");
        if (output.StructSize != SizeOf<NativeOperationCancelOutput>()
            || output.OperationId != input.OperationId
            || output.StateRevision == 0
            || !Enum.IsDefined((NativeOperationState)output.State)
            || (output.Flags & ~(ulong)NativeOperationFlags.Known) != 0
            || !ReservedZero(in output))
        {
            throw new InvalidDataException(
                "Native operation-coordinator cancel output is non-canonical.");
        }
        return output;
    }

    internal NativeOperationPlanBatch PlanAndReadActionsLocked(
        long observedUtcMilliseconds,
        ulong observedMonotonicMilliseconds)
    {
        ThrowIfDisposed();
        var input = new NativeOperationPlanInput
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize = SizeOf<NativeOperationPlanInput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            PlanEpoch = Next(ref nextPlanEpoch, "plan epoch"),
            ObservedUtcMilliseconds = observedUtcMilliseconds,
            ObservedMonotonicMilliseconds = observedMonotonicMilliseconds,
            ActionCapacity = checked((uint)actionBuffer.Length),
            Flags = 0
        };
        RequireOk(session.Plan(in input, out var output), "plan");
        ValidatePlanOutput(in input, in output);
        var count = checked((int)output.ActionCount);
        if (count == 0)
        {
            return new NativeOperationPlanBatch(output, []);
        }

        var read = CreateReadInput(output.StateRevision, output.PlanEpoch);
        RequireOk(
            session.ReadActions(in read, actionBuffer.AsSpan(0, count)),
            "read actions");
        var actions = new NativeOperationActionOutput[count];
        actionBuffer.AsSpan(0, count).CopyTo(actions);
        for (var index = 0; index < actions.Length; index++)
        {
            ValidateAction(in actions[index], in output);
        }
        return new NativeOperationPlanBatch(output, actions);
    }

    internal bool ApplyActionFeedbackLocked(ref NativeOperationActionFeedbackInput input)
    {
        ThrowIfDisposed();
        input.AbiVersion = NativeOperationCoordinatorAbi.Version;
        input.StructSize = SizeOf<NativeOperationActionFeedbackInput>();
        input.ConfigurationGeneration = ConfigurationGeneration;
        input.FeedbackEpoch = Next(ref nextFeedbackEpoch, "feedback epoch");
        input.Flags = 0;
        ZeroReserved(ref input);
        var status = session.Feedback(in input);
        if (status == NativeOperationCoordinatorStatus.StaleFrame)
        {
            return false;
        }
        RequireOk(status, "action feedback");
        return true;
    }

    internal bool CompleteLocked(ref NativeOperationCompletionInput input)
    {
        ThrowIfDisposed();
        input.AbiVersion = NativeOperationCoordinatorAbi.Version;
        input.StructSize = SizeOf<NativeOperationCompletionInput>();
        input.ConfigurationGeneration = ConfigurationGeneration;
        input.CompletionEpoch = Next(ref nextCompletionEpoch, "completion epoch");
        input.Flags = 0;
        ZeroReserved(ref input);
        var status = session.Complete(in input);
        if (status == NativeOperationCoordinatorStatus.StaleFrame)
        {
            return false;
        }
        RequireOk(status, "completion");
        return true;
    }

    internal bool ReportProgressLocked(ref NativeOperationProgressInput input)
    {
        ThrowIfDisposed();
        input.AbiVersion = NativeOperationCoordinatorAbi.Version;
        input.StructSize = SizeOf<NativeOperationProgressInput>();
        input.ConfigurationGeneration = ConfigurationGeneration;
        input.Flags = 0;
        ZeroReserved(ref input);
        var status = session.ReportProgress(in input);
        if (status == NativeOperationCoordinatorStatus.StaleFrame)
        {
            return false;
        }
        RequireOk(status, "progress");
        return true;
    }

    internal NativeOperationCanonicalImage ExportCanonicalNativeImageLocked()
    {
        ThrowIfDisposed();
        var input = new NativeOperationPersistenceInput
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize = SizeOf<NativeOperationPersistenceInput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            OperationEpoch = Next(ref nextPersistenceEpoch, "persistence epoch"),
            Flags = 0
        };
        RequireOk(
            session.ExportPersistence(in input, out var header, persistenceBuffer),
            "export persistence");
        ValidatePersistenceHeader(in header, input.OperationEpoch);
        var records = new NativeOperationOutput[checked((int)header.OperationCount)];
        persistenceBuffer.AsSpan(0, records.Length).CopyTo(records);
        ValidateRecords(records, header);
        return new NativeOperationCanonicalImage(header, records);
    }

    internal void ImportCanonicalNativeImageLocked(
        NativeOperationCanonicalImage image,
        long observedUtcMilliseconds,
        ulong observedMonotonicMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(image);
        ThrowIfDisposed();
        var importedHeader = image.Header;
        ValidatePersistenceHeaderForImport(in importedHeader, image.Records);
        var operationEpoch = importedHeader.OperationEpoch == ulong.MaxValue
            ? throw new InvalidDataException(
                "The operation-coordinator persistence epoch is exhausted.")
            : importedHeader.OperationEpoch + 1;
        var input = new NativeOperationPersistenceImportInput
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize = SizeOf<NativeOperationPersistenceImportInput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            OperationEpoch = operationEpoch,
            ObservedUtcMilliseconds = observedUtcMilliseconds,
            ObservedMonotonicMilliseconds = observedMonotonicMilliseconds,
            ClockInstanceId = clockInstanceId,
            Flags = 0
        };
        RequireOk(
            session.ImportPersistence(in input, in importedHeader, image.Records),
            "import persistence");
        nextSubmitEpoch = image.Header.LastSubmitEpoch;
        nextRequestEpoch = image.Header.LastRequestEpoch;
        nextFeedbackEpoch = image.Header.LastFeedbackEpoch;
        nextCompletionEpoch = image.Header.LastCompletionEpoch;
        nextPlanEpoch = image.Header.LastPlanEpoch;
        nextPersistenceEpoch = operationEpoch;
    }

    internal NativeOperationOutput[] ReadCommittedProjectionLocked()
    {
        ThrowIfDisposed();
        var snapshot = SnapshotLocked();
        var count = checked((int)snapshot.OperationCount);
        if (count == 0)
        {
            return [];
        }
        var read = CreateReadInput(snapshot.StateRevision, 0);
        RequireOk(
            session.ReadOperations(in read, operationBuffer.AsSpan(0, count)),
            "read operations");
        var operations = new NativeOperationOutput[count];
        operationBuffer.AsSpan(0, count).CopyTo(operations);
        ValidateRecords(operations, persistenceHeader: null);
        return operations;
    }

    internal NativeOperationSnapshotOutput SnapshotLocked()
    {
        ThrowIfDisposed();
        RequireOk(session.Snapshot(out var output), "snapshot");
        if (output.AbiVersion != NativeOperationCoordinatorAbi.Version
            || output.StructSize != SizeOf<NativeOperationSnapshotOutput>()
            || output.ConfigurationGeneration != ConfigurationGeneration
            || output.StateRevision == 0
            || output.OperationCount > operationBuffer.Length
            || output.ActionCount > actionBuffer.Length
            || output.ActiveOperationCount > output.OperationCount
            || output.RunningOperationCount > output.ActiveOperationCount
            || output.TerminalOperationCount > output.OperationCount
            || (output.Flags & ~(ulong)NativeOperationSnapshotFlags.Known) != 0
            || output.ReservedU32 != 0
            || !ReservedZero(in output))
        {
            throw new InvalidDataException(
                "Native operation-coordinator snapshot is non-canonical.");
        }
        return output;
    }

    internal void ReconfigureLocked(CompiledHostManagerOperationCoordinatorPlan nextPlan)
    {
        ArgumentNullException.ThrowIfNull(nextPlan);
        ThrowIfDisposed();
        if (!nextPlan.IsPublished
            || nextPlan.Recreate != plan.Recreate
            || nextPlan.Build != plan.Build
            || nextPlan.HotPublish.ConfigurationGeneration
                <= plan.HotPublish.ConfigurationGeneration)
        {
            throw new InvalidOperationException(
                "The operation-coordinator hot plan is not a compatible monotonic update.");
        }
        var configuration =
            CreateConfiguration(nextPlan, sessionInstanceId, clockInstanceId);
        RequireOk(session.Reconfigure(in configuration), "reconfigure");
        plan = nextPlan;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        session.Dispose();
    }

    internal static NativeOperationHandle128 CreateRandomHandle()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var result = new NativeOperationHandle128(
            BitConverter.ToUInt64(bytes[..8]),
            BitConverter.ToUInt64(bytes[8..]));
        return result.IsZero ? CreateRandomHandle() : result;
    }

    internal static ulong MonotonicMilliseconds()
        => checked((ulong)(Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency));

    private NativeOperationReadInput CreateReadInput(ulong stateRevision, ulong planEpoch)
        => new()
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize = SizeOf<NativeOperationReadInput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            StateRevision = stateRevision,
            PlanEpoch = planEpoch,
            Flags = 0
        };

    private void ValidatePlanOutput(
        in NativeOperationPlanInput input,
        in NativeOperationPlanOutput output)
    {
        if (output.AbiVersion != NativeOperationCoordinatorAbi.Version
            || output.StructSize != SizeOf<NativeOperationPlanOutput>()
            || output.ConfigurationGeneration != ConfigurationGeneration
            || output.PlanEpoch != input.PlanEpoch
            || output.StateRevision == 0
            || output.ActionCount > input.ActionCapacity
            || output.ActiveOperationCount > plan.Recreate.Capacity.MaximumOperationCount
            || output.RunningOperationCount > output.ActiveOperationCount
            || output.TerminalOperationCount > plan.Recreate.Capacity.MaximumOperationCount
            || (output.Flags & ~(ulong)NativeOperationSnapshotFlags.Known) != 0
            || !ReservedZero(in output))
        {
            throw new InvalidDataException(
                "Native operation-coordinator plan output is non-canonical.");
        }
    }

    private void ValidateAction(
        in NativeOperationActionOutput action,
        in NativeOperationPlanOutput output)
    {
        if (action.StructSize != SizeOf<NativeOperationActionOutput>()
            || !Enum.IsDefined((NativeOperationActionKind)action.Kind)
            || action.ActionId == 0
            || action.ConfigurationGeneration != ConfigurationGeneration
            || action.OperationId.IsZero
            || action.AttemptToken.IsZero
            || action.PlanEpoch != output.PlanEpoch
            || action.AttemptNumber == 0
            || action.DeadlineMonotonicMilliseconds == 0
            || (action.Flags & ~(ulong)NativeOperationActionFlags.Known) != 0
            || action.ReservedU32 != 0
            || !ReservedZero(in action))
        {
            throw new InvalidDataException(
                "Native operation-coordinator action output is non-canonical.");
        }
    }

    private void ValidatePersistenceHeader(
        in NativeOperationPersistenceHeader header,
        ulong expectedOperationEpoch)
    {
        if (header.AbiVersion != NativeOperationCoordinatorAbi.Version
            || header.StructSize != SizeOf<NativeOperationPersistenceHeader>()
            || header.ConfigurationGeneration != ConfigurationGeneration
            || header.SessionInstanceId != sessionInstanceId
            || header.ClockInstanceId != clockInstanceId
            || header.OperationEpoch != expectedOperationEpoch
            || header.StateRevision == 0
            || header.OperationCount > persistenceBuffer.Length
            || header.RecordSize != SizeOf<NativeOperationOutput>()
            || header.NextActionId == 0
            || header.Flags != 0
            || (header.ChecksumHigh == 0 && header.ChecksumLow == 0)
            || !ReservedZero(in header))
        {
            throw new InvalidDataException(
                "Native operation-coordinator persistence header is non-canonical.");
        }
    }

    private void ValidatePersistenceHeaderForImport(
        in NativeOperationPersistenceHeader header,
        IReadOnlyList<NativeOperationOutput> records)
    {
        if (header.AbiVersion != NativeOperationCoordinatorAbi.Version
            || header.StructSize != SizeOf<NativeOperationPersistenceHeader>()
            || header.ConfigurationGeneration == 0
            || header.ConfigurationGeneration > ConfigurationGeneration
            || header.SessionInstanceId != sessionInstanceId
            || header.ClockInstanceId.IsZero
            || header.OperationEpoch == 0
            || header.StateRevision == 0
            || header.OperationCount != records.Count
            || header.OperationCount > persistenceBuffer.Length
            || header.RecordSize != SizeOf<NativeOperationOutput>()
            || header.NextActionId == 0
            || header.Flags != 0
            || (header.ChecksumHigh == 0 && header.ChecksumLow == 0)
            || !ReservedZero(in header))
        {
            throw new InvalidDataException(
                "The imported operation-coordinator persistence header is invalid.");
        }
        ValidateRecords(records, header);
    }

    private static void ValidateRecords(
        IReadOnlyList<NativeOperationOutput> records,
        NativeOperationPersistenceHeader? persistenceHeader)
    {
        ulong previousSubmitSequence = 0;
        foreach (var record in records)
        {
            if (record.StructSize != SizeOf<NativeOperationOutput>()
                || !Enum.IsDefined((NativeOperationState)record.State)
                || record.OperationId.IsZero
                || record.KindHandle.IsZero
                || record.SubmitSequence == 0
                || (persistenceHeader.HasValue
                    && record.SubmitSequence <= previousSubmitSequence)
                || record.StateRevision == 0
                || record.MaximumAttempts == 0
                || record.AttemptNumber > record.MaximumAttempts
                || record.ExecutionTimeoutMilliseconds == 0
                || record.CancelGraceMilliseconds == 0
                || record.TerminalRetentionMilliseconds == 0
                || (record.Flags & ~(ulong)NativeOperationFlags.Known) != 0
                || (record.ProgressValidMask
                    & ~(ulong)NativeOperationProgressValidity.Known) != 0
                || record.ReservedU32 != 0
                || !ReservedZero(in record))
            {
                throw new InvalidDataException(
                    "A native operation-coordinator record is non-canonical.");
            }
            previousSubmitSequence = record.SubmitSequence;
        }
    }

    private static unsafe NativeOperationCoordinatorConfiguration CreateConfiguration(
        CompiledHostManagerOperationCoordinatorPlan plan,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 clockInstanceId)
    {
        var capacity = plan.Recreate.Capacity;
        var hot = plan.HotPublish;
        return new NativeOperationCoordinatorConfiguration
        {
            AbiVersion = NativeOperationCoordinatorAbi.Version,
            StructSize = SizeOf<NativeOperationCoordinatorConfiguration>(),
            Generation = hot.ConfigurationGeneration,
            SessionInstanceId = sessionInstanceId,
            ClockInstanceId = clockInstanceId,
            MaximumOperationCount = capacity.MaximumOperationCount,
            MaximumDomainCount = capacity.MaximumDomainCount,
            MaximumActionCount = capacity.MaximumActionCount,
            OperationIndexCapacity = capacity.OperationIndexCapacity,
            DomainIndexCapacity = capacity.DomainIndexCapacity,
            MaximumGlobalRunningCount = hot.MaximumGlobalRunningCount,
            MaximumRecentTerminalCount = hot.MaximumRecentTerminalCount,
            MaximumReadCount = capacity.MaximumReadCount,
            MaximumStartActionsPerPlan = hot.MaximumStartActionsPerPlan,
            MaximumCancelActionsPerPlan = hot.MaximumCancelActionsPerPlan,
            MaximumRecoverActionsPerPlan = hot.MaximumRecoverActionsPerPlan,
            MaximumFutureSkewMilliseconds = hot.MaximumFutureSkewMilliseconds,
            MaximumPersistenceByteCount = capacity.MaximumPersistenceByteCount,
            ResidentByteBudget = capacity.ResidentByteBudget
        };
    }

    private static ulong Next(ref ulong value, string name)
    {
        if (value == ulong.MaxValue)
        {
            throw new InvalidOperationException($"{name} space is exhausted.");
        }
        return ++value;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static void RequireOk(
        NativeOperationCoordinatorStatus status,
        string operation)
    {
        if (status != NativeOperationCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native operation-coordinator {operation} failed with raw status {(int)status}.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private static unsafe bool ReservedZero(in NativeOperationSubmitOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 3);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationCancelOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 3);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationPlanOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 4);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationActionOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 3);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationSnapshotOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 4);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationPersistenceHeader value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 4);
        }
    }

    private static unsafe bool ReservedZero(in NativeOperationOutput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            return AllZero(reserved, 2);
        }
    }

    private static unsafe bool AllZero(ulong* values, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (values[index] != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static unsafe void ZeroReserved(ref NativeOperationSubmitInput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            new Span<ulong>(reserved, 4).Clear();
        }
    }

    private static unsafe void ZeroReserved(ref NativeOperationCancelInput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            new Span<ulong>(reserved, 4).Clear();
        }
    }

    private static unsafe void ZeroReserved(ref NativeOperationActionFeedbackInput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            new Span<ulong>(reserved, 3).Clear();
        }
    }

    private static unsafe void ZeroReserved(ref NativeOperationCompletionInput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            new Span<ulong>(reserved, 3).Clear();
        }
    }

    private static unsafe void ZeroReserved(ref NativeOperationProgressInput value)
    {
        fixed (ulong* reserved = value.Reserved)
        {
            new Span<ulong>(reserved, 3).Clear();
        }
    }
}
