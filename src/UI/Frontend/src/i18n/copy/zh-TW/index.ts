import { zhBackendMessagesCopy } from "./backendMessages.ts";
import { zhDetailsCopy } from "./details.ts";
import { zhDeviceAdaptersCopy } from "./deviceAdapters.ts";
import { zhDeviceDetailsCopy } from "./deviceDetails.ts";
import { zhDeviceTopologyCopy } from "./deviceTopology.ts";
import { zhHotkeysCopy } from "./hotkeys.ts";
import { zhMigrationCopy } from "./migration.ts";
import { zhMetricLabelsCopy } from "./metricLabels.ts";
import { zhMonitorCopy } from "./monitor.ts";
import { zhMonitorViewsCopy } from "./monitorViews.ts";
import { zhOptimizationCopy } from "./optimization.ts";
import { zhRuntimeAndReportsCopy } from "./runtimeAndReports.ts";
import { zhRuntimeErrorsCopy } from "./runtimeErrors.ts";
import { zhShellCopy } from "./shell.ts";
import { zhSoftwareCopy } from "./software.ts";
import { zhSoftwareActionsCopy } from "./softwareActions.ts";
import { zhSoftwareDetailCopy } from "./softwareDetail.ts";
import { zhStatusCopy } from "./status.ts";
import { zhStoresCopy } from "./stores.ts";
import { zhTaskCenterCopy } from "./taskCenter.ts";

// 中文基底：全应用文案的唯一权威形状，其他语言按同一形状提供翻译。
export const zhAppCopy = {
  ...zhShellCopy,
  ...zhSoftwareCopy,
  ...zhMonitorCopy,
  ...zhMonitorViewsCopy,
  ...zhStatusCopy,
  ...zhOptimizationCopy,
  ...zhDeviceTopologyCopy,
  ...zhDeviceDetailsCopy,
  ...zhDeviceAdaptersCopy,
  ...zhBackendMessagesCopy,
  ...zhDetailsCopy,
  ...zhSoftwareDetailCopy,
  ...zhSoftwareActionsCopy,
  ...zhMigrationCopy,
  ...zhHotkeysCopy,
  ...zhTaskCenterCopy,
  ...zhRuntimeAndReportsCopy,
  ...zhStoresCopy,
  ...zhRuntimeErrorsCopy,
  ...zhMetricLabelsCopy
};

export type AppCopy = typeof zhAppCopy;