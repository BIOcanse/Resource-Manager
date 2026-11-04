import type { MetricFieldReading } from "./data/metricFields/metricFieldSource.ts";
import type { AnimationStyleId } from "./presentation/animationStyle.ts";
import type {
  HostManagerNativePlanStatus,
  HostManagerSchedulingExclusionReason,
  HostManagerSchedulingFact
} from "./data/optimization/hostManagerSchedulingFactCodes.ts";

export type { HostManagerNativePlanStatus, HostManagerSchedulingExclusionReason, HostManagerSchedulingFact };

/** Top-level pages. */
export type PageId =
  | "monitor"
  | "components"
  | "manage"
  | "control"
  | "details"
  | "settings";

export interface BackendMessage {
  /** Backend message domain. */
  domain: number;
  /** Message code within the domain. */
  code: number;
  /** Ordered factual arguments for rendering the message. */
  args: string[];
}

export interface BackendStartupCapabilities {
  profileId: string;
  readOnly: boolean;
  mutablePersistence: boolean;
  gpuLaunchInterceptionReconciliation: boolean;
  runtimeEffectOwners: boolean;
  publicServiceCoordination: boolean;
  optimizationRuntime: boolean;
}
export type ManagementRole = "dependency" | "support";
export type ManagementKind = "Dependency" | "Support" | "adapted" | "controlled" | "Unconfirmed" | "Unclassified" | "game" | "high_performance" | "other";
export type ManualSoftwareKind = "adapted" | "game" | "high_performance" | "other";
export type ResourceScaleMode = "capacity" | "active";
export type ResourceTableViewMode = "software" | "process" | "performance";
// Style and palette catalog: presentation/uiStyles.ts.
export type AppUiStyle = "standard" | "classic" | "soft" | "outline" | "terminal";

export type AppPalette =
  | "system" | "light" | "dark" | "lowContrast"
  | "paper" | "graphite" | "blueprint"
  | "phosphor" | "amber" | "ice";
// On-demand monitoring is enabled or disabled; legacy auto normalizes to enabled.
// display follows the screen; custom caps resource bars at resourceBarFrameRate.
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
export type AppPresetNumericSettingMode = "aotu" | "preset" | "custom";
export type AppLogicRefreshIntervalPreset = "responsive" | "balanced" | "lowPower" | "quiet";
export type AppLogicRefreshIntervalKey =
  | "monitor"
  | "resourceTable"
  | "localSystem";
/** Software details dialog tabs. */
export type SoftwareDetailTabId = "overview" | "policy" | "processes" | "overlay";

/** Settings tabs contain application-wide settings. */
export type SettingsSection = "appearance" | "systemIntegration" | "publicServices" | "trustedAccess" | "notifications" | "updates" | "debug" | "credits";
/** A settings section owns its reset, save, and reload scope across settings, management, or firmware pages. */
export type SettingsPanel = SettingsSection | "runtimePolicy" | "memory" | "memoryCpuTier" | "memoryProtection" | "cpuMonopolyProtection"
  | "hardwareControl" | "reportRules" | "performanceOverlay" | "crashDumps" | "crashDumpKeys";

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
  /** Device-field picker visibility and source mark; absent for other metrics. */
  readingSource?: ReadingSourceDisplay;
  /** Backend reading metadata; matching IDs identify alternative sources of one quantity. Absent without reading metadata. */
  reading?: MetricFieldReading | null;
  /** Backend-ordered component source candidates. Non-component catalog entries use an empty array; locally composed cards omit them. */
  candidateSources?: readonly MetricSourceCandidate[];
}

/** A raw field mapped to a component address. */
export interface MetricSourceCandidate {
  readonly fieldName: string;
  readonly deviceIdentity: string;
  readonly sourceId: string;
  readonly reading: MetricFieldReading | null;
  /** Uses declared capabilities only; missing facts mean unverified. */
  readonly sourceAvailability: "available" | "unsupported" | "unverified";
  /** Whether automatic selection currently uses this source; all are false before selection. */
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
  // Total in numericValue units, only for metrics with a meaningful total such as RAM or VRAM.
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
  /** Opaque catalog token; changes with catalog or reading-source metadata. Compare for equality only. */
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

/** Slot size: small = 1 unit, medium = 2, large = 3, extra-large fills the card. */
export type DashboardSlotSize = "small" | "medium" | "large" | "full";

/** A metric and its slot size; device fields and GPU metrics include bindings. */
export interface DashboardCardSlot {
  metricId: string;
  binding?: DashboardMetricBinding | null;
  size: DashboardSlotSize;
}

export interface DashboardCardSettings {
  id: string;
  /** Slots in top-to-bottom order; sizing rules are in features/monitor/cardSlots.ts. */
  slots: DashboardCardSlot[];
}

/** Legacy cache format, converted to slots on read; writes use slots. Remove with native-shell validation once supported caches no longer use it. */
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
  /** null or absent selects automatically by metricId; otherwise reads the explicit backend process field. Used by the two whole-machine memory bars. */
  source?: string | null;
}

export interface DashboardSettings {
  version: number;
  /** Read-only layout derived from current point templates and the catalog; never cached. */
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
  /** null or absent lets the backend select by column metric; otherwise reads the named process field. */
  source?: string | null;
}

/** Frontend cache/source result. Never an API wire contract. */
export interface DashboardSettingsResult {
  settings: DashboardSettings;
  updatedAt: string;
  source: { kind: "frontendCache" | "pointValues"; sourceVersion: number };
}

export interface AppSettings {
  memoryProtection?: import("./data/memoryProtection/memoryProtectionTypes.ts").HostMemoryProtectionSettings | null;
  /** CPU exclusive protection, present only when supplied by the backend; no frontend default. */
  cpuMonopolyProtection?: import("./data/cpuMonopolyProtection/cpuMonopolyProtection.ts").CpuMonopolyProtectionSettings;
  /** Memory/CPU tradeoff tier and per-item overrides. Saving replaces the section; removing an override clears it. */
  memoryCpuTier?: import("./data/memoryCpuTier/memoryCpuTierTypes.ts").MemoryCpuTierSettings;
  reports?: AppReportSettings;
  version?: string;
  /** With keepAlive off, controls apply once at startup; otherwise they repeat every reassertionIntervalSeconds. */
  control?: { keepAlive: boolean; reassertionIntervalSeconds: number };
  performance?: AppPerformanceSettings;
  appearance?: AppAppearanceSettings;
  systemIntegration?: AppSystemIntegrationSettings;
  debug?: AppDebugSettings;
  publicService?: AppLocalPublicServiceSettings;
  aiModelService?: AppAiModelServiceSettings;
  update?: AppUpdateSettings;
  /** Per-software overlay settings; each details-tab PATCH contains only the edited software entry. */
  performanceOverlay?: PerformanceOverlayAppSettings;
  /** Native-shell preference cache; excluded from backend settings requests. */
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
/** Backend report-rule threshold unit; count denotes an item count, such as queued storage requests. */
export const reportRuleDisplayUnits = ["celsius", "percent", "milliseconds", "minutes", "mebibytes", "gibibytes", "count"] as const;
export type ReportRuleDisplayUnit = typeof reportRuleDisplayUnits[number];
export interface ReportRuleSetting {
  name: string;
  reportType: string;
  comparison: "atOrAbove" | "atOrBelow";
  displayUnit: ReportRuleDisplayUnit;
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
/** Notify gates both delivery channels without clearing their choices: System uses Windows notifications, InApp uses frontend notices. */
export interface AppNotificationSettings {
  updateNotify: "on" | "off";
  updateInApp: "on" | "off";
  updateSystem: "on" | "off";
  accessRequestNotify: "on" | "off";
  accessRequestInApp: "on" | "off";
  accessRequestSystem: "on" | "off";
  memoryProtectionNotify: "on" | "off";
  memoryProtectionInApp: "on" | "off";
  memoryProtectionSystem: "on" | "off";
  cpuMonopolyProtectionNotify: "on" | "off";
  cpuMonopolyProtectionInApp: "on" | "off";
  crashReportNotify: "on" | "off";
  crashReportInApp: "on" | "off";
  crashReportSystem: "on" | "off";
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
  /** Whether restored scheduling records remain in history; disabled by default. */
  keepSmartRecordHistory: boolean;
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
  /** Count of objects with held adjustments; placement and process-policy changes count each process once. */
  appliedTargetCount: number;
  /** null admits runtime adjustments; a reason code closes admission to all new adjustments. */
  faultClosedReason: string | null;
  coordinator: HostManagerCoordinatorFacts | null;
  /** Failure of the latest completed scheduling segment; null after success or before completion. Independent of schedulerRunning and faultClosedReason. */
  failure: HostManagerCycleFailure | null;
  /** Facts excluded by the failed segment; zero for a successful segment. */
  factExclusionCount: number;
  /** Exclusion counts grouped by fact and reason. */
  factExclusionCounts: HostManagerFactExclusionCount[];
  /** Exclusion details, capped at 64 entries by the backend. */
  factExclusions: HostManagerFactExclusion[];
}

/** Diagnostic exception type; never parsed or displayed. */
export type HostManagerCycleFailure =
  | { kind: "cycle-failed"; exceptionType: string; nativePlanStatus: null }
  | { kind: "native-plan-failed"; exceptionType: string; nativePlanStatus: HostManagerNativePlanStatus };

export type HostManagerCycleFailureKind = HostManagerCycleFailure["kind"];

/** Grouped by fact and reason; a group may span GPU adapters, so it has no adapter ID. */
export interface HostManagerFactExclusionCount {
  fact: HostManagerSchedulingFact;
  reason: HostManagerSchedulingExclusionReason;
  count: number;
}

export interface HostManagerFactExclusion {
  processId: number | null;
  /** Backend ulong encoded as decimal text. */
  processStartKey: string | null;
  targetId: string | null;
  fact: HostManagerSchedulingFact;
  reason: HostManagerSchedulingExclusionReason;
  /** Opaque decimal-text GPU adapter ulong; null for exclusions unrelated to an adapter. */
  adapterKey: string | null;
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
  /** History sequence, state, and insertion time; all null when retention was disabled. In-ledger records are always active. */
  sequence: number | null;
  state: "active" | null;
  recordedAt: string | null;
}

/** Backend restoreResult for a scheduling record. */
export type SmartRecordRestoreResult = "restored" | "targetExited" | "ownershipLost" | "failed" | "unknown";

/** Scheduling history record with placement context, restore time, and result. */
export interface SmartRecordHistoryEntry {
  sequence: number;
  state: "history";
  kind: string;
  recordId: string;
  targetId: string;
  softwareId: string | null;
  resourceKind: string;
  appliedAt: string;
  processId: number | null;
  processStartKey: string | null;
  processName: string | null;
  recordedAt: string;
  restoredAt: string;
  restoreResult: SmartRecordRestoreResult;
}

/** Newest-first history page; null nextBeforeSequence means no older records. */
export interface SmartRecordHistoryPage {
  records: SmartRecordHistoryEntry[];
  nextBeforeSequence: number | null;
}

export interface HostManagerCoordinatorFacts {
  revision: string;
  pendingCount: number;
  inflightCount: number;
  reasonMask: string;
}

// Capacity radix: native uses 1024 for memory and 1000 for storage;
// binary always uses 1024 (KiB/MiB/GiB), decimal uses 1000 (kB/MB/GB).
export type ByteUnitMode = "native" | "binary" | "decimal";

// Device-field list presentation is computed by presentation/readingSources.ts.
export interface ReadingSourceDisplay {
  /** Whether this source appears in pickers and device details. */
  readonly listed: boolean;
  /** Source mark: preferred, pending, or null for no mark. */
  readonly mark: "preferred" | "pending" | null;
}

export interface AppAppearanceSettings {
  uiStyle: AppUiStyle;
  // Palette within the current style.
  theme: AppPalette;
  // Animation style from presentation/animationStyle.ts.
  animationStyle?: AnimationStyleId;
  resourceBarFrameRateMode?: AppResourceBarFrameRateMode;
  // Custom frames per second (10-240); retained but unused in display mode.
  resourceBarFrameRate?: number;
  barColorMode?: AppBarColorMode;
  fontSmoothing?: AppFontSmoothing;
  language?: AppLanguageMode;
  byteUnitMode?: ByteUnitMode;
}

export interface AppSystemIntegrationSettings {
  restartAfterCrashEnabled?: boolean;
  crashRestartDelaySeconds?: number;
  crashRestartCount?: number;
  autoStartEnabled: boolean;
  taskManagerShortcutReplacementEnabled: boolean;
  hotkeys: AppEditableHotkeySettings[];
  crashDumps?: AppCrashDumpSettings;
  /** Crash-dump keys, present only when supplied by the backend. */
  crashDumpKeys?: AppCrashDumpKeySettings;
  /** Device-restart record capacity; round-tripped unchanged by the UI. */
  deviceRestart?: AppDeviceRestartSettings;
}

/** The UI edits only enabled. Null dump1Keys/dump2Key use right Ctrl plus Scroll Lock twice; previousValues are backend-owned registry originals. */
export interface AppCrashDumpKeySettings {
  enabled: boolean;
  dump1Keys?: number | null;
  dump2Key?: number | null;
  previousValues?: unknown;
}

export interface AppDeviceRestartSettings {
  recordCapacity: number;
}

/** Actual registry state from top-level crashDumpKeys diagnostics. */
export interface CrashDumpKeyDiagnostics {
  readonly state: "takesEffectAfterRestart" | "off" | "restorePending" | "configurationMismatch" | "failed";
  readonly keyCombination: "rightCtrlScrollLockTwice" | "custom";
  readonly dump1Keys: number | null;
  readonly dump2Key: number | null;
  readonly takesEffectAfterRestart: boolean;
  readonly kernelDumpConfigured: boolean | null;
  readonly reason: string | null;
  readonly detail: number;
  readonly failedSlot: number;
  readonly rollbackDetail: number;
}

/** Default crash-dump mode follows the release channel: enabled for beta, ask for stable. Effective values and sources come from backend diagnostics. */
export type AppCrashDumpMode = "default" | "ask" | "on" | "off";
export type AppCrashDumpType = "mini" | "full";
export interface AppCrashDumpSettings {
  mode: AppCrashDumpMode;
  dumpType: AppCrashDumpType;
  /** Records and dumps retained per application; at least 1, bounded by backend keepCountMaximum. */
  keepCount: number;
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
  /** Resolved read interval from a custom value, selected tier, or automatic foreground tier. */
  readonly resolved: FrontendReadIntervals;
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

/** Desired and actual registry crash-dump settings supplied with backend settings results. */
export interface CrashDumpDiagnostics {
  state: "notApplied" | "applied" | "mismatch" | "failed" | "skipped";
  effectiveMode: "ask" | "on" | "off";
  modeSource: "user" | "channelDefault";
  defaultMode: "ask" | "on" | "off";
  channel: "beta" | "stable";
  dumpFolder: string;
  /** Backend keepCount limit from service_recovery.crash_reports.keep_count_maximum. */
  keepCountMaximum: number;
  images: readonly string[];
  actual: readonly {
    image: string;
    present: boolean;
    ownedByResourceManager: boolean;
    dumpFolder: string | null;
    dumpType: number | null;
    customDumpFlags: number | null;
    dumpCount: number | null;
  }[] | null;
  skipReason: string | null;
  writeError: string | null;
  readError: string | null;
}

export type BackendAppPerformanceSettings = Omit<AppPerformanceSettings,
  "monitorRefreshIntervalMs" | "resourceTableRefreshIntervalMs" | "localSystemRefreshIntervalMs">;
export type BackendAppSettings = Omit<AppSettings, "appearance" | "performance"> & { performance?: BackendAppPerformanceSettings };

/** Application settings keep their own backend disposition contract. */
export interface AppSettingsResult {
  updatedAt?: string;
  serviceRecovery?: ServiceRecoveryDiagnostics | null;
  crashDumps?: CrashDumpDiagnostics | null;
  crashDumpKeys?: CrashDumpKeyDiagnostics | null;
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
  /** JSON path of the rejected value, e.g. "$.debug.debugLogEnabled", sent with invalid-settings-value-type. */
  runtimeFailurePath?: string | null;
}

export interface ResourceProcessSegment {
  limit?: number | null;
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
  limit?: number | null;
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

/** Per-metric facts are independent across resource facts, bars, and process details. totalValue reads totalMetricId; processSumValue sums this frame. Signed residualValue = totalValue - processSumValue only for additive metrics; non-additive metrics have no residual row. Capture time and window belong to this metric. A null total includes an unavailableReason and no process rows. */
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
  /** Actual process field read by this bar; null for other metric kinds. */
  processFieldName: string | null;
  unit: string;
  scaleMode: ResourceScaleMode;
  /** Backend comparison base: capacity for CPU, memory, GPU usage, and VRAM; max(1, frame total) for disk/network throughput. */
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
  /** Column ID; resourceTableColumnLabel supplies localized headings. */
  id: string;
  unit: string;
  visible: boolean;
  sortable: boolean;
  width: number;
  /** Column source, as in ResourceTableColumnSettings; null selects automatically. */
  source: string | null;
  /** Actual processFieldName; null for other metric kinds or before data arrives. */
  processFieldName: string | null;
  /** This frame's additiveAcrossProcesses; null for non-metric columns or before data arrives. */
  additiveAcrossProcesses: boolean | null;
}

export interface ResourceTableValue {
  /** Same-unit limit published for this exact process instance or software; zero is a value. */
  limit?: number | null;
  integerValue?: string | null;
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
  sharedValue?: number | null;
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

/** Scope matches the query: table frames carry facts (wire v2); bar frames carry breakdowns (wire v10). */
export type ResourceMonitorSnapshot = ResourceFactsMonitorSnapshot | ResourceBarsMonitorSnapshot;

export interface ResourceFactsMonitorSnapshot {
  readonly scope: "table";
  capturedAt?: string;
  facts: import("./features/resourceTable/resourceFacts.ts").ResourceFacts;
}

export interface ResourceBarsMonitorSnapshot {
  readonly scope: "bars";
  capturedAt?: string;
  breakdown: ResourceBreakdownSnapshot;
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

/** Backend installer source kind; determines button labels and whether to ask for a version. */
export type ComponentInstallerSourceKind = "direct" | "githubRelease" | "manual";

/** Version option; unavailable options remain listed with a reason. */
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
  /** Publisher and version are facts; the frontend joins and elides them. */
  publisher?: string | null;
  version?: string | null;
  operations?: {
    canUninstall?: boolean;
    uninstallKind?: string;
    uninstallConstraint?: "none" | "empty_managed_root" | "missing_uninstall_entry" | "manual_registration" | "missing_strategy" | "self" | "portable";
  };
}

export interface SoftwareSourceFact {
  kind: "dependency" | "windows_registry" | "steam_library" | "self" | "adapter" | "controlled" | "portable" | "manual" | "identity_catalog" | "policy" | "runtime";
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
  hostMemoryProtectionNoDump: boolean;
  hostMemoryProtectionNeverTerminate: boolean;
  softwareId: string;
  softwareName: string;
  allowedProviders: string[];
  startupTargetGpu: GpuPlacementTarget;
  runtimeHotSwitchEnabled?: boolean | null;
  preferredRuntimeSwitchMethod: GpuPlacementRuntimeSwitchMethod;
  baseScoreOverride?: number | null;
  processOverrideAllowed: boolean;
  updatedAt?: string;
  // Each scheduling opt-out independently disables its domain, including promotion; protection only prevents demotion.
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
  /** Effective software base score and its five-layer source chain. */
  baseScore: BaseScoreResolution;
  /** Base scores and source chains by processKey over historical processes and saved process policies. */
  processBaseScores: Record<string, BaseScoreResolution>;
}

/** Five fixed source layers; later non-null layers override earlier ones. */
export type BaseScoreLayerSource = "type_default" | "catalog_group" | "catalog_process" | "user_software" | "user_process";

/** An empty layer has null value and key. Keys by layer: type, groupId, processEntryKey, softwareId, processKey. */
export interface BaseScoreLayer {
  source: BaseScoreLayerSource;
  value: number | null;
  key: string | null;
}

/** Effective value and source from the last non-null layer. */
export interface BaseScoreResolution {
  value: number;
  source: BaseScoreLayerSource;
  layers: BaseScoreLayer[];
}

/** Read-only backend overlay-module registration: none, partial, or complete. */
export type OverlayInjectionModules = "none" | "partial" | "complete";

/** Overlay item shares recording validation: kind is frames/software/metric; source is auto or a catalog source. intervalMs is null or 100-60000, always null for frames; syncPrecision is low/mid/high. */
export interface PerformanceOverlayItem {
  kind: "frames" | "software" | "metric";
  id: string;
  source: string;
  intervalMs: number | null;
  syncPrecision: "low" | "mid" | "high";
}

/** Backend-normalized software overlay entry; absence means disabled. Per-software editingConstraints define edit limits. */
export interface PerformanceOverlayEntry {
  enabled: boolean;
  mode: "external" | "injected";
  /** Overlay items in draw order, passed unchanged to the overlay subscription. */
  items: PerformanceOverlayItem[];
  sizeMode: "absolutePixels" | "windowRatio";
  fontSizePx: number;
  regionWidthRatio: number;
  regionHeightRatio: number;
  anchor: "topLeft" | "topRight" | "bottomLeft" | "bottomRight";
  marginPx: number;
}

/** Overlay settings keyed by case-insensitive software ID; the backend validates the entry count. */
export interface PerformanceOverlayAppSettings {
  software: Record<string, PerformanceOverlayEntry>;
}

/** Per-software backend entry or disabled default, with settings revision and module registration. */
export interface PerformanceOverlaySettings extends PerformanceOverlayEntry {
  softwareId: string;
  /** Backend settings revision, sent as expectedRevision when saving. */
  revision: string;
  /** Whether a stored entry exists; false means the other fields contain the backend disabled default. */
  stored: boolean;
  injectionModules: OverlayInjectionModules;
}

/** Read-only per-software GET constraints; neither persisted nor included in PATCH. */
export interface PerformanceOverlayEditingConstraints {
  readonly maximumItems: number;
  readonly fontSizePxMinimum: number;
  readonly fontSizePxMaximum: number;
  readonly marginPxMinimum: number;
  readonly marginPxMaximum: number;
  readonly regionRatioMinimum: number;
  readonly regionRatioMaximum: number;
  readonly regionRatioStep: number;
}

export interface PerformanceOverlayEditorSettings extends PerformanceOverlaySettings {
  readonly editingConstraints: PerformanceOverlayEditingConstraints;
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
  /** Device-reported name; null for generated names, which use displayNameCode. */
  displayName: string | null;
  connectorKind: string;
  busKind: string;
  hardwareKind: string | null;
  protocol: string | null;
  speed: string | null;
  physicalMaximumSpeed?: string | null;
  deviceId: string;
  /** Device-information address used for sampled-field subscriptions; null when this port has no information object. */
  informationIdentity?: string | null;
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
  /** Generated node-name code; null when the device supplies its name. */
  displayNameCode?: BackendMessage | null;
  /** Generated hardware-kind code; null when hardwareKind supplies a technical name. */
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

  /** Interface name from the model profile; null without a profile. */
  connectorTechnology: string | null;
  /** Windows output-technology ID; wording belongs to the frontend. */
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
/** Raw DisplayID 1.3 Type V option declarations; Type V does not generate timings. */
export interface DisplayEdidTypeVOptions { formulaCode: number; stereoSupportCode: number; fractionalRefreshSupported: boolean }
/** DisplayID 1.3 Type VI declarations; conflicting clock high bits make only pixelClockHz null. */
export interface DisplayEdidTypeViTiming {
  pixelClockHz: number | null;
  horizontalBlankingPixels: number; horizontalFrontPorchPixels: number; horizontalSyncWidthPixels: number;
  verticalBlankingLines: number; verticalFrontPorchLines: number; verticalSyncWidthLines: number;
  positiveHorizontalSync: boolean; positiveVerticalSync: boolean; stereoSupportCode: number;
}
/** Optional Type VI 17-byte tail: aspect multiplier (ratio = multiplier * 3:256) and vertical image size in mm. */
export interface DisplayEdidTypeViImageSize {
  aspectMultiplier: number; verticalImageBaseSizeMillimeters: number;
  sizeMultiplier: number; verticalImageSizeMillimeters: number;
}
export interface DisplayEdidMode extends DisplayEdidReference {
  encoding: string; revision: number; code: number | null; width: number | null; height: number | null;
  aspectRatioDeclared?: DisplayEdidAspectRatio | null;
  /** Emitted only for matching Type V/VI declarations; omitted properties remain absent in older payloads. */
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
/** Original descriptor location and container within a DisplayID declaration block. */
export interface DisplayEdidDisplayIdSource extends Omit<DisplayEdidReference, "bitIndex"> {
  revision: number; containerFlags: number; containerRevision: number;
}
/** DisplayID 2.0 tag 0x25 dynamic timing declaration, distinct from measured refresh or available system modes. */
export interface DisplayEdidDynamicTimingRange extends DisplayEdidDisplayIdSource {
  minimumPixelClockHz: number; maximumPixelClockHz: number;
  minimumVerticalRateHz: number; maximumVerticalRateHz: number;
  seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking: boolean;
}
/** Maximum frame-duration step in exact microseconds; null encodes zero, allowing any step within the range without jitter. */
export interface DisplayEdidAdaptiveSyncTolerance {
  flickerRestrictedToSingleStep: boolean; maximumSingleStepMicroseconds: number | null;
  zeroCodeMeansNoJitterThroughoutRange: boolean;
}
/** DisplayID tag 0x2B Adaptive-Sync declaration; base limits differ from the effective range for a selected timing. */
export interface DisplayEdidAdaptiveSyncRange extends DisplayEdidDisplayIdSource {
  baseMinimumRefreshRateHz: number; baseMaximumRefreshRateHz: number;
  nativePanelRange: boolean; adaptiveVTotalSupported: boolean; seamlessTransitionSupported: boolean;
  frameDurationIncrease: DisplayEdidAdaptiveSyncTolerance; frameDurationDecrease: DisplayEdidAdaptiveSyncTolerance;
}
export interface DisplayEdidCapability {
  modes: DisplayEdidMode[]; ranges: DisplayEdidRange[]; edidRevision: number; baseFeatures: number;
  ctaFlags: number | null; selectionDeclarations: DisplayEdidSelection[] | null;
  /** Keep both collections absent for older payloads to preserve their display behavior. */
  dynamicTimingRanges?: DisplayEdidDynamicTimingRange[];
  adaptiveSyncRanges?: DisplayEdidAdaptiveSyncRange[];
}


export interface DeviceTopologyNetworkConnection {
  interfaceName?: string | null;
  /** Backend DeviceNetworkConnectionStates ID; wording belongs to the frontend. */
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
  /** Battery charge is sampled separately: subscribe to mobile_all_battery_percent_reported at the port informationIdentity. */
  storages: DeviceTopologySmartDeviceStorage[];
  source: BackendMessage;
}

/** Free storage is sampled separately: subscribe to mobile_all_storage_free_bytes_reported at the port informationIdentity and match by name. */
export interface DeviceTopologySmartDeviceStorage {
  name: string;
  capacityBytes?: number | null;
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

/** Free space is sampled separately at the volume device address via volume_all_free_bytes_actual. */
export interface DeviceTopologyStorageVolume {
  driveLetter?: string | null;
  label?: string | null;
  fileSystem?: string | null;
  capacityBytes?: number | null;
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
  /** Backend dedicated-VRAM capability: true = present, false = shared-only, null = unknown. */
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
  /** Active entries only; history is excluded. */
  activeReportCount: number;
  /** Currently matching candidates omitted because entry capacity is full. */
  unrecordedReportCount: number;
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
  /** Physical-disk evidence only for storage_not_responding and storage_slow reports. */
  storageResponse?: OptimizationStorageResponseEvidence | null;
  resourceKind: string;
  averageValue: number;
  peakValue: number;
  currentValue: number;
  // Raw units: %, B (bytes), degrees Celsius, ms, or count.
  // The backend sends facts; optimizationPresentation.ts owns sentences and conversions.
  valueUnit: string;
  activeSampleCount: number;
  sampleCount: number;
  durationSeconds: number;
}

/** Stable evidence-gap codes; an unproven fact is null and its reason appears here. */
export const storageResponseUnavailableEvidenceCodes = [
  "physical_disk_volume_relation_unavailable",
  "page_file_location_unavailable",
  "memory_protection_reserve_location_unavailable",
  "resource_manager_data_location_unavailable"
] as const;
export type StorageResponseUnavailableEvidence = typeof storageResponseUnavailableEvidenceCodes[number];

/** Physical-disk evidence; missing numbers are null, including latency with no completions. Pagefile, protection reserve, and RM-data presence are true/false/null (unproven). */
export interface OptimizationStorageResponseEvidence {
  physicalDiskIdentity: string;
  queueLength: number | null;
  completedReadOperationsPerSecond: number | null;
  completedWriteOperationsPerSecond: number | null;
  latencyMilliseconds: number | null;
  holdsPageFile: boolean | null;
  holdsMemoryProtectionReserve: boolean | null;
  holdsResourceManagerData: boolean | null;
  unavailableEvidence: readonly StorageResponseUnavailableEvidence[];
}

export interface OptimizationReportAction {
  id: string;
  kind: string;
  enabled: boolean;
  /** Requires user confirmation before execution; true for restart-device, false when omitted by older replies. */
  requiresConfirmation: boolean;
  /** Risk codes for the confirmation dialog; empty when omitted by older replies. */
  riskCodes: readonly string[];
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

/** Active while the condition holds, then retained as history rather than deleted. */
export type ReportEntryState = "active" | "history";

export interface OptimizationReportItem {
  id: string;
  type: string;
  state: ReportEntryState;
  /** Target trust is independent of lifecycle; active and historical entries can both be trusted. */
  trusted: boolean;
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
  /** 0 disables protection; 1 prevents freezing. */
  protectionLevel: number;
  /** Replaced default level and source; both present together or both null. */
  defaultProtectionLevel?: number | null;
  defaultProtectionSource?: string | null;
  /** Software catalog group supplying this entry or its replaced default. */
  policyGroupId?: string | null;
  /** Process entry matches an image name and, optionally, a full path. */
  processName?: string | null;
  executablePath?: string | null;
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
