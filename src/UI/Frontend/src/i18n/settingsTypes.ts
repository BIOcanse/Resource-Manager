import type { KnownAccessScope } from "../features/trustedAccess/trustedAccessTypes.ts";
import type { AppCrashDumpMode, AppCrashDumpType, AppBarColorMode, AppFontSmoothing, AppLanguageMode, AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset, AppPresetNumericSettingMode, AppPalette, AppResourceBarFrameRateMode, AppUiStyle, AppUpdateMode, ByteUnitMode, SettingsPanel } from "../types.ts";
import type { AnimationStyleId } from "../presentation/animationStyle.ts";

export type ConcreteAppLanguageMode = Exclude<AppLanguageMode, "system">;

export interface LanguageOption {
  id: AppLanguageMode;
  label: string;
  nativeLabel: string;
}

/** Credit link kinds in display order. */
export const creditLinkKinds = ["official", "github", "docs", "nuget", "npm", "license"] as const;
export type CreditLinkKind = typeof creditLinkKinds[number];

/** Verbatim official project description and its source page. */
export interface CreditDescription {
  text: string;
  source: string;
}

export interface CreditLink {
  kind: CreditLinkKind;
  /** Fixed button label. */
  label: string;
  /** Accessible name combining the action and project name. */
  accessibleName: string;
  href: string;
}

/** Upstream project with an optional official description and links. */
export interface CreditSubject {
  /** Omit a single project name when it matches the credit row. */
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

/** Notification categories listed in settings. */
export type NotificationKindKey = "memoryProtection" | "cpuMonopolyProtection" | "update" | "accessRequest" | "crashReport";

export interface NotificationsSettingsCopy {
  intro: string;
  kinds: Record<NotificationKindKey, { title: string; description: string }>;
  notifyTitle: string;
  notifyDescription: string;
  systemTitle: string;
  systemDescription: string;
  inAppTitle: string;
  inAppDescription: string;
  /** Distinguish identically named switches by category and option. */
  switchLabel: (kind: string, option: string) => string;
  /** Access request notice; program is the observed executable filename. */
  accessRequestNoticeTitle: string;
  accessRequestNoticeMessage: (name: string, program: string) => string;
  accessRequestNoticeOpen: string;
}

/** Crash dump settings, history, request cards, dialogs and notifications. */
export interface CrashDumpsCopy {
  title: string;
  description: string;
  modeTitle: string;
  modeDescription: string;
  modeOptions: Record<AppCrashDumpMode, { label: string; description: string }>;
  /** Effective crash dump mode and its source. */
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
  /** Actual registry settings for one executable image. */
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
  /** Formatted storage sizes, including units. */
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
  /** Settings section names; crash dump controls retain separate headings. */
  sections: Record<Exclude<SettingsPanel, "crashDumps" | "crashDumpKeys">, string>;
  saveState: {
    saving: string;
    saved: string;
    constrained: string;
    partial: string;
    error: string;
    dirty: string;
    conflict: string;
    /** Rejected JSON type; the header includes the rejected path. */
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
  // Descriptions add information beyond the option title.


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
    // Color scheme names for every interface style.
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
  /** Hardware control reapplication interval. */
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
    /** Fixed button labels; service names remain untranslated. */
    linkLabels: Record<CreditLinkKind, string>;
    /** Source link following an official description. */
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
  /** Classified API failures; other failures use generic messages. */
  failureReasons: Record<"unauthorized" | "scope-required" | "default-scope-denied", string>;
  defaultTitle: string;
  defaultDescription: string;
  defaultEnabled: string;
  defaultDisabled: string;
  defaultKeyName: string;
  defaultFilePath: string;
  /** Default key path relative to the installation directory, with Windows separators. */
  defaultFileLocation: string;
  defaultSecretLocalOnly: string;
  defaultAccessLossWarning: string;
  /** Recovery instructions when the default key is disabled. */
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
  /** Announce one-time key availability without exposing the key in a live region. */
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
  /** Decision result with the request name and outcome. */
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
  /** Time the release catalog was read. */
  catalogCheckedAt: string;
  /** Time of the latest update check. */
  lastUpdateCheck: string;
  selectedTarget: string;
  prepare: string;
  confirmPrepare: (version: string) => string;
  prepareFailed: string;
  waitingForExit: string;
  stageLabel: string;
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
  // Interface style options in catalog order.
  uiStyleOptions: Array<{ id: AppUiStyle; label: string }>;
  // Animation style options in catalog order.
  animationStyleOptions: Array<{ id: AnimationStyleId; label: string; description: string }>;
  barColorOptions: Array<{ id: AppBarColorMode; label: string; description: string }>;
  byteUnitOptions: Array<{ id: ByteUnitMode; label: string; description: string }>;
  fontSmoothingOptions: Array<{ id: AppFontSmoothing; label: string; description: string }>;
  creditGroups: CreditGroup[];
}
