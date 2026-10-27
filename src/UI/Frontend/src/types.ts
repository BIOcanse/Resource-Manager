import type { MetricFieldReading } from "./data/metricFields/metricFieldSource.ts";
import type { MotionFeelId } from "./presentation/motionFeel.ts";

export type PageId =
  | "monitor"
  | "components"
  | "optimization"
  | "diskUsage"
  | "control"
  | "details"
  | "settings";

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
export type ManagementRole = "dependency" | "support";
export type ManagementKind = "Dependency" | "Support" | "adapted" | "controlled" | "Unconfirmed" | "Unclassified" | "game" | "high_performance" | "other";
export type ManualSoftwareKind = "adapted" | "game" | "high_performance" | "other";
export type ResourceScaleMode = "capacity" | "active";
export type ResourceTableViewMode = "software" | "process" | "performance";
// 界面风格和配色的目录在 presentation/uiStyles.ts，规则见 docs/ui_styles.md。
export type AppUiStyle = "standard" | "classic" | "soft" | "outline" | "terminal";

export type AppPalette =
  | "system" | "light" | "dark" | "lowContrast"
  | "paper" | "graphite" | "blueprint"
  | "phosphor" | "amber" | "ice";
// 只有开、关两档（按需监控用）；旧设置里的 auto 由归一化读成 enabled。
export type AppAnimationMode = "auto" | "normal" | "none" | "ultra";
// 资源条动画帧率：display = 跟随屏幕，custom = 按 resourceBarFrameRate 限帧；见 presentation/resourceBarFrameRate.ts。
export type AppResourceBarFrameRateMode = "display" | "custom";
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
export type AppOptimizationMode = "normal" | "limited" | "cpu" | "gpu"
  | "memory+cpu" | "memory+gpu" | "cpu+gpu" | "smart";
export type ResourceManagerSelfCpuGrade = "normal" | "optimize";
export type ResourceManagerSelfGpuGrade = "normal" | "optimize";
export type AppPresetNumericSettingMode = "aotu" | "preset" | "custom";
export type AppLogicRefreshIntervalPreset = "responsive" | "balanced" | "lowPower" | "quiet";
export type AppLogicRefreshIntervalKey =
  | "monitor"
  | "resourceTable"
  | "localSystem";
export type SettingsSection = "performance" | "appearance" | "systemIntegration" | "updates" | "debug" | "credits";

/** Frontend view model; backend wire facts are declared in data/monitor/metricWire.ts. */
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
  fieldCode?: number;
  fieldName?: string | null;
  deviceIdentity?: string | null;
  sourceId?: string | null;
  valueKind?: string | null;
  /** 设备字段在当前读数来源设置下是否列进选择器、带什么标记；不是设备字段时没有。 */
  readingSource?: ReadingSourceDisplay;
  /** 设备字段的读取项（后端原样）；同一 id 的字段是同一物理量的不同来源。不是设备字段或没有读取项时没有。 */
  reading?: MetricFieldReading | null;
  /**
   * 后端目录给的部件地址候选来源（按后端排序，原样）。只有后端目录条目有；部件地址以外的条目是空数组。
   * 设备字段卡片条目（前端拼的）没有。
   */
  candidateSources?: readonly MetricSourceCandidate[];
}

/** 部件地址能映射到的一个原始字段（docs/metric_reading_source_selection.md「部件目录的候选来源」）。 */
export interface MetricSourceCandidate {
  readonly fieldName: string;
  readonly deviceIdentity: string;
  readonly sourceId: string;
  readonly reading: MetricFieldReading | null;
  /** 只取已有能力事实；没有事实是 unverified。 */
  readonly sourceAvailability: "available" | "unsupported" | "unverified";
  /** 这个部件地址现在（自动选择）用的就是它；尚无选中来源时全为 false。 */
  readonly isSelected: boolean;
}

export interface MetricValue {
  id?: string;
  label?: string;
  group?: string;
  // Frontend-only override for locally composed field cards and missing-device states.
  displayValue?: string;
  numericValue?: number | null;
  // Raw unit code. All formatting and conversion are frontend-owned.
  unit?: string;
  // 和 numericValue 同单位的总量，只有存在有意义总量的指标才有（内存、显存）。
  total?: number | null;
  percent?: number | null;
  detail?: string | null;
  fieldCode?: number;
  fieldName?: string | null;
  deviceIdentity?: string | null;
  sourceId?: string | null;
  valueKind?: string | null;
  integerValue?: string | null;
  bytesValue?: string | null;
  booleanValue?: boolean | null;
  status?: number;
  observedAt?: string | null;
  sampleDurationMilliseconds?: number | null;
}

export interface MetricSnapshot {
  version: 5;
  capturedAt: string | null;
  /** 后端目录 owner 给的不透明标记，目录或读取项来源元数据变了才换；只比相等，不解析。 */
  catalogRevision: string;
  items: Record<string, MetricValue>;
}

export interface GpuSpecializedTelemetrySnapshot {
  version: 3;
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
  counterClass: number;
  value?: number | null;
  unit: string;
  providerId: string;
  isIntrusive: boolean;
  engineName?: string | null;
}

/** 卡片上一个槽位的大小：最小 1 个单位、中 2、大 3、超大占满整张卡片（features/monitor/cardSlots.ts）。 */
export type DashboardSlotSize = "small" | "medium" | "large" | "full";

/** 卡片上的一个槽位：一个监控项和它的大小；设备字段和显卡指标带绑定。 */
export interface DashboardCardSlot {
  metricId: string;
  binding?: DashboardMetricBinding | null;
  size: DashboardSlotSize;
}

export interface DashboardCardSettings {
  id: string;
  /** 从上到下的槽位，单位之和的规则见 features/monitor/cardSlots.ts。 */
  slots: DashboardCardSlot[];
}

/**
 * 有槽位之前的卡片缓存：一个主读数加若干行。读缓存时转换成槽位（features/monitor/cardSlots.ts 的 legacyCardSlots），
 * 保存只写槽位。等支持的缓存里不会再有这种格式时，连同原生壳 FrontendDashboardCache 的旧格式校验一起删掉。
 */
export interface LegacyDashboardCardSettings {
  id: string;
  main?: string | null;
  small?: string[] | null;
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
  /**
   * 这一条的来源：null 或没有是“自动选择”（按 metricId 订阅），否则是后端进程字段名，作显式口径只读那个字段。
   * 只有两条整机内存资源条认它（features/resourceBreakdown/resourceBarSources.ts）。
   */
  source?: string | null;
}

export interface DashboardSettings {
  version: number;
  /** 前端根据本次构建的点模板和真实目录产生的布局；只读，不写入缓存。 */
  usesFactoryDefaults?: boolean;
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
  /**
   * 这一列的来源：null 或没有是“自动选择”（后端按列的指标选口径），否则是后端进程字段名
   * （如 process_all_private_commit_actual），只读那个字段。见 docs/metric_item_source_choice_2026-10-25.md。
   */
  source?: string | null;
}

/** Frontend cache/source result. Never an API wire contract. */
export interface DashboardSettingsResult {
  settings: DashboardSettings;
  updatedAt: string;
  source: { kind: "frontendCache" | "pointValues"; sourceVersion: number };
}

export interface AppSettings {
  reports?: AppReportSettings;
  version?: string;
  control?: { reassertionIntervalSeconds: number };
  performance?: AppPerformanceSettings;
  appearance?: AppAppearanceSettings;
  systemIntegration?: AppSystemIntegrationSettings;
  debug?: AppDebugSettings;
  publicService?: AppLocalPublicServiceSettings;
  aiModelService?: AppAiModelServiceSettings;
  update?: AppUpdateSettings;
  /** 前端偏好（原生壳持有的偏好缓存），不进入后端设置请求。 */
  notifications?: AppNotificationSettings;
}

export interface ReportRuleValues {
  activationThreshold: number | null;
  clearThreshold: number | null;
  requiredConsecutiveHits: number | null;
  requiredConsecutiveMisses: number | null;
  zeroActivityThreshold: number | null;
  executableNames: string[] | null;
}
export type ReportRuleField = keyof ReportRuleValues;
export interface ReportRuleOverride extends ReportRuleValues { rule: string; }
export interface AppReportSettings { ruleOverrides: ReportRuleOverride[]; }
export interface ReportRuleSetting {
  name: string;
  reportType: string;
  comparison: "atOrAbove" | "atOrBelow";
  displayUnit: "celsius" | "percent" | "milliseconds" | "minutes" | "mebibytes" | "gibibytes";
  scale: number;
  step: number;
  thresholdMinimum: number;
  thresholdMaximum: number;
  countMaximum: number;
  zeroActivityMaximum: number | null;
  editableFields: ReportRuleField[];
  defaults: ReportRuleValues;
}
export interface ReportRuleSettingsResponse { rules: ReportRuleSetting[]; }

export type AppUpdateMode = "off" | "notify" | "automatic";
export interface AppUpdateSettings {
  mode: AppUpdateMode;
  periodicCheckEnabled: boolean;
  checkIntervalHours: number;
}
/** 新版本提示途径的开关：界面内提示由网页前端执行，Windows 系统通知由原生壳执行。 */
export interface AppNotificationSettings {
  updateInApp: "on" | "off";
  updateSystem: "on" | "off";
  accessRequestSystem: "on" | "off";
}

export interface AppPerformanceSettings {
  optimizationMode: AppOptimizationMode;
  preciseGpuPlacementEnabled: boolean;
  gpuOverflowUsagePercent: number;
  gpuOverflowDedicatedMemoryPercent: number;
  realtimeActionBudgetMode: "fixed" | "percent";
  realtimeActionBudgetCount: number;
  realtimeActionBudgetPercent: number;
  physicalMemoryAutomaticCleanupPercent: number;
  virtualMemoryAutomaticCleanupPercent: number;
  physicalMemoryOptimizationTargetUsagePercent: number;
  virtualMemoryOptimizationTargetUsagePercent: number;
  monitorRefreshIntervalMs: AppPresetNumericSetting;
  resourceTableRefreshIntervalMs: AppPresetNumericSetting;
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
  /** 产品当前持有改动的对象数（放置、进程策略、软件档位、进程内存优先级），同一进程只算一个。 */
  appliedTargetCount: number;
  /** null：运行时正常准入；非 null：运行时故障关闭，不会执行任何新调整，值是诊断原因码。 */
  faultClosedReason: string | null;
  coordinator: HostManagerCoordinatorFacts | null;
}

export interface HostManagerRollbackStateDocument {
  version: number;
  lastRunAt?: string | null;
  lastRestoreAt?: string | null;
  coordinator: HostManagerCoordinatorFacts | null;
  appliedPlacements: HostManagerAppliedPlacementReceipt[];
}

export interface HostManagerAppliedPlacementReceipt {
  targetId: string;
  processId: number | null;
  processStartKey: string | null;
  processName: string | null;
  softwareId?: string | null;
  resourceKind: string;
  records: HostManagerAppliedRecord[];
  appliedAt: string;
  updatedAt: string;
}

export interface HostManagerAppliedRecord {
  kind: string;
  recordId: string;
}

export interface HostManagerCoordinatorFacts {
  revision: string;
  pendingCount: number;
  inflightCount: number;
  reasonMask: string;
}

// 容量数字用哪个进制显示：native = 各按本来的进制（内存类 1024、存储类 1000），
// binary = 一律 1024（KiB/MiB/GiB），decimal = 一律 1000（kB/MB/GB）。
export type ByteUnitMode = "native" | "binary" | "decimal";

// 读数来源怎么列：preferred = 每个读取项只列本机首选来源（默认），all = 列出所有可用来源并标出首选。
// 后端只存和校验；筛选和标记见 presentation/readingSources.ts。
export type AppReadingSourceMode = "preferred" | "all";

// 一个设备字段在当前读数来源设置下怎么显示，由 presentation/readingSources.ts 给出。
export interface ReadingSourceDisplay {
  /** 当前模式下列表（选择器、设备详情）里是否列出这一来源。 */
  readonly listed: boolean;
  /** 来源旁的小标记：preferred = 首选，pending = 待确认；不加标记为 null。 */
  readonly mark: "preferred" | "pending" | null;
}

export interface AppAppearanceSettings {
  uiStyle: AppUiStyle;
  // 当前风格下的配色。
  theme: AppPalette;
  animations?: AppAnimationMode;
  // 手感：动画怎么走（目录 presentation/motionFeel.ts）；动画档位决定走不走。
  motionFeel?: MotionFeelId;
  resourceBarFrameRateMode?: AppResourceBarFrameRateMode;
  // custom 时的帧/秒（10–240）；display 时保留不用。
  resourceBarFrameRate?: number;
  barColorMode?: AppBarColorMode;
  fontSmoothing?: AppFontSmoothing;
  language?: AppLanguageMode;
  byteUnitMode?: ByteUnitMode;
  readingSourceMode?: AppReadingSourceMode;
}

export interface AppSystemIntegrationSettings {
  restartAfterCrashEnabled?: boolean;
  crashRestartDelaySeconds?: number;
  crashRestartCount?: number;
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

export type FrontendReadIntervals = Readonly<Record<AppLogicRefreshIntervalKey, number>>;
export interface FrontendRefreshIntervals {
  readonly foreground: FrontendReadIntervals;
  readonly backgroundOptimized: FrontendReadIntervals;
  readonly presets: Readonly<Record<AppLogicRefreshIntervalKey, Readonly<Record<AppLogicRefreshIntervalPreset, number>>>>;
  readonly defaults: Readonly<Record<AppLogicRefreshIntervalKey, AppPresetNumericSetting>>;
}

export interface ServiceRecoveryDiagnostics {
  state: "notApplied" | "applied" | "skipped" | "failed" | "mismatch";
  configuredResetPeriodSeconds: number;
  actual: {
    resetPeriodSeconds: number;
    actions: readonly { type: number; delayMilliseconds: number }[];
    failureActionsOnNonCrashFailures: boolean;
  } | null;
  skipReason: string | null;
  writeError: string | null;
  readError: string | null;
}

export type BackendAppPerformanceSettings = Omit<AppPerformanceSettings,
  "monitorRefreshIntervalMs" | "resourceTableRefreshIntervalMs" | "localSystemRefreshIntervalMs">;
export type BackendAppSettings = Omit<AppSettings, "appearance" | "performance"> & { performance?: BackendAppPerformanceSettings };

/** Application settings keep their own backend disposition contract. */
export interface AppSettingsResult {
  serviceRecovery?: ServiceRecoveryDiagnostics;
  settings?: BackendAppSettings;
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
  sharedValue?: number | null;
  processId: number;
  processStartKey?: string | null;
  name: string;
  executablePath?: string | null;
  value: number;
  systemPercent: number;
  softwarePercent: number;
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
  sharedValue?: number | null;
  softwareId: string;
  name: string;
  kind: string;
  displayKind: string;
  value: number;
  systemPercent: number;
  processCount: number;
  processes: ResourceProcessSegment[];
  baseScore: number;
}

/** One GPU engine of a per-engine process field or fact (backend ProcessGpuEngineScope). */
export interface ResourceGpuEngineScope {
  physicalIndex: number;
  engineIndex: number;
}

/**
 * 一个指标在本帧自己的整机事实（metric-source round 4）：资源事实、资源条和进程明细里每个指标各带一份，
 * 互不借用。totalValue 是 totalMetricId 的整机读数；processSumValue 是本帧发布的进程之和；
 * residualValue = totalValue − processSumValue，带符号，只在 additiveAcrossProcesses 时有值，
 * 不可加的口径（工作集）为 null，也没有残差条目。capturedAt 和 windowMilliseconds 是这个指标自己的采样时刻和窗口。
 * totalValue 为 null 时 unavailableReason 写原因码，后端不发进程行。
 */
export interface ResourceMetricFacts {
  totalMetricId: string | null;
  totalValue: number | null;
  processSumValue: number | null;
  residualValue: number | null;
  additiveAcrossProcesses: boolean;
  capturedAt: string | null;
  windowMilliseconds: number | null;
  quantity: string | null;
  unavailableReason: string | null;
  gpuEngine: ResourceGpuEngineScope | null;
  engineClass: string | null;
}

export interface ResourceBreakdownBar extends ResourceMetricFacts {
  residualSource?: "counters" | null;
  sharedValue?: number | null;
  metricId: string;
  /** 这一条实际读的进程字段；不是进程字段的条为 null。 */
  processFieldName: string | null;
  unit: string;
  scaleMode: ResourceScaleMode;
  /**
   * 后端的比较基数。整机容量类指标（cpu/memory/virtualMemory/gpu.N.usage/vram，见 resourceBarSupportsCapacity）是容量；
   * 吞吐类（disk.io、network.traffic）是本帧总量 max(1, total)，不是设备容量。
   */
  capacityValue: number | null;
  totalSystemPercent: number | null;
  software: ResourceSoftwareSegment[];
}

export interface ResourceBreakdownSnapshot {
  capturedAt?: string;
  bars?: ResourceBreakdownBar[];
}

export type PhysicalMemoryResidualFrame = {
  version: 1;
  source: "pfn";
  capturedAt: string;
} & ({
  status: "ready";
  reasonCode: string | null;
  usedBytes: number;
  attributedProcessPrivateBytes: number;
  residualBytes: number;
  unclassifiedBytes: number;
  parts: { kind: string; bytes: number }[];
} | {
  status: "unavailable";
  reasonCode: string;
  usedBytes: null;
  attributedProcessPrivateBytes: null;
  residualBytes: null;
  unclassifiedBytes: null;
  parts: [];
});
export interface ResourceProcessDetailsSnapshot {
  physicalMemoryResidual?: PhysicalMemoryResidualFrame;
  capturedAt?: string;
  metrics: ({
    metricId: string;
    capacityValue: number | null;
    software: { softwareId: string; processes: ResourceProcessSegment[] }[];
  } & Pick<ResourceMetricFacts, "totalMetricId" | "totalValue" | "processSumValue" | "residualValue" | "additiveAcrossProcesses" | "capturedAt">)[];
}

export interface ResourceTableColumn {
  /** 列标识；表头措辞由 resourceTableColumnLabel 按当前语言给出。 */
  id: string;
  unit: string;
  visible: boolean;
  sortable: boolean;
  width: number;
  /** 列的来源（同 ResourceTableColumnSettings.source）；null 是自动选择。 */
  source: string | null;
  /** 这一列的指标实际读的进程字段（资源事实的 processFieldName）；不是进程字段或还没有数据时为 null。 */
  processFieldName: string | null;
  /** 这一列的指标在本帧声明的 additiveAcrossProcesses；不是指标列或还没有数据时为 null。 */
  additiveAcrossProcesses: boolean | null;
}

export interface ResourceTableValue {
  value?: number | null;
  percent?: number | null;
  /** Text produced by the frontend table projection; never read from the facts wire. */
  displayValue?: string | null;
  unit: string;
  availability?: string | null;
  /** Backend reason code when the metric has no total (ResourceMetricFacts.unavailableReason). */
  unavailableReason?: string | null;
  /** A table-only explanation, e.g. why an independent system total is not additive. */
  basis?: string;
  heatPercent?: number | null;
  sharedValue?: number | null;
  privateHeatPercent?: number;
  attributionKind?: string;
}

export interface ResourceTableRow {
  /** Software identity kind and containing whole-machine fact; IDs remain opaque. */
  displayKind?: string | null;
  totalMetricId?: string | null;
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
  outcome: SystemProcessOutcome;
  nativeErrorCode?: number | null;
  path?: string | null;
}

export interface SystemProcessOperationResult {
  action: "Terminate" | "Dump";
  items: SystemProcessOperationItem[];
  directoryPath?: string | null;
}

export interface LocalOnlineSearchResult {
  query: string;
  url: string;
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
  facts?: import("./features/resourceTable/resourceFacts.ts").ResourceFacts;
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
  vendor?: string;
  sourcePageUrl?: string;
  externalTermsUrl?: string | null;
  managementRole?: ManagementRole;
  isBundled?: boolean;
  requiresElevation?: boolean;
  requiresExternalTermsAcknowledgement?: boolean;
  capabilities?: ComponentCapability[];
  [key: string]: unknown;
}

export interface ComponentCapability {
  id?: string;
  providerKind?: string;
  providerId?: string;
  [key: string]: unknown;
}

export interface ComponentProviderState {
  providerId?: string;
  state?: string;
  providerKind?: string;
  message?: BackendMessage;
  runtimePath?: string | null;
  capabilities?: string[];
  [key: string]: unknown;
}

/** 安装器来源形态，由后端声明；界面据此标注按钮并决定要不要问版本。 */
export type ComponentInstallerSourceKind = "direct" | "githubRelease" | "manual";

/** 版本对话框里的一个选项。不可用时仍然出现在列表里，并带上原因。 */
export interface ComponentVersionOption {
  choice: string;
  available: boolean;
  version?: string | null;
  assetName?: string | null;
  unavailableReasonCode?: string | null;
  series?: string | null;
  channel?: string | null;
  publishedAt?: string | null;
}

export interface ComponentVersionOptions {
  id: string;
  sourceKind: ComponentInstallerSourceKind;
  options: ComponentVersionOption[];
  catalogStatus?: string;
  complete?: boolean;
}

export interface ManagedComponent {
  definition?: ManagedComponentDefinition;
  installerSourceKind?: ComponentInstallerSourceKind;
  state?: string;
  message?: BackendMessage;
  installRoot?: string;
  installerDirectory?: string;
  installerPath?: string | null;
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
  kind: string;
  managementRole?: ManagementRole | null;
  state?: string;
  sourceFacts?: SoftwareSourceFact[];
  registrationFacts?: SoftwareRegistrationFacts | null;
  categorySource?: string | null;
  rootPaths?: string[];
  suggestedRootPaths?: string[];
  executablePaths?: string[];
  softwareIdentityId?: string | null;
  issues?: SoftwareIssueTag[];
  requiresRootPathConfirmation?: boolean;
  identityConfirmed?: boolean;
  /** 发布者与版本是事实，由前端拼接与省略。 */
  publisher?: string | null;
  version?: string | null;
  operations?: {
    canUninstall?: boolean;
    uninstallKind?: string;
    uninstallConstraint?: "none" | "empty_managed_root" | "missing_uninstall_entry" | "manual_registration" | "missing_strategy" | "self" | "portable";
  };
}

export interface SoftwareSourceFact {
  kind: "dependency" | "windows_registry" | "self" | "adapter" | "controlled" | "portable" | "manual" | "identity_catalog" | "policy" | "runtime";
  catalogVersion?: string | null;
  catalogSource?: string | null;
  policyGroupId?: string | null;
  policyGroupName?: string | null;
}

export interface SoftwareRegistrationFacts {
  kind: "self" | "adapter" | "controlled" | "portable" | "manual";
  processCount: number;
  serviceCount: number;
  endpointState?: string | null;
}

export interface SoftwareIssueReference {
  label: string;
  url: string;
}

export interface SoftwareIssueEvidence {
  metricKind: string;
  value: number;
  valueUnit: string;
  moduleName?: string | null;
}

export interface SoftwareIssueTag {
  evidence?: SoftwareIssueEvidence[];
  id: string;
  kind: string;
  severity: "info" | "warning" | "critical" | string;
  source: "runtime_attribution" | "static_catalog" | "dynamic_report" | string;
  label: string;
  message: string;
  dynamic: boolean;
  references: SoftwareIssueReference[];
  reportIds: string[];
}

export type GpuPlacementTarget = "system_default_gpu" | "auto_idle_gpu" | "integrated_gpu" | "high_performance_gpu" | string;
export type GpuPlacementRuntimeSwitchMethod = "future_frame_takeover" | "window_rerender";
export type CpuMaximumOccupancyMode = "single_ccd" | "all_cores";

export interface GpuPlacementSoftwarePolicy {
  softwareId: string;
  softwareName: string;
  allowedProviders: string[];
  startupTargetGpu: GpuPlacementTarget;
  runtimeHotSwitchEnabled?: boolean | null;
  preferredRuntimeSwitchMethod: GpuPlacementRuntimeSwitchMethod;
  baseScoreOverride?: number | null;
  processOverrideAllowed: boolean;
  updatedAt?: string;
  // 三个调度退出项各自独立，完全不管对应的域（含不提升）；与“保护”（只防降级）互不影响。
  cpuSchedulingExcluded: boolean;
  memorySchedulingExcluded: boolean;
  gpuSchedulingExcluded: boolean;
  cpuMaximumOccupancyMode: CpuMaximumOccupancyMode;
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
  allowedProviders: string[];
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

/** Publication and delivery acknowledgement; scheduler execution is not observed here. */
export interface RuntimePolicySaveResult<T> {
  policy: T;
  startupInterception?: GpuLaunchInterceptionStatus | null;
  runtimeApplicationDisposition:
    | "committedAndPublished"
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
  reasonCode: string;
  missingFiles: string[];
}

export interface GpuPlacementProcessCapabilities {
  processKey: string;
  startup: GpuPlacementProviderCapability;
  runtime: GpuPlacementProviderCapability;
}

export interface GpuPlacementExactTargetOption {
  targetGpu: string;
  adapterName?: string | null;
  available: boolean;
  reasonCode?: string | null;
}

export interface GpuPlacementTargetInventory {
  state: "current" | "unavailable";
  exactTargets: GpuPlacementExactTargetOption[];
  reasonCode?: string | null;
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

/** 后端为该软件登记的叠加层注入模块：都没有、只有一部分、全部。只读，来自后端的启动模块选择。 */
export type OverlayInjectionModules = "none" | "partial" | "complete";

/** 浮窗一项选的来源：一台设备上的一个后端原始字段（和卡片的“字段@设备”同一个东西）。 */
export interface OverlayMetricSource {
  fieldName: string;
  deviceIdentity: string;
}

export interface PerformanceOverlaySettings {
  softwareId: string;
  injectionModules: OverlayInjectionModules;
  enabled: boolean;
  mode: "external" | "injected";
  metrics: string[];
  /**
   * 每一项的来源，按项目的指标 id 存（docs/metric_item_source_choice_2026-10-25.md）。没有这一项是“自动选择”：
   * 按部件地址订阅，后端按本机选来源；有就只读这个字段（原生壳按 fields= 订阅），读不到显示不可用。
   * 和 metrics 一起存在前端偏好缓存里，不进后端设置。
   */
  metricSources: Record<string, OverlayMetricSource>;
  sizeMode: "absolutePixels" | "windowRatio";
  fontSizePx: number;
  regionWidthRatio: number;
  regionHeightRatio: number;
  anchor: "topLeft" | "topRight" | "bottomLeft" | "bottomRight";
  marginPx: number;
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
  informationFields: DeviceTopologyInformationSnapshot;
}

export interface DeviceTopologyInformationSnapshot {
  observedAt: string | null;
  devices: {
    deviceIdentity: string;
    objectType: string;
    fields: { definition: { name: string }; value: unknown }[];
  }[];
}

export interface DeviceTopologySnapshot {
  capturedAt: string;
  gpuInformation: DeviceTopologyGpuInformation[] | null;
  machineIdentity: DeviceTopologyMachineIdentity;
  externalMachines: DeviceTopologyExternalMachine[] | null;
  system: DeviceTopologySystemIdentity;
  ports: DeviceTopologyPort[];
  notes: BackendMessage[];
}

export interface DeviceTopologyGpuInformation {
  pnpInstanceId: string;
  adapterLuid: string | null;
  name: string | null;
  pciVendorId: string | null;
  pciDeviceId: string | null;
  driverVersion: string | null;
  adapterTypeFlags: number | null;
  isDetachable: boolean | null;
  attachmentKind: "graphics_dock" | null;
  attachmentEvidenceSource: "d3dkmt-adapter-type" | null;
}

export interface DeviceTopologyExternalMachine {
  localLinkDeviceId: string;
  peerIdentity: string | null;
  formFactor: "workstation";
  model: string | null;
  components: { component: "soc"; model: string | null }[];
  evidence: {
    source: "windows-usb4net";
    compatibleId: "USB4\\USB4NET";
    mediaConnectStateRaw: 1;
    peerPropertiesReason: string;
  };
}
export interface DeviceTopologyIdentityFact<T> {
  value: T | null;
  reason: string | null;
}

export interface DeviceTopologyMachineIdentity {
  formFactor: "mobile_workstation" | "laptop" | "workstation" | "desktop" | "unknown";
  evidence: {
    chassisTypes: DeviceTopologyIdentityFact<number[]>;
    systemBatteryPresent: DeviceTopologyIdentityFact<boolean>;
    cpuModels: DeviceTopologyIdentityFact<string[]>;
    gpuModels: DeviceTopologyIdentityFact<string[]>;
    gpuAttachments: DeviceTopologyIdentityFact<{
      model: string; isDetachable: boolean | null; adapterTypeFlags: number | null;
      attachmentKind: "graphics_dock" | null; evidenceSource: string | null;
    }[]>;
    workstationComponents: { component: "cpu" | "gpu"; model: string; ruleId: string }[];
  };
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

export interface DeviceTopologyTransport {
  code: "hd_audio" | "bluetooth" | "ucsi" | "nvme" | "ahci" | "i2c_hid" | "acpi" | "pci" | "usb" | "usb4" | null;
  reason: string | null;
  ruleId: string | null;
  evidence: { source: "setupapi-cfgmgr32" | "usb-hub-ioctl"; deviceId: string; depth: number;
    enumeratorName: string | null; busTypeGuid: string | null; service: string | null; parentDeviceId: string | null }[];
}

export interface DeviceTopologyPort {
  transport: DeviceTopologyTransport;
  id: string;
  isPhysicalConnector: boolean;
  /** 设备自报的名字；我们自己生成名字时为 null，改看 displayNameCode。 */
  displayName: string | null;
  connectorKind: string;
  busKind: string;
  hardwareKind: string | null;
  protocol: string | null;
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
  currentWidth?: number | null;
  currentHeight?: number | null;
  currentRefreshRateNumerator?: number | null;
  currentRefreshRateDenominator?: number | null;
  currentBitsPerColorChannel?: number | null;
  edidRawDeclaredBase64?: string | null;
  modeCapabilityDeclared?: DisplayEdidCapability | null;

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

export interface DisplayEdidRate { numerator: number; denominator: number; }
export interface DisplayEdidReference { blockIndex: number; byteOffset: number; rawDescriptor: number[]; bitIndex: number | null; }
export interface DisplayEdidTiming {
  pixelClockHz: number; horizontalActive: number; horizontalBlanking: number;
  horizontalSyncOffset: number; horizontalSyncWidth: number; verticalActive: number;
  verticalBlanking: number; verticalSyncOffset: number; verticalSyncWidth: number;
  flags: number; positiveHorizontalSync: boolean | null; positiveVerticalSync: boolean | null;
  frameRate: DisplayEdidRate; fieldRate: DisplayEdidRate | null;
}
export interface DisplayEdidCvtParameters {
  reducedBlankingVersion: number; stereoCode: number;
  alternativeRefresh1000Over1001Supported: boolean | null;
  horizontalBlankingBase160Pixels: boolean | null; earlyVsyncRequired: boolean | null;
  deltaHorizontalBlankingCode: number | null; additionalVerticalBlankingCode: number | null;
  alternativeMinimumVerticalBlanking: boolean | null;
}
export interface DisplayEdidAspectRatio { code: number; horizontal: number | null; vertical: number | null }
/** DisplayID 1.3 Type V 选项字节里的原始声明；Type V 不生成时序。 */
export interface DisplayEdidTypeVOptions { formulaCode: number; stereoSupportCode: number; fractionalRefreshSupported: boolean }
/** DisplayID 1.3 Type VI 的声明值；时钟高位与规范上限矛盾时 pixelClockHz 为 null，其余值照常有效。 */
export interface DisplayEdidTypeViTiming {
  pixelClockHz: number | null;
  horizontalBlankingPixels: number; horizontalFrontPorchPixels: number; horizontalSyncWidthPixels: number;
  verticalBlankingLines: number; verticalFrontPorchLines: number; verticalSyncWidthLines: number;
  positiveHorizontalSync: boolean; positiveVerticalSync: boolean; stereoSupportCode: number;
}
/** Type VI 17 字节记录的可选尾部：宽高比倍率（比值 = 倍率×3:256）与垂直图像尺寸（毫米）。 */
export interface DisplayEdidTypeViImageSize {
  aspectMultiplier: number; verticalImageBaseSizeMillimeters: number;
  sizeMultiplier: number; verticalImageSizeMillimeters: number;
}
export interface DisplayEdidMode extends DisplayEdidReference {
  encoding: string; revision: number; code: number | null; width: number | null; height: number | null;
  aspectRatioDeclared?: DisplayEdidAspectRatio | null;
  /** 以下三项只在对应 Type V／VI 声明存在时由后端输出；旧载荷缺属性保持缺失。 */
  displayIdTypeVOptions?: DisplayEdidTypeVOptions | null;
  displayIdTypeViTiming?: DisplayEdidTypeViTiming | null;
  displayIdTypeViImageSize?: DisplayEdidTypeViImageSize | null;
  horizontalPixelRepetition: number | null; interlaced: boolean | null; nominalVerticalRateHz: number | null;
  native: boolean | null; preferred: boolean | null; yCbCr420Only: boolean | null;
  yCbCr420Encoding: boolean | null; yCbCr420Supported: boolean | null;
  yCbCr420CapabilityMap: DisplayEdidReference | null; containerFlags: number | null;
  containerRevision: number | null; dscPassThrough: boolean | null; formula: string | null;
  reducedBlanking: boolean | null; timing: DisplayEdidTiming | null; cvtParameters: DisplayEdidCvtParameters | null;
}
export interface DisplayEdidRange extends Omit<DisplayEdidReference, "bitIndex"> {
  encoding: string; revision: number; formula: string; minimumVerticalRateHz: number; maximumVerticalRateHz: number;
  minimumHorizontalFrequencyHz: number; maximumHorizontalFrequencyHz: number; maximumPixelClockHz: number | null;
  secondaryGtf: { startHorizontalFrequencyHz: number; cTimesTwo: number; m: number; k: number; jTimesTwo: number } | null;
  cvt: { version: number; maximumActivePixelsPerLine: number | null; supportedAspectRatiosMask: number;
    preferredAspectRatioCode: number; standardBlanking: boolean; reducedBlanking: boolean;
    scalingMask: number; preferredVerticalRateHz: number | null } | null;
}
export interface DisplayEdidSelection extends Omit<DisplayEdidReference, "bitIndex"> {
  encoding: string; revision: number; flags: number | null;
  references: { byteOffset: number; shortVideoReference: number; targetEncoding: string;
    code: number | null; target: DisplayEdidReference | null }[];
  imageSize: { widthCode: number; heightCode: number; unitMicrometers: number } | null;
}
/** DisplayID 声明块里一条描述符的原位置与容器来源。 */
export interface DisplayEdidDisplayIdSource extends Omit<DisplayEdidReference, "bitIndex"> {
  revision: number; containerFlags: number; containerRevision: number;
}
/** DisplayID 2.0 tag 0x25 动态时序范围声明；不是实测刷新率或系统可用模式。 */
export interface DisplayEdidDynamicTimingRange extends DisplayEdidDisplayIdSource {
  minimumPixelClockHz: number; maximumPixelClockHz: number;
  minimumVerticalRateHz: number; maximumVerticalRateHz: number;
  seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking: boolean;
}
/** 帧时长单步变化上限（精确微秒）；null 即零码：整个范围内任意变化都满足无抖动要求。 */
export interface DisplayEdidAdaptiveSyncTolerance {
  flickerRestrictedToSingleStep: boolean; maximumSingleStepMicroseconds: number | null;
  zeroCodeMeansNoJitterThroughoutRange: boolean;
}
/** DisplayID tag 0x2B Adaptive-Sync 声明；基础最小／最大值不是选定时序后的实际范围。 */
export interface DisplayEdidAdaptiveSyncRange extends DisplayEdidDisplayIdSource {
  baseMinimumRefreshRateHz: number; baseMaximumRefreshRateHz: number;
  nativePanelRange: boolean; adaptiveVTotalSupported: boolean; seamlessTransitionSupported: boolean;
  frameDurationIncrease: DisplayEdidAdaptiveSyncTolerance; frameDurationDecrease: DisplayEdidAdaptiveSyncTolerance;
}
export interface DisplayEdidCapability {
  modes: DisplayEdidMode[]; ranges: DisplayEdidRange[]; edidRevision: number; baseFeatures: number;
  ctaFlags: number | null; selectionDeclarations: DisplayEdidSelection[] | null;
  /** 旧载荷没有这两个集合时保持缺失，界面按旧载荷显示。 */
  dynamicTimingRanges?: DisplayEdidDynamicTimingRange[];
  adaptiveSyncRanges?: DisplayEdidAdaptiveSyncRange[];
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
  wireless?: DeviceTopologyWirelessConnection | null;
}

export interface DeviceTopologyWirelessConnection {
  source: string;
  interfaceStateRaw: number;
  supportedPhyTypesRaw: number[] | null;
  supportedStandards: string[] | null;
  negotiatedPhyTypeRaw: number | null;
  negotiatedStandard: string | null;
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
  supportedProtocols: string | null;
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
  deviceClassCode: number | null;
  deviceSubClassCode: number | null;
  deviceProtocolCode: number | null;
  interfaces: { interfaceNumber: number; alternateSetting: number;
    classCode: number; subClassCode: number; protocolCode: number }[] | null;
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
  visualLayoutKind: "ring_bus" | "ccd_grid" | "grid" | string;
  visualLayoutSource: string;
  physicalCoreCount: number;
  logicalProcessorCount: number;
  ccdCount: number;
  simultaneousMultithreading: boolean;
  ccds: CpuCcdModel[];
  physicalCores: CpuPhysicalCoreModel[];
  logicalProcessors: CpuLogicalProcessorModel[];
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
  rasterPerformanceScore: number;
  defaultPerformanceScore: number;
  performanceScore: number;
  hasPerformanceOverride: boolean;
  isIntegrated: boolean;
  source: string;
  matchedPreset?: string | null;
  isPresetMatch: boolean;
  /** 后端认定这块 GPU 是否有专用显存指标：true 有，false 没有（只有共享内存），null 后端还不知道。 */
  hasDedicatedMemoryMetrics: boolean | null;
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
  usagePercent?: number | null;
  physicalCoreIndexes: number[];
  logicalProcessorIds: number[];
  source: string;
}

export interface CpuPhysicalCoreModel {
  id: string;
  index: number;
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
  stage: "Download" | null;
}

export interface OperationSnapshot {
  id: string;
  kind: string;
  domainKey: string | null;
  subject: OperationSubject | null;
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
  resultCode?: string | null;
  resultMessage?: BackendMessage | null;
  errorMessage?: BackendMessage | null;
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

export interface SoftwareDetailModel {
  type: "component" | "software";
  id: string;
  name: string;
  kind: string;
  displayKind: string;
  state: string;
  message: string;
  rootPaths: string[];
  suggestedRootPaths: string[];
  executablePaths: string[];
  requiresRootPathConfirmation: boolean;
  identityConfirmed: boolean;
  issues: SoftwareIssueTag[];
  baseRows: DetailRow[];
  softwareIdentityId: string;
  metadataRows: DetailRow[];
  metadataStatus?: import("./frontendRuntime/source/SourceSnapshot").SourceStatus;
  manualRegistration?: boolean;
  pathRows: DetailRow[];
  operationRows: DetailRow[];
  capabilities: ComponentCapability[];
  providers: ComponentProviderState[];
}

export type DetailValue = string | number | boolean | null | undefined | DetailValue[];
export type DetailRow = [string, DetailValue];

export interface DetailNotice {
  onRuntimePublished?: () => void;
  message: string;
  tone?: "info" | "success" | "warning" | "error" | "progress";
  details?: string[];
}

export interface OptimizationRecorderStatus {
  evaluationState: "notEvaluated" | "completed" | "failed";
  unavailableRules: readonly string[];
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
  name: string;
  softwareId?: string | null;
  softwareName?: string | null;
  softwareKind?: string | null;
  displayKind?: string | null;
  processNames: readonly string[];
  processIds: readonly number[];
  driveLetter?: string | null;
}

export interface OptimizationReportEvidence {
  process?: {
    processId: number;
    processStartKey: string;
    name: string;
    memoryBytes: number | null;
    idleDurationSeconds: number | null;
    commandLine: string | null;
  } | null;
  resourceKind: string;
  averageValue: number;
  peakValue: number;
  currentValue: number;
  // 数值的单位标记："%" / "B"（原始字节）/ "°C" / "ms" / "count"。
  // 后端只给事实，成句和换算由 features/optimization/optimizationPresentation.ts 负责。
  valueUnit: string;
  activeSampleCount: number;
  sampleCount: number;
  durationSeconds: number;
}

export interface OptimizationReportAction {
  id: string;
  kind: string;
  enabled: boolean;
}

export interface OptimizationActivityContext {
  contextKind: "unknown" | "normal" | "game" | "high_performance" | string;
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
  severity: "info" | "warning" | "critical" | string;
  confidence: "low" | "medium" | "high" | string;
  createdAt?: string;
  updatedAt?: string;
  firstObservedAt?: string;
  lastObservedAt?: string;
  context?: OptimizationActivityContext | null;
  target: OptimizationReportTarget;
  evidence: OptimizationReportEvidence;
  suggestedActions: readonly OptimizationReportAction[];
}

export interface TrustedOptimizationTarget {
  id: string;
  targetType: string;
  targetKey: string;
  name: string;
  reportType: string;
  resourceKind: string;
  trustedAt: string;
  createdFromReportId: string;
  lastVerifiedAt?: string | null;
  state: string;
}

export interface ProtectedOptimizationTarget {
  id: string;
  targetType: string;
  targetKey: string;
  softwareId?: string | null;
  softwareName?: string | null;
  softwareKind?: string | null;
  protectedAt: string;
  protectionSource: string;
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


export type SystemProcessOutcome = 'Terminated' | 'DumpCreated' | 'BackendProtected' | 'CriticalProcessProtected' | 'IdentityChanged' | 'ProcessExited' | 'AccessDenied' | 'NativeFailure' | 'FileFailure' | 'RequestRejected';

export interface OperationSubject {
  targetKind: "Component" | "Dependency" | "Software" | "Path" | "AllVolumes";
  /** Target data recorded at acceptance; null means no name was recorded. */
  targetName: string | null;
  targetId: string | null;
  scope: string | null;
  mode: string | null;
  targetPath: string | null;
}
