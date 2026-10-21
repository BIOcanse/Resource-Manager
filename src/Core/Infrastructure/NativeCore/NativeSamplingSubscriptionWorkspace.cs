using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSamplingSubscriptionWorkspace : IDisposable
{
    private readonly NativeSamplingSubscriptionSession?[] sessions =
        new NativeSamplingSubscriptionSession?[CompiledHostManagerSamplingSubscriptionPlan.RoleCount];
    private bool initializationStarted;
    private bool initialized;
    private bool disposed;
    private SamplingSessionScratch? scratch;

    public void Initialize(CompiledHostManagerSamplingSubscriptionPlan plan,
        Func<ulong, IComputeScratch>? createScratch = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initializationStarted)
            throw new InvalidOperationException("Sampling subscription workspace initialization was already attempted.");
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Sampling subscription plan is not published.");
        }

        // The owner retains this metadata object before any Native session is allocated.
        // A partial failure therefore leaves every created session available to explicit close.
        initializationStarted = true;
        var configurations = new NativeSamplingSubscriptionConfiguration[sessions.Length];
        ulong total = 0;
        for (var index = 0; index < sessions.Length; index++)
        {
            configurations[index] = CreateConfiguration(
                plan.Build.AbiVersion,
                plan.Recreate.Roles[index],
                plan.HotPublish.Roles[index],
                plan.ConfigurationGeneration);
            total = checked(total + 32UL * configurations[index].MaximumItemCount);
        }
        if (createScratch is not null)
            scratch = new SamplingSessionScratch(createScratch(checked(total + 7)));
        ulong offset = 0;
        for (var index = 0; index < sessions.Length; index++)
        {
            var configuration = configurations[index];
            var length = checked(32UL * configuration.MaximumItemCount);
            var sliceOffset = offset;
            sessions[index] = scratch is null
                ? new NativeSamplingSubscriptionSession(in configuration)
                : new NativeSamplingSubscriptionSession(in configuration,
                    () => scratch.Acquire(sliceOffset, length));
            offset = checked(offset + length);
        }
        initialized = true;
    }

    public NativeSamplingSubscriptionSession GetSession(int roleId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized)
            throw new InvalidOperationException("Sampling subscription workspace is not initialized.");
        if (roleId is < 1 or > CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(roleId));
        }
        return sessions[roleId - 1]!;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var session in sessions)
        {
            session?.Dispose();
        }
        scratch?.Dispose();
        disposed = true;
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
