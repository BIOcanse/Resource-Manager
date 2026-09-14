using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativePortableSoftwareRegistryWorkspace : IDisposable
{
    public NativePortableSoftwareRegistryWorkspace(
        CompiledHostManagerPortableSoftwareRegistryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Portable software registry plan is not published.");
        }

        var configuration = CreateConfiguration(plan);
        Session = new NativePortableSoftwareRegistrySession(in configuration);
    }

    public NativePortableSoftwareRegistrySession Session { get; }

    public void Dispose() => Session.Dispose();

    internal static NativePortableSoftwareRegistryConfiguration CreateConfiguration(
        CompiledHostManagerPortableSoftwareRegistryPlan plan)
    {
        var capacity = plan.Recreate.Capacity;
        return new NativePortableSoftwareRegistryConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativePortableSoftwareRegistryConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            MaximumRegistrationCount = checked((uint)capacity.MaximumRegistrationCount),
            MaximumPathCount = checked((uint)capacity.MaximumPathCount),
            MaximumPersistenceOperationCount = checked((uint)capacity.MaximumPersistenceOperationCount),
            MaximumRegistrationSnapshotCount = checked((uint)capacity.MaximumRegistrationSnapshotCount),
            MaximumPathSnapshotCount = checked((uint)capacity.MaximumPathSnapshotCount),
            MaximumExecutablePathByteCount = checked((uint)capacity.MaximumExecutablePathByteCount),
            MaximumRootPathByteCount = checked((uint)capacity.MaximumRootPathByteCount),
            RegistrationIndexCapacity = checked((uint)capacity.RegistrationIndexCapacity),
            PathIndexCapacity = checked((uint)capacity.PathIndexCapacity),
            MaximumFutureSkewMilliseconds = checked((uint)plan.HotPublish.MaximumFutureSkewMilliseconds),
            Flags = 0,
            ResidentByteBudget = checked((ulong)plan.HotPublish.ResidentByteBudget)
        };
    }
}
