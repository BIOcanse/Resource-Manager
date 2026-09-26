using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

internal sealed class NativePublicServiceCoordinatorWorkspace : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly object commandGate = new();
    private readonly NativePublicServiceCoordinatorSession session;
    private ulong nextCommandEpoch;

    internal NativePublicServiceCoordinatorWorkspace(
        CompiledHostManagerPublicServiceCoordinatorPlan plan,
        ulong hostInstanceId,
        ulong sessionIncarnation,
        ulong catalogGeneration)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException(
                "The public-service coordinator plan must be published.");
        }
        var recreate = plan.Recreate;
        var capacity = recreate.Capacity;
        var configuration = new NativePublicServiceCoordinatorConfiguration
        {
            AbiVersion = NativePublicServiceCoordinatorAbi.Version,
            StructSize = SizeOf<NativePublicServiceCoordinatorConfiguration>(),
            Generation = plan.HotPublish.ConfigurationGeneration,
            SessionInstanceLow = sessionIncarnation,
            SessionInstanceHigh = hostInstanceId,
            MaximumCapabilityCount = checked((uint)capacity.MaximumCapabilityCount),
            MaximumRouteCount = checked((uint)capacity.MaximumRouteCount),
            MaximumModelCount = checked((uint)capacity.MaximumModelCount),
            MaximumModelAliasCount = checked((uint)capacity.MaximumModelAliasCount),
            MaximumRequestCount = checked((uint)capacity.MaximumRequestCount),
            MaximumRateBucketCount = checked((uint)capacity.MaximumRateBucketCount),
            MaximumLeaseCount = checked((uint)capacity.MaximumLeaseCount),
            MaximumSubscriptionCount = checked((uint)capacity.MaximumSubscriptionCount),
            MaximumTaskCount = checked((uint)capacity.MaximumTaskCount),
            CapabilityIndexCapacity = checked((uint)capacity.CapabilityIndexCapacity),
            ModelIndexCapacity = checked((uint)capacity.ModelIndexCapacity),
            AliasIndexCapacity = checked((uint)capacity.AliasIndexCapacity),
            RequestIndexCapacity = checked((uint)capacity.RequestIndexCapacity),
            RateBucketIndexCapacity = checked((uint)capacity.RateBucketIndexCapacity),
            LeaseIndexCapacity = checked((uint)capacity.LeaseIndexCapacity),
            SubscriptionIndexCapacity = checked((uint)capacity.SubscriptionIndexCapacity),
            TaskIndexCapacity = checked((uint)capacity.TaskIndexCapacity),
            MaximumCatalogTextBytes = checked((uint)capacity.MaximumCatalogTextBytes),
            MaximumModelTextBytes = checked((uint)capacity.MaximumModelTextBytes),
            MaximumConcurrentModelTasks = checked(
                (uint)recreate.MaximumConcurrentModelTasks),
            MaximumRequestsPerRateWindow = checked(
                (uint)recreate.MaximumRequestsPerRateWindow),
            MaximumInflightRequestsPerCaller = checked(
                (uint)recreate.MaximumInflightRequestsPerCaller),
            RetryableTaskOutcomeMask = recreate.RetryableTaskOutcomeMask,
            RateWindowMilliseconds = checked((ulong)recreate.RateWindowMilliseconds),
            RequestTimeoutMilliseconds = checked(
                (ulong)recreate.RequestTimeoutMilliseconds),
            LeaseTimeoutMilliseconds = checked((ulong)recreate.LeaseTimeoutMilliseconds),
            SubscriptionTimeoutMilliseconds = checked(
                (ulong)recreate.SubscriptionTimeoutMilliseconds),
            TaskTimeoutMilliseconds = checked((ulong)recreate.TaskTimeoutMilliseconds),
            RetryDelayMilliseconds = checked((ulong)recreate.RetryDelayMilliseconds),
            ResidentByteBudget = checked((ulong)capacity.ResidentByteBudget),
            Flags = recreate.LoopbackOnly
                ? (ulong)NativePublicServiceCoordinatorConfigurationFlags.LoopbackOnly
                : 0,
            ModelCatalogAcquisitionIntervalMilliseconds = checked(
                (ulong)recreate.ModelCatalogAcquisitionIntervalMilliseconds),
            ModelCatalogLastGoodLifetimeMilliseconds = checked(
                (ulong)recreate.ModelCatalogLastGoodLifetimeMilliseconds),
            ModelCatalogAcquisitionTimeoutMilliseconds = checked(
                (ulong)recreate.ModelCatalogAcquisitionTimeoutMilliseconds),
            MaximumTaskAttemptCount = checked((uint)recreate.MaximumTaskAttemptCount),
            RetryableHttpStatusPolicyMask = recreate.RetryableHttpStatusPolicyMask
        };
        session = new NativePublicServiceCoordinatorSession(in configuration);
        try
        {
            PublishCatalog(plan.HotPublish, catalogGeneration);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal void PublishCatalog(
        CompiledHostManagerPublicServiceCoordinatorHotPublishPlan plan,
        ulong catalogGeneration)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (catalogGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogGeneration));
        }

        var text = new ArrayBufferWriter<byte>();
        var capabilities = new NativePublicServiceCapabilityInput[plan.Capabilities.Length];
        for (var index = 0; index < capabilities.Length; index++)
        {
            var source = plan.Capabilities[index];
            capabilities[index] = new NativePublicServiceCapabilityInput
            {
                StructSize = SizeOf<NativePublicServiceCapabilityInput>(),
                Flags = (uint)((source.Enabled
                        ? NativePublicServiceCapabilityFlags.Enabled
                        : NativePublicServiceCapabilityFlags.None)
                    | (source.Available
                        ? NativePublicServiceCapabilityFlags.Available
                        : NativePublicServiceCapabilityFlags.None)),
                CapabilityHandle = source.Handle,
                PayloadHandle = source.PayloadHandle
            };
        }

        var routes = new NativePublicServiceRouteInput[plan.Routes.Length];
        for (var index = 0; index < routes.Length; index++)
        {
            var source = plan.Routes[index];
            var path = AppendText(text, source.Path);
            routes[index] = new NativePublicServiceRouteInput
            {
                StructSize = SizeOf<NativePublicServiceRouteInput>(),
                Flags = (uint)((source.ExactPath
                        ? NativePublicServiceRouteFlags.ExactPath
                        : NativePublicServiceRouteFlags.None)
                    | (source.CatalogRoute
                        ? NativePublicServiceRouteFlags.CatalogRoute
                        : NativePublicServiceRouteFlags.None)
                    | (source.BypassRateLimit
                        ? NativePublicServiceRouteFlags.BypassRateLimit
                        : NativePublicServiceRouteFlags.None)),
                RouteHandle = source.Handle,
                CapabilityHandle = source.CapabilityHandle,
                Path = path,
                MethodMask = source.MethodMask,
                PayloadHandle = source.PayloadHandle
            };
        }

        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceCatalogReplaceInput
            {
                StructSize = SizeOf<NativePublicServiceCatalogReplaceInput>(),
                ServiceEnabled = plan.ServiceEnabled ? 1U : 0U,
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                CatalogGeneration = catalogGeneration,
                CapabilityCount = checked((uint)capabilities.Length),
                RouteCount = checked((uint)routes.Length),
                TextByteCount = checked((uint)text.WrittenCount)
            };
            RequireOk(
                session.ReplaceCatalog(
                    in input,
                    capabilities,
                    routes,
                    text.WrittenSpan),
                "replace catalog");
        }
    }

    internal NativePublicServiceAccessOutput Admit(
        ulong callerHandle,
        NativePublicServiceRemoteScope remoteScope,
        NativePublicServiceHttpMethod method,
        string path)
    {
        var pathBytes = StrictUtf8.GetBytes(path);
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceAccessInput
            {
                StructSize = SizeOf<NativePublicServiceAccessInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                CallerHandle = callerHandle,
                RemoteScope = (uint)remoteScope,
                Method = (uint)method,
                Path = new NativePublicServiceTextSpan
                {
                    Offset = 0,
                    Length = checked((uint)pathBytes.Length)
                }
            };
            RequireOk(
                session.AdmitRequest(in input, pathBytes, out var output),
                "admit request");
            return output;
        }
    }

    internal unsafe NativePublicServiceRequestCompletionDisposition CompleteRequest(
        ulong requestHandle)
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceRequestCompleteInput
            {
                StructSize = SizeOf<NativePublicServiceRequestCompleteInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                RequestHandle = requestHandle
            };
            RequireOk(
                session.CompleteRequest(in input, out var output),
                "complete request");
            var disposition =
                (NativePublicServiceRequestCompletionDisposition)output.Disposition;
            if (output.StructSize != SizeOf<NativePublicServiceRequestCompleteOutput>()
                || output.RequestHandle != requestHandle
                || output.Reserved[0] != 0
                || output.Reserved[1] != 0
                || output.Reserved[2] != 0
                || disposition is not (
                    NativePublicServiceRequestCompletionDisposition.Completed
                    or NativePublicServiceRequestCompletionDisposition.Expired))
            {
                throw new InvalidOperationException(
                    "Native public-service request completion is non-canonical.");
            }
            return disposition;
        }
    }

    internal NativePublicServiceModelAcquisitionPlanOutput PlanModelAcquisition()
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceModelAcquisitionPlanInput
            {
                StructSize = SizeOf<NativePublicServiceModelAcquisitionPlanInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds
            };
            RequireOk(
                session.PlanModelAcquisition(in input, out var output),
                "plan model acquisition");
            return output;
        }
    }

    internal void CompleteModelAcquisition(
        ulong attemptHandle,
        NativePublicServiceModelAcquisitionCompletionStatus status)
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceModelAcquisitionCompletionInput
            {
                StructSize = SizeOf<NativePublicServiceModelAcquisitionCompletionInput>(),
                Status = (uint)status,
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                AttemptHandle = attemptHandle
            };
            RequireOk(
                session.CompleteModelAcquisition(in input),
                "complete model acquisition");
        }
    }

    internal void PublishModels(
        ulong acquisitionAttemptHandle,
        ulong modelGeneration,
        ReadOnlySpan<NativePublicServiceModelInput> models,
        ReadOnlySpan<NativePublicServiceModelAliasInput> aliases,
        ReadOnlySpan<byte> text)
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceModelReplaceInput
            {
                StructSize = SizeOf<NativePublicServiceModelReplaceInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                ModelGeneration = modelGeneration,
                ModelCount = checked((uint)models.Length),
                AliasCount = checked((uint)aliases.Length),
                TextByteCount = checked((uint)text.Length),
                AcquisitionAttemptHandle = acquisitionAttemptHandle
            };
            RequireOk(
                session.ReplaceModels(in input, models, aliases, text),
                "replace models");
        }
    }

    internal NativePublicServiceModelResolveOutput ResolveModel(string alias)
    {
        var aliasBytes = StrictUtf8.GetBytes(alias);
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceModelResolveInput
            {
                StructSize = SizeOf<NativePublicServiceModelResolveInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                Alias = new NativePublicServiceTextSpan
                {
                    Offset = 0,
                    Length = checked((uint)aliasBytes.Length)
                }
            };
            RequireOk(
                session.ResolveModel(in input, aliasBytes, out var output),
                "resolve model");
            return output;
        }
    }

    internal NativePublicServiceTaskOutput EnqueueTask(
        ulong callerHandle,
        ulong modelHandle,
        ulong payloadHandle,
        long baseScore,
        NativePublicServiceTaskKind kind)
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceTaskEnqueueInput
            {
                StructSize = SizeOf<NativePublicServiceTaskEnqueueInput>(),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                CallerHandle = callerHandle,
                ModelHandle = modelHandle,
                PayloadHandle = payloadHandle,
                BaseScore = baseScore,
                Kind = (uint)kind
            };
            RequireOk(session.EnqueueTask(in input, out var output), "enqueue task");
            return output;
        }
    }

    internal NativePublicServiceTaskPlanBatch PlanTasks(int maximumCount)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        lock (commandGate)
        {
            var command = NextCommand();
            var output = new NativePublicServiceTaskOutput[maximumCount];
            var input = new NativePublicServiceTaskPlanInput
            {
                StructSize = SizeOf<NativePublicServiceTaskPlanInput>(),
                MaximumOutputCount = checked((uint)maximumCount),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds
            };
            RequireOk(session.PlanTasks(in input, output, out var plan), "plan tasks");
            if (plan.OutputCount > output.Length)
            {
                throw new InvalidOperationException(
                    "Native public-service task count exceeds the supplied buffer.");
            }
            return new NativePublicServiceTaskPlanBatch(
                output.AsSpan(0, checked((int)plan.OutputCount)).ToArray(),
                plan.NextWakeMonotonicMilliseconds,
                plan.Flags);
        }
    }

    internal unsafe NativePublicServiceTaskCompletionResult CompleteTask(
        ulong taskHandle,
        uint attempt,
        NativePublicServiceTaskEffectOutcome outcome,
        uint httpStatusCode)
    {
        lock (commandGate)
        {
            var command = NextCommand();
            var input = new NativePublicServiceTaskCompletionInput
            {
                StructSize = SizeOf<NativePublicServiceTaskCompletionInput>(),
                Outcome = (uint)outcome,
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds,
                TaskHandle = taskHandle,
                Attempt = attempt,
                HttpStatusCode = httpStatusCode
            };
            RequireOk(
                session.CompleteTask(in input, out var output),
                "complete task");
            var disposition =
                (NativePublicServiceTaskCompletionDisposition)output.Disposition;
            if (output.StructSize != SizeOf<NativePublicServiceTaskCompletionOutput>()
                || output.TaskHandle != taskHandle
                || output.Attempt != attempt
                || output.ReservedU32 != 0
                || output.Reserved[0] != 0
                || output.Reserved[1] != 0
                || disposition is not (
                    NativePublicServiceTaskCompletionDisposition.Succeeded
                    or NativePublicServiceTaskCompletionDisposition.TerminalFailure
                    or NativePublicServiceTaskCompletionDisposition.RetryScheduled)
                || (disposition == NativePublicServiceTaskCompletionDisposition.RetryScheduled)
                    != (output.NextWakeMonotonicMilliseconds != 0))
            {
                throw new InvalidOperationException(
                    "Native public-service task completion is non-canonical.");
            }
            return new NativePublicServiceTaskCompletionResult(
                disposition,
                output.NextWakeMonotonicMilliseconds);
        }
    }

    internal unsafe NativePublicServiceTaskOutput[] CancelQueuedTasks()
    {
        lock (commandGate)
        {
            var maximumCount = checked((int)session.Capacity.TaskCapacity);
            var tasks = new NativePublicServiceTaskOutput[maximumCount];
            var command = NextCommand();
            var input = new NativePublicServiceTaskCancelInput
            {
                StructSize = SizeOf<NativePublicServiceTaskCancelInput>(),
                MaximumOutputCount = checked((uint)maximumCount),
                CommandEpoch = command.Epoch,
                CommandMonotonicMilliseconds = command.MonotonicMilliseconds
            };
            RequireOk(
                session.CancelQueuedTasks(in input, tasks, out var output),
                "cancel queued tasks");
            if (output.StructSize != SizeOf<NativePublicServiceTaskCancelOutput>()
                || output.OutputCount > tasks.Length
                || output.RemainingTaskCount != 0
                || output.ReservedU32 != 0
                || output.Reserved[0] != 0
                || output.Reserved[1] != 0
                || output.Reserved[2] != 0
                || output.Reserved[3] != 0)
            {
                throw new InvalidOperationException(
                    "Native public-service task cancellation is non-canonical.");
            }
            return tasks.AsSpan(0, checked((int)output.OutputCount)).ToArray();
        }
    }

    internal NativePublicServiceCapabilityOutput[] ReadCapabilities(int maximumCount)
    {
        var values = new NativePublicServiceCapabilityOutput[maximumCount];
        RequireOk(session.ReadCapabilities(values, out var written), "read capabilities");
        if (written > values.Length)
        {
            throw new InvalidOperationException(
                "Native public-service capability count exceeds the supplied buffer.");
        }
        return values.AsSpan(0, checked((int)written)).ToArray();
    }

    internal NativePublicServiceCoordinatorSnapshot ReadSnapshot()
    {
        RequireOk(session.Snapshot(out var snapshot), "read snapshot");
        return snapshot;
    }

    public void Dispose() => session.Dispose();

    internal static ulong MonotonicMilliseconds()
        => checked((ulong)Environment.TickCount64);

    private CommandIdentity NextCommand()
    {
        if (nextCommandEpoch == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The native public-service command epoch is exhausted.");
        }
        nextCommandEpoch++;
        return new CommandIdentity(nextCommandEpoch, MonotonicMilliseconds());
    }

    private static NativePublicServiceTextSpan AppendText(
        ArrayBufferWriter<byte> writer,
        string value)
    {
        var byteCount = StrictUtf8.GetByteCount(value);
        var offset = writer.WrittenCount;
        var destination = writer.GetSpan(byteCount);
        var written = StrictUtf8.GetBytes(value, destination);
        writer.Advance(written);
        return new NativePublicServiceTextSpan
        {
            Offset = checked((uint)offset),
            Length = checked((uint)written)
        };
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static void RequireOk(
        NativePublicServiceCoordinatorStatus status,
        string operation)
    {
        if (status != NativePublicServiceCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native public-service coordinator failed to {operation}: {status}.");
        }
    }

    private readonly record struct CommandIdentity(
        ulong Epoch,
        ulong MonotonicMilliseconds);
}

internal readonly record struct NativePublicServiceTaskPlanBatch(
    NativePublicServiceTaskOutput[] Tasks,
    ulong NextWakeMonotonicMilliseconds,
    ulong Flags);

internal readonly record struct NativePublicServiceTaskCompletionResult(
    NativePublicServiceTaskCompletionDisposition Disposition,
    ulong NextWakeMonotonicMilliseconds);
