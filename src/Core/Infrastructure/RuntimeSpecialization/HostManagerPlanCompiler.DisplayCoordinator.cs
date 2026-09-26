using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerDisplayCoordinatorBuildPlan
        CompileDisplayCoordinatorBuild(
            uint abiVersion,
            HostManagerDisplayCoordinatorCapacityProfile source,
            IReadOnlySet<string> nativeModules)
    {
        if (abiVersion != NativeDisplayCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.display_coordinator_abi_version must match the native display-coordinator ABI.");
        }
        if (!nativeModules.Contains("display_coordinator"))
        {
            throw new InvalidDataException(
                "build_specialize.native_modules must include display_coordinator.");
        }

        return new CompiledHostManagerDisplayCoordinatorBuildPlan(
            abiVersion,
            "display_coordinator",
            CompileDisplayCoordinatorCapacity(
                source,
                "build_specialize.capacity_limits.display_coordinator"));
    }

    private static CompiledHostManagerDisplayCoordinatorRecreatePlan
        CompileDisplayCoordinatorRecreate(
            HostManagerDisplayCoordinatorRecreateProfile source,
            CompiledHostManagerDisplayCoordinatorBuildPlan build,
            int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileDisplayCoordinatorCapacity(
            source.Capacity
                ?? throw Missing("host_recreate.display_coordinator.capacity"),
            "host_recreate.display_coordinator.capacity");
        if (!capacity.FitsWithin(build.CapacityLimits))
        {
            throw new InvalidDataException(
                "host_recreate.display_coordinator.capacity exceeds its build-specialize limits.");
        }

        var requiredMask = CompileDisplaySourceMask(
            source.RequiredSources,
            "host_recreate.display_coordinator.required_sources");
        var optionalMask = CompileDisplaySourceMask(
            source.OptionalSources,
            "host_recreate.display_coordinator.optional_sources");
        var knownMask = (ulong)NativeDisplaySourceMask.Known;
        if (requiredMask == 0
            || (requiredMask & optionalMask) != 0
            || (requiredMask | optionalMask) != knownMask)
        {
            throw new InvalidDataException(
                "display-coordinator required and optional sources must be disjoint and cover all known sources.");
        }
        if (source.IdentityContractVersion
                != NativeDisplayCoordinatorAbi.IdentityContractVersion
            || source.CapabilityContractVersion
                != NativeDisplayCoordinatorAbi.CapabilityContractVersion)
        {
            throw new InvalidDataException(
                "display-coordinator identity and capability contract versions must match the native ABI.");
        }

        return new CompiledHostManagerDisplayCoordinatorRecreatePlan(
            checked((ulong)profileRevision << 32),
            capacity,
            NormalizeStrictRelativePath(
                source.PersistenceRelativePath,
                "host_recreate.display_coordinator.persistence_relative_path"),
            requiredMask,
            optionalMask,
            CompileDisplaySourcePriority(
                source.IdentitySourcePriority,
                "host_recreate.display_coordinator.identity_source_priority"),
            CompileDisplaySourcePriority(
                source.FriendlyNameSourcePriority,
                "host_recreate.display_coordinator.friendly_name_source_priority"),
            CompileDisplaySourcePriority(
                source.CapabilitySourcePriority,
                "host_recreate.display_coordinator.capability_source_priority"),
            CompileDisplaySourcePriority(
                source.ProjectionSourcePriority,
                "host_recreate.display_coordinator.projection_source_priority"),
            source.IdentityContractVersion,
            source.CapabilityContractVersion,
            source.MaximumFutureSkewMilliseconds);
    }

    private static CompiledHostManagerDisplayCoordinatorPlan
        CompileDisplayCoordinatorPlan(
            CompiledHostManagerDisplayCoordinatorBuildPlan build,
            CompiledHostManagerDisplayCoordinatorRecreatePlan recreate)
    {
        var result = new CompiledHostManagerDisplayCoordinatorPlan(
            build,
            recreate,
            HostManagerPlanIdentity.ComputeDigest(new
            {
                Module = "display_coordinator",
                Build = build,
                Recreate = recreate
            }));
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                "The compiled display-coordinator plan is invalid.");
        }

        return result;
    }

    private static CompiledHostManagerDisplayCoordinatorCapacityPlan
        CompileDisplayCoordinatorCapacity(
            HostManagerDisplayCoordinatorCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new CompiledHostManagerDisplayCoordinatorCapacityPlan(
            source.MaximumSourceCount,
            source.MaximumObservationCount,
            source.MaximumNodeCount,
            source.MaximumEdgeCount,
            source.MaximumCapabilityCount,
            source.MaximumDiffEntryCount,
            source.MaximumTextBindingCount,
            source.MaximumTextByteCount,
            source.MaximumUnresolvedCount,
            source.MaximumSourceBatchCount,
            source.IdentityIndexCapacity,
            source.TextIndexCapacity,
            source.ResidentByteBudget);
        if (!result.IsPublished
            || result.MaximumSourceCount != NativeDisplayCoordinatorAbi.SourceCount
            || result.MaximumSourceBatchCount != NativeDisplayCoordinatorAbi.SourceCount
            || !IsPowerOfTwo(result.IdentityIndexCapacity)
            || !IsPowerOfTwo(result.TextIndexCapacity))
        {
            throw new InvalidDataException(
                $"{path} contains an invalid display-coordinator capacity.");
        }

        return result;
    }

    private static ulong CompileDisplaySourceMask(
        string[] sourceNames,
        string path)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        ulong mask = 0;
        foreach (var sourceName in sourceNames)
        {
            var source = ParseDisplaySource(sourceName, path);
            var bit = 1UL << (checked((int)source) - 1);
            if ((mask & bit) != 0)
            {
                throw new InvalidDataException(
                    $"{path} contains duplicate source '{sourceName}'.");
            }
            mask |= bit;
        }

        return mask;
    }

    private static ulong CompileDisplaySourcePriority(
        string[] sourceNames,
        string path)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        if (sourceNames.Length != NativeDisplayCoordinatorAbi.SourceCount)
        {
            throw new InvalidDataException(
                $"{path} must list every display source exactly once.");
        }

        ulong order = 0;
        ulong mask = 0;
        for (var index = 0; index < sourceNames.Length; index++)
        {
            var source = ParseDisplaySource(sourceNames[index], path);
            var bit = 1UL << (checked((int)source) - 1);
            if ((mask & bit) != 0)
            {
                throw new InvalidDataException(
                    $"{path} contains duplicate source '{sourceNames[index]}'.");
            }
            mask |= bit;
            order |= checked((ulong)source) << (index * 8);
        }
        if (mask != (ulong)NativeDisplaySourceMask.Known)
        {
            throw new InvalidDataException(
                $"{path} must cover every known display source.");
        }

        return order;
    }

    private static uint ParseDisplaySource(string value, string path)
        => value switch
        {
            "display_config" => (uint)NativeDisplaySource.DisplayConfig,
            "dxgi" => (uint)NativeDisplaySource.Dxgi,
            "edid" => (uint)NativeDisplaySource.Edid,
            "setup_api_monitor" => (uint)NativeDisplaySource.SetupApiMonitor,
            "oem_connector_profile" => (uint)NativeDisplaySource.OemConnectorProfile,
            _ => throw new InvalidDataException(
                $"{path} contains unknown source '{value}'.")
        };

    private static bool IsPowerOfTwo(uint value)
        => value > 0 && (value & (value - 1)) == 0;
}
