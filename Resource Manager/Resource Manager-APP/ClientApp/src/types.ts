export type PageId = "monitor" | "components" | "optimization" | "details" | "settings";

export interface BackendMessage {
  /** 消息域，见后端 BackendMessageDomains。 */
  domain: number;
  /** 该域内的消息码。 */
  code: number;
  /** 渲染这条消息需要的事实参数，按顺序。 */
  args: string[];
}

export interface BackendStartupCapabilities {
  profileId: string;
  readOnly: boolean;
  mutablePersistence: boolean;
  legacyPersistenceImport: boolean;
  gpuLaunchInterceptionReconciliation: boolean;
  runtimeEffectOwners: boolean;
  publicServiceCoordination: boolean;
  optimizationRuntime: boolean;
  sharedResourceOwnership: boolean;
}
export type ManagementRole = "Dependency" | "Support";
export type ManagementKind = ManagementRole | "Adapted" | "Controlled" | "Unconfirmed" | "Game" | "HighPerformance" | "Other";
export type ManualSoftwareKind = "Adapted" | "Game" | "HighPerformance" | "Other";
export type ResourceScaleMode = "capacity" | "active";
export type ResourceTableViewMode = "software" | "process" | "performance";
export type AppThemeMode = "system" | "light" | "dark" | "lowContrast";
export type AppAdaptiveBooleanMode = "auto" | "enabled" | "disabled";
export type AppFrontendHiddenRefreshMode = "auto" | "pauseWhenHidden" | "continueWhenHidden";
export type AppAnimationMode = "auto" | "normal" | "none" | "ultra";
export type AppBarColorMode = "type" | "distinct";
export type AppFontSmoothing = "auto" | "system" | "grayscale" | "disabled";
export type AppLanguageMode =
  | "system"
  | "zh-CN"
  | "zh-TW"
  | "en-US"
  | "ja-JP"
  | "ko-KR"
  | "fr-FR"
  | "de-DE"
  | "es-ES"
  | "es-MX"
  | "pt-BR"
  | "pt-PT"
  | "ru-RU"
  | "uk-UA"
  | "pl-PL"
  | "tr-TR"
  | "it-IT"
  | "nl-NL"
  | "sv-SE"
  | "fi-FI"
  | "da-DK"
  | "nb-NO"
  | "cs-CZ"
  | "hu-HU"
  | "ro-RO"
  | "el-GR"
  | "he-IL"
  | "ar-SA"
  | "hi-IN"
  | "id-ID"
  | "vi-VN"
  | "th-TH";
export type AppOptimizationMode = "normal" | "limited" | "smart";
export type ResourceManagerSelfCpuGrade = "normal" | "optimize";
export type ResourceManagerSelfGpuGrade = "normal" | "optimize";
export type AppGpuPerformanceUseCase = "general" | "ai" | "gaming";
export type AppPresetNumericSettingMode = "aotu" | "preset" | "custom";
export type AppLogicRefreshIntervalPreset = "responsive" | "balanced" | "lowPower" | "quiet";
export type AppLogicRefreshIntervalKey =
  | "monitor"
  | "resourceTable"
  | "management"
  | "discovery"
  | "optimization"
  | "localSystem";
export type SettingsSection = "performance" | "appearance" | "systemIntegration" | "debug" | "credits";

export interface MetricDefinition {
  id: string;
  label: string;
  group: string;
  unit: string;
  preferredSlot: string;
  detail?: string | null;
  requiredComponentId?: string | null;
  requiredComponentName?: string | null;
  selectable?: boolean;
  disabledReason?: BackendMessage | null;
  scopeKind?: string | null;
  scopeKey?: string | null;
}

export interface MetricValue {
  id?: string;
  label?: string;
  group?: string;
  displayValue?: string;
  numericValue?: number | null;
  unit?: string;
  percent?: number | null;
  detail?: string | null;
}

export interface MetricSnapshot {
  version: 4;
  capturedAt: string | null;
  items: Record<string, MetricValue>;
}

export interface GpuSpecializedTelemetrySnapshot {
  version: 2;
  capturedAt: string;
  adapters: GpuSpecializedTelemetryAdapter[];
}

export interface GpuSpecializedTelemetryAdapter {
  adapterIndex: number;
  adapterName: string;
  vendorId: string;
  architecture: string;
  counters: GpuSpecializedTelemetryCounter[];
}

export interface GpuSpecializedTelemetryCounter {
  counterId: string;
  displayName: string;
  counterClass: number;
  value?: number | null;
  unit: string;
  providerId: string;
  isIntrusive: boolean;
  engineName?: string | null;
}

export interface DashboardCardSettings {
  id: string;
  main?: string | null;
  small: string[];
  mainBinding?: DashboardMetricBinding | null;
  smallBindings?: Array<DashboardMetricBinding | null> | null;
}

export interface DashboardMetricBinding {
  scopeKind: string;
  scopeKey: string;
}

export interface ResourceBarSettings {
  id: string;
  metricId: string;
  scaleMode: ResourceScaleMode;
  binding?: DashboardMetricBinding | null;
}

export interface DashboardSettings {
  version: number;
  cards: DashboardCardSettings[];
  resourceBars: ResourceBarSettings[];
  resourceTableColumns: ResourceTableColumnSettings[];
  resourceTableProcessColumns: ResourceTableColumnSettings[];
}

export interface ResourceTableColumnSettings {
  id: string;
  visible: boolean;
  width?: number | null;
  binding?: DashboardMetricBinding | null;
}

export interface DashboardSettingsResult {
  settings: DashboardSettings;
  updatedAt: string;
  storagePath: string;
  source: {
    kind: number | string;
    sourceVersion: number;
    inputSha256: string;
    effectiveSha256: string;
    rewritePerformed: boolean;
    recoveryDisposition: string;
    recoveryArtifactPath?: string | null;
    lastKnownGoodPath?: string | null;
  };
}

export interface AppSettings {
  version?: string;
  performance?: AppPerformanceSettings;
  appearance?: AppAppearanceSettings;
  systemIntegration?: AppSystemIntegrationSettings;
  debug?: AppDebugSettings;
  publicService?: AppLocalPublicServiceSettings;
  aiModelService?: AppAiModelServiceSettings;
}

export interface AppPerformanceSettings {
  smartMonitoringEnabled: boolean;
  monitoringIdleSeconds: number;
  optimizationMode: AppOptimizationMode;
  pauseFrontendRefreshWhenHiddenInNormalMode: boolean;
  preciseGpuPlacementEnabled: boolean;
  vramMoveDownPhysicalMemoryDangerPercent: number;
  physicalMemoryMoveDownVirtualMemoryDangerPercent: number;
  physicalMemoryAutomaticCleanupPercent: number;
  virtualMemoryAutomaticCleanupPercent: number;
  physicalMemoryOptimizationTargetUsagePercent: number;
  virtualMemoryOptimizationTargetUsagePercent: number;
  gpuPerformanceUseCases: AppGpuPerformanceUseCase[];
  smartMonitoringMode: AppAdaptiveBooleanMode;
  /** 开启后，处于自动调度模式的机制可以按本机事实做额外优化。 */
  automaticSchedulingOptimizationsEnabled: boolean;
  frontendHiddenRefreshMode: AppFrontendHiddenRefreshMode;
  monitorRefreshIntervalMs: AppPresetNumericSetting;
  resourceTableRefreshIntervalMs: AppPresetNumericSetting;
  managementRefreshIntervalMs: AppPresetNumericSetting;
  discoveryRefreshIntervalMs: AppPresetNumericSetting;
  optimizationRefreshIntervalMs: AppPresetNumericSetting;
  localSystemRefreshIntervalMs: AppPresetNumericSetting;
}

export interface AppPresetNumericSetting {
  mode: AppPresetNumericSettingMode;
  preset: AppLogicRefreshIntervalPreset;
  customValue: number;
}

export interface HostManagerSmartCoordinatorStatus {
  mode: AppOptimizationMode;
  schedulerRunning: boolean;
  lastRunAt?: string | null;
  lastRestoreAt?: string | null;
  pendingChangeCount: number;
  appliedTargetCount: number;
  message: string;
}

export interface HostManagerRollbackStateDocument {
  version: number;
  lastRunAt?: string | null;
  lastRestoreAt?: string | null;
  message: string;
  appliedPlacements: HostManagerAppliedPlacementReceipt[];
}

export interface HostManagerAppliedPlacementReceipt {
  targetId: string;
  displayName: string;
  softwareId?: string | null;
  resourceKind: string;
  records: HostManagerAppliedRecord[];
  appliedAt: string;
  updatedAt: string;
}

export interface HostManagerAppliedRecord {
  kind: string;
  recordId: string;
  metadata?: Record<string, string> | null;
}

export interface AppAppearanceSettings {
  theme: AppThemeMode;
  animations?: AppAnimationMode;
  resourceBarHardwareAccelerationEnabled?: boolean;
  resourceBarHardwareAccelerationMode?: AppAdaptiveBooleanMode;
  barColorMode?: AppBarColorMode;
  fontSmoothing?: AppFontSmoothing;
  language?: AppLanguageMode;
}

export interface AppSystemIntegrationSettings {
  autoStartEnabled: boolean;
  taskManagerShortcutReplacementEnabled: boolean;
  hotkeys: AppEditableHotkeySettings[];
}

export interface AppEditableHotkeySettings {
  actionId: string;
  enabled: boolean;
  encoding: number[];
}

export interface AppDebugSettings {
  debugModeEnabled: boolean;
  debugLogEnabled: boolean;
  hostManagerSmartCoordinatorScoreOnlyEnabled: boolean;
  hostManagerSmartCoordinatorPerformanceLogEnabled: boolean;
}

export interface AppSettingsResult {
  settings?: AppSettings;
  revision?: string;
  storagePath?: string;
  source?: {
    kind?: number | string;
    sourceVersion?: string;
    inputSha256?: string;
    effectiveSha256?: string;
    rewritePerformed?: boolean;
    recoveryDisposition?: string;
    recoveryArtifactPath?: string | null;
    lastKnownGoodPath?: string | null;
  };
  runtimeApplicationDisposition?:
    | "notRequested"
    | "rejectedBeforeCommit"
    | "committedAndApplied"
    | "committedWithCapabilityConstraints"
    | "committedWithDeliveryFailures"
    | "savedNotApplied"
    | "revisionConflict";
  runtimePlanVersion?: number | null;
  runtimePublicationSequence?: number | null;
  runtimeDeliveryFailureCount?: number;
  runtimeCapabilityConstrainedPaths?: readonly string[];
  runtimeFailureCode?: string | null;
}

export interface ResourceProcessSegment {
  processId: number;
  processStartKey?: string | null;
  name: string;
  executablePath?: string | null;
  value: number;
  systemPercent: number;
  softwarePercent: number;
  displayValue: string;
  userName?: string | null;
  architecture?: string | null;
  attributionKind?: string;
  baseScore?: number | null;
}

export interface AppLocalPublicServiceSettings {
  enabled: boolean;
  fileIndexEnabled: boolean;
  databaseServiceEnabled: boolean;
  aiModelCatalogEnabled: boolean;
}

export interface AppAiModelServiceSettings {
  provider: "lm-studio";
  endpoint: string;
  autoStartEnabled: boolean;
}

export type AiGatewayCompatibilityProfile = "openai" | "anthropic";

export interface AiGatewayCredentialView {
  id: string;
  displayName: string;
  compatibilityProfile: AiGatewayCompatibilityProfile;
  createdAt: string;
  baseUrl: string;
  authenticationHeader: string;
}

export interface AiGatewayCredentialCreatedView {
  credential: AiGatewayCredentialView;
  apiKey: string;
}

export interface ResourceSoftwareSegment {
  softwareId: string;
  name: string;
  kind: string;
  displayKind: string;
  value: number;
  systemPercent: number;
  displayValue: string;
  processCount: number;
  processes: ResourceProcessSegment[];
  baseScore?: number | null;
}

export interface ResourceBreakdownBar {
  metricId: string;
  label: string;
  unit: string;
  scaleMode: ResourceScaleMode;
  totalValue: number | null;
  capacityValue: number | null;
  totalSystemPercent: number | null;
  totalDisplay: string;
  software: ResourceSoftwareSegment[];
}

export interface ResourceBreakdownSnapshot {
  capturedAt?: string;
  bars?: ResourceBreakdownBar[];
}

export interface ResourceTableColumn {
  /** 列标识；表头措辞由 resourceTableColumnLabel 按当前语言给出。 */
  id: string;
  unit: string;
  visible: boolean;
  sortable: boolean;
  width: number;
}

export interface ResourceTableValue {
  value?: number | null;
  percent?: number | null;
  displayValue: string;
  unit?: string;
  availability?: string | null;
  heatPercent?: number | null;
  sharedValue?: number | null;
  privateHeatPercent?: number;
  attributionKind?: string;
}

export interface ResourceTableRow {
  id: string;
  parentId?: string | null;
  depth: number;
  kind: "summary" | "software" | "process" | string;
  name: string;
  status: string;
  softwareName?: string | null;
  softwareId?: string | null;
  processId?: number | null;
  processStartKey?: string | null;
  processCount: number;
  processIds?: number[];
  processNames?: string[];
  executablePaths?: string[];
  impactScore: number;
  values: Record<string, ResourceTableValue>;
  sortKeys?: Record<string, number>;
}

export interface SystemProcessOperationItem {
  processId: number;
  processStartKey?: string | null;
  processName?: string | null;
  state: string;
  message: string;
  path?: string | null;
}

export interface SystemProcessOperationResult {
  message: string;
  items: SystemProcessOperationItem[];
  directoryPath?: string | null;
}

export interface LocalOnlineSearchResult {
  query: string;
  url: string;
  message: string;
}

export interface ResourceTableSort {
  columnId: string;
  direction: "asc" | "desc" | string;
}

export interface ResourceTableSnapshot {
  capturedAt?: string;
  columns: ResourceTableColumn[];
  rows: ResourceTableRow[];
  sort: ResourceTableSort;
  viewMode?: "software" | "process" | string;
}

export interface ResourceMonitorSnapshot {
  capturedAt?: string;
  breakdown: ResourceBreakdownSnapshot;
  table: ResourceTableSnapshot;
}

export interface SystemProcessIdentity {
  processId: number;
  processStartKey: string;
}

export interface ControlledRegistration {
  id?: string;
  command?: string;
  software?: string[];
  processes?: unknown[];
  services?: unknown[];
  status?: string;
}

export interface ManagedComponentDefinition {
  id?: string;
  name?: string;
  purpose?: string;
  vendor?: string;
  category?: string;
  sourcePageUrl?: string;
  externalTermsUrl?: string;
  managementRole?: ManagementRole;
  isBundled?: boolean;
  requiresElevation?: boolean;
  requiresExternalTermsAcknowledgement?: boolean;
  installNote?: string;
  capabilities?: ComponentCapability[];
  [key: string]: unknown;
}

export interface ComponentCapability {
  id?: string;
  label?: string;
  providerKind?: string;
  providerId?: string;
  [key: string]: unknown;
}

export interface ComponentProviderState {
  providerId?: string;
  name?: string;
  state?: string;
  providerKind?: string;
  message?: string;
  capabilities?: string[];
  [key: string]: unknown;
}

/** 安装器来源形态，由后端声明；界面据此标注按钮并决定要不要问版本。 */
export type ComponentInstallerSourceKind = "direct" | "githubRelease" | "manual";

/** 版本对话框里的一个选项。不可用时仍然出现在列表里，并带上原因。 */
export interface ComponentVersionOption {
  choice: "verified" | "latest";
  available: boolean;
  version?: string | null;
  assetName?: string | null;
  unavailableReason?: string | null;
}

export interface ComponentVersionOptions {
  id: string;
  sourceKind: ComponentInstallerSourceKind;
  options: ComponentVersionOption[];
}

export interface ManagedComponent {
  definition?: ManagedComponentDefinition;
  installerSourceKind?: ComponentInstallerSourceKind;
  state?: string;
  stateLabel?: string;
  message?: BackendMessage;
  installRoot?: string;
  installerDirectory?: string;
  installerPath?: string;
  canDownload?: boolean;
  canInstall?: boolean;
  canVerify?: boolean;
  installerAvailable?: boolean;
  providerActive?: boolean;
  installed?: boolean;
  providers?: ComponentProviderState[];
}

export interface SoftwareRecord {
  id: string;
  name: string;
  kind?: string;
  displayKind?: string;
  managementRole?: ManagementRole | null;
  state?: string;
  sources?: string[];
  rootPaths?: string[];
  suggestedRootPaths?: string[];
  executablePaths?: string[];
  softwareIdentityId?: string | null;
  issues?: SoftwareIssueTag[];
  requiresRootPathConfirmation?: boolean;
  identityConfirmed?: boolean;
  message?: string;
  /** 给了码就以码为准，前端按当前语言渲染；还没迁的生产方继续用 message。 */
  messageCode?: BackendMessage;
  /** 发布者与版本是事实，由前端拼接与省略。 */
  publisher?: string | null;
  version?: string | null;
  operations?: {
    canUninstall?: boolean;
    uninstallKind?: string;
    uninstallLabel?: string;
    uninstallMessage?: string;
    uninstallLabelCode?: BackendMessage;
    uninstallMessageCode?: BackendMessage;
  };
}

export interface SoftwareIssueReference {
  label: string;
  url: string;
}

export interface SoftwareIssueTag {
  id: string;
  kind: string;
  severity: "Info" | "Warning" | "Critical" | string;
  source: "StaticCatalog" | "DynamicReport" | string;
  label: string;
  message: string;
  dynamic: boolean;
  references: SoftwareIssueReference[];
  reportIds: string[];
}

export type GpuPlacementPolicyMode = "Inherit" | "Disabled" | "Preview" | "Auto" | "Manual";
export type GpuPlacementRiskLevel = "Low" | "Medium" | "High";
export type GpuPlacementTarget = "SystemDefaultGpu" | "AutoIdleGpu" | "IntegratedGpu" | "HighPerformanceGpu" | string;
export type GpuPlacementSchedulingMode = "Precise" | "Ordinary";
export type GpuPlacementRuntimeSchedulingMode = "Precise" | "Ordinary";
export type GpuPlacementRuntimeSwitchMethod = "FutureFrameTakeover" | "WindowRerender";
export type CpuMaximumOccupancyMode = "SingleCcd" | "AllCores";
export type GpuPlacementExplicitSelectionMode =
  | "DefaultSkip"
  | "PreviewOnly"
  | "AllowLow"
  | "AllowMedium"
  | "AllowHigh";

export interface GpuPlacementSoftwarePolicy {
  softwareId: string;
  softwareName: string;
  enabledMode: GpuPlacementPolicyMode;
  maxRisk: GpuPlacementRiskLevel;
  allowedProviders: string[];
  schedulingMode: GpuPlacementSchedulingMode;
  startupTargetGpu: GpuPlacementTarget;
  targetGpu: GpuPlacementTarget;
  runtimeSchedulingMode: GpuPlacementRuntimeSchedulingMode;
  explicitSelectionMode: GpuPlacementExplicitSelectionMode;
  runtimeHotSwitchEnabled?: boolean | null;
  preferredRuntimeSwitchMethod: GpuPlacementRuntimeSwitchMethod;
  baseScoreOverride?: number | null;
  processOverrideAllowed: boolean;
  updatedAt?: string;
  keepCpuProcessesOnMainCcd?: boolean | null;
  gpuExclusive: boolean;
  absolutePerformanceModeEnabled: boolean;
  cpuMaximumOccupancyMode: CpuMaximumOccupancyMode;
  cpuExclusiveLocksAffinity: boolean;
  cpuManualExclusivePositionIds?: string[] | null;
  cpuManualLockedPositionIds?: string[] | null;
}

export interface PortableSoftwareRootConfirmationResult {
  softwareId: string;
  rootPath: string;
  confirmedExecutableCount: number;
  requiresRootPathConfirmation: boolean;
  state: string;
  message: string;
}

export interface GpuPlacementProcessPolicy {
  softwareId: string;
  processKey: string;
  processName: string;
  executablePath?: string | null;
  inherit: boolean;
  enabledMode: GpuPlacementPolicyMode;
  maxRisk: GpuPlacementRiskLevel;
  allowedProviders: string[];
  targetGpu: GpuPlacementTarget;
  explicitSelectionMode: GpuPlacementExplicitSelectionMode;
  baseScoreOverride?: number | null;
  updatedAt?: string;
  startupInterceptionEnabled: boolean;
}

export interface GpuLaunchInterceptionStatus {
  processKey: string;
  executablePath?: string | null;
  requested: boolean;
  registered: boolean;
  status: string;
  message: string;
  registeredViews: string[];
  recentLaunchResult?: GpuLaunchExecutionReport | null;
}

export interface GpuLaunchExecutionReport {
  executablePath: string;
  softwareId?: string | null;
  processKey?: string | null;
  processId?: number | null;
  outcome: string;
  message: string;
  startupTargetGpu?: string | null;
  assignedPositionId?: string | null;
  targetAdapterName?: string | null;
  occurredAt: string;
}

export interface GpuPlacementProcessPolicySaveResult {
  policy: GpuPlacementProcessPolicy;
  startupInterception?: GpuLaunchInterceptionStatus | null;
  runtimeApplicationDisposition:
    | "committedAndApplied"
    | "committedWithDeliveryFailures"
    | "savedNotApplied";
  startupInterceptionDisposition: "applied" | "notAttempted" | "applyFailed";
  runtimePlanVersion?: number | null;
  runtimePublicationSequence?: number | null;
  runtimeDeliveryFailureCount: number;
  failureCode?: string | null;
}

export interface GpuPlacementObservedProcess {
  processKey: string;
  processName: string;
  executablePath?: string | null;
  architecture?: string | null;
  firstObservedAt?: string;
  lastObservedAt?: string;
  observationCount: number;
  lastProcessId?: number | null;
  evidenceSources: string[];
  confidence: number;
}

export interface GpuPlacementSoftwareProcessHistory {
  softwareId: string;
  softwareName: string;
  processes: GpuPlacementObservedProcess[];
  updatedAt?: string;
}

export type GpuPlacementCapabilityState = "supported" | "unsupported" | "unknown";

export interface GpuPlacementProviderCapability {
  state: GpuPlacementCapabilityState;
  providerId: string;
  graphicsApi: string;
  architecture?: string | null;
  reason: string;
}

export interface GpuPlacementProcessCapabilities {
  processKey: string;
  startup: GpuPlacementProviderCapability;
  runtime: GpuPlacementProviderCapability;
}

export interface GpuPlacementExactTargetOption {
  targetGpu: string;
  displayName: string;
  available: boolean;
  reason?: string | null;
}

export interface GpuPlacementTargetInventory {
  state: "current" | "unavailable";
  exactTargets: GpuPlacementExactTargetOption[];
  reason?: string | null;
}

export interface GpuPlacementSoftwareSettingsSnapshot {
  softwareId: string;
  softwareName: string;
  softwarePolicy: GpuPlacementSoftwarePolicy;
  processPolicies: GpuPlacementProcessPolicy[];
  processHistory: GpuPlacementSoftwareProcessHistory;
  startupInterceptions?: GpuLaunchInterceptionStatus[] | null;
  targetInventory?: GpuPlacementTargetInventory | null;
  processCapabilities?: GpuPlacementProcessCapabilities[] | null;
}

export interface GpuPlacementObservedProcessInput {
  processName: string;
  executablePath?: string | null;
  architecture?: string | null;
  processId?: number | null;
  evidenceSources: string[];
}

export interface GpuPlacementProcessObservationRequest {
  softwareId: string;
  softwareName: string;
  processes: GpuPlacementObservedProcessInput[];
}

export type DeviceTopologySnapshotStatus = "warming" | "ready" | "refreshing" | "failed";
export type DeviceTopologySnapshotSource = "memory" | "persisted" | "live";
export type DeviceTopologySourceDiagnosticStatus = "required-incomplete" | "optional-degraded";

export interface DeviceTopologySourceDiagnostic {
  sourceId: string;
  status: DeviceTopologySourceDiagnosticStatus;
  code: string;
  messageCode: BackendMessage;
}

export interface DeviceTopologySnapshotState {
  schemaVersion: string;
  state: DeviceTopologySnapshotStatus;
  snapshot?: DeviceTopologySnapshot | null;
  contentGeneration: number;
  stateRevision: number;
  source: DeviceTopologySnapshotSource;
  lastSuccessAt?: string | null;
  lastAttemptAt?: string | null;
  failureCode?: string | null;
  attemptDiagnostics: DeviceTopologySourceDiagnostic[];
}

export interface DeviceTopologySnapshot {
  capturedAt: string;
  system: DeviceTopologySystemIdentity;
  ports: DeviceTopologyPort[];
  notes: BackendMessage[];
}

export interface DeviceTopologySystemIdentity {
  manufacturer: string;
  model: string;
  brandDisplayName: string;
  brandLogoText: string;
  biosVersion?: string | null;
  baseBoardManufacturer?: string | null;
  baseBoardProduct?: string | null;
}

export interface DeviceTopologyPort {
  id: string;
  isPhysicalConnector: boolean;
  /** 设备自报的名字；我们自己生成名字时为 null，改看 displayNameCode。 */
  displayName: string | null;
  connectorKind: string;
  busKind: string;
  hardwareKind: string | null;
  protocol: string;
  speed: string | null;
  physicalMaximumSpeed?: string | null;
  deviceId: string;
  pnpClass?: string | null;
  manufacturer?: string | null;
  service?: string | null;
  status?: string | null;
  confidence: BackendMessage;
  source: BackendMessage;
  upstreamDeviceId?: string | null;
  upstreamDisplayName?: string | null;
  topologyPath: BackendMessage;
  nativeParentDeviceId?: string | null;
  nativeParentDisplayName?: string | null;
  locationInfo?: string | null;
  locationPaths: string[];
  classGuid?: string | null;
  display?: DeviceTopologyDisplayConnection | null;
  network?: DeviceTopologyNetworkConnection | null;
  idResolution?: DeviceTopologyIdResolution | null;
  advancedInterconnect?: DeviceTopologyAdvancedInterconnect | null;
  usb?: DeviceTopologyUsbConnection | null;
  hardwareIds: string[];
  compatibleIds: string[];
  devNodeStatus?: number | null;
  problemCode?: number | null;
  /** 我们自己生成的节点名；设备自报了名字时为 null。 */
  displayNameCode?: BackendMessage | null;
  /** 我们自己生成的硬件类别；hardwareKind 有技术名时为 null。 */
  hardwareKindCode?: BackendMessage | null;
  hid?: DeviceTopologyHidCapabilities | null;
  camera?: DeviceTopologyCameraCapabilities | null;
  smartDevice?: DeviceTopologySmartDeviceCapabilities | null;
  storage?: DeviceTopologyStorageDevice | null;
}

export interface DeviceTopologyDisplayConnection {
  /** 机型接口档案给的接口名；没有档案时为 null。 */
  connectorTechnology: string | null;
  /** Windows 输出技术 id，措辞由前端出。 */
  outputTechnology: string;
  monitorName: string;
  resolution?: string | null;
  refreshRate: string | null;
  active: boolean;
  targetAvailable: boolean;
  internal: boolean;
  connectorInstance: number;
  monitorDevicePath?: string | null;
  bitsPerColorChannel?: number | null;
  colorEncoding?: string | null;
  advancedColorSupported?: boolean | null;
  advancedColorEnabled?: boolean | null;
  wideColorEnforced?: boolean | null;
  sdrWhiteLevelNits?: number | null;
  hdrFormats?: string | null;
  displayTechnology?: BackendMessage | null;
  panelTechnology?: string | null;
  edidVersion?: string | null;
  edidProductName?: string | null;
  edidSerialNumber?: string | null;
  physicalSize?: string | null;
  minimumLuminanceNits?: number | null;
  maximumLuminanceNits?: number | null;
  maximumFullFrameLuminanceNits?: number | null;
  colorCapabilitySource?: string | null;
  colorSpace?: string | null;
}

export interface DeviceTopologyNetworkConnection {
  interfaceName?: string | null;
  /** 后端给的状态 id，见 DeviceNetworkConnectionStates；措辞由前端出。 */
  connectionState: string;
  transmitLinkSpeed?: string | null;
  receiveLinkSpeed?: string | null;
  permanentAddress?: string | null;
  activeMtuBytes?: number | null;
  hardwareInterface?: boolean | null;
  connectorPresent?: boolean | null;
}

export interface DeviceTopologyIdResolution {
  database: string;
  version: string;
  vendorName?: string | null;
  deviceName?: string | null;
  subsystemName?: string | null;
}

export interface DeviceTopologyAdvancedInterconnect {
  kind: string;
  role: BackendMessage;
  technology: string;
  evidence: BackendMessage;
}

export interface DeviceTopologyUsbConnection {
  hubDevicePath: string;
  portNumber: number;
  deviceConnected: boolean;
  connectionStatus: BackendMessage;
  negotiatedSpeed: string | null;
  deviceAddress: number;
  vendorId?: string | null;
  productId?: string | null;
  deviceIsHub: boolean;
  supportedProtocols: string;
  operatingAtSuperSpeedOrHigher?: boolean | null;
  superSpeedCapableOrHigher?: boolean | null;
  operatingAtSuperSpeedPlusOrHigher?: boolean | null;
  superSpeedPlusCapableOrHigher?: boolean | null;
  portIsUserConnectable?: boolean | null;
  portIsDebugCapable?: boolean | null;
  portHasMultipleCompanions?: boolean | null;
  portConnectorIsTypeC?: boolean | null;
  companionPorts: DeviceTopologyUsbCompanionPort[];
  deviceSpecification: string;
  deviceRevision: string;
  deviceClass: string;
  manufacturerName?: string | null;
  productName?: string | null;
  serialNumber?: string | null;
  interfaceProtocols: string[];
  downstreamHubDevicePath?: string | null;
  endpoints?: DeviceTopologyUsbEndpoint[] | null;
}

export interface DeviceTopologyUsbEndpoint {
  interfaceNumber: number;
  alternateSetting: number;
  interfaceProtocol: string;
  endpointAddress: number;
  direction: string;
  transferType: string;
  maximumPacketSize: number;
  interval: number;
  serviceIntervalMicroseconds?: number | null;
  theoreticalReportRateHz?: number | null;
}

export interface DeviceTopologyHidCapabilities {
  hidType: string;
  hidSpecification?: string | null;
  inputPollingIntervalMicroseconds?: number | null;
  theoreticalReportRateHz?: number | null;
  reportedDpi?: number | null;
  reportedScanRateHz?: number | null;
  standardCapabilitySource: BackendMessage;
  vendorCapabilitySource?: BackendMessage | null;
}

export interface DeviceTopologyCameraCapabilities {
  capabilitySource: BackendMessage;
  bestMode?: DeviceTopologyCameraMode | null;
  nativeModes: DeviceTopologyCameraMode[];
}

export interface DeviceTopologyCameraMode {
  width: number;
  height: number;
  maximumFrameRate: number;
  pixelFormat: string;
}

export interface DeviceTopologySmartDeviceCapabilities {
  deviceType: string;
  manufacturer?: string | null;
  model?: string | null;
  serialNumber?: string | null;
  firmwareVersion?: string | null;
  protocol?: string | null;
  transport?: string | null;
  batteryPercent?: number | null;
  storages: DeviceTopologySmartDeviceStorage[];
  source: BackendMessage;
}

export interface DeviceTopologySmartDeviceStorage {
  name: string;
  capacityBytes?: number | null;
  freeBytes?: number | null;
  fileSystem?: string | null;
}

export interface DeviceTopologyStorageDevice {
  physicalDeviceId: string;
  model?: string | null;
  manufacturer?: string | null;
  serialNumber?: string | null;
  firmwareRevision?: string | null;
  mediaType?: string | null;
  busType?: string | null;
  capacityBytes?: number | null;
  bytesPerSector?: number | null;
  partitionStyle?: string | null;
  healthStatus?: string | null;
  partitions: DeviceTopologyStoragePartition[];
  source: BackendMessage;
}

export interface DeviceTopologyStoragePartition {
  deviceId: string;
  partitionNumber?: number | null;
  type?: string | null;
  capacityBytes?: number | null;
  startingOffsetBytes?: number | null;
  bootable?: boolean | null;
  bootPartition?: boolean | null;
  primaryPartition?: boolean | null;
  volumes: DeviceTopologyStorageVolume[];
}

export interface DeviceTopologyStorageVolume {
  driveLetter?: string | null;
  label?: string | null;
  fileSystem?: string | null;
  capacityBytes?: number | null;
  freeBytes?: number | null;
  mountState: string;
  volumeSerialNumber?: string | null;
}

export interface DeviceTopologyUsbCompanionPort {
  companionIndex: number;
  portNumber: number;
  hubSymbolicLinkName?: string | null;
}

export interface CpuTopologySnapshot {
  capturedAt: string;
  cpuName: string;
  specification: CpuSpecificationModel;
  topologySource: string;
  usageSource: string;
  affinityTargetKind: string;
  visualLayoutKind: "RingBus" | "CcdGrid" | "Grid" | string;
  visualLayoutSource: string;
  physicalCoreCount: number;
  logicalProcessorCount: number;
  ccdCount: number;
  simultaneousMultithreading: boolean;
  ccds: CpuCcdModel[];
  physicalCores: CpuPhysicalCoreModel[];
  logicalProcessors: CpuLogicalProcessorModel[];
  notes: string[];
}

export interface CpuCoreResidencySnapshot {
  capturedAt: string;
  window: string;
  sessionGeneration: number;
  measuredFrom: string;
  measuredThrough: string;
  processes: CpuProcessCoreResidency[];
}

export interface CpuExclusiveBindingSnapshot {
  capturedAt: string;
  cpuName: string;
  bindings: CpuExclusiveBinding[];
}

export interface CpuExclusiveBinding {
  softwareId: string;
  softwareName: string;
  requestedExclusivePositionIds: string[];
  expandedExclusivePhysicalCoreIds: string[];
  requestedLockedPositionIds: string[];
  expandedLockedPhysicalCoreIds: string[];
  locksAffinity: boolean;
  absolutePerformanceModeEnabled: boolean;
  maximumOccupancyMode: string;
  updatedAt: string;
}

export interface CpuProcessCoreResidency {
  processInstanceId: string;
  processId: number;
  processStartKey?: string | null;
  processName: string;
  executionTimeMilliseconds: number;
  switchCount: number;
  threadCount: number;
  primaryCcdId?: string | null;
  primaryPhysicalCoreId?: string | null;
  primaryLogicalProcessorId?: number | null;
  ccds: CpuCcdResidency[];
  physicalCores: CpuPhysicalCoreResidency[];
  logicalProcessors: CpuLogicalProcessorResidency[];
  threads: CpuThreadCoreResidency[];
}

export interface CpuCcdResidency {
  ccdId: string;
  executionTimeMilliseconds: number;
  switchCount: number;
  sharePercent: number;
}

export interface CpuPhysicalCoreResidency {
  physicalCoreId: string;
  ccdId: string;
  executionTimeMilliseconds: number;
  switchCount: number;
  sharePercent: number;
}

export interface CpuLogicalProcessorResidency {
  logicalProcessorId: number;
  physicalCoreId: string;
  ccdId: string;
  executionTimeMilliseconds: number;
  switchCount: number;
  sharePercent: number;
}

export interface CpuThreadCoreResidency {
  threadInstanceId: string;
  threadId: number;
  executionTimeMilliseconds: number;
  switchCount: number;
  primaryCcdId?: string | null;
  primaryPhysicalCoreId?: string | null;
  primaryLogicalProcessorId?: number | null;
  physicalCoreIds?: string[] | null;
}

export interface CpuCorePerformanceOverrideItem {
  coreIndex: number;
  performanceScore?: number | null;
}

export interface CpuCorePerformanceOverrideRequest {
  cpuName: string;
  scores: CpuCorePerformanceOverrideItem[];
}

export interface CpuCorePerformanceOverrideResult {
  cpuName: string;
  scoresByCoreIndex: Record<string, number>;
  updatedAt: string;
  storagePath: string;
}

export interface GpuPerformanceScoreOverrideItem {
  gpuId: string;
  performanceScore?: number | null;
}

export interface GpuPerformanceScoreOverrideRequest {
  scores: GpuPerformanceScoreOverrideItem[];
}

export interface GpuPerformanceScoreOverrideResult {
  scoresByGpuId: Record<string, number>;
  updatedAt: string;
  storagePath: string;
}

export interface GpuPerformanceScoreSnapshot {
  capturedAt: string;
  gpus: GpuPerformanceScoreItem[];
  storagePath: string;
}

export interface GpuPerformanceScoreItem {
  gpuId: string;
  index: number;
  name: string;
  rasterPerformanceScore?: number;
  generationBonusScore?: number;
  useCaseBonusScore?: number;
  gpuPerformanceUseCases?: AppGpuPerformanceUseCase[];
  defaultPerformanceScore: number;
  performanceScore: number;
  hasPerformanceOverride: boolean;
  isIntegrated: boolean;
  source: string;
  matchedPreset?: string | null;
}

export interface CpuSpecificationModel {
  name: string;
  vendor: string;
  family: string;
  physicalCoreCount: number;
  logicalProcessorCount: number;
  maxClockSpeedMhz?: number | null;
  currentClockSpeedMhz?: number | null;
  l2CacheSizeKb?: number | null;
  l3CacheSizeKb?: number | null;
  source: string;
}

export interface CpuCcdModel {
  id: string;
  index: number;
  label: string;
  usagePercent?: number | null;
  physicalCoreIndexes: number[];
  logicalProcessorIds: number[];
  source: string;
}

export interface CpuPhysicalCoreModel {
  id: string;
  index: number;
  label: string;
  ccdId: string;
  efficiencyClass: number;
  performanceScore: number;
  usagePercent?: number | null;
  logicalProcessorIds: number[];
  cacheLevels: CpuCoreCacheLevelModel[];
}

export interface CpuCoreCacheLevelModel {
  level: number;
  sizeKb?: number | null;
  logicalProcessorIds: number[];
  scope: string;
}

export interface CpuLogicalProcessorModel {
  id: number;
  processorGroup: number;
  groupRelativeIndex: number;
  physicalCoreId: string;
  ccdId: string;
  performanceScore: number;
  usagePercent?: number | null;
  affinitySelectable: boolean;
}

export interface ManualSoftwareRequest {
  name: string;
  kind: ManualSoftwareKind;
  rootPaths: string[];
  sourceSoftwareId?: string | null;
}

export type OperationState =
  | "queued"
  | "startPending"
  | "running"
  | "cancelPending"
  | "retryWait"
  | "recoveryPending"
  | "succeeded"
  | "failed"
  | "canceled"
  | "stateUncertain";

export interface OperationProgress {
  sequence: string;
  percent: number | null;
  bytesDone: string | null;
  bytesTotal: string | null;
  speedBytesPerSecond: string | null;
  stage: string | null;
  message: string | null;
}

export interface OperationSnapshot {
  id: string;
  kind: string;
  domainKey: string | null;
  title: string | null;
  state: OperationState;
  configurationGeneration: string;
  stateRevision: string;
  attemptNumber: number;
  maximumAttempts: number;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  cancelRequested: boolean;
  progress: OperationProgress | null;
  result: string | null;
  error: string | null;
}

export interface FileChangeDriveDelta {
  drive?: string | null;
  netBytes?: number | null;
}

export interface FileChangeReport {
  createdCount?: number | null;
  modifiedCount?: number | null;
  deletedCount?: number | null;
  netBytes?: number | null;
  driveDeltas?: FileChangeDriveDelta[];
  truncated?: boolean;
}

export type MigrationKind = "Data" | "Root";
export type MigrationTargetCategory = "UserData" | "Misc";

export interface MigrationRoots {
  userDataRoot?: string;
  miscRoot?: string;
  dependencyRoot?: string;
  managedSoftwareRoot?: string;
}

export interface MigrationPlanItem {
  id: string;
  sourcePath: string;
  destinationPath: string;
  backupPath: string;
  exists: boolean;
  isDirectory: boolean;
  sizeBytes?: number | null;
  risk: string;
  classification: string;
  canExecute: boolean;
  message: string;
}

export interface MigrationPlan {
  softwareName: string;
  targetCategory: MigrationTargetCategory | string;
  migrationKind: MigrationKind | string;
  targetRoot: string;
  canExecute: boolean;
  summary: string;
  items: MigrationPlanItem[];
}

export interface MigrationActionResult {
  sourcePath: string;
  destinationPath: string;
  backupPath: string;
  state: string;
  message: string;
}

export interface MigrationExecuteResult {
  plan: MigrationPlan;
  results: MigrationActionResult[];
}

export interface SoftwareDataMigrationRecord {
  id: string;
  softwareName?: string;
  targetCategory?: string;
  migrationKind?: string;
  sourcePath?: string;
  destinationPath?: string;
  backupPath?: string;
  state?: string;
  createdAt?: string;
  restoredAt?: string | null;
}

export interface SoftwareDataRestoreResult {
  record: SoftwareDataMigrationRecord;
  state: string;
  message: string;
}

export interface SoftwareRootResolutionSource {
  sourceType: string;
  sourceId: string;
  displayName: string;
  rootPath: string;
  matchReason: string;
}

export interface MigrationDiscoveryEvidence {
  provider: string;
  eventType: string;
  path: string;
  processId: number;
  processName: string;
  observedAt: string;
  sizeBytes?: number | null;
}

export interface MigrationCandidate {
  path: string;
  rootPath?: string;
  evidence?: string;
  recommendedTargetCategory?: MigrationTargetCategory | string;
  recommendedMigrationKind?: MigrationKind | string;
  confidence?: number;
  observedWriteCount?: number;
  lastObservedAt?: string;
  message?: string;
  provider?: string;
  evidenceItems?: MigrationDiscoveryEvidence[];
  processNames?: string[];
  directory?: string;
  name?: string;
}

export interface DiscoverySession {
  id: string;
  softwareName: string;
  processNames: string[];
  programRootPaths: string[];
  programRootSources?: SoftwareRootResolutionSource[];
  state: string;
  startedAt?: string;
  stoppedAt?: string | null;
  observedWriteCount: number;
  candidates: MigrationCandidate[];
  message?: string;
  provider?: string;
  providerState?: string;
  providerMessage?: string;
}

export interface SoftwareDetailModel {
  type: "component" | "software";
  id: string;
  name: string;
  kind: string;
  displayKind: string;
  state: string;
  message: string;
  dataSearchName: string;
  rootPaths: string[];
  suggestedRootPaths: string[];
  executablePaths: string[];
  requiresRootPathConfirmation: boolean;
  identityConfirmed: boolean;
  issues: SoftwareIssueTag[];
  rootMigrationPaths: string[];
  rootMigrationDisabledReason: string;
  baseRows: DetailRow[];
  softwareIdentityId: string;
  metadataRows: DetailRow[];
  pathRows: DetailRow[];
  operationRows: DetailRow[];
  capabilities: ComponentCapability[];
  providers: ComponentProviderState[];
}

export type DetailValue = string | number | boolean | null | undefined | DetailValue[];
export type DetailRow = [string, DetailValue];

export interface DetailNotice {
  message: string;
  tone?: "info" | "success" | "warning" | "error" | "progress";
  details?: string[];
}

export interface OptimizationRecorderStatus {
  lastEvaluationAt?: string | null;
  lastObservedAt?: string | null;
  configuredRuleCount: number;
  availableRuleCount: number;
  activeReportCount: number;
  trustedCount: number;
  protectedCount: number;
  sampleIntervalSeconds: number;
}

export interface OptimizationReportTarget {
  targetType: string;
  targetKey: string;
  displayName: string;
  softwareId?: string | null;
  softwareName?: string | null;
  softwareKind?: string | null;
  displayKind?: string | null;
  processNames: readonly string[];
  processIds: readonly number[];
  driveLetter?: string | null;
}

export interface OptimizationReportEvidence {
  resourceKind: string;
  averageValue: number;
  peakValue: number;
  currentValue: number;
  averageDisplay: string;
  peakDisplay: string;
  currentDisplay: string;
  activeSampleCount: number;
  sampleCount: number;
  durationSeconds: number;
  details: readonly string[];
}

export interface OptimizationReportAction {
  id: string;
  label: string;
  kind: string;
  enabled: boolean;
  disabledReason?: string | null;
}

export interface OptimizationActivityContext {
  contextKind: "Unknown" | "Normal" | "Game" | "HighPerformance" | string;
  foregroundProcessId?: number | null;
  foregroundProcessName?: string | null;
  foregroundExecutablePath?: string | null;
  foregroundSoftwareId?: string | null;
  foregroundSoftwareName?: string | null;
  confidence?: string | null;
  evidence: readonly string[];
}

export interface OptimizationReportItem {
  id: string;
  type: string;
  state: string;
  severity: "Info" | "Warning" | "Critical" | string;
  confidence: string;
  createdAt?: string;
  updatedAt?: string;
  firstObservedAt?: string;
  lastObservedAt?: string;
  title: string;
  message: string;
  context?: OptimizationActivityContext | null;
  target: OptimizationReportTarget;
  evidence: OptimizationReportEvidence;
  suggestedActions: readonly OptimizationReportAction[];
}

export interface TrustedOptimizationTarget {
  id: string;
  targetType: string;
  targetKey: string;
  displayName: string;
  reportType: string;
  resourceKind: string;
  trustedAt: string;
  trustedReason: string;
  createdFromReportId: string;
  lastVerifiedAt?: string | null;
  state: string;
}

export interface ProtectedOptimizationTarget {
  id: string;
  targetType: string;
  targetKey: string;
  displayName: string;
  softwareId?: string | null;
  softwareName?: string | null;
  softwareKind?: string | null;
  protectedAt: string;
  protectedReason: string;
  createdFromReportId: string;
  lastVerifiedAt?: string | null;
  state: string;
  allowsPlacementAvoidance: boolean;
  protectionLevel: number;
}

export interface OptimizationReportOverview {
  capturedAt: string;
  reports: readonly OptimizationReportItem[];
  trustedTargets: readonly TrustedOptimizationTarget[];
  protectedTargets: readonly ProtectedOptimizationTarget[];
  status: OptimizationRecorderStatus;
}

export interface OptimizationLevel1ActionPreview {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  currentValue: string;
  proposedValue: string;
  currentRawValue?: string | null;
  proposedRawValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationLevel1Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  actions: OptimizationLevel1ActionPreview[];
}

export interface OptimizationLevel1AppliedAction {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  previousDisplayValue: string;
  appliedDisplayValue: string;
  previousRawValue: string;
  appliedRawValue: string;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationLevel1Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationLevel1AppliedAction[];
}

export interface OptimizationLevel1ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationLevel1Preview;
  record?: OptimizationLevel1Record | null;
}

export interface OptimizationLevel1RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationLevel1Record;
}

export interface CpuAffinityPlan {
  available: boolean;
  source: string;
  logicalProcessorCount: number;
  affinityMask?: number | null;
  logicalProcessorIds: number[];
  summary: string;
  disabledReason?: string | null;
}

export interface OptimizationLevel2ActionPreview {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  currentValue: string;
  proposedValue: string;
  currentRawValue?: string | null;
  proposedRawValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationLevel2Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  affinityPlan: CpuAffinityPlan;
  actions: OptimizationLevel2ActionPreview[];
}

export interface OptimizationLevel2AppliedAction {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  previousDisplayValue: string;
  appliedDisplayValue: string;
  previousRawValue: string;
  appliedRawValue: string;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationLevel2Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationLevel2AppliedAction[];
}

export interface OptimizationLevel2ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationLevel2Preview;
  record?: OptimizationLevel2Record | null;
}

export interface OptimizationLevel2RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationLevel2Record;
}

export type OptimizationLevel3ActionPreview = OptimizationLevel2ActionPreview;

export interface OptimizationLevel3Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  affinityPlan: CpuAffinityPlan;
  actions: OptimizationLevel3ActionPreview[];
}

export type OptimizationLevel3AppliedAction = OptimizationLevel2AppliedAction;

export interface OptimizationLevel3Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationLevel3AppliedAction[];
}

export interface OptimizationLevel3ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationLevel3Preview;
  record?: OptimizationLevel3Record | null;
}

export interface OptimizationLevel3RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationLevel3Record;
}

export interface OptimizationLevel4ActionPreview {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  threadCount: number;
  currentValue: string;
  proposedValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationLevel4Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  actions: OptimizationLevel4ActionPreview[];
}

export interface ProcessThreadSuspendRecord {
  threadId: number;
  previousSuspendCount: number;
  suspendedAt: string;
  message?: string | null;
}

export interface OptimizationLevel4AppliedAction {
  id: string;
  kind: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  threads: ProcessThreadSuspendRecord[];
  workingSetTrimAttempted: boolean;
  workingSetTrimSucceeded: boolean;
  workingSetTrimMessage?: string | null;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationLevel4Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationLevel4AppliedAction[];
}

export interface OptimizationLevel4ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationLevel4Preview;
  record?: OptimizationLevel4Record | null;
}

export interface OptimizationLevel4RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationLevel4Record;
}

export interface OptimizationA1ActionPreview {
  id: string;
  kind: string;
  processId?: number | null;
  processName?: string | null;
  executablePath?: string | null;
  processStartedAt?: string | null;
  processIds: number[];
  processNames: string[];
  currentValue: string;
  proposedValue: string;
  currentRawValue?: string | null;
  proposedRawValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationA1Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  actions: OptimizationA1ActionPreview[];
}

export interface OptimizationA1AppliedAction {
  id: string;
  kind: string;
  processId?: number | null;
  processName?: string | null;
  executablePath?: string | null;
  processStartedAt?: string | null;
  processIds: number[];
  processNames: string[];
  previousDisplayValue: string;
  appliedDisplayValue: string;
  previousRawValue?: string | null;
  appliedRawValue: string;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationA1Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationA1AppliedAction[];
}

export interface OptimizationA1ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationA1Preview;
  record?: OptimizationA1Record | null;
}

export interface OptimizationA1RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationA1Record;
}

export interface OptimizationA2ActionPreview {
  id: string;
  kind: string;
  processId?: number | null;
  processName?: string | null;
  executablePath?: string | null;
  processStartedAt?: string | null;
  processIds: number[];
  processNames: string[];
  currentValue: string;
  proposedValue: string;
  currentRawValue?: string | null;
  proposedRawValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationA2Preview {
  reportId: string;
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  target: OptimizationReportTarget;
  targetAffinityPlan: CpuAffinityPlan;
  actions: OptimizationA2ActionPreview[];
}

export interface OptimizationA2AppliedAction {
  id: string;
  kind: string;
  processId?: number | null;
  processName?: string | null;
  executablePath?: string | null;
  processStartedAt?: string | null;
  processIds: number[];
  processNames: string[];
  previousDisplayValue: string;
  appliedDisplayValue: string;
  previousRawValue?: string | null;
  appliedRawValue: string;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationA2Record {
  id: string;
  reportId: string;
  targetKey: string;
  displayName: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  actions: OptimizationA2AppliedAction[];
}

export interface OptimizationA2ApplyResult {
  reportId: string;
  state: string;
  message: string;
  preview: OptimizationA2Preview;
  record?: OptimizationA2Record | null;
}

export interface OptimizationA2RestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationA2Record;
}

export interface OptimizationProtectionPlacementOwner {
  recordId: string;
  targetKey: string;
  displayName: string;
  affinityMask: number;
  affinityDisplay: string;
  logicalProcessorIds: number[];
  actionCount: number;
}

export interface OptimizationProtectionPlacementActionPreview {
  id: string;
  kind: string;
  protectedTargetId: string;
  protectedTargetKey: string;
  protectedTargetName: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  currentValue: string;
  proposedValue: string;
  currentRawValue: string;
  proposedRawValue: string;
  willChange: boolean;
  enabled: boolean;
  disabledReason?: string | null;
  details: string[];
}

export interface OptimizationProtectionPlacementPreview {
  capturedAt: string;
  eligible: boolean;
  summary: string;
  disabledReason?: string | null;
  avoidancePlan: CpuAffinityPlan;
  enhancedOwners: OptimizationProtectionPlacementOwner[];
  actions: OptimizationProtectionPlacementActionPreview[];
}

export interface OptimizationProtectionPlacementAppliedAction {
  id: string;
  kind: string;
  protectedTargetId: string;
  protectedTargetKey: string;
  protectedTargetName: string;
  processId: number;
  processName: string;
  executablePath?: string | null;
  processStartedAt?: string | null;
  previousDisplayValue: string;
  appliedDisplayValue: string;
  previousRawValue: string;
  appliedRawValue: string;
  appliedAt: string;
  restoredAt?: string | null;
  state: string;
  message?: string | null;
}

export interface OptimizationProtectionPlacementRecord {
  id: string;
  createdAt: string;
  updatedAt: string;
  state: string;
  enhancedAffinityMask: number;
  avoidanceAffinityMask: number;
  enhancedAffinityDisplay: string;
  avoidanceAffinityDisplay: string;
  enhancedOwners: OptimizationProtectionPlacementOwner[];
  actions: OptimizationProtectionPlacementAppliedAction[];
}

export interface OptimizationProtectionPlacementApplyResult {
  state: string;
  message: string;
  preview: OptimizationProtectionPlacementPreview;
  record?: OptimizationProtectionPlacementRecord | null;
}

export interface OptimizationProtectionPlacementRestoreResult {
  recordId: string;
  state: string;
  message: string;
  restoredCount: number;
  skippedCount: number;
  record: OptimizationProtectionPlacementRecord;
}
