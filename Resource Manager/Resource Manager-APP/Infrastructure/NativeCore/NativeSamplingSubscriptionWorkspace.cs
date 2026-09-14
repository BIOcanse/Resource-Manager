using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSamplingSubscriptionWorkspace : IDisposable
{
    private readonly NativeSamplingSubscriptionSession[] sessions;
    private bool disposed;

    public NativeSamplingSubscriptionWorkspace(CompiledHostManagerSamplingSubscriptionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Sampling subscription plan is not published.");
        }

        sessions = new NativeSamplingSubscriptionSession[CompiledHostManagerSamplingSubscriptionPlan.RoleCount];
        var created = 0;
        try
        {
            for (var index = 0; index < sessions.Length; index++)
            {
                var configuration = CreateConfiguration(
                    plan.Build.AbiVersion,
                    plan.Recreate.Roles[index],
                    plan.HotPublish.Roles[index],
                    plan.ConfigurationGeneration);
                sessions[index] = new NativeSamplingSubscriptionSession(in configuration);
                created++;
            }
        }
        catch
        {
            for (var index = created - 1; index >= 0; index--)
            {
                sessions[index].Dispose();
            }
            throw;
        }
    }

    public NativeSamplingSubscriptionSession GetSession(int roleId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (roleId is < 1 or > CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(roleId));
        }
        return sessions[roleId - 1];
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    internal static NativeSamplingSubscriptionConfiguration CreateConfiguration(
        uint abiVersion,
        CompiledHostManagerSamplingSubscriptionRoleCapacityPlan capacity,
        CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hot,
        ulong generation)
    {
        if (capacity.RoleId != hot.RoleId || generation == 0)
        {
            throw new InvalidOperationException("Sampling subscription role configuration identity is invalid.");
        }

        return new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = abiVersion,
            StructSize = checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
            Generation = generation,
            MaximumSourceCount = checked((uint)capacity.MaximumSourceCount),
            MaximumItemCount = checked((uint)capacity.MaximumItemCount),
            MaximumMembershipCount = checked((uint)capacity.MaximumMembershipCount),
            MaximumDueItemCount = checked((uint)capacity.MaximumDueItemCount),
            MaximumSourceViewCount = checked((uint)capacity.MaximumSourceViewCount),
            MaximumExpiredSourceCount = checked((uint)capacity.MaximumExpiredSourceCount),
            DefaultIntervalMilliseconds = checked((ulong)hot.DefaultIntervalMilliseconds),
            MinimumIntervalMilliseconds = checked((ulong)hot.MinimumIntervalMilliseconds),
            ActiveTtlMilliseconds = checked((ulong)hot.ActiveTtlMilliseconds),
            MaximumFutureSkewMilliseconds = checked((ulong)hot.MaximumFutureSkewMilliseconds),
            Flags = 0,
            ReservedU64_0 = 0,
            ReservedU64_1 = 0,
            ReservedU64_2 = 0,
            ReservedU32_0 = 0,
            ReservedU32_1 = 0
        };
    }
}
