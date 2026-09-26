import {
  buildLocalSystemStatusSubscriptionUrl,
  type LocalSystemStatus
} from "../../data/localSystem/localSystemStatusApi.ts";
import {
  buildControlActualStateSubscriptionUrl,
  controlActualStateDecoder
} from "../../data/control/controlActualApi.ts";
import type { ControlActualState } from "../../control/controlTypes.ts";
import { localSystemStatusDecoder } from
  "../../data/localSystem/localSystemStatusDecoder.ts";
import { buildDeviceTopologyStateSubscriptionUrl } from
  "../../features/deviceTopology/deviceTopologyApi.ts";
import { deviceTopologyStateDecoder } from
  "../../features/deviceTopology/deviceTopologyStateDecoder.ts";
import type { DeviceTopologySnapshotState } from "../../types.ts";
import type { BackendStartupCapabilities } from "../../types.ts";
import { getRuntimeCapabilities } from "../../data/runtimeCapabilities/runtimeCapabilitiesApi.ts";
import {
  buildResourceManagerSelfSchedulingSubscriptionUrl,
  type ResourceManagerSelfSchedulingSnapshot
} from "../../data/selfScheduling/resourceManagerSelfSchedulingApi.ts";
import { resourceManagerSelfSchedulingDecoder } from
  "../../data/selfScheduling/resourceManagerSelfSchedulingDecoder.ts";
import {
  buildOperationsSubscriptionUrl,
  type HostManagerOperationsState
} from "../../data/operations/operationsApi.ts";
import { operationsStateDecoder } from
  "../../data/operations/operationsStateDecoder.ts";
import {
  getAppSettings
} from "../../data/appSettings/appSettingsApi.ts";
import {
  buildResourceMonitorSubscriptionUrl,
  normalizeResourceMonitorQuery,
  resourceMonitorDecoder,
  type ResourceMonitorQuery
} from "../../data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceMonitorSnapshot } from "../../types.ts";
import type {
  DashboardSettingsResult,
  GpuSpecializedTelemetrySnapshot,
  GpuPerformanceScoreSnapshot,
  MetricDefinition,
  MetricSnapshot
} from "../../types.ts";
import {
  buildMetricSnapshotSubscriptionUrl,
  getDashboardSettingsSource,
  getMetricCatalogSource,
  normalizeMetricSnapshotQuery,
  type MetricSnapshotQuery
} from "../../data/monitor/monitorSourcesApi.ts";
import { metricSnapshotDecoder } from
  "../../data/monitor/monitorSourceDecoders.ts";
import type {
  CpuCoreResidencySnapshot,
  CpuExclusiveBindingSnapshot,
  CpuTopologySnapshot
} from "../../types.ts";
import {
  buildCpuResidencySubscriptionUrl,
  buildCpuTopologySubscriptionUrl,
  getCpuExclusiveBindingsSource,
} from "../../data/cpu/cpuSourcesApi.ts";
import {
  cpuResidencyDecoder,
  cpuTopologyDecoder
} from "../../data/cpu/cpuSourceDecoders.ts";
import type {
  CommittedAppSettingsResult
} from "../../data/appSettings/appSettingsResultDecoder.ts";
import type { RequestClient } from "../request/RequestClient.ts";
import type { BackendSubscriptionChannel } from
  "../push/BackendSubscriptionChannel.ts";
import {
  BackendCurrentValueSource,
  BackendPushValueSource,
  BackendPushValueSourceFamily,
  type CurrentValueSource,
  type PushValueSource,
  type PushValueSourceFamily
} from "../push/PushValueSourceFamily.ts";
import {
  getSoftwareMetadata,
  normalizeSoftwareMetadataQuery,
  type SoftwareMetadataQuery
} from "../../features/softwareMetadata/softwareMetadataApi.ts";
import type { SoftwareMetadataLookupResult } from
  "../../features/softwareMetadata/softwareMetadataDecoder.ts";
import { getOptimizationReports } from
  "../../data/optimization/optimizationReportsApi.ts";
import {
  buildHostManagerSmartStateSubscriptionUrl,
  hostManagerSmartStateDecoder
} from "../../data/optimization/hostManagerSmartStateSource.ts";
import type { OptimizationReportOverview } from "../../types.ts";
import type { HostManagerRollbackStateDocument } from "../../types.ts";
import {
  buildGpuSpecializedTelemetrySubscriptionUrl,
  getGpuPerformanceScoreSource,
  getGpuSchedulingModelSource,
  gpuSpecializedTelemetryDecoder,
  normalizeGpuSpecializedTelemetryQuery,
  normalizeGpuSchedulingModelQuery,
  type GpuSchedulingModelQuery,
  type GpuSchedulingModelSnapshot,
  type GpuSpecializedTelemetryQuery
} from "../../data/gpu/gpuSchedulingModelSource.ts";
import type { SourceFamily, SourceHandle } from "./SourceDescriptor.ts";
import {
  canonicalizeSourceQuery,
  type SourceRegistry
} from "./SourceRegistry.ts";

export interface FrontendSources {
  readonly appSettings: SourceHandle<CommittedAppSettingsResult>;
  readonly runtimeCapabilities: SourceHandle<BackendStartupCapabilities>;
  readonly selfScheduling: PushValueSource<ResourceManagerSelfSchedulingSnapshot>;
  readonly localSystemStatus: PushValueSource<LocalSystemStatus>;
  readonly controlActualState: PushValueSource<ControlActualState>;
  readonly deviceTopology: PushValueSource<DeviceTopologySnapshotState>;
  readonly operations: CurrentValueSource<HostManagerOperationsState>;
  readonly cpuTopology: PushValueSource<CpuTopologySnapshot | null>;
  readonly cpuResidency: PushValueSource<CpuCoreResidencySnapshot | null>;
  readonly smartCoordinatorState: PushValueSource<HostManagerRollbackStateDocument>;
  readonly cpuExclusiveBindings: SourceHandle<CpuExclusiveBindingSnapshot | null>;
  readonly metricCatalog: SourceHandle<MetricDefinition[]>;
  readonly dashboardSettings: SourceHandle<DashboardSettingsResult>;
  readonly optimizationReports: SourceHandle<OptimizationReportOverview>;
  readonly gpuPerformanceScores: SourceHandle<GpuPerformanceScoreSnapshot>;
  readonly metricSnapshot: PushValueSourceFamily<MetricSnapshotQuery, MetricSnapshot>;
  readonly resourceMonitor: PushValueSourceFamily<
    ResourceMonitorQuery,
    ResourceMonitorSnapshot
  >;
  readonly gpuSpecializedTelemetry: PushValueSourceFamily<
    GpuSpecializedTelemetryQuery,
    GpuSpecializedTelemetrySnapshot
  >;
  readonly gpuSchedulingModel: SourceFamily<
    GpuSchedulingModelQuery,
    GpuSchedulingModelSnapshot
  >;
  readonly softwareMetadata: SourceFamily<
    SoftwareMetadataQuery,
    SoftwareMetadataLookupResult
  >;
}

export function createFrontendSources(
  requestClient: RequestClient,
  sourceRegistry: SourceRegistry,
  subscriptionChannel: BackendSubscriptionChannel
): FrontendSources {
  return Object.freeze({
    appSettings: sourceRegistry.define<CommittedAppSettingsResult>({
      key: "app.settings",
      load: (signal) => getAppSettings(requestClient, signal),
      retainLastGood: true
    }),
    runtimeCapabilities: sourceRegistry.define<BackendStartupCapabilities>({
      key: "runtime.capabilities",
      load: (signal) => getRuntimeCapabilities(requestClient, signal),
      canRetainStale: () => false
    }),
    selfScheduling: new BackendPushValueSource<ResourceManagerSelfSchedulingSnapshot>({
      key: "resource-manager.self-scheduling",
      channel: subscriptionChannel,
      buildUrl: buildResourceManagerSelfSchedulingSubscriptionUrl,
      decoder: resourceManagerSelfSchedulingDecoder
    }),
    localSystemStatus: new BackendPushValueSource<LocalSystemStatus>({
      key: "local-system.status",
      channel: subscriptionChannel,
      buildUrl: buildLocalSystemStatusSubscriptionUrl,
      decoder: localSystemStatusDecoder
    }),
    // 控制面的实际状态：机器现在实际是什么样，和期望状态并排显示。
    controlActualState: new BackendPushValueSource<ControlActualState>({
      key: "control.actual",
      channel: subscriptionChannel,
      buildUrl: buildControlActualStateSubscriptionUrl,
      decoder: controlActualStateDecoder
    }),
    deviceTopology: new BackendPushValueSource<DeviceTopologySnapshotState>({
      key: "device-topology.state",
      channel: subscriptionChannel,
      buildUrl: buildDeviceTopologyStateSubscriptionUrl,
      decoder: deviceTopologyStateDecoder
    }),
    operations: new BackendCurrentValueSource<HostManagerOperationsState>({
      key: "host-manager.operations.state",
      channel: subscriptionChannel,
      buildUrl: buildOperationsSubscriptionUrl,
      decoder: operationsStateDecoder
    }),
    cpuTopology: new BackendPushValueSource<CpuTopologySnapshot | null>({
      key: "cpu.topology",
      channel: subscriptionChannel,
      buildUrl: buildCpuTopologySubscriptionUrl,
      decoder: cpuTopologyDecoder
    }),
    cpuResidency: new BackendPushValueSource<CpuCoreResidencySnapshot | null>({
      key: "cpu.residency",
      channel: subscriptionChannel,
      buildUrl: buildCpuResidencySubscriptionUrl,
      decoder: cpuResidencyDecoder
    }),
    smartCoordinatorState: new BackendPushValueSource<HostManagerRollbackStateDocument>({
      key: "host-manager.smart-state",
      channel: subscriptionChannel,
      buildUrl: buildHostManagerSmartStateSubscriptionUrl,
      decoder: hostManagerSmartStateDecoder
    }),
    cpuExclusiveBindings: sourceRegistry.define<CpuExclusiveBindingSnapshot | null>({
      key: "cpu.exclusive-bindings",
      load: (signal) => getCpuExclusiveBindingsSource(requestClient, signal),
      selectCapturedAt: (value) => value?.capturedAt ?? null
    }),
    metricCatalog: sourceRegistry.define<MetricDefinition[]>({
      key: "monitor.metric-catalog",
      load: (signal) => getMetricCatalogSource(requestClient, signal),
      retainLastGood: false
    }),
    dashboardSettings: sourceRegistry.define<DashboardSettingsResult>({
      key: "monitor.dashboard-settings",
      load: (signal) => getDashboardSettingsSource(requestClient, signal),
      selectCapturedAt: (value) => value.updatedAt ?? null
    }),
    optimizationReports: sourceRegistry.define<OptimizationReportOverview>({
      key: "optimization.reports",
      load: (signal) => getOptimizationReports(requestClient, signal),
      retainLastGood: true,
      selectCapturedAt: (value) =>
        value.status.lastEvaluationAt ?? value.capturedAt
    }),
    gpuPerformanceScores: sourceRegistry.define<GpuPerformanceScoreSnapshot>({
      key: "gpu.performance-scores",
      load: (signal) => getGpuPerformanceScoreSource(requestClient, signal),
      retainLastGood: false,
      selectCapturedAt: (value) => value.capturedAt
    }),
    metricSnapshot: new BackendPushValueSourceFamily<
      MetricSnapshotQuery,
      MetricSnapshot
    >({
      key: "monitor.metric-snapshot",
      channel: subscriptionChannel,
      buildUrl: (query, intervalMs) => buildMetricSnapshotSubscriptionUrl(
        normalizeMetricSnapshotQuery(query),
        intervalMs),
      canonicalizeQuery: (query) => canonicalizeSourceQuery(
        normalizeMetricSnapshotQuery(query)).key,
      decoder: metricSnapshotDecoder
    }),
    resourceMonitor: new BackendPushValueSourceFamily<
      ResourceMonitorQuery,
      ResourceMonitorSnapshot
    >({
      key: "resource-monitor.snapshot",
      channel: subscriptionChannel,
      buildUrl: (query, intervalMs) => buildResourceMonitorSubscriptionUrl(
        normalizeResourceMonitorQuery(query),
        intervalMs),
      canonicalizeQuery: (query) => canonicalizeSourceQuery(
        normalizeResourceMonitorQuery(query)).key,
      decoder: resourceMonitorDecoder
    }),
    gpuSpecializedTelemetry: new BackendPushValueSourceFamily<
      GpuSpecializedTelemetryQuery,
      GpuSpecializedTelemetrySnapshot
    >({
      key: "monitor.gpu-specialized",
      channel: subscriptionChannel,
      buildUrl: (query, intervalMs) =>
        buildGpuSpecializedTelemetrySubscriptionUrl(
          normalizeGpuSpecializedTelemetryQuery(query),
          intervalMs),
      canonicalizeQuery: (query) => canonicalizeSourceQuery(
        normalizeGpuSpecializedTelemetryQuery(query)).key,
      decoder: gpuSpecializedTelemetryDecoder
    }),
    gpuSchedulingModel: sourceRegistry.defineFamily<
      GpuSchedulingModelQuery,
      GpuSchedulingModelSnapshot
    >({
      key: "details.gpu-scheduling-model",
      canonicalizeQuery: (query) =>
        canonicalizeSourceQuery(normalizeGpuSchedulingModelQuery(query)),
      load: (query, signal) =>
        getGpuSchedulingModelSource(requestClient, query, signal),
      retainLastGood: false,
      selectCapturedAt: (value) => value.capturedAt
    }),
    softwareMetadata: sourceRegistry.defineFamily<
      SoftwareMetadataQuery,
      SoftwareMetadataLookupResult
    >({
      key: "software.metadata.detail",
      canonicalizeQuery: (query) =>
        canonicalizeSourceQuery(normalizeSoftwareMetadataQuery(query)),
      load: (query, signal) =>
        getSoftwareMetadata(requestClient, query, signal)
    })
  });
}
