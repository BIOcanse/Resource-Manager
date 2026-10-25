import type { KnownAccessScope } from "../features/trustedAccess/trustedAccessTypes.ts";
import type { AppAnimationMode, AppBarColorMode, AppFontSmoothing, AppLanguageMode, AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset, AppPresetNumericSettingMode, AppPalette, AppReadingSourceMode, AppResourceBarFrameRateMode, AppUiStyle, AppUpdateMode, ByteUnitMode, SettingsSection } from "../types.ts";

export type ConcreteAppLanguageMode = Exclude<AppLanguageMode, "system">;

export interface LanguageOption {
  id: AppLanguageMode;
  label: string;
  nativeLabel: string;
}

export interface CreditLink {
  label: string;
  href: string;
}

export interface CreditItem {
  name: string;
  role: string;
  note: string;
  links: CreditLink[];
}

export interface CreditGroup {
  title: string;
  items: CreditItem[];
}

export interface CreditCatalogItem {
  group: keyof SettingsCopy["credits"]["groups"];
  name: string;
  role: keyof SettingsCopy["credits"]["roles"];
  note: keyof SettingsCopy["credits"]["notes"];
  // subject：同一项里有两个同类链接（例如两个官网）时，写明各自指向哪个项目，用来区分显示。
  links: Array<{ label: keyof SettingsCopy["credits"]["linkLabels"]; href: string; subject?: string }>;
}

export interface SettingsCopy {
  updates: UpdatesCopy;
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
    animationTitle: string;
    animationDescription: string;
    resourceBarFrameRateTitle: string;
    resourceBarFrameRateDescription: string;
    resourceBarFrameRateModeOptions: Array<{ id: AppResourceBarFrameRateMode; label: string; description: string }>;
    resourceBarFrameRateCustomLabel: string;
    barColorTitle: string;
    barColorDescription: string;
    byteUnitTitle: string;
    byteUnitDescription: string;
    readingSourceTitle: string;
    readingSourceDescription: string;
    readingSourceModeOptions: Array<{ id: AppReadingSourceMode; label: string; description: string }>;
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
    animationOptions: Record<AppAnimationMode, { label: string; description: string }>;
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
    notes: {
      windowsFoundation: string;
      microsoftTools: string;
      managedServices: string;
      nativeToolchain: string;
      buildContributors: string;
      testContributors: string;
      upstreamContributors: string;
      resourceManager: string;
      solid: string;
      lucide: string;
      seroval: string;
      webView2: string;
      sqlite: string;
      zig: string;
      softwareCatalog: string;
      vite: string;
      typescript: string;
      node: string;
      dotnet: string;
      minHook: string;
      detours: string;
      traceEvent: string;
      systemManagement: string;
      nvml: string;
      nvapi: string;
      adlx: string;
      pawnIo: string;
      pawnIoModules: string;
      libreHardwareMonitor: string;
      blacktempelToolkits: string;
      hidSharp: string;
      monoPosix: string;
      usbIds: string;
      pciIds: string;
    };
    linkLabels: {
      official: string;
      github: string;
      docs: string;
      license: string;
      nuget: string;
      gpuOpen: string;
      eula: string;
      runtime: string;
      aspnet: string;
      vite: string;
      solidPlugin: string;
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
  enableDefaultElevated: string;
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
  inAppNoticeTitle: string;
  inAppNoticeDescription: string;
  systemNoticeTitle: string;
  systemNoticeDescription: string;
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
  animationOptions: Array<{ id: AppAnimationMode; label: string; description: string }>;
  barColorOptions: Array<{ id: AppBarColorMode; label: string; description: string }>;
  byteUnitOptions: Array<{ id: ByteUnitMode; label: string; description: string }>;
  fontSmoothingOptions: Array<{ id: AppFontSmoothing; label: string; description: string }>;
  creditGroups: CreditGroup[];
}
