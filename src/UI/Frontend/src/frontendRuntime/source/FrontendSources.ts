import type { CpuObservationResult } from "../../data/cpu/cpuSourceDecoders.ts";
import { createControlSources } from "../../data/control/controlSources.ts";
import { createLoginStartupSource } from "../../data/loginStartup/loginStartupSource.ts";
import { createSettingsReadSources } from "../../data/settingsReadSources.ts";
import { metricCurrentItems } from "../../data/monitor/metricCurrentItems.ts";
import { metricFieldCurrentItems } from "../../data/metricFields/metricFieldCurrentItems.ts";
import { gpuSpecializedCurrentItems } from "../../data/gpu/gpuSpecializedCurrentItems.ts";
import { resourceMonitorCurrentItems, resourceDetailsCurrentItems } from "../../data/resourceMonitor/resourceCurrentItems.ts";
import { getManagementComponents, getManagementSoftware, getManagementBrowserRuntimes } from "../../data/managementInventory.ts";
import type { ManagedComponent, PerformanceOverlaySettings, SoftwareRecord } from "../../types.ts";
import { listEnabledPerformanceOverlays } from "../../api.ts";
import type { BrowserRuntimeSnapshot } from "../../features/browserRuntimes/browserRuntimeTypes.ts";
import {
  buildProductUpdateStatusSubscriptionUrl,
  productUpdateStatusDecoder
} from "../../data/productUpdateStatus.ts";
import type { ProductUpdateState } from "../../features/settings/productUpdatePresentation.ts";
import {
  buildKernelLeakReportSubscriptionUrl,
  kernelLeakReportDecoder,
  type KernelLeakReport
} from "../../data/kernelLeaks/kernelLeakReport.ts";
import {
  buildSystemEventsSubscriptionUrl,
  systemEventsReportDecoder,
  type SystemEventsReport
} from "../../data/freezeReports/systemEvents.ts";
import {
  buildFreezeRecorderSubscriptionUrl,
  freezeRecorderStatusDecoder,
  type FreezeRecorderStatus
} from "../../data/freezeReports/freezeRecorder.ts";
import {
  backendNotificationListDecoder, backendNotificationsSubscriptionPath, type BackendNotificationList
} from "../../data/notifications/backendNotifications.ts";
import {
  crashReportListDecoder, crashReportsSubscriptionPath, type CrashReportList
} from "../../data/crashReports/crashReports.ts";
import {
  buildLocalSystemStatusSubscriptionUrl,
  type LocalSystemStatus
} from "../../data/localSystem/localSystemStatusApi.ts";
import {
  buildControlActualStateSubscriptionUrl,
  controlActualStateDecoder
} from "../../data/control/controlActualApi.ts";
import type { ControlActualState } from "../../features/control/controlTypes.ts";
import { localSystemStatusDecoder } from
  "../../data/localSystem/localSystemStatusDecoder.ts";
import {
  buildDeviceTopologyFieldsSubscriptionUrl,
  buildDeviceTopologyStateSubscriptionUrl,
  normalizeInformationFieldSelection
} from "../../features/deviceTopology/deviceTopologyApi.ts";
import type { SubscriptionFieldReference } from "../push/BackendSubscriptionChannel.ts";
import {
  deviceTopologyInformationDecoder,
  deviceTopologyStateDecoder
} from "../../features/deviceTopology/deviceTopologyStateDecoder.ts";
import type { DeviceTopologyInformationSnapshot, DeviceTopologySnapshotState } from "../../types.ts";
import type { BackendStartupCapabilities } from "../../types.ts";
import { getRuntimeCapabilities } from "../../data/runtimeCapabilities/runtimeCapabilitiesApi.ts";
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
  buildResourceProcessDetailsSubscriptionUrl,
  normalizeResourceBreakdownQuery,
  normalizeResourceMonitorQuery,
  resourceMonitorDecoder,
  resourceProcessDetailsDecoder,
  type ResourceBreakdownQuery,
  type ResourceMonitorQuery
} from "../../data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceMonitorSnapshot, ResourceProcessDetailsSnapshot } from "../../types.ts";
import { getProcessFieldCatalog, type ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
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
  cpuResidencySubscriptionDecoder,
  cpuTopologySubscriptionDecoder
} from "../../data/cpu/cpuSourceDecoders.ts";
import type {
  CommittedAppSettingsResult
} from "../../data/appSettings/appSettingsResultDecoder.ts";
import type { RequestClient } from "../request/RequestClient.ts";
import {
  buildManagementInventoryRevisionSubscriptionUrl,
  managementInventoryRevisionDecoder,
  type ManagementInventoryRevisionSnapshot
} from "../../data/managementInventoryRevision.ts";
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
import { buildAccessRequestPendingSubscriptionUrl } from
  "../../features/trustedAccess/trustedAccessApi.ts";
import { accessRequestPendingSnapshotDecoder } from
  "../../features/trustedAccess/trustedAccessDecoder.ts";
import type { AccessRequestPendingSnapshot } from
  "../../features/trustedAccess/trustedAccessTypes.ts";
import {
  buildHostManagerSmartStateSubscriptionUrl,
  hostManagerSmartStateDecoder
} from "../../data/optimization/hostManagerSmartStateSource.ts";
import {
  buildHostManagerSmartStatusSubscriptionUrl,
  hostManagerSmartStatusDecoder
} from "../../data/optimization/hostManagerSmartStatusSource.ts";
import type { OptimizationReportOverview } from "../../types.ts";
import {
  getRecording,
  listRecordings, getRecordingTargets, listProfiles,
  type RecordingHeader,
  type RecordingProfile,
  type RecordingTarget,
  type RecordingReport
} from "../../features/targetedReport/targetedRecordingApi.ts";
import type {
  HostManagerRollbackStateDocument,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
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
import { metricCatalogPushDecoder, type MetricCatalogPush } from "../../data/monitor/metricCatalogPush.ts";
import {
  buildMetricFieldCatalogSubscriptionUrl,
  buildMetricFieldSubscriptionUrl,
  metricFieldDevicesDecoder,
  normalizeMetricFieldQuery,
  type MetricFieldDevice,
  type MetricFieldQuery
} from "../../data/metricFields/metricFieldSource.ts";
import type { SourceFamily, SourceHandle } from "./SourceDescriptor.ts";
import { createMemoryProtectionSubscriptionSource } from "../../data/memoryProtection/memoryProtectionSubscriptionSource.ts";
import type { HostMemoryProtectionView } from "../../data/memoryProtection/memoryProtectionTypes.ts";
import { createCpuMonopolySubscriptionSource } from "../../data/cpuMonopolyProtection/cpuMonopolySubscriptionSource.ts";
import type { CpuMonopolyView } from "../../data/cpuMonopolyProtection/cpuMonopolyProtection.ts";
import { readDeviceActionRecords, deviceActionRecordsPageSize, type DeviceActionRecordsView } from "../../data/deviceActions/deviceActions.ts";
import type { AiGatewayCredentialView } from "../../types.ts";
import { getAiGatewayCredentials } from "../../data/aiGatewayCredentials.ts";
import { getCpuBaselineRatio, type CpuBaselineRatioSettings } from "../../data/cpu/cpuBaselineRatioApi.ts";
import {
  canonicalizeSourceQuery,
  type SourceRegistry
} from "./SourceRegistry.ts";

export interface FrontendSources {
  readonly memoryProtection: CurrentValueSource<HostMemoryProtectionView>;
  readonly cpuMonopolyProtection: CurrentValueSource<CpuMonopolyView>;
  readonly deviceActionRecords: SourceHandle<DeviceActionRecordsView>;
  readonly control: ReturnType<typeof createControlSources>;
  readonly loginStartup: SourceHandle<boolean>;
  readonly settingsReads: ReturnType<typeof createSettingsReadSources>;
  readonly managementComponents: SourceHandle<ManagedComponent[]>;
  readonly managementSoftware: SourceHandle<SoftwareRecord[]>;
  readonly performanceOverlays: SourceHandle<PerformanceOverlaySettings[]>;
  readonly managementBrowserRuntimes: SourceHandle<BrowserRuntimeSnapshot>;
  readonly aiGatewayCredentials: SourceHandle<AiGatewayCredentialView[]>;
  readonly resourceProcessDetails: PushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>;
  readonly appSettings: SourceHandle<CommittedAppSettingsResult>;
  readonly runtimeCapabilities: SourceHandle<BackendStartupCapabilities>;
  readonly localSystemStatus: PushValueSource<LocalSystemStatus>;
  readonly controlActualState: PushValueSource<ControlActualState>;
  /** Topology structure and health: current value on subscribe, then changes; includes only Fixed and EventDriven information fields. */
  readonly deviceTopology: PushValueSource<DeviceTopologySnapshotState>;
  /** Selected device fields, including Sampled values. Requires a nonempty selection; emits current values and subsequent changes. */
  readonly deviceTopologyFields: PushValueSourceFamily<readonly SubscriptionFieldReference[], DeviceTopologyInformationSnapshot>;
  readonly operations: CurrentValueSource<HostManagerOperationsState>;
  readonly managementInventoryRevision: CurrentValueSource<ManagementInventoryRevisionSnapshot>;
  readonly accessRequests: CurrentValueSource<AccessRequestPendingSnapshot>;
  readonly productUpdateStatus: CurrentValueSource<ProductUpdateState>;
  /** Kernel leak reports; subscribe only while the report view is visible. */
  readonly kernelLeakReport: CurrentValueSource<KernelLeakReport>;
  /** System event reports; subscribe only while the report view is visible. */
  readonly systemEvents: CurrentValueSource<SystemEventsReport>;
  /** Freeze recorder status and boot review summary; subscribe only while the recorder view is visible. */
  readonly freezeRecorder: CurrentValueSource<FreezeRecorderStatus>;
  /** Backend notifications, subscribed while the interface is visible. */
  readonly backendNotifications: CurrentValueSource<BackendNotificationList>;
  /** Complete crash record list on subscribe and change; subscribe only while its view is visible. */
  readonly crashReports: CurrentValueSource<CrashReportList>;
  readonly cpuTopology: PushValueSource<CpuTopologySnapshot | null>;
  readonly cpuResidency: PushValueSource<CpuObservationResult<CpuCoreResidencySnapshot>>;
  readonly smartCoordinatorState: PushValueSource<HostManagerRollbackStateDocument>;
  /** Backend scheduling status for the optimization view. */
  readonly smartCoordinatorStatus: PushValueSource<HostManagerSmartCoordinatorStatus>;
  readonly cpuExclusiveBindings: SourceHandle<CpuObservationResult<CpuExclusiveBindingSnapshot>>;
  readonly cpuBaselineRatio: SourceHandle<CpuBaselineRatioSettings | null>;
  readonly metricCatalog: SourceHandle<MetricDefinition[]>;
  /** Backend process field catalog used to select resource table sources. */
  readonly processFieldCatalog: SourceHandle<readonly ProcessMetricField[]>;
  readonly dashboardSettings: SourceHandle<DashboardSettingsResult>;
  readonly optimizationReports: SourceHandle<OptimizationReportOverview>;
  readonly targetedRecordings: SourceHandle<RecordingHeader[]>;
  readonly targetedRecordingTargets: SourceHandle<RecordingTarget[]>;
  readonly targetedRecordingProfiles: SourceHandle<RecordingProfile[]>;
  readonly targetedRecordingReport: SourceFamily<
    { readonly recordingId: string },
    RecordingReport
  >;
  readonly gpuPerformanceScores: SourceHandle<GpuPerformanceScoreSnapshot>;
  readonly metricSnapshot: PushValueSourceFamily<MetricSnapshotQuery, MetricSnapshot>;
  /** One frame containing the catalog revision, device field bindings and metric catalog. */
  readonly metricFieldCatalog: PushValueSource<MetricCatalogPush>;
  /** Device field values subscribed by field set. */
  readonly metricFields: PushValueSourceFamily<MetricFieldQuery, readonly MetricFieldDevice[]>;
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
    memoryProtection: createMemoryProtectionSubscriptionSource(subscriptionChannel),
    cpuMonopolyProtection: createCpuMonopolySubscriptionSource(subscriptionChannel),
    deviceActionRecords: sourceRegistry.define<DeviceActionRecordsView>({
      key: "device-actions.records",
      load: async signal => {
        const page = await readDeviceActionRecords(requestClient, { limit: deviceActionRecordsPageSize }, signal);
        return { records: page.records, hasMore: page.records.length === deviceActionRecordsPageSize };
      },
      retainLastGood: true
    }),
    control: createControlSources(sourceRegistry, requestClient),
    loginStartup: createLoginStartupSource(sourceRegistry),
    settingsReads: createSettingsReadSources(sourceRegistry, requestClient),
    managementComponents: sourceRegistry.define({
      key: "management.components",
      load: signal => getManagementComponents(requestClient, signal),
      retainLastGood: false
    }),
    managementSoftware: sourceRegistry.define({
      key: "management.software",
      load: (signal, options) => getManagementSoftware(requestClient, signal, options),
      retainLastGood: false
    }),
    // Overlay overview uses backend-enabled software and its registered launch modules.
    performanceOverlays: sourceRegistry.define<PerformanceOverlaySettings[]>({
      key: "settings.performance-overlays",
      load: signal => listEnabledPerformanceOverlays(requestClient, signal),
      retainLastGood: false
    }),
    managementBrowserRuntimes: sourceRegistry.define({
      key: "management.browser-runtimes",
      load: (signal, options) => getManagementBrowserRuntimes(requestClient, signal, options),
      retainLastGood: false,
      selectCapturedAt: value => value.capturedAt
    }),
    aiGatewayCredentials: sourceRegistry.define<AiGatewayCredentialView[]>({
      key: "ai-gateway.credentials",
      load: signal => getAiGatewayCredentials(requestClient, signal),
      retainLastGood: false
    }),
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
    localSystemStatus: new BackendPushValueSource<LocalSystemStatus>({
      key: "local-system.status",
      channel: subscriptionChannel,
      buildUrl: buildLocalSystemStatusSubscriptionUrl,
      decoder: localSystemStatusDecoder
    }),
    // Actual control state is displayed alongside the desired state.
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
    deviceTopologyFields: new BackendPushValueSourceFamily<readonly SubscriptionFieldReference[], DeviceTopologyInformationSnapshot>({
      key: "device-topology.fields",
      channel: subscriptionChannel,
      buildUrl: (_fields, intervalMs) => buildDeviceTopologyFieldsSubscriptionUrl(intervalMs),
      buildFields: normalizeInformationFieldSelection,
      canonicalizeQuery: (fields) => JSON.stringify(normalizeInformationFieldSelection(fields)),
      decoder: deviceTopologyInformationDecoder
    }),
    operations: new BackendCurrentValueSource<HostManagerOperationsState>({
      key: "host-manager.operations.state",
      channel: subscriptionChannel,
      buildUrl: buildOperationsSubscriptionUrl,
      decoder: operationsStateDecoder
    }),
    productUpdateStatus: new BackendCurrentValueSource<ProductUpdateState>({
      key: "updates.product.status",
      channel: subscriptionChannel,
      buildUrl: buildProductUpdateStatusSubscriptionUrl,
      decoder: productUpdateStatusDecoder
    }),
    kernelLeakReport: new BackendCurrentValueSource<KernelLeakReport>({
      key: "kernel-leaks.report",
      channel: subscriptionChannel,
      buildUrl: buildKernelLeakReportSubscriptionUrl,
      decoder: kernelLeakReportDecoder
    }),
    systemEvents: new BackendCurrentValueSource<SystemEventsReport>({
      key: "system-events.report",
      channel: subscriptionChannel,
      buildUrl: buildSystemEventsSubscriptionUrl,
      decoder: systemEventsReportDecoder
    }),
    freezeRecorder: new BackendCurrentValueSource<FreezeRecorderStatus>({
      key: "freeze-recorder.status",
      channel: subscriptionChannel,
      buildUrl: buildFreezeRecorderSubscriptionUrl,
      decoder: freezeRecorderStatusDecoder
    }),
    backendNotifications: new BackendCurrentValueSource<BackendNotificationList>({
      key: "notifications.list",
      channel: subscriptionChannel,
      buildUrl: () => backendNotificationsSubscriptionPath,
      decoder: backendNotificationListDecoder
    }),
    crashReports: new BackendCurrentValueSource<CrashReportList>({
      key: "crash-reports.list",
      channel: subscriptionChannel,
      buildUrl: () => crashReportsSubscriptionPath,
      decoder: crashReportListDecoder
    }),
    managementInventoryRevision: new BackendCurrentValueSource<ManagementInventoryRevisionSnapshot>({
      key: "management.inventory-revision",
      channel: subscriptionChannel,
      buildUrl: buildManagementInventoryRevisionSubscriptionUrl,
      decoder: managementInventoryRevisionDecoder
    }),
    accessRequests: new BackendCurrentValueSource<AccessRequestPendingSnapshot>({
      key: "access-requests.pending",
      channel: subscriptionChannel,
      buildUrl: () => buildAccessRequestPendingSubscriptionUrl(),
      decoder: accessRequestPendingSnapshotDecoder
    }),
    cpuTopology: new BackendPushValueSource<CpuTopologySnapshot | null>({
      key: "cpu.topology",
      channel: subscriptionChannel,
      buildUrl: buildCpuTopologySubscriptionUrl,
      decoder: cpuTopologySubscriptionDecoder
    }),
    cpuResidency: new BackendPushValueSource<CpuObservationResult<CpuCoreResidencySnapshot>>({
      key: "cpu.residency",
      channel: subscriptionChannel,
      buildUrl: buildCpuResidencySubscriptionUrl,
      decoder: cpuResidencySubscriptionDecoder
    }),
    smartCoordinatorState: new BackendPushValueSource<HostManagerRollbackStateDocument>({
      key: "host-manager.smart-state",
      channel: subscriptionChannel,
      buildUrl: buildHostManagerSmartStateSubscriptionUrl,
      decoder: hostManagerSmartStateDecoder
    }),
    smartCoordinatorStatus: new BackendPushValueSource<HostManagerSmartCoordinatorStatus>({
      key: "host-manager.smart-status",
      channel: subscriptionChannel,
      buildUrl: buildHostManagerSmartStatusSubscriptionUrl,
      decoder: hostManagerSmartStatusDecoder
    }),
    cpuBaselineRatio: sourceRegistry.define<CpuBaselineRatioSettings | null>({
      key: "cpu.baseline-ratio",
      load: (signal) => getCpuBaselineRatio(requestClient, signal),
      retainLastGood: false
    }),
    cpuExclusiveBindings: sourceRegistry.define<CpuObservationResult<CpuExclusiveBindingSnapshot>>({
      key: "cpu.exclusive-bindings",
      load: (signal) => getCpuExclusiveBindingsSource(requestClient, signal),
      selectCapturedAt: (value) => "state" in value ? null : value.capturedAt,
      selectUnavailableReason: (value) => "state" in value ? value.reasonCode : null
    }),
    metricCatalog: sourceRegistry.define<MetricDefinition[]>({
      key: "monitor.metric-catalog",
      load: (signal) => getMetricCatalogSource(requestClient, signal),
      retainLastGood: false
    }),
    processFieldCatalog: sourceRegistry.define<readonly ProcessMetricField[]>({
      key: "resource-breakdown.process-field-catalog",
      load: (signal) => getProcessFieldCatalog(requestClient, signal),
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
    targetedRecordingTargets: sourceRegistry.define<RecordingTarget[]>({
      key: "targeted-recordings.targets",
      load: (signal) => getRecordingTargets(requestClient, signal),
      retainLastGood: false
    }),
    targetedRecordingProfiles: sourceRegistry.define<RecordingProfile[]>({
      key: "targeted-recordings.profiles",
      load: (signal) => listProfiles(requestClient, signal),
      retainLastGood: false
    }),
    targetedRecordings: sourceRegistry.define<RecordingHeader[]>({
      key: "targeted-recordings.list",
      load: (signal) => listRecordings(requestClient, signal),
      retainLastGood: false
    }),
    targetedRecordingReport: sourceRegistry.defineFamily<
      { readonly recordingId: string },
      RecordingReport
    >({
      key: "targeted-recordings.report",
      canonicalizeQuery: (query) => {
        const recordingId = query.recordingId.trim();
        if (!recordingId) {
          throw new Error("A recording ID is required.");
        }
        const value = Object.freeze({ recordingId });
        return { key: canonicalizeSourceQuery(value).key, value };
      },
      load: (query, signal) => getRecording(requestClient, query.recordingId, signal),
      retainLastGood: false
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
      decoder: metricSnapshotDecoder,
      currentItems: metricCurrentItems
    }),
    metricFieldCatalog: new BackendPushValueSource<MetricCatalogPush>({
      key: "metric-fields.catalog",
      channel: subscriptionChannel,
      buildUrl: buildMetricFieldCatalogSubscriptionUrl,
      decoder: metricCatalogPushDecoder
    }),
    metricFields: new BackendPushValueSourceFamily<MetricFieldQuery, readonly MetricFieldDevice[]>({
      key: "metric-fields.devices",
      channel: subscriptionChannel,
      buildUrl: buildMetricFieldSubscriptionUrl,
      canonicalizeQuery: (query) => canonicalizeSourceQuery(normalizeMetricFieldQuery(query)).key,
      decoder: metricFieldDevicesDecoder,
      currentItems: metricFieldCurrentItems
    }),
    resourceProcessDetails: new BackendPushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>({
      key: "resource-monitor.process-details",
      channel: subscriptionChannel,
      buildUrl: (query, intervalMs) => buildResourceProcessDetailsSubscriptionUrl(normalizeResourceBreakdownQuery(query), intervalMs),
      canonicalizeQuery: (query) => canonicalizeSourceQuery(normalizeResourceBreakdownQuery(query)).key,
      decoder: resourceProcessDetailsDecoder,
      currentItems: resourceDetailsCurrentItems
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
      decoder: resourceMonitorDecoder,
      currentItems: resourceMonitorCurrentItems
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
      decoder: gpuSpecializedTelemetryDecoder,
      currentItems: gpuSpecializedCurrentItems
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
