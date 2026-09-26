using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSoftwareIdentityResolutionWorkspace : IDisposable
{
    public NativeSoftwareIdentityResolutionWorkspace(
        CompiledHostManagerSoftwareIdentityResolutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Software identity resolution plan is not published.");
        }

        var configuration = CreateConfiguration(plan);
        Session = new NativeSoftwareIdentityResolutionSession(in configuration);
        try
        {
            LoadPolicy(Session, plan);
        }
        catch
        {
            Session.Dispose();
            throw;
        }
    }

    public NativeSoftwareIdentityResolutionSession Session { get; }

    public ulong PolicyGeneration { get; private set; }

    public void Dispose() => Session.Dispose();

    internal static NativeSoftwareIdentityResolutionConfiguration CreateConfiguration(
        CompiledHostManagerSoftwareIdentityResolutionPlan plan)
    {
        var capacity = plan.Recreate.Capacity;
        ulong requiredSourceMask = 0;
        foreach (var sourceId in plan.Recreate.SourceIds)
        {
            requiredSourceMask |= 1UL << checked((int)sourceId - 1);
        }

        ulong stopMask = 0;
        foreach (var policy in plan.HotPublish.SourcePolicies)
        {
            if (policy.StopOnUnavailable)
            {
                stopMask |= 1UL << checked((int)policy.SourceId - 1);
            }
        }

        return new NativeSoftwareIdentityResolutionConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityResolutionConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            MaximumPolicyCount = checked((uint)capacity.MaximumPolicyCount),
            MaximumObservationCount = checked((uint)capacity.MaximumObservationCount),
            PolicyIndexCapacity = checked((uint)capacity.PolicyIndexCapacity),
            ReservedU32 = 0,
            RequiredSourceMask = requiredSourceMask,
            StopOnUnavailableSourceMask = stopMask,
            MaximumFutureSkewMilliseconds = checked((ulong)plan.HotPublish.MaximumFutureSkewMilliseconds),
            ResidentByteBudget = checked((ulong)plan.HotPublish.ResidentByteBudget),
            Flags = 0
        };
    }

    private void LoadPolicy(
        NativeSoftwareIdentityResolutionSession session,
        CompiledHostManagerSoftwareIdentityResolutionPlan plan)
    {
        var policies = plan.HotPublish.SourcePolicies
            .Select(static policy => new NativeSoftwareIdentitySourcePolicyInput
            {
                StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentitySourcePolicyInput>()),
                SourceId = policy.SourceId,
                Priority = policy.Priority,
                Flags = 0
            })
            .ToArray();
        var input = new NativeSoftwareIdentityPolicyReplaceInput
        {
            AbiVersion = NativeSoftwareIdentityResolutionAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityPolicyReplaceInput>()),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            PolicyGeneration = plan.ConfigurationGeneration,
            OperationEpoch = 1,
            PolicyCount = checked((uint)policies.Length),
            ReservedU32 = 0,
            ValidMask = (ulong)NativeSoftwareIdentityPolicyReplaceValidity.Required,
            Flags = 0
        };
        var status = session.ReplacePolicy(in input, policies);
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native software identity source policy load failed with {status}.");
        }
        PolicyGeneration = input.PolicyGeneration;
    }
}
