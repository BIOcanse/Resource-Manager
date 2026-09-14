import type { OptimizationReportItem } from "../types";
import { userFacingDateTime, userFacingOptionalValue } from "./userFacingText";
import type { UserDetailSection } from "./userDetails";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "./userDetails";

export interface OptimizationReportPresentation {
  title: string;
  summary: string;
  details: UserDetailSection[];
}

export function presentOptimizationReport(report: OptimizationReportItem): OptimizationReportPresentation {
  const name = userFacingOptionalValue(report.target.displayName, "当前对象");
  const resource = resourceLabel(report.evidence.resourceKind);
  return {
    title: reportTitle(report.type, name, resource),
    summary: reportSummary(report, name, resource),
    details: reportDetails(report, name, resource)
  };
}

export function optimizationSeverityLabel(value: string) {
  switch (value.toLocaleLowerCase()) {
    case "critical": return "需要尽快处理";
    case "warning": return "建议关注";
    default: return "提示";
  }
}

function reportTitle(type: string, name: string, resource: string) {
  switch (type) {
    case "GameBackgroundResourceUsage": return `${name} 在游戏运行时仍有后台占用`;
    case "BackgroundHighUsage": return `${name} 的后台${resource}占用较高`;
    case "BackgroundPersistentMicroUsage": return `${name} 持续产生少量后台占用`;
    case "VramResidency": return `${name} 长时间占用显存`;
    case "DiskPressure": return `${name} 的可用空间不足`;
    case "RollingDiskTraffic": return `${name} 的磁盘读写量较高`;
    case "RollingDiskWrite": return `${name} 的磁盘写入量较高`;
    case "RollingNetworkTraffic": return `${name} 的网络流量较高`;
    case "RollingNetworkActivity": return `${name} 的网络活动较频繁`;
    case "SoftwareFootprint": return `${name} 占用的存储空间较大`;
    case "PowerProfileNotPerformanceFocused": return "当前电源模式可能限制性能";
    case "ExternalDisplayLinkCapabilityGap": return `${name} 的显示连接能力受限`;
    case "DeviceDriverProblem": return `${name} 的设备驱动存在问题`;
    case "ExternalDiskDuplicateSecurityScan": return `${name} 可能被重复扫描`;
    case "ExternalDiskIdleTimeoutTooShort": return `${name} 进入休眠过于频繁`;
    case "PhysicalDiskLatencyHigh": return `${name} 的响应延迟较高`;
    case "PhysicalDiskHealthWarning": return `${name} 的健康状态需要关注`;
    case "ExternalDiskLinkCapabilityGap": return `${name} 的连接速度受限`;
    case "VolumeFragmentationHigh": return `${name} 的文件分布较分散`;
    case "CpuSustainedThermalThrottling": return "处理器持续受到温度限制";
    case "SystemInterruptPressure": return "系统中断占用持续偏高";
    default: return `${name} 需要关注`;
  }
}

function reportSummary(report: OptimizationReportItem, name: string, resource: string) {
  const current = userFacingOptionalValue(report.evidence.currentDisplay);
  const average = userFacingOptionalValue(report.evidence.averageDisplay);
  switch (report.type) {
    case "DiskPressure": return `${name} 当前可用空间为 ${current}。`;
    case "SoftwareFootprint": return `${name} 当前占用 ${current} 存储空间。`;
    case "RollingDiskTraffic":
    case "RollingDiskWrite":
    case "RollingNetworkTraffic": return `最近 24 小时累计 ${average}。`;
    case "RollingNetworkActivity": return `最近一段时间网络活动持续较多。`;
    case "PowerProfileNotPerformanceFocused": return "需要高性能时，可以检查 Windows 电源模式。";
    case "CpuSustainedThermalThrottling": return "处理器性能正在因温度持续降低。";
    case "SystemInterruptPressure": return "硬件或驱动活动正在占用较多处理器时间。";
    default: return `当前${resource}为 ${current}，观察平均值为 ${average}。`;
  }
}

function reportDetails(report: OptimizationReportItem, name: string, resource: string): UserDetailSection[] {
  const foreground = report.context?.contextKind === "Game"
    ? report.context.foregroundSoftwareName || report.context.foregroundProcessName
    : null;
  const processes = (report.target.processNames ?? []).filter(Boolean).slice(0, 6).join("、");
  return compactUserDetailSections([
    userDetailSection("当前情况", [
      userDetailItem("对象", name),
      userDetailItem("影响", resource),
      userDetailItem("当前", userFacingOptionalValue(report.evidence.currentDisplay)),
      userDetailItem("平均", userFacingOptionalValue(report.evidence.averageDisplay)),
      userDetailItem("峰值", userFacingOptionalValue(report.evidence.peakDisplay)),
      userDetailItem("状态", optimizationSeverityLabel(report.severity))
    ]),
    userDetailSection("观察范围", [
      foreground ? userDetailItem("前台软件", foreground) : null,
      processes ? userDetailItem("相关进程", processes) : null,
      report.firstObservedAt ? userDetailItem("首次发现", userFacingDateTime(report.firstObservedAt)) : null,
      report.lastObservedAt ? userDetailItem("最近发现", userFacingDateTime(report.lastObservedAt)) : null,
      report.evidence.durationSeconds > 0 ? userDetailItem("持续时间", formatDuration(report.evidence.durationSeconds)) : null,
      report.evidence.activeSampleCount > 0 ? userDetailItem("发现次数", `${report.evidence.activeSampleCount} 次`) : null
    ])
  ]);
}

function resourceLabel(value: string) {
  switch (value.toLocaleLowerCase()) {
    case "cpu": return "处理器";
    case "gpu": return "图形处理器";
    case "memory": return "内存";
    case "vram": return "显存";
    case "disk": return "磁盘空间";
    case "diskwrite": return "磁盘写入";
    case "network": return "网络流量";
    case "networkactivity": return "网络活动";
    case "softwarefootprint": return "存储空间";
    case "powerprofile": return "电源模式";
    case "devicedriver": return "设备驱动";
    case "diskhealth": return "磁盘健康";
    case "disklink": return "磁盘连接";
    case "disklatency": return "磁盘响应";
    case "diskpower": return "磁盘电源状态";
    case "cputhermal": return "处理器温度";
    case "systeminterrupt": return "系统中断";
    default: return "资源使用";
  }
}

function formatDuration(seconds: number) {
  if (seconds >= 86400) return `${Math.round(seconds / 86400)} 天`;
  if (seconds >= 3600) return `${Math.round(seconds / 3600)} 小时`;
  if (seconds >= 60) return `${Math.round(seconds / 60)} 分钟`;
  return `${Math.max(1, Math.round(seconds))} 秒`;
}
