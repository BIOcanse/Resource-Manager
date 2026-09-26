using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerSchedulingPlanBinding(
    ulong HostPublicationSequence,
    ulong HostPlanEpoch,
    string HostPlanSha256,
    ulong SmartConfigurationGeneration,
    string SmartConfigurationSha256,
    bool MemoryModePolicyEnabled,
    string MemoryModePolicySourceKind,
    ulong MemoryModeConfigurationGeneration,
    string MemoryModeConfigurationSha256)
{
    internal bool IsPublished => HostPublicationSequence > 0
        && HostPlanEpoch > 0
        && IsCanonicalSha256(HostPlanSha256)
        && SmartConfigurationGeneration > 0
        && IsCanonicalSha256(SmartConfigurationSha256)
        && MemoryModeConfigurationGeneration > 0
        && IsCanonicalSha256(MemoryModeConfigurationSha256)
        && (MemoryModePolicyEnabled
            ? string.Equals(
                MemoryModePolicySourceKind,
                HostManagerMemoryModePolicySourceKinds.ProductBaseline,
                StringComparison.Ordinal)
            : MemoryModePolicySourceKind.Length == 0);

    internal static HostManagerSchedulingPlanBinding Create(
        HostManagerSmartCoordinatorRuntimePlan desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var policy = desired.SmartCoordinator.HotPublish.MemoryModePolicy;
        var result = new HostManagerSchedulingPlanBinding(
            desired.PublicationSequence,
            desired.HostPlan.PlanEpoch,
            desired.HostPlan.PlanSha256,
            desired.SmartCoordinator.ConfigurationGeneration,
            desired.SmartCoordinator.ConfigurationSha256,
            policy.Enabled,
            policy.SourceKind,
            policy.ConfigurationGeneration,
            policy.ConfigurationSha256);
        return result.IsPublished
            ? result
            : throw new InvalidDataException(
                "The Host scheduling plan binding is not completely published.");
    }

    internal static bool IsCanonicalSha256(string value)
        => value is not null
            && value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal sealed record HostManagerMemoryModePolicyEvidence(
    string SourceKind,
    string ConfigurationSha256,
    string BindingSha256)
{
    private const string DigestNamespace =
        "rm-host-memory-mode-policy-binding-v3";

    internal bool IsPublished => string.Equals(
            SourceKind,
            HostManagerMemoryModePolicySourceKinds.ProductBaseline,
            StringComparison.Ordinal)
        && HostManagerSchedulingPlanBinding.IsCanonicalSha256(ConfigurationSha256)
        && HostManagerSchedulingPlanBinding.IsCanonicalSha256(BindingSha256);

    internal static HostManagerMemoryModePolicyEvidence Create(
        HostManagerSchedulingPlanBinding binding,
        string sourceKind,
        string configurationSha256,
        bool allowUnrestricted)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.IsPublished
            || !binding.MemoryModePolicyEnabled
            || !string.Equals(
                sourceKind,
                binding.MemoryModePolicySourceKind,
                StringComparison.Ordinal)
            || !string.Equals(
                configurationSha256,
                binding.MemoryModeConfigurationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The memory-mode policy does not match the current Host plan binding.");
        }

        return new HostManagerMemoryModePolicyEvidence(
            sourceKind,
            configurationSha256,
            ComputeBindingSha256(
                binding,
                sourceKind,
                configurationSha256,
                allowUnrestricted));
    }

    internal bool IsBoundTo(
        HostManagerSchedulingPlanBinding binding,
        bool allowUnrestricted)
        => IsPublished
            && binding.IsPublished
            && binding.MemoryModePolicyEnabled
            && string.Equals(
                SourceKind,
                binding.MemoryModePolicySourceKind,
                StringComparison.Ordinal)
            && string.Equals(
                ConfigurationSha256,
                binding.MemoryModeConfigurationSha256,
                StringComparison.Ordinal)
            && string.Equals(
                BindingSha256,
                ComputeBindingSha256(
                    binding,
                    SourceKind,
                    ConfigurationSha256,
                    allowUnrestricted),
                StringComparison.Ordinal);

    private static string ComputeBindingSha256(
        HostManagerSchedulingPlanBinding binding,
        string sourceKind,
        string configurationSha256,
        bool allowUnrestricted)
    {
        var canonical = string.Join(
            '\n',
            DigestNamespace,
            binding.HostPublicationSequence.ToString(CultureInfo.InvariantCulture),
            binding.HostPlanEpoch.ToString(CultureInfo.InvariantCulture),
            binding.HostPlanSha256,
            binding.SmartConfigurationGeneration.ToString(CultureInfo.InvariantCulture),
            binding.SmartConfigurationSha256,
            binding.MemoryModeConfigurationGeneration.ToString(CultureInfo.InvariantCulture),
            binding.MemoryModeConfigurationSha256,
            sourceKind,
            configurationSha256,
            allowUnrestricted ? "1" : "0") + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
