using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerPublicServiceCoordinatorBuildPlan
        CompilePublicServiceCoordinatorBuild(
            uint abiVersion,
            HostManagerPublicServiceCoordinatorCapacityProfile source,
            CompiledHostManagerNativeBinaryIdentity nativeBinary)
    {
        if (abiVersion != NativePublicServiceCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.public_service_coordinator_abi_version does not match the binary.");
        }

        return new CompiledHostManagerPublicServiceCoordinatorBuildPlan(
            abiVersion,
            "public_service_coordinator",
            nativeBinary.FileName,
            nativeBinary.Sha256,
            CompilePublicServiceCoordinatorCapacity(
                source,
                "build_specialize.capacity_limits.public_service_coordinator"));
    }

    private static CompiledHostManagerPublicServiceCoordinatorRecreatePlan
        CompilePublicServiceCoordinatorRecreate(
            HostManagerPublicServiceCoordinatorRecreateProfile source,
            CompiledHostManagerPublicServiceCoordinatorBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompilePublicServiceCoordinatorCapacity(
            source.Capacity,
            "host_recreate.public_service_coordinator.capacity");
        if (!capacity.FitsWithin(build.CapacityLimits))
        {
            throw new InvalidDataException(
                "host_recreate.public_service_coordinator capacity exceeds build limits.");
        }

        var result = new CompiledHostManagerPublicServiceCoordinatorRecreatePlan(
            capacity,
            source.MaximumConcurrentModelTasks,
            source.MaximumRequestsPerRateWindow,
            source.MaximumInflightRequestsPerCaller,
            CompileRetryableTaskOutcomeMask(
                source.RetryableTaskOutcomes
                    ?? throw Missing(
                        "host_recreate.public_service_coordinator.retryable_task_outcomes")),
            source.MaximumTaskAttemptCount,
            CompileRetryableHttpStatusPolicyMask(
                source.RetryableHttpStatusPolicies
                    ?? throw Missing(
                        "host_recreate.public_service_coordinator.retryable_http_status_policies")),
            source.RateWindowMilliseconds,
            source.RequestTimeoutMilliseconds,
            source.LeaseTimeoutMilliseconds,
            source.SubscriptionTimeoutMilliseconds,
            source.TaskTimeoutMilliseconds,
            source.RetryDelayMilliseconds,
            source.ModelCatalogAcquisitionIntervalMilliseconds,
            source.ModelCatalogLastGoodLifetimeMilliseconds,
            source.ModelCatalogAcquisitionTimeoutMilliseconds,
            source.LoopbackOnly);
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                "host_recreate.public_service_coordinator violates the native configuration contract.");
        }
        return result;
    }

    private static uint CompileRetryableTaskOutcomeMask(
        IReadOnlyCollection<string> outcomes)
    {
        uint mask = 0;
        foreach (var outcome in outcomes)
        {
            var value = outcome switch
            {
                "provider_unavailable" =>
                    (uint)NativePublicServiceTaskOutcomeMask.ProviderUnavailable,
                "timeout" => (uint)NativePublicServiceTaskOutcomeMask.Timeout,
                "transport_failure" =>
                    (uint)NativePublicServiceTaskOutcomeMask.TransportFailure,
                "rejected" => (uint)NativePublicServiceTaskOutcomeMask.Rejected,
                "failed" => (uint)NativePublicServiceTaskOutcomeMask.Failed,
                _ => throw new InvalidDataException(
                    $"Unknown public-service task outcome '{outcome}'.")
            };
            if ((mask & value) != 0)
            {
                throw new InvalidDataException(
                    $"Duplicate public-service task outcome '{outcome}'.");
            }
            mask |= value;
        }
        return mask;
    }

    private static uint CompileRetryableHttpStatusPolicyMask(
        IReadOnlyCollection<string> policies)
    {
        uint mask = 0;
        foreach (var policy in policies)
        {
            var value = policy switch
            {
                "request_timeout" =>
                    (uint)NativePublicServiceTaskHttpRetryPolicyMask.RequestTimeout,
                "throttled" =>
                    (uint)NativePublicServiceTaskHttpRetryPolicyMask.Throttled,
                "server_error" =>
                    (uint)NativePublicServiceTaskHttpRetryPolicyMask.ServerError,
                _ => throw new InvalidDataException(
                    $"Unknown public-service HTTP retry policy '{policy}'.")
            };
            if ((mask & value) != 0)
            {
                throw new InvalidDataException(
                    $"Duplicate public-service HTTP retry policy '{policy}'.");
            }
            mask |= value;
        }
        return mask;
    }

    private static CompiledHostManagerPublicServiceCoordinatorHotPublishPlan
        CompilePublicServiceCoordinatorHotPublish(
            HostManagerPublicServiceCoordinatorHotPublishProfile source,
            AppSettings settings,
            int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        var capabilities = (source.Capabilities
                ?? throw Missing("hot_publish.public_service_coordinator.capabilities"))
            .Select(item => CompilePublicServiceCapability(item, settings.PublicService))
            .OrderBy(static item => item.Handle)
            .ToImmutableArray();
        var routes = (source.Routes
                ?? throw Missing("hot_publish.public_service_coordinator.routes"))
            .Select(CompilePublicServiceRoute)
            .OrderBy(static item => item.Handle)
            .ToImmutableArray();
        var result = new CompiledHostManagerPublicServiceCoordinatorHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.ServiceName,
            source.ApiVersion,
            source.BasePath,
            settings.PublicService.Enabled,
            source.AnonymousCallerHandle,
            source.AiProviderHandle,
            settings.AiModelService.Provider,
            settings.AiModelService.Endpoint,
            settings.AiModelService.AutoStartEnabled,
            source.ModelAliasProjectionContractVersion,
            source.LoadTaskBaseScore,
            source.UnloadTaskBaseScore,
            capabilities,
            routes);
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                "hot_publish.public_service_coordinator violates the native catalog contract.");
        }
        return result;
    }

    private static CompiledHostManagerPublicServiceCoordinatorPlan
        CompilePublicServiceCoordinatorPlan(
            CompiledHostManagerPublicServiceCoordinatorBuildPlan build,
            CompiledHostManagerPublicServiceCoordinatorRecreatePlan recreate,
            CompiledHostManagerPublicServiceCoordinatorHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerPublicServiceCoordinatorPlan(
            build,
            recreate,
            hotPublish,
            configurationSha256);
    }

    private static CompiledHostManagerPublicServiceCoordinatorCapacityPlan
        CompilePublicServiceCoordinatorCapacity(
            HostManagerPublicServiceCoordinatorCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new CompiledHostManagerPublicServiceCoordinatorCapacityPlan(
            source.MaximumCapabilityCount,
            source.MaximumRouteCount,
            source.MaximumModelCount,
            source.MaximumModelAliasCount,
            source.MaximumRequestCount,
            source.MaximumRateBucketCount,
            source.MaximumLeaseCount,
            source.MaximumSubscriptionCount,
            source.MaximumTaskCount,
            source.CapabilityIndexCapacity,
            source.ModelIndexCapacity,
            source.AliasIndexCapacity,
            source.RequestIndexCapacity,
            source.RateBucketIndexCapacity,
            source.LeaseIndexCapacity,
            source.SubscriptionIndexCapacity,
            source.TaskIndexCapacity,
            source.MaximumCatalogTextBytes,
            source.MaximumModelTextBytes,
            source.ResidentByteBudget);
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                $"{path} violates the native public-service capacity contract.");
        }
        return result;
    }

    private static CompiledHostManagerPublicServiceCapabilityPlan CompilePublicServiceCapability(
        HostManagerPublicServiceCapabilityProfile source,
        AppLocalPublicServiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        var enabled = source.Setting switch
        {
            "always" => true,
            "file_index" => settings.FileIndexEnabled,
            "database" => settings.DatabaseServiceEnabled,
            "ai_model_catalog" => settings.AiModelCatalogEnabled,
            _ => throw new InvalidDataException(
                $"Unknown public-service capability setting '{source.Setting}'.")
        };
        return new CompiledHostManagerPublicServiceCapabilityPlan(
            source.Handle,
            source.PayloadHandle,
            source.Id,
            source.DisplayName,
            source.Description,
            enabled,
            source.Available);
    }

    private static CompiledHostManagerPublicServiceRoutePlan CompilePublicServiceRoute(
        HostManagerPublicServiceRouteProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        uint mask = 0;
        foreach (var method in source.Methods
                     ?? throw Missing("hot_publish.public_service_coordinator.routes[].methods"))
        {
            var value = method switch
            {
                "GET" => (uint)NativePublicServiceMethodMask.Get,
                "HEAD" => (uint)NativePublicServiceMethodMask.Head,
                "POST" => (uint)NativePublicServiceMethodMask.Post,
                "PUT" => (uint)NativePublicServiceMethodMask.Put,
                "DELETE" => (uint)NativePublicServiceMethodMask.Delete,
                "PATCH" => (uint)NativePublicServiceMethodMask.Patch,
                _ => throw new InvalidDataException(
                    $"Unknown public-service HTTP method '{method}'.")
            };
            if ((mask & value) != 0)
            {
                throw new InvalidDataException(
                    $"Duplicate public-service HTTP method '{method}'.");
            }
            mask |= value;
        }

        return new CompiledHostManagerPublicServiceRoutePlan(
            source.Handle,
            source.CapabilityHandle,
            source.PayloadHandle,
            source.Path,
            mask,
            source.ExactPath,
            source.CatalogRoute,
            source.BypassRateLimit);
    }
}
