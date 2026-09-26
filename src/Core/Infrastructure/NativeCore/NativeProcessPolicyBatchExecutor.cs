using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeProcessPolicyBatchExecutor
{
    private static readonly uint HeaderSize = checked((uint)Unsafe.SizeOf<NativeProcessPolicyBatchHeader>());
    private static readonly uint ItemSize = checked((uint)Unsafe.SizeOf<NativeProcessPolicyBatchItem>());
    private static readonly uint ResultSize = checked((uint)Unsafe.SizeOf<NativeProcessPolicyBatchItemResult>());

    private readonly HostManagerProcessPolicyExecutorRuntime? runtime;
    private readonly uint fixedAbiVersion;
    private readonly int fixedMaximumBatchItems;
    private ResourceManager.App.Infrastructure.RuntimeSpecialization.ProcessPolicyExecutorRuntimePlan? appliedPlan;

    internal NativeProcessPolicyBatchExecutor(
        ResourceManager.App.Infrastructure.RuntimeSpecialization.HostManagerProcessPolicyExecutorRuntime runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        fixedAbiVersion = 0;
        fixedMaximumBatchItems = 0;
    }

    internal NativeProcessPolicyBatchExecutor(uint abiVersion, int maximumBatchItems)
    {
        if (abiVersion == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(abiVersion));
        }
        if (maximumBatchItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchItems));
        }

        fixedAbiVersion = abiVersion;
        fixedMaximumBatchItems = maximumBatchItems;
    }

    public unsafe NativeCoreResultCode Apply(
        NativeProcessPolicyBatchItem[] items,
        NativeProcessPolicyBatchItemResult[] results)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Length < items.Length)
        {
            return NativeCoreResultCode.BufferTooSmall;
        }
        if (items.Length == 0)
        {
            return NativeCoreResultCode.Ok;
        }

        var plan = CaptureAppliedPlan();
        var deploymentAttempt = BeginDeploymentAttempt(plan);
        var maximumBatchItems = plan?.HotPublish.MaximumBatchItems ?? fixedMaximumBatchItems;
        var expectedAbi = plan?.AbiVersion ?? fixedAbiVersion;
        try
        {
            if (NativeCoreLibrary.GetProcessPolicyExecutorAbiVersion() != expectedAbi)
            {
                CompleteDeploymentFailure(deploymentAttempt, "process-policy-executor-abi-mismatch");
                return NativeCoreResultCode.AbiMismatch;
            }
        }
        catch
        {
            CompleteDeploymentFailure(deploymentAttempt, "process-policy-executor-abi-unavailable");
            throw;
        }

        var result = NativeCoreResultCode.Ok;
        var successfulChunkCount = 0;
        try
        {
            fixed (NativeProcessPolicyBatchItem* itemPointer = items)
            fixed (NativeProcessPolicyBatchItemResult* resultPointer = results)
            {
                var offset = 0;
                while (offset < items.Length)
                {
                    var count = Math.Min(maximumBatchItems, items.Length - offset);
                    var header = new NativeProcessPolicyBatchHeader
                    {
                        AbiVersion = NativeProcessPolicyBatchAbi.Version,
                        StructSize = HeaderSize,
                        ItemStructSize = ItemSize,
                        ResultStructSize = ResultSize,
                        ItemCount = checked((uint)count),
                        Flags = NativeProcessPolicyBatchAbi.LittleEndianFlag
                    };
                    result = (NativeCoreResultCode)NativeCoreLibrary.ApplyProcessPolicyBatch(
                        in header,
                        itemPointer + offset,
                        checked((uint)count),
                        resultPointer + offset,
                        checked((uint)count));
                    if (result != NativeCoreResultCode.Ok)
                    {
                        break;
                    }
                    successfulChunkCount++;
                    offset += count;
                }
            }
        }
        catch
        {
            CompleteDeploymentFailure(deploymentAttempt, "process-policy-executor-native-exception");
            throw;
        }
        if (result == NativeCoreResultCode.Ok
            && successfulChunkCount > 0
            && plan is not null)
        {
            appliedPlan = plan;
            if (deploymentAttempt is { } attempt)
            {
                runtime!.CompleteSucceeded(attempt);
            }
        }
        if (result != NativeCoreResultCode.Ok)
        {
            CompleteDeploymentFailure(
                deploymentAttempt,
                $"process-policy-executor-{result}".ToLowerInvariant());
        }

        return result;
    }

    private ProcessPolicyExecutorRuntimePlan? CaptureAppliedPlan()
    {
        if (runtime is null)
        {
            return null;
        }

        var desired = runtime.CaptureDesired();
        if (appliedPlan is null)
        {
            return desired;
        }
        if (appliedPlan.HostPlan.BuildSha256 != desired.HostPlan.BuildSha256
            || !runtime.CanApplyHot(desired.HostPlan))
        {
            return appliedPlan;
        }

        return desired;
    }

    private HostManagerDeploymentAttemptToken? BeginDeploymentAttempt(
        ProcessPolicyExecutorRuntimePlan? plan)
    {
        if (runtime is null || plan is null)
        {
            return null;
        }
        if (appliedPlan is null)
        {
            return runtime.BeginInitialCreate(plan.HostPlan);
        }
        if (!string.Equals(
                appliedPlan.HostPlan.DeploymentDigests.ProcessPolicyExecutor.HotPublishSha256,
                plan.HostPlan.DeploymentDigests.ProcessPolicyExecutor.HotPublishSha256,
                StringComparison.Ordinal))
        {
            return runtime.BeginHotPublish(plan.HostPlan);
        }
        return null;
    }

    private void CompleteDeploymentFailure(
        HostManagerDeploymentAttemptToken? attempt,
        string failureCode)
    {
        if (attempt is { } activeAttempt)
        {
            runtime!.CompleteFailed(activeAttempt, failureCode);
        }
    }

    public static NativeProcessPolicyBatchItem CreateItem(
        uint processId,
        NativeProcessPolicyFields fields,
        uint priorityClass,
        ulong affinityMask,
        uint memoryPriority,
        uint expectedMemoryPriority,
        uint powerControlMask,
        uint powerStateMask,
        bool trimWorkingSet,
        ulong expectedStartFileTime)
    {
        if (trimWorkingSet)
        {
            fields |= NativeProcessPolicyFields.TrimWorkingSet;
        }

        return new NativeProcessPolicyBatchItem
        {
            StructSize = ItemSize,
            ProcessId = processId,
            Fields = fields,
            PriorityClass = priorityClass,
            AffinityMask = affinityMask,
            MemoryPriority = memoryPriority,
            ExpectedMemoryPriority = expectedMemoryPriority,
            PowerControlMask = powerControlMask,
            PowerStateMask = powerStateMask,
            ExpectedStartFileTime = expectedStartFileTime
        };
    }
}
