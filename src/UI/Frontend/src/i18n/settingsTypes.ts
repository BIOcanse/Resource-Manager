import type { KnownAccessScope } from "../features/trustedAccess/trustedAccessTypes.ts";
import type { AppCrashDumpMode, AppCrashDumpType, AppBarColorMode, AppFontSmoothing, AppLanguageMode, AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset, AppPresetNumericSettingMode, AppPalette, AppResourceBarFrameRateMode, AppUiStyle, AppUpdateMode, ByteUnitMode, SettingsSection } from "../types.ts";
import type { AnimationStyleId } from "../presentation/animationStyle.ts";

export type ConcreteAppLanguageMode = Exclude<AppLanguageMode, "system">;

export interface LanguageOption {
  id: AppLanguageMode;
  label: string;
  nativeLabel: string;
}

/** 按钮只有这几种（docs/credits_sources.md）；界面按这个顺序排。 */
export const creditLinkKinds = ["official", "github", "docs", "nuget", "npm", "license"] as const;
export type CreditLinkKind = typeof creditLinkKinds[number];

/** 项目自己的官方说明：GitHub About 或官网的一句话，原语言逐字，source 是取自的页面。 */
export interface CreditDescription {
  text: string;
  source: string;
}

export interface CreditLink {
  kind: CreditLinkKind;
  /** 按钮上的固定词。 */
  label: string;
  /** 无障碍名称：按钮词 · 子项目名。 */
  accessibleName: string;
  href: string;
}

/** 一行致谢里的一个上游项目：它的官方说明（没有就是 null）和它的链接。 */
export interface CreditSubject {
  /** 子项目名；只有一个子项目且和行名相同时是 null，界面不重复写。 */
  name: string | null;
  description: CreditDescription | null;
  links: CreditLink[];
}

export interface CreditItem {
  name: string;
  role: string;
  subjects: CreditSubject[];
}

export interface CreditGroup {
  title: string;
  items: CreditItem[];
}

export interface CreditCatalogItem {
  group: keyof SettingsCopy["credits"]["groups"];
  name: string;
  role: keyof SettingsCopy["credits"]["roles"];
  subjects: Array<{
    name: string;
    description?: CreditDescription;
    links: Array<{ kind: CreditLinkKind; href: string }>;
  }>;
}

/** 设置页“通知”列出的种类。 */
export type NotificationKindKey = "memoryProtection" | "update" | "accessRequest" | "crashReport";

export interface NotificationsSettingsCopy {
  intro: string;
  kinds: Record<NotificationKindKey, { title: string; description: string }>;
  notifyTitle: string;
  notifyDescription: string;
  systemTitle: string;
  systemDescription: string;
  inAppTitle: string;
  inAppDescription: string;
  /** 开关的无障碍名称：种类和那一项，三类的同名开关要分得开。 */
  switchLabel: (kind: string, option: string) => string;
  /** 新访问请求的界面提示；program 是后端观察到的可执行文件的文件名。 */
  accessRequestNoticeTitle: string;
  accessRequestNoticeMessage: (name: string, program: string) => string;
  accessRequestNoticeOpen: string;
}

/**
 * 崩溃转储（docs/crash_dumps_2026-10-31.md）：设置 · 系统集成里的设置和历史记录、请求卡片、崩溃提示弹窗、
 * crash-report 通知的界面提示。
 */
export interface CrashDumpsCopy {
  title: string;
  description: string;
  modeTitle: string;
  modeDescription: string;
  modeOptions: Record<AppCrashDumpMode, { label: string; description: string }>;
  /** 实际生效的模式和它的来源，例如“当前：已开启（beta 版默认开启）”。 */
  effective: (mode: string, source: string) => string;
  effectiveModes: Record<"ask" | "on" | "off", string>;
  sourceUser: string;
  channelNames: Record<"beta" | "stable", string>;
  defaultModeNames: Record<"ask" | "on" | "off", string>;
  channelDefault: (channel: string, mode: string) => string;
  turnOff: string;
  turnOffDescription: string;
  dumpTypeTitle: string;
  dumpTypeDescription: string;
  dumpTypeOptions: Record<AppCrashDumpType, { label: string; description: string }>;
  keepCountTitle: string;
  keepCountDescription: string;
  statusTitle: string;
  status: Record<"notApplied" | "applied" | "mismatch" | "failed" | "skipped", string>;
  skipReasons: Record<"not-running-as-installed-service", string>;
  skipReasonOther: (reason: string) => string;
  writeFailed: (error: string) => string;
  readFailed: (error: string) => string;
  imageStates: Record<"written" | "absent" | "foreign", string>;
  /** 注册表里一个映像的实际键：类型、保留份数和目录。 */
  actualDetail: (dumpType: string, count: number | null, folder: string) => string;
  dumpTypeOther: (value: number) => string;
  historyTitle: string;
  historyDescription: string;
  historyEmpty: string;
  dumpFolderLabel: string;
  roles: Record<"service" | "window" | "resident", string>;
  program: (program: string, role: string) => string;
  timeLabel: string;
  versionLabel: string;
  versionUnknown: string;
  faultLabel: string;
  faultUnknown: string;
  dumpLabel: string;
  dumpNotEnabled: string;
  dumpMissing: string;
  sizeLabel: string;
  reportedLabel: string;
  reported: string;
  notReported: string;
  packageLabel: string;
  /** Both values are formatted storage sizes with units, rather than integer counts. */
  packageExceeds: (packageSizeText: string, attachmentLimitText: string) => string;
  openFolder: string;
  buildPackage: string;
  reportOnGitHub: string;
  delete: string;
  deleteConfirmTitle: string;
  deleteConfirmMessage: (program: string, time: string) => string;
  revealFailed: string;
  openFailed: string;
  actionFailed: string;
  requestTitle: string;
  requestWhat: string;
  requestWhere: (folder: string) => string;
  requestSize: string;
  requestContent: string;
  requestEnable: string;
  requestDecline: string;
  requestFailed: string;
  dialogTitle: string;
  dialogIntro: string;
  dialogCrashed: (program: string, time: string) => string;
  dialogIssueLink: string;
  dialogLocalPath: string;
  dialogLater: string;
  noticeTitle: string;
  noticeMessage: string;
  noticeOpen: string;
}

export interface SettingsCopy {
  updates: UpdatesCopy;
  notifications: NotificationsSettingsCopy;
  crashDumps: CrashDumpsCopy;
  navigationLabel: string;
  sections: Record<SettingsSection, string>;
  saveState: {
    saving: string;
    saved: string;
    constrained: string;
    partial: string;
    error: string;
    dirty: string;
    conflict: string;
    /** The settings owner rejected a value of the wrong JSON type; the header adds the rejected path. */
    invalidValueType: string;
  };
  loadState: {
    loading: string;
    errorTitle: string;
    errorDescription: string;
    staleTitle: string;
    staleDescription: string;
  };
  actions: {
    save: string;
    restoreDefaults: string;
    retry: string;
    reload: string;
    reapply: string;
  };
  // 选项的 description 是**可选的补充**，不是必填栏位。
  // 标题已经说清楚的选项（"开启""关闭"）再挂一句
  // "始终启用这项设置"只是把标题重说一遍，那是噪音不是帮助。
  performance: {
    preciseGpuPlacementTitle: string;
    preciseGpuPlacementDescription: string;
    preciseGpuPlacementModeOptions: {
      basic: { label: string; description: string };
      precise: { label: string; description: string };
    };
    automaticMemoryCleanupTitle: string;
    automaticMemoryCleanupDescription: string;
    physicalMemoryAutomaticCleanupLabel: string;
    virtualMemoryAutomaticCleanupLabel: string;
    memoryOptimizationTargetTitle: string;
    memoryOptimizationTargetDescription: string;
    physicalMemoryOptimizationTargetLabel: string;
    virtualMemoryOptimizationTargetLabel: string;
    gpuOverflowTitle: string;
    gpuOverflowDescription: string;
    gpuOverflowUsageLabel: string;
    gpuOverflowDedicatedMemoryLabel: string;
    realtimeActionBudgetTitle: string;
    realtimeActionBudgetDescription: string;
    realtimeActionBudgetModeOptions: Array<{ id: "fixed" | "percent"; label: string }>;
    realtimeActionBudgetCountLabel: string;
    realtimeActionBudgetPercentLabel: string;
    keepSmartRecordHistoryTitle: string;
    keepSmartRecordHistoryDescription: string;
    keepSmartRecordHistoryOptions: {
      on: { label: string; description: string };
      off: { label: string; description: string };
    };
    refreshCadenceTitle: string;
    refreshCadenceDescription: string;
    presetNumericModeOptions: Array<{ id: AppPresetNumericSettingMode; label: string; description: string }>;
    refreshCadencePresetLabel: string;
    refreshCadenceCustomLabel: string;
    refreshCadencePresetOptions: Array<{ id: AppLogicRefreshIntervalPreset; label: string; description: string }>;
    refreshCadenceItems: Record<AppLogicRefreshIntervalKey, { label: string; description: string }>;
  };
  appearance: {
    cacheDescription: string;
    resetCache: string;
    uiStyleTitle: string;
    uiStyleDescription: string;
    themeTitle: string;
    themeDescription: string;
    animationStyleTitle: string;
    animationStyleDescription: string;
    animationStyleOptions: Record<AnimationStyleId, { label: string; description: string }>;
    resourceBarFrameRateTitle: string;
    resourceBarFrameRateDescription: string;
    resourceBarFrameRateModeOptions: Array<{ id: AppResourceBarFrameRateMode; label: string; description: string }>;
    resourceBarFrameRateCustomLabel: string;
    barColorTitle: string;
    barColorDescription: string;
    byteUnitTitle: string;
    byteUnitDescription: string;
    fontSmoothingTitle: string;
    fontSmoothingDescription: string;
    languageTitle: string;
    languageDescription: string;
    settingsLanguageTitle: string;
    settingsLanguageDescription: string;
    languageSelectLabel: string;
    uiStyleOptions: Record<AppUiStyle, string>;
    // 所有风格的配色名称；每个风格列出哪些配色见 presentation/uiStyles.ts。
    themeOptions: Record<AppPalette, string>;
    barColorOptions: Record<AppBarColorMode, { label: string; description: string }>;
    byteUnitOptions: Record<ByteUnitMode, { label: string; description: string }>;
    fontSmoothingOptions: Record<AppFontSmoothing, { label: string; description: string }>;
  };
  systemIntegration: {
    crashRecoveryTitle: string;
    crashRecoveryDescription: string;
    crashRestartDelayTitle: string;
    crashRestartCountTitle: string;
    crashCounterResetDescription: string;
    crashCounterResetTitle: string;
    crashRecoveryActualTitle: string;
    crashRecoveryUnavailable: string;
    crashRecoveryApplied: string;
    crashRecoverySkipped: string;
    crashRecoveryFailed: string;
    crashRecoveryMismatch: string;
    crashRecoveryNonCrashWarning: string;
    crashRecoveryWriteFailed: string;
    crashRecoveryReadFailed: string;
    autoStartTitle: string;
    autoStartDescription: string;
    loginStartTitle: string;
    loginStartDescription: string;
    loginStartUnavailable: string;
    loginStartLoading: string;
    loginStartWriteFailed: string;
    loginStartRetry: string;
    taskManagerTitle: string;
    taskManagerDescription: string;
    forceTerminateTitle: string;
    forceTerminateDescription: string;
    forceTerminateWarning: string;
    performanceOverlayHotkeyTitle: string;
    performanceOverlayHotkeyDescription: string;
    hotkeyEmpty: string;
    hotkeyAddKey: string;
    hotkeyKeyLabel: string;
    hotkeyRemoveKey: string;
    hotkeyUnordered: string;
    hotkeyOrdered: string;
    publicServiceTitle: string;
    publicServiceDescription: string;
    publicFileIndexTitle: string;
    publicFileIndexDescription: string;
    publicDatabaseServiceTitle: string;
    publicDatabaseServiceDescription: string;
    publicAiModelCatalogTitle: string;
    publicAiModelCatalogDescription: string;
    lmStudioEndpointTitle: string;
    lmStudioEndpointDescription: string;
    lmStudioAutoStartTitle: string;
    lmStudioAutoStartDescription: string;
    aiGatewayTitle: string;
    aiGatewayDescription: string;
    aiGatewayOpenAiProfile: string;
    aiGatewayAnthropicProfile: string;
    aiGatewayNamePlaceholder: string;
    aiGatewayGenerate: string;
    aiGatewayGenerating: string;
    aiGatewayOneTimeTitle: string;
    aiGatewayOneTimeDescription: string;
    aiGatewayApiKeyLabel: string;
    aiGatewayBaseUrlLabel: string;
    aiGatewayCopy: string;
    aiGatewayRevoke: string;
    aiGatewayEmpty: string;
    aiGatewayLoadFailed: string;
    trustedAccess: TrustedAccessCopy;
  };
  debug: {
    debugModeTitle: string;
    debugModeDescription: string;
    debugLogTitle: string;
    debugLogDescription: string;
    hostManagerSmartCoordinatorScoreOnlyTitle: string;
    hostManagerSmartCoordinatorScoreOnlyDescription: string;
    hostManagerSmartCoordinatorPerformanceLogTitle: string;
    hostManagerSmartCoordinatorPerformanceLogDescription: string;
  };
  /** 设置 → 硬件控制：重新应用间隔这一行。 */
  hardwareControl: {
    keepAliveTitle: string;
    keepAliveDescription: string;
    reassertionTitle: string;
    reassertionDescription: string;
  };
  performanceOverlay: {
    intro: string;
    loading: string;
    loadFailed: string;
    emptyTitle: string;
    emptyDetail: (tab: string) => string;
    itemCount: (count: number) => string;
    position: (anchor: string) => string;
    open: string;
    openLabel: (name: string) => string;
    softwareMissing: string;
  };
  credits: {
    heroTitle: string;
    heroBody: string;
    dependencyListTitle: string;
    dependencyListBody: string;
    groups: {
      project: string;
      runtime: string;
      windows: string;
      data: string;
      build: string;
      hardware: string;
    };
    roles: {
      project: string;
      frontendFramework: string;
      icons: string;
      serialization: string;
      charts: string;
      webView: string;
      database: string;
      nativeCompiler: string;
      softwareCatalog: string;
      buildToolchain: string;
      typeSystem: string;
      buildRuntime: string;
      dotnetRuntime: string;
      hookLibrary: string;
      startupInjectionLibrary: string;
      traceLibrary: string;
      windowsManagement: string;
      nvidiaTelemetry: string;
      nvidiaExtension: string;
      amdTelemetry: string;
      signedHardwareAccess: string;
      hardwareBridge: string;
      externalReference: string;
      deviceIdDatabase: string;
    };
    /** 按钮上的固定词；GitHub、NuGet、npm 是名称，各语言相同。 */
    linkLabels: Record<CreditLinkKind, string>;
    /** 官方说明后面的“来源”链接。 */
    sourceLabel: string;
    dependencyColumns: {
      name: string;
      version: string;
      license: string;
      source: string;
    };
  };
}

export interface TrustedAccessCopy {
  title: string;
  description: string;
  loading: string;
  loadFailed: string;
  actionFailed: string;
  retry: string;
  /** API 边界归类出的失败原因；其他失败显示 loadFailed / actionFailed 等通用文案。 */
  failureReasons: Record<"unauthorized" | "scope-required" | "default-scope-denied", string>;
  defaultTitle: string;
  defaultDescription: string;
  defaultEnabled: string;
  defaultDisabled: string;
  defaultKeyName: string;
  defaultFilePath: string;
  /** 默认密钥文件的位置（相对安装目录，Windows 分隔符）。 */
  defaultFileLocation: string;
  defaultSecretLocalOnly: string;
  defaultAccessLossWarning: string;
  /** 默认密钥关闭后显示的恢复说明。 */
  defaultDisabledRecovery: string;
  enableDefault: string;
  disableDefault: string;
  regenerateDefault: string;
  confirmDisableTitle: string;
  confirmDisableMessage: string;
  confirmRegenerateTitle: string;
  confirmRegenerateMessage: string;
  confirmRotateTitle: (name: string) => string;
  confirmRotateMessage: string;
  confirmRevokeTitle: (name: string) => string;
  confirmRevokeMessage: string;
  createTitle: string;
  createDescription: string;
  nameLabel: string;
  namePlaceholder: string;
  createScopesLabel: string;
  noScopesAvailable: string;
  create: string;
  working: string;
  oneTimeTitle: string;
  oneTimeDescription: string;
  /** 一次性密钥出现时读屏播报的句子；密钥本身不进实时区域。 */
  oneTimeAnnouncement: string;
  apiKeyLabel: string;
  copy: string;
  copied: string;
  copyFailed: string;
  dismissSecret: string;
  createdAt: string;
  keysTitle: string;
  keysEmpty: string;
  revoke: string;
  rotate: string;
  rotateKeyLabel: (name: string) => string;
  revokeKeyLabel: (name: string) => string;
  requestsTitle: string;
  requestsDescription: string;
  reviewRequests: string;
  requestsLoading: string;
  requestsUnavailable: string;
  requestsLoadFailed: string;
  requestsStale: string;
  requestsEmpty: string;
  requestNamed: (name: string) => string;
  requestCount: (count: number) => string;
  expiresAt: string;
  observedExecutable: string;
  observedProcess: string;
  submittedAt: string;
  observedCallerIsInformational: string;
  requestScopesLabel: string;
  requestUnavailableScopeHelp: string;
  administratorHolderScope: string;
  approve: string;
  deny: string;
  denyReasonLabel: string;
  requestActionFailed: string;
  requestApproved: string;
  requestDenied: string;
  requestDeniedWithReason: (reason: string) => string;
  requestExpired: string;
  /** 一条决定结果：请求名称和结果句子。 */
  requestOutcome: (name: string, result: string) => string;
  denialReasons: Record<"not-approved" | "unsupported" | "duplicate" | "not-needed", string>;
  scopeLabels: Record<KnownAccessScope, string>;
  unknownScope: string;
}

export interface UpdatesCopy {
  modeTitle: string;
  modeDescription: string;
  modeOptions: Array<{ id: AppUpdateMode; label: string; description?: string }>;
  periodicTitle: string;
  periodicDescription: string;
  internetNote: string;
  offReason: string;
  intervalTitle: string;
  intervalLabel: (minimum: number, maximum: number) => string;
  manualCheckNote: string;
  waitingForInternet: string;
  availableVersion: string;
  nextCheck: string;
  nextCheckAfterInternet: string;
  nextCheckOff: string;
  connectedElapsed: string;
  noticeTitle: string;
  noticeMessage: (version: string) => string;
  noticeReadyMessage: (version: string) => string;
  noticeOpenUpdates: string;
  installedVersionTitle: string;
  unknownVersion: string;
  checking: string;
  checkForUpdates: string;
  historyIncomplete: string;
  historyStale: string;
  versionsTitle: string;
  versionsDescription: string;
  lastChecked: string;
  selectedTarget: string;
  prepare: string;
  confirmPrepare: (version: string) => string;
  prepareFailed: string;
  waitingForExit: string;
  stageLabel: string;
  unknownStage: string;
  stages: Record<string, string>;
  otherVersions: string;
  installedUnknown: string;
  stable: string;
  preview: string;
  noLatest: string;
  noStable: string;
  latest: string;
  latestStable: string;
  verified: string;
  unavailableReasons: Record<string, string>;
}

export type SettingsCopyPatch = {
  [Key in keyof SettingsCopy]?: SettingsCopy[Key] extends (...args: never[]) => unknown
    ? SettingsCopy[Key]
    : SettingsCopy[Key] extends Record<string, unknown>
      ? SettingsCopyPatchObject<SettingsCopy[Key]>
      : SettingsCopy[Key];
};

export type SettingsCopyPatchObject<T> = {
  [Key in keyof T]?: T[Key] extends (...args: never[]) => unknown
    ? T[Key]
    : T[Key] extends Record<string, unknown>
      ? SettingsCopyPatchObject<T[Key]>
      : T[Key];
};

export interface SettingsTextBundle extends SettingsCopy {
  language: ConcreteAppLanguageMode;
  // 按目录顺序排好的风格选项。
  uiStyleOptions: Array<{ id: AppUiStyle; label: string }>;
  // 按动画风格目录顺序排好的选项。
  animationStyleOptions: Array<{ id: AnimationStyleId; label: string; description: string }>;
  barColorOptions: Array<{ id: AppBarColorMode; label: string; description: string }>;
  byteUnitOptions: Array<{ id: ByteUnitMode; label: string; description: string }>;
  fontSmoothingOptions: Array<{ id: AppFontSmoothing; label: string; description: string }>;
  creditGroups: CreditGroup[];
}
