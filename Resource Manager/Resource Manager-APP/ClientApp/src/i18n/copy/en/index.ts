import { enBackendMessagesCopy } from "./backendMessages.ts";
import { enDetailsCopy } from "./details.ts";
import { enDeviceAdaptersCopy } from "./deviceAdapters.ts";
import { enDeviceDetailsCopy } from "./deviceDetails.ts";
import { enDeviceTopologyCopy } from "./deviceTopology.ts";
import { enHotkeysCopy } from "./hotkeys.ts";
import { enMigrationCopy } from "./migration.ts";
import { enMetricLabelsCopy } from "./metricLabels.ts";
import { enMonitorCopy } from "./monitor.ts";
import { enMonitorViewsCopy } from "./monitorViews.ts";
import { enOptimizationCopy } from "./optimization.ts";
import { enRuntimeAndReportsCopy } from "./runtimeAndReports.ts";
import { enRuntimeErrorsCopy } from "./runtimeErrors.ts";
import { enShellCopy } from "./shell.ts";
import { enSoftwareCopy } from "./software.ts";
import { enSoftwareActionsCopy } from "./softwareActions.ts";
import { enSoftwareDetailCopy } from "./softwareDetail.ts";
import { enStatusCopy } from "./status.ts";
import { enStoresCopy } from "./stores.ts";
import { enTaskCenterCopy } from "./taskCenter.ts";
import type { AppCopy } from "../zh/index.ts";

// 英文基底：非中文语言在此之上合并各自补丁。
export const enAppCopy: AppCopy = {
  ...enShellCopy,
  ...enSoftwareCopy,
  ...enMonitorCopy,
  ...enMonitorViewsCopy,
  ...enStatusCopy,
  ...enOptimizationCopy,
  ...enDeviceTopologyCopy,
  ...enDeviceDetailsCopy,
  ...enDeviceAdaptersCopy,
  ...enBackendMessagesCopy,
  ...enDetailsCopy,
  ...enSoftwareDetailCopy,
  ...enSoftwareActionsCopy,
  ...enMigrationCopy,
  ...enHotkeysCopy,
  ...enTaskCenterCopy,
  ...enRuntimeAndReportsCopy,
  ...enStoresCopy,
  ...enRuntimeErrorsCopy,
  ...enMetricLabelsCopy
};