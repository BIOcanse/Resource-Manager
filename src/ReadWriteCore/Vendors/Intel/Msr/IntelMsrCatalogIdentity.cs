using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Intel.Msr;

public enum IntelMsrTargetKind { PhysicalCore, Package, LogicalProcessor, PhysicalDie, Platform }

public sealed record IntelMsrCatalogIdentity(int DisplayIndex, string ExactIdentityKey,
    CpuProcessorAddress Processor, uint Signature, uint? X2ApicId, IntelMsrTargetKind Kind,
    bool SupportsDts, bool SupportsTemperatureTarget)
{
    public bool IsPackage => Kind == IntelMsrTargetKind.Package;
    public bool IsCore => Kind == IntelMsrTargetKind.PhysicalCore;
    public bool IsDie => Kind == IntelMsrTargetKind.PhysicalDie;
    public bool IsPlatform => Kind == IntelMsrTargetKind.Platform;
    public bool SupportsPerformanceFeedback { get; init; }
    public ushort? IntelProcessorBaseFrequencyDeclaredMhz { get; init; }
    public IntelRaplProfile RaplProfile { get; init; }
    public IntelThermalStatusCapabilities ThermalStatusCapabilities { get; init; }
    public string? CoreId { get; init; }
    public string? PackageId { get; init; }
    public string? DieId { get; init; }
    public string? NameDeclared { get; init; }
    public IReadOnlyList<CpuProcessorAddress> ProcessorAddresses { get; init; } = [];
    public string BindingKey => $"{ExactIdentityKey}|{Processor.ProcessorGroup}:{Processor.GroupRelativeIndex}|{Signature}|{X2ApicId}|{Kind}|{SupportsDts}|{SupportsTemperatureTarget}|{SupportsPerformanceFeedback}|{IntelProcessorBaseFrequencyDeclaredMhz}|{(int)RaplProfile.Domains}|{(int)RaplProfile.PowerInformationDomains}|{RaplProfile.DramPowerUsesRaplUnits}|{RaplProfile.FixedDramUnitJoules?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{(int)ThermalStatusCapabilities}|core:{CoreId}|package:{PackageId}|die:{DieId}|members:{string.Join(',', ProcessorAddresses.Select(static address => $"{address.ProcessorGroup}:{address.GroupRelativeIndex}"))}";

    public bool AppliesTo(MetricFieldCode code) => IntelThermalStatusCatalog.Find(code) is { } status
        ? (IsCore || IsPackage) && status.IsPackage == IsPackage && (ThermalStatusCapabilities & status.Capability) != 0
        : IntelRaplPowerInformation.Find(code) is { } info
        ? (IsPackage || IsDie) && info.IsDie == IsDie && IntelRaplPowerInformation.Supports(RaplProfile, code)
        : IntelRaplFieldCatalog.Find(code) is { } domain
        ? (IsPackage || IsDie) && domain.IsDie == IsDie && (RaplProfile.Domains & domain.Domain) != 0 : code switch
    {
        MetricFieldCode.IntelPsysEnergyCounter or MetricFieldCode.IntelPsysEnergyUnit => IsPlatform,
        MetricFieldCode.IntelAperfCounter or MetricFieldCode.IntelMperfCounter or MetricFieldCode.IntelPerformanceRatio => Kind == IntelMsrTargetKind.LogicalProcessor && SupportsPerformanceFeedback,
        MetricFieldCode.IntelEffectiveFrequencyMhz => Kind == IntelMsrTargetKind.LogicalProcessor && SupportsPerformanceFeedback && IntelProcessorBaseFrequencyDeclaredMhz is > 0,
        MetricFieldCode.IntelCoreDtsDistance => IsCore && SupportsDts,
        MetricFieldCode.IntelPackageDtsDistance => IsPackage && SupportsDts,
        MetricFieldCode.IntelCoreDtsTemperature or MetricFieldCode.IntelCoreTemperatureTarget => IsCore && SupportsDts && SupportsTemperatureTarget,
        MetricFieldCode.IntelPackageDtsTemperature or MetricFieldCode.IntelPackageTemperatureTarget => IsPackage && SupportsDts && SupportsTemperatureTarget,
        _ => false
    };
}

public sealed record IntelMsrTopologyRead(NativeMetricSnapshotCatalogTopologyStatus CoreStatus,
    NativeMetricSnapshotCatalogTopologyStatus PackageStatus,
    NativeMetricSnapshotCatalogTopologyStatus LogicalProcessorStatus, IReadOnlyList<IntelMsrCatalogIdentity> Identities)
{
    public NativeMetricSnapshotCatalogTopologyStatus DieStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus PlatformStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
}
