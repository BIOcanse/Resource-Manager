import type { OptimizationReportItem } from "../../types";
import { userFacingDateTime, userFacingOptionalValue } from "../../presentation/userFacingText.ts";
import type { UserDetailSection } from "../../presentation/userDetails.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails.ts";
import { uiText } from "../../text.ts";
import { formatBytes } from "../../presentation/byteUnits.ts";

export function optimizationTargetName(target: { name: string; targetType: string }): string {
  return target.name || (target.targetType === "cpu_package" ? uiText.smartReport.resource.cpu
    : target.targetType === "system" ? uiText.optimizationReport.resource.systemInterrupt
      : uiText.optimizationReport.currentTarget);
}

export interface OptimizationReportPresentation {
  title: string;
  summary: string;
  details: UserDetailSection[];
}

export function presentOptimizationReport(report: OptimizationReportItem): OptimizationReportPresentation {
  const name = optimizationTargetName(report.target);
  const resource = optimizationResourceLabel(report.evidence.resourceKind);
  return {
    title: reportTitle(report.type, name, resource),
    summary: reportSummary(report, name, resource),
    details: reportDetails(report, name, resource)
  };
}

export function optimizationSeverityLabel(value: string) {
  switch (value) {
    case "critical": return uiText.optimizationReport.severityCritical;
    case "warning": return uiText.optimizationReport.severityWarning;
    default: return uiText.optimizationReport.severityInfo;
  }
}

function reportTitle(type: string, name: string, resource: string) {
  switch (type) {
    case "suspected_stray_process": return uiText.optimizationReport.strayProcess.title(name);
    case "game_background_resource_usage": return uiText.optimizationReport.title.gameBackgroundResourceUsage(name);
    case "background_high_usage": return uiText.optimizationReport.title.backgroundHighUsage(name, resource);
    case "background_persistent_micro_usage": return uiText.optimizationReport.title.backgroundPersistentMicroUsage(name);
    case "vram_residency": return uiText.optimizationReport.title.vramResidency(name);
    case "disk_pressure": return uiText.optimizationReport.title.diskPressure(name);
    case "rolling_disk_traffic": return uiText.optimizationReport.title.rollingDiskTraffic(name);
    case "rolling_disk_write": return uiText.optimizationReport.title.rollingDiskWrite(name);
    case "rolling_network_traffic": return uiText.optimizationReport.title.rollingNetworkTraffic(name);
    case "rolling_network_activity": return uiText.optimizationReport.title.rollingNetworkActivity(name);
    case "software_footprint": return uiText.optimizationReport.title.softwareFootprint(name);
    case "power_profile_not_performance_focused": return uiText.optimizationReport.title.powerProfileNotPerformanceFocused;
    case "external_display_link_capability_gap": return uiText.optimizationReport.title.externalDisplayLinkCapabilityGap(name);
    case "device_driver_problem": return uiText.optimizationReport.title.deviceDriverProblem(name);
    case "external_disk_duplicate_security_scan": return uiText.optimizationReport.title.externalDiskDuplicateSecurityScan(name);
    case "external_disk_idle_timeout_too_short": return uiText.optimizationReport.title.externalDiskIdleTimeoutTooShort(name);
    case "physical_disk_latency_high": return uiText.optimizationReport.title.physicalDiskLatencyHigh(name);
    case "physical_disk_health_warning": return uiText.optimizationReport.title.physicalDiskHealthWarning(name);
    case "external_disk_link_capability_gap": return uiText.optimizationReport.title.externalDiskLinkCapabilityGap(name);
    case "volume_fragmentation_high": return uiText.optimizationReport.title.volumeFragmentationHigh(name);
    case "cpu_sustained_thermal_throttling": return uiText.optimizationReport.title.cpuSustainedThermalThrottling;
    case "system_interrupt_pressure": return uiText.optimizationReport.title.systemInterruptPressure;
    default: return uiText.optimizationReport.title.generic(name);
  }
}

function reportSummary(report: OptimizationReportItem, name: string, resource: string) {
  const current = optimizationEvidenceValue(report.evidence, report.evidence.currentValue);
  const average = optimizationEvidenceValue(report.evidence, report.evidence.averageValue);
  switch (report.type) {
    case "suspected_stray_process": {
      const process = report.evidence.process;
      return uiText.optimizationReport.strayProcess.summary(name,
        process?.memoryBytes == null ? userFacingOptionalValue(null) : formatBytes(process.memoryBytes, "memory"),
        process?.idleDurationSeconds == null ? userFacingOptionalValue(null) : formatDuration(process.idleDurationSeconds));
    }
    case "disk_pressure": return uiText.optimizationReport.summary.diskPressure(name, current);
    case "software_footprint": return uiText.optimizationReport.summary.softwareFootprint(name, current);
    case "rolling_disk_traffic":
    case "rolling_disk_write":
    case "rolling_network_traffic": return uiText.optimizationReport.summary.rollingTotal(average);
    case "rolling_network_activity": return uiText.optimizationReport.summary.rollingNetworkActivity;
    case "power_profile_not_performance_focused": return uiText.optimizationReport.summary.powerProfileNotPerformanceFocused;
    case "cpu_sustained_thermal_throttling": return uiText.optimizationReport.summary.cpuSustainedThermalThrottling;
    case "system_interrupt_pressure": return `${uiText.optimizationReport.summary.systemInterruptPressure} ${uiText.optimizationReport.detail.current}: ${current}. ${uiText.management.issueText.interruptAdvice}`;
    default: return uiText.optimizationReport.summary.generic(resource, current, average);
  }
}

function reportDetails(report: OptimizationReportItem, name: string, resource: string): UserDetailSection[] {
  if (report.type === "suspected_stray_process") {
    const process = report.evidence.process;
    const text = uiText.optimizationReport;
    return compactUserDetailSections([userDetailSection(text.detail.currentSituation, [
      userDetailItem(text.detail.target, name),
      userDetailItem(text.strayProcess.pid, process?.processId.toString() ?? userFacingOptionalValue(null)),
      userDetailItem(text.strayProcess.startKey, process?.processStartKey ?? userFacingOptionalValue(null)),
      userDetailItem(text.resource.memory, process?.memoryBytes == null ? userFacingOptionalValue(null) : formatBytes(process.memoryBytes, "memory")),
      userDetailItem(text.strayProcess.idleDuration, process?.idleDurationSeconds == null ? userFacingOptionalValue(null) : formatDuration(process.idleDurationSeconds)),
      userDetailItem(text.strayProcess.commandLine, userFacingOptionalValue(process?.commandLine)),
      userDetailItem(text.detail.lastObserved, userFacingDateTime(report.lastObservedAt))
    ])]);
  }
  const foreground = report.context?.contextKind === "game"
    ? report.context.foregroundSoftwareName || report.context.foregroundProcessName
    : null;
  const processes = (report.target.processNames ?? []).filter(Boolean).slice(0, 6).join("、");
  return compactUserDetailSections([
    userDetailSection(uiText.optimizationReport.detail.currentSituation, [
      userDetailItem(uiText.optimizationReport.detail.target, name),
      userDetailItem(uiText.optimizationReport.detail.impact, resource),
      userDetailItem(
        uiText.optimizationReport.detail.current,
        optimizationEvidenceValue(report.evidence, report.evidence.currentValue)),
      userDetailItem(
        uiText.optimizationReport.detail.average,
        optimizationEvidenceValue(report.evidence, report.evidence.averageValue)),
      userDetailItem(
        uiText.optimizationReport.detail.peak,
        optimizationEvidenceValue(report.evidence, report.evidence.peakValue)),
      userDetailItem(uiText.optimizationReport.detail.status, optimizationSeverityLabel(report.severity))
    ]),
    userDetailSection(uiText.optimizationReport.detail.observationScope, [
      foreground ? userDetailItem(uiText.optimizationReport.detail.foregroundSoftware, foreground) : null,
      processes ? userDetailItem(uiText.optimizationReport.detail.relatedProcesses, processes) : null,
      report.firstObservedAt ? userDetailItem(uiText.optimizationReport.detail.firstObserved, userFacingDateTime(report.firstObservedAt)) : null,
      report.lastObservedAt ? userDetailItem(uiText.optimizationReport.detail.lastObserved, userFacingDateTime(report.lastObservedAt)) : null,
      report.evidence.durationSeconds > 0 ? userDetailItem(uiText.optimizationReport.detail.duration, formatDuration(report.evidence.durationSeconds)) : null,
      report.evidence.activeSampleCount > 0 ? userDetailItem(uiText.optimizationReport.detail.occurrences, uiText.optimizationReport.detail.occurrenceTimes(report.evidence.activeSampleCount)) : null
    ])
  ]);
}

/**
 * 报告证据里的一个数值怎么显示。
 *
 * 后端只给数值和单位标记，不换算也不成句。容量走字节单位的唯一所有者，
 * 所以内存报告会跟着用户选的进制显示成 GiB 或 GB；其余单位在这里成句。
 */
export function optimizationEvidenceValue(
  evidence: OptimizationReportItem["evidence"],
  value: number
): string {
  if (!Number.isFinite(value)) {
    return userFacingOptionalValue(null);
  }
  switch (evidence.valueUnit) {
    case "B":
      return formatBytes(value, "memory");
    case "%":
      return `${trimNumber(value, 2)}%`;
    case "count":
      return uiText.optimizationReport.detail.occurrenceTimes(Math.round(value));
    default:
      return `${trimNumber(value, 3)} ${evidence.valueUnit}`.trim();
  }
}

// 小数位按需要保留：整数不拖小数点，末尾的零去掉。
function trimNumber(value: number, maximumDigits: number) {
  return Number(value.toFixed(maximumDigits)).toString();
}

export function optimizationResourceLabel(value: string) {
  switch (value) {
    case "cpu": return uiText.optimizationReport.resource.cpu;
    case "gpu": return uiText.optimizationReport.resource.gpu;
    case "memory": return uiText.optimizationReport.resource.memory;
    case "vram": return uiText.optimizationReport.resource.vram;
    case "disk": return uiText.optimizationReport.resource.disk;
    case "disk_write": return uiText.optimizationReport.resource.diskWrite;
    case "network": return uiText.optimizationReport.resource.network;
    case "network_activity": return uiText.optimizationReport.resource.networkActivity;
    case "software_footprint": return uiText.optimizationReport.resource.softwareFootprint;
    case "power_profile": return uiText.optimizationReport.resource.powerProfile;
    case "device_driver": return uiText.optimizationReport.resource.deviceDriver;
    case "disk_health": return uiText.optimizationReport.resource.diskHealth;
    case "disk_link": return uiText.optimizationReport.resource.diskLink;
    case "disk_latency": return uiText.optimizationReport.resource.diskLatency;
    case "disk_power": return uiText.optimizationReport.resource.diskPower;
    case "cpu_thermal": return uiText.optimizationReport.resource.cpuThermal;
    case "system_interrupt": return uiText.optimizationReport.resource.systemInterrupt;
    default: return uiText.optimizationReport.resource.generic;
  }
}

function formatDuration(seconds: number) {
  if (seconds >= 86400) return uiText.optimizationReport.duration.days(Math.round(seconds / 86400));
  if (seconds >= 3600) return uiText.optimizationReport.duration.hours(Math.round(seconds / 3600));
  if (seconds >= 60) return uiText.optimizationReport.duration.minutes(Math.round(seconds / 60));
  return uiText.optimizationReport.duration.seconds(Math.max(1, Math.round(seconds)));
}
