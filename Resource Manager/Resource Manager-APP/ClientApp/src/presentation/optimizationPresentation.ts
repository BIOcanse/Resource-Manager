import type { OptimizationReportItem } from "../types";
import { userFacingDateTime, userFacingOptionalValue } from "./userFacingText";
import type { UserDetailSection } from "./userDetails";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "./userDetails";
import { uiText } from "../text.ts";

export interface OptimizationReportPresentation {
  title: string;
  summary: string;
  details: UserDetailSection[];
}

export function presentOptimizationReport(report: OptimizationReportItem): OptimizationReportPresentation {
  const name = userFacingOptionalValue(report.target.displayName, uiText.optimizationReport.currentTarget);
  const resource = resourceLabel(report.evidence.resourceKind);
  return {
    title: reportTitle(report.type, name, resource),
    summary: reportSummary(report, name, resource),
    details: reportDetails(report, name, resource)
  };
}

export function optimizationSeverityLabel(value: string) {
  switch (value.toLocaleLowerCase()) {
    case "critical": return uiText.optimizationReport.severityCritical;
    case "warning": return uiText.optimizationReport.severityWarning;
    default: return uiText.optimizationReport.severityInfo;
  }
}

function reportTitle(type: string, name: string, resource: string) {
  switch (type) {
    case "GameBackgroundResourceUsage": return uiText.optimizationReport.title.gameBackgroundResourceUsage(name);
    case "BackgroundHighUsage": return uiText.optimizationReport.title.backgroundHighUsage(name, resource);
    case "BackgroundPersistentMicroUsage": return uiText.optimizationReport.title.backgroundPersistentMicroUsage(name);
    case "VramResidency": return uiText.optimizationReport.title.vramResidency(name);
    case "DiskPressure": return uiText.optimizationReport.title.diskPressure(name);
    case "RollingDiskTraffic": return uiText.optimizationReport.title.rollingDiskTraffic(name);
    case "RollingDiskWrite": return uiText.optimizationReport.title.rollingDiskWrite(name);
    case "RollingNetworkTraffic": return uiText.optimizationReport.title.rollingNetworkTraffic(name);
    case "RollingNetworkActivity": return uiText.optimizationReport.title.rollingNetworkActivity(name);
    case "SoftwareFootprint": return uiText.optimizationReport.title.softwareFootprint(name);
    case "PowerProfileNotPerformanceFocused": return uiText.optimizationReport.title.powerProfileNotPerformanceFocused;
    case "ExternalDisplayLinkCapabilityGap": return uiText.optimizationReport.title.externalDisplayLinkCapabilityGap(name);
    case "DeviceDriverProblem": return uiText.optimizationReport.title.deviceDriverProblem(name);
    case "ExternalDiskDuplicateSecurityScan": return uiText.optimizationReport.title.externalDiskDuplicateSecurityScan(name);
    case "ExternalDiskIdleTimeoutTooShort": return uiText.optimizationReport.title.externalDiskIdleTimeoutTooShort(name);
    case "PhysicalDiskLatencyHigh": return uiText.optimizationReport.title.physicalDiskLatencyHigh(name);
    case "PhysicalDiskHealthWarning": return uiText.optimizationReport.title.physicalDiskHealthWarning(name);
    case "ExternalDiskLinkCapabilityGap": return uiText.optimizationReport.title.externalDiskLinkCapabilityGap(name);
    case "VolumeFragmentationHigh": return uiText.optimizationReport.title.volumeFragmentationHigh(name);
    case "CpuSustainedThermalThrottling": return uiText.optimizationReport.title.cpuSustainedThermalThrottling;
    case "SystemInterruptPressure": return uiText.optimizationReport.title.systemInterruptPressure;
    default: return uiText.optimizationReport.title.generic(name);
  }
}

function reportSummary(report: OptimizationReportItem, name: string, resource: string) {
  const current = userFacingOptionalValue(report.evidence.currentDisplay);
  const average = userFacingOptionalValue(report.evidence.averageDisplay);
  switch (report.type) {
    case "DiskPressure": return uiText.optimizationReport.summary.diskPressure(name, current);
    case "SoftwareFootprint": return uiText.optimizationReport.summary.softwareFootprint(name, current);
    case "RollingDiskTraffic":
    case "RollingDiskWrite":
    case "RollingNetworkTraffic": return uiText.optimizationReport.summary.rollingTotal(average);
    case "RollingNetworkActivity": return uiText.optimizationReport.summary.rollingNetworkActivity;
    case "PowerProfileNotPerformanceFocused": return uiText.optimizationReport.summary.powerProfileNotPerformanceFocused;
    case "CpuSustainedThermalThrottling": return uiText.optimizationReport.summary.cpuSustainedThermalThrottling;
    case "SystemInterruptPressure": return uiText.optimizationReport.summary.systemInterruptPressure;
    default: return uiText.optimizationReport.summary.generic(resource, current, average);
  }
}

function reportDetails(report: OptimizationReportItem, name: string, resource: string): UserDetailSection[] {
  const foreground = report.context?.contextKind === "Game"
    ? report.context.foregroundSoftwareName || report.context.foregroundProcessName
    : null;
  const processes = (report.target.processNames ?? []).filter(Boolean).slice(0, 6).join("、");
  return compactUserDetailSections([
    userDetailSection(uiText.optimizationReport.detail.currentSituation, [
      userDetailItem(uiText.optimizationReport.detail.target, name),
      userDetailItem(uiText.optimizationReport.detail.impact, resource),
      userDetailItem(uiText.optimizationReport.detail.current, userFacingOptionalValue(report.evidence.currentDisplay)),
      userDetailItem(uiText.optimizationReport.detail.average, userFacingOptionalValue(report.evidence.averageDisplay)),
      userDetailItem(uiText.optimizationReport.detail.peak, userFacingOptionalValue(report.evidence.peakDisplay)),
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

function resourceLabel(value: string) {
  switch (value.toLocaleLowerCase()) {
    case "cpu": return uiText.optimizationReport.resource.cpu;
    case "gpu": return uiText.optimizationReport.resource.gpu;
    case "memory": return uiText.optimizationReport.resource.memory;
    case "vram": return uiText.optimizationReport.resource.vram;
    case "disk": return uiText.optimizationReport.resource.disk;
    case "diskwrite": return uiText.optimizationReport.resource.diskWrite;
    case "network": return uiText.optimizationReport.resource.network;
    case "networkactivity": return uiText.optimizationReport.resource.networkActivity;
    case "softwarefootprint": return uiText.optimizationReport.resource.softwareFootprint;
    case "powerprofile": return uiText.optimizationReport.resource.powerProfile;
    case "devicedriver": return uiText.optimizationReport.resource.deviceDriver;
    case "diskhealth": return uiText.optimizationReport.resource.diskHealth;
    case "disklink": return uiText.optimizationReport.resource.diskLink;
    case "disklatency": return uiText.optimizationReport.resource.diskLatency;
    case "diskpower": return uiText.optimizationReport.resource.diskPower;
    case "cputhermal": return uiText.optimizationReport.resource.cpuThermal;
    case "systeminterrupt": return uiText.optimizationReport.resource.systemInterrupt;
    default: return uiText.optimizationReport.resource.generic;
  }
}

function formatDuration(seconds: number) {
  if (seconds >= 86400) return uiText.optimizationReport.duration.days(Math.round(seconds / 86400));
  if (seconds >= 3600) return uiText.optimizationReport.duration.hours(Math.round(seconds / 3600));
  if (seconds >= 60) return uiText.optimizationReport.duration.minutes(Math.round(seconds / 60));
  return uiText.optimizationReport.duration.seconds(Math.max(1, Math.round(seconds)));
}
