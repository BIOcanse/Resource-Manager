using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerPortableSoftwareRegistryBuildPlan CompilePortableSoftwareRegistryBuild(
        uint abiVersion,
        HostManagerPortableSoftwareRegistryCapacityProfile source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativePortableSoftwareRegistryAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.portable_software_registry_abi_version does not match the binary.");
        }

        return new CompiledHostManagerPortableSoftwareRegistryBuildPlan(
            abiVersion,
            "software_identity_portable_registry",
            binary.FileName,
            binary.Sha256,
            CompilePortableSoftwareRegistryCapacity(
                source,
                "build_specialize.capacity_limits.portable_software_registry"));
    }

    private static CompiledHostManagerPortableSoftwareRegistryRecreatePlan CompilePortableSoftwareRegistryRecreate(
        HostManagerPortableSoftwareRegistryRecreateProfile source,
        CompiledHostManagerPortableSoftwareRegistryBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompilePortableSoftwareRegistryCapacity(
            source.Capacity ?? throw Missing("host_recreate.portable_software_registry.capacity"),
            "host_recreate.portable_software_registry.capacity");
        var limit = build.CapacityLimits;
        ValidateMaximum(capacity.MaximumRegistrationCount, limit.MaximumRegistrationCount, "host_recreate.portable_software_registry.capacity.maximum_registration_count");
        ValidateMaximum(capacity.MaximumPathCount, limit.MaximumPathCount, "host_recreate.portable_software_registry.capacity.maximum_path_count");
        ValidateMaximum(capacity.MaximumPersistenceOperationCount, limit.MaximumPersistenceOperationCount, "host_recreate.portable_software_registry.capacity.maximum_persistence_operation_count");
        ValidateMaximum(capacity.MaximumRegistrationSnapshotCount, limit.MaximumRegistrationSnapshotCount, "host_recreate.portable_software_registry.capacity.maximum_registration_snapshot_count");
        ValidateMaximum(capacity.MaximumPathSnapshotCount, limit.MaximumPathSnapshotCount, "host_recreate.portable_software_registry.capacity.maximum_path_snapshot_count");
        ValidateMaximum(capacity.MaximumExecutablePathByteCount, limit.MaximumExecutablePathByteCount, "host_recreate.portable_software_registry.capacity.maximum_executable_path_byte_count");
        ValidateMaximum(capacity.MaximumRootPathByteCount, limit.MaximumRootPathByteCount, "host_recreate.portable_software_registry.capacity.maximum_root_path_byte_count");
        ValidateMaximum(capacity.RegistrationIndexCapacity, limit.RegistrationIndexCapacity, "host_recreate.portable_software_registry.capacity.registration_index_capacity");
        ValidateMaximum(capacity.PathIndexCapacity, limit.PathIndexCapacity, "host_recreate.portable_software_registry.capacity.path_index_capacity");
        return new CompiledHostManagerPortableSoftwareRegistryRecreatePlan(capacity);
    }

    private static CompiledHostManagerPortableSoftwareRegistryHotPublishPlan CompilePortableSoftwareRegistryHotPublish(
        HostManagerPortableSoftwareRegistryHotPublishProfile source,
        int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateNonNegative(
            source.MaximumFutureSkewMilliseconds,
            "hot_publish.portable_software_registry.maximum_future_skew_ms");
        ValidatePositive(
            source.ResidentByteBudget,
            "hot_publish.portable_software_registry.resident_byte_budget");
        return new CompiledHostManagerPortableSoftwareRegistryHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.MaximumFutureSkewMilliseconds,
            source.ResidentByteBudget);
    }

    private static CompiledHostManagerPortableSoftwareRegistryPlan CompilePortableSoftwareRegistryPlan(
        CompiledHostManagerPortableSoftwareRegistryBuildPlan build,
        CompiledHostManagerPortableSoftwareRegistryRecreatePlan recreate,
        CompiledHostManagerPortableSoftwareRegistryHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerPortableSoftwareRegistryPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static CompiledHostManagerPortableSoftwareRegistryCapacityPlan
        CompilePortableSoftwareRegistryCapacity(
            HostManagerPortableSoftwareRegistryCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumRegistrationCount, $"{path}.maximum_registration_count");
        ValidatePositive(source.MaximumPathCount, $"{path}.maximum_path_count");
        ValidatePositive(source.MaximumPersistenceOperationCount, $"{path}.maximum_persistence_operation_count");
        ValidatePositive(source.MaximumRegistrationSnapshotCount, $"{path}.maximum_registration_snapshot_count");
        ValidatePositive(source.MaximumPathSnapshotCount, $"{path}.maximum_path_snapshot_count");
        ValidatePositive(source.MaximumExecutablePathByteCount, $"{path}.maximum_executable_path_byte_count");
        ValidatePositive(source.MaximumRootPathByteCount, $"{path}.maximum_root_path_byte_count");
        ValidatePositive(source.RegistrationIndexCapacity, $"{path}.registration_index_capacity");
        ValidatePositive(source.PathIndexCapacity, $"{path}.path_index_capacity");
        if (source.MaximumPathCount < source.MaximumRegistrationCount
            || source.MaximumPersistenceOperationCount > source.MaximumPathCount
            || source.MaximumRegistrationSnapshotCount > source.MaximumRegistrationCount
            || source.MaximumPathSnapshotCount > source.MaximumPathCount
            || source.MaximumExecutablePathByteCount < 3
            || source.MaximumRootPathByteCount < 3
            || !IsPowerOfTwoAtLeast(source.RegistrationIndexCapacity, source.MaximumRegistrationCount)
            || !IsPowerOfTwoAtLeast(source.PathIndexCapacity, source.MaximumPathCount))
        {
            throw new InvalidDataException($"{path} violates the published portable registry capacity relations.");
        }

        return new CompiledHostManagerPortableSoftwareRegistryCapacityPlan(
            source.MaximumRegistrationCount,
            source.MaximumPathCount,
            source.MaximumPersistenceOperationCount,
            source.MaximumRegistrationSnapshotCount,
            source.MaximumPathSnapshotCount,
            source.MaximumExecutablePathByteCount,
            source.MaximumRootPathByteCount,
            source.RegistrationIndexCapacity,
            source.PathIndexCapacity);
    }

    private static bool IsPowerOfTwoAtLeast(int value, int minimum)
        => value >= minimum && value > 0 && (value & (value - 1)) == 0;
}
