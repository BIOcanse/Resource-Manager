using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePortableSoftwareRegistryWorkspace : IDisposable
{
    public NativePortableSoftwareRegistryWorkspace(
        CompiledHostManagerPortableSoftwareRegistryPlan plan,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Portable software registry plan is not published.");
        }

        ArgumentNullException.ThrowIfNull(createScratch);
        var configuration = CreateConfiguration(plan);
        Session = new NativePortableSoftwareRegistrySession(in configuration);
        try
        {
            var capacity = Session.Capacity;
            OperationCapacity = checked((int)capacity.PersistenceOperationCapacity);
            RegistrationCapacity = checked((int)capacity.RegistrationSnapshotCapacity);
            PathCapacity = checked((int)capacity.PathSnapshotCapacity);
            registrationsOffset = checked((ulong)OperationCapacity * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<NativePortableSoftwarePersistenceOperation>());
            pathsOffset = checked(registrationsOffset + (ulong)RegistrationCapacity * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<NativePortableSoftwareRegistrationSnapshot>());
            var length = checked(pathsOffset + (ulong)PathCapacity * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<NativePortableSoftwarePathSnapshot>());
            scratch = createScratch(length);
            if (scratch.Length != length)
            {
                scratch.Dispose();
                throw new InvalidOperationException("Portable registry output scratch length mismatch.");
            }
        }
        catch { Session.Dispose(); throw; }
    }

    public NativePortableSoftwareRegistrySession Session { get; }

    public void Dispose()
    {
        scratch.Dispose(); // A failed close keeps the native session available for an explicit retry.
        Session.Dispose();
    }

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
