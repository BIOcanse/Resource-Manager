import { retryableLocaleImport } from "./retryableLocaleImport.ts";
import type { AppBarColorMode, AppFontSmoothing, ByteUnitMode } from "../types.ts";
import { uiStyles } from "../presentation/uiStyles.ts";
import { animationStyles } from "../presentation/animationStyle.ts";
import { createCreditGroups } from "./settingsCredits.ts";
import { resolveLanguageMode } from "./settingsLanguages.ts";
import { createSettingsLocale } from "./settingsLocaleFactory.ts";
import { loadingUpdatesCopy } from "./settingsUpdateCopy.ts";
import { enCrashDumpsCopy } from "./crashDumpsCopy.ts";
import { createLanguageBundleLoader } from "./languageBundleLoader.ts";
import type { ConcreteAppLanguageMode, SettingsCopy, SettingsTextBundle } from "./settingsTypes.ts";

type SettingsLocaleModule = { default: SettingsCopy };
type SettingsLocaleLoader = () => Promise<SettingsLocaleModule>;

const localeLoaders = {
  "zh-CN": retryableLocaleImport(() => import("./settingsLocales/zh-CN.ts")),
  "zh-TW": retryableLocaleImport(() => import("./settingsLocales/zh-TW.ts")),
  "en-US": retryableLocaleImport(() => import("./settingsLocales/en-US.ts")),
  "ja-JP": retryableLocaleImport(() => import("./settingsLocales/ja-JP.ts")),
  "ko-KR": retryableLocaleImport(() => import("./settingsLocales/ko-KR.ts")),
  "fr-FR": retryableLocaleImport(() => import("./settingsLocales/fr-FR.ts")),
  "de-DE": retryableLocaleImport(() => import("./settingsLocales/de-DE.ts")),
  "es-ES": retryableLocaleImport(() => import("./settingsLocales/es-ES.ts")),
  "es-MX": retryableLocaleImport(() => import("./settingsLocales/es-MX.ts")),
  "pt-BR": retryableLocaleImport(() => import("./settingsLocales/pt-BR.ts")),
  "pt-PT": retryableLocaleImport(() => import("./settingsLocales/pt-PT.ts")),
  "ru-RU": retryableLocaleImport(() => import("./settingsLocales/ru-RU.ts")),
  "uk-UA": retryableLocaleImport(() => import("./settingsLocales/uk-UA.ts")),
  "pl-PL": retryableLocaleImport(() => import("./settingsLocales/pl-PL.ts")),
  "tr-TR": retryableLocaleImport(() => import("./settingsLocales/tr-TR.ts")),
  "it-IT": retryableLocaleImport(() => import("./settingsLocales/it-IT.ts")),
  "nl-NL": retryableLocaleImport(() => import("./settingsLocales/nl-NL.ts")),
  "sv-SE": retryableLocaleImport(() => import("./settingsLocales/sv-SE.ts")),
  "fi-FI": retryableLocaleImport(() => import("./settingsLocales/fi-FI.ts")),
  "da-DK": retryableLocaleImport(() => import("./settingsLocales/da-DK.ts")),
  "nb-NO": retryableLocaleImport(() => import("./settingsLocales/nb-NO.ts")),
  "cs-CZ": retryableLocaleImport(() => import("./settingsLocales/cs-CZ.ts")),
  "hu-HU": retryableLocaleImport(() => import("./settingsLocales/hu-HU.ts")),
  "ro-RO": retryableLocaleImport(() => import("./settingsLocales/ro-RO.ts")),
  "el-GR": retryableLocaleImport(() => import("./settingsLocales/el-GR.ts")),
  "he-IL": retryableLocaleImport(() => import("./settingsLocales/he-IL.ts")),
  "ar-SA": retryableLocaleImport(() => import("./settingsLocales/ar-SA.ts")),
  "hi-IN": retryableLocaleImport(() => import("./settingsLocales/hi-IN.ts")),
  "id-ID": retryableLocaleImport(() => import("./settingsLocales/id-ID.ts")),
  "vi-VN": retryableLocaleImport(() => import("./settingsLocales/vi-VN.ts")),
  "th-TH": retryableLocaleImport(() => import("./settingsLocales/th-TH.ts"))
} satisfies Record<ConcreteAppLanguageMode, SettingsLocaleLoader>;


export const loadingSettingsText: SettingsTextBundle = {
  updates: loadingUpdatesCopy,
  notifications: { intro: "", kinds: { memoryProtection: { title: "", description: "" }, update: { title: "", description: "" },
    accessRequest: { title: "", description: "" }, crashReport: { title: "", description: "" } }, notifyTitle: "", notifyDescription: "", systemTitle: "", systemDescription: "",
    inAppTitle: "", inAppDescription: "", switchLabel: () => "", accessRequestNoticeTitle: "",
    accessRequestNoticeMessage: () => "", accessRequestNoticeOpen: "" },
  crashDumps: blankCopy(enCrashDumpsCopy),
  language: "zh-CN",
  navigationLabel: "",
  sections: { runtimePolicy: "", memory: "", memoryCpuTier: "", memoryProtection: "", hardwareControl: "", reportRules: "", appearance: "", performanceOverlay: "", systemIntegration: "", publicServices: "", trustedAccess: "", notifications: "", updates: "", debug: "", credits: "" },
  saveState: { saving: "", saved: "", constrained: "", partial: "", error: "", dirty: "", conflict: "", invalidValueType: "" },
  loadState: {
    loading: "",
    errorTitle: "",
    errorDescription: "",
    staleTitle: "",
    staleDescription: ""
  },
  actions: { save: "", restoreDefaults: "", retry: "", reload: "", reapply: "" },
  performance: {
    preciseGpuPlacementTitle: "",
    preciseGpuPlacementDescription: "",
    preciseGpuPlacementModeOptions: {
      basic: { label: "", description: "" },
      precise: { label: "", description: "" }
    },
    automaticMemoryCleanupTitle: "",
    automaticMemoryCleanupDescription: "",
    physicalMemoryAutomaticCleanupLabel: "",
    virtualMemoryAutomaticCleanupLabel: "",
    memoryOptimizationTargetTitle: "",
    memoryOptimizationTargetDescription: "",
    physicalMemoryOptimizationTargetLabel: "",
    virtualMemoryOptimizationTargetLabel: "",
    gpuOverflowTitle: "",
    gpuOverflowDescription: "",
    gpuOverflowUsageLabel: "",
    gpuOverflowDedicatedMemoryLabel: "",
    realtimeActionBudgetTitle: "",
    realtimeActionBudgetDescription: "",
    realtimeActionBudgetModeOptions: [],
    realtimeActionBudgetCountLabel: "",
    realtimeActionBudgetPercentLabel: "",
    keepSmartRecordHistoryTitle: "",
    keepSmartRecordHistoryDescription: "",
    keepSmartRecordHistoryOptions: {
      on: { label: "", description: "" },
      off: { label: "", description: "" }
    },
    refreshCadenceTitle: "",
    refreshCadenceDescription: "",
    presetNumericModeOptions: [],
    refreshCadencePresetLabel: "",
    refreshCadenceCustomLabel: "",
    refreshCadencePresetOptions: [],
    refreshCadenceItems: {
      monitor: { label: "", description: "" },
      resourceTable: { label: "", description: "" },
      localSystem: { label: "", description: "" }
    }
  },
  appearance: {
    cacheDescription: "",
    resetCache: "",
    uiStyleTitle: "",
    uiStyleDescription: "",
    themeTitle: "",
    themeDescription: "",
    animationStyleTitle: "",
    animationStyleDescription: "",
    animationStyleOptions: {
      smooth: { label: "", description: "" },
      desktop: { label: "", description: "" },
      terminal: { label: "", description: "" },
      none: { label: "", description: "" },
      ultra: { label: "", description: "" }
    },
    resourceBarFrameRateTitle: "",
    resourceBarFrameRateDescription: "",
    resourceBarFrameRateModeOptions: [],
    resourceBarFrameRateCustomLabel: "",
    barColorTitle: "",
    byteUnitTitle: "",
    byteUnitDescription: "",
    barColorDescription: "",
    fontSmoothingTitle: "",
    fontSmoothingDescription: "",
    languageTitle: "",
    languageDescription: "",
    settingsLanguageTitle: "",
    settingsLanguageDescription: "",
    languageSelectLabel: "",
    uiStyleOptions: { standard: "", classic: "", soft: "", outline: "", terminal: "" },
    themeOptions: {
      system: "", light: "", dark: "", lowContrast: "",
      paper: "", graphite: "", blueprint: "",
      phosphor: "", amber: "", ice: ""
    },
    barColorOptions: {
      type: { label: "", description: "" },
      distinct: { label: "", description: "" }
    },
    byteUnitOptions: {
      native: { label: "", description: "" },
      binary: { label: "", description: "" },
      decimal: { label: "", description: "" }
    },
    fontSmoothingOptions: {
      auto: { label: "", description: "" },
      system: { label: "", description: "" },
      grayscale: { label: "", description: "" },
      disabled: { label: "", description: "" }
    },
  },
  systemIntegration: {
    crashRecoveryTitle: "",
    crashRecoveryDescription: "",
    crashRestartDelayTitle: "",
    crashRestartCountTitle: "",
    crashCounterResetDescription: "",
    crashCounterResetTitle: "",
    crashRecoveryActualTitle: "",
    crashRecoveryUnavailable: "",
    crashRecoveryApplied: "",
    crashRecoverySkipped: "",
    crashRecoveryFailed: "",
    crashRecoveryMismatch: "",
    crashRecoveryNonCrashWarning: "",
    crashRecoveryWriteFailed: "",
    crashRecoveryReadFailed: "",
    autoStartTitle: "",
    autoStartDescription: "",
    loginStartTitle: "",
    loginStartDescription: "",
    loginStartUnavailable: "",
    loginStartLoading: "",
    loginStartWriteFailed: "",
    loginStartRetry: "",
    taskManagerTitle: "",
    taskManagerDescription: "",
    forceTerminateTitle: "",
    forceTerminateDescription: "",
    forceTerminateWarning: "",
    performanceOverlayHotkeyTitle: "",
    performanceOverlayHotkeyDescription: "",
    hotkeyEmpty: "",
    hotkeyAddKey: "",
    hotkeyKeyLabel: "",
    hotkeyRemoveKey: "",
    hotkeyUnordered: "",
    hotkeyOrdered: "",
    publicServiceTitle: "",
    publicServiceDescription: "",
    publicFileIndexTitle: "",
    publicFileIndexDescription: "",
    publicDatabaseServiceTitle: "",
    publicDatabaseServiceDescription: "",
    publicAiModelCatalogTitle: "",
    publicAiModelCatalogDescription: "",
    lmStudioEndpointTitle: "",
    lmStudioEndpointDescription: "",
    lmStudioAutoStartTitle: "",
    lmStudioAutoStartDescription: "",
    aiGatewayTitle: "",
    aiGatewayDescription: "",
    aiGatewayOpenAiProfile: "",
    aiGatewayAnthropicProfile: "",
    aiGatewayNamePlaceholder: "",
    aiGatewayGenerate: "",
    aiGatewayGenerating: "",
    aiGatewayOneTimeTitle: "",
    aiGatewayOneTimeDescription: "",
    aiGatewayApiKeyLabel: "",
    aiGatewayBaseUrlLabel: "",
    aiGatewayCopy: "",
    aiGatewayRevoke: "",
    aiGatewayEmpty: "",
    aiGatewayLoadFailed: "",
    trustedAccess: {
      title: "",
      description: "",
      loading: "",
      loadFailed: "",
      actionFailed: "",
      retry: "",
      failureReasons: { unauthorized: "", "scope-required": "", "default-scope-denied": "" },
      defaultTitle: "",
      defaultDescription: "",
      defaultEnabled: "",
      defaultDisabled: "",
      defaultKeyName: "",
      defaultFilePath: "",
      defaultFileLocation: "",
      defaultSecretLocalOnly: "",
      defaultAccessLossWarning: "",
      defaultDisabledRecovery: "",
      enableDefault: "",
      disableDefault: "",
      regenerateDefault: "",
      confirmDisableTitle: "",
      confirmDisableMessage: "",
      confirmRegenerateTitle: "",
      confirmRegenerateMessage: "",
      confirmRotateTitle: () => "",
      confirmRotateMessage: "",
      confirmRevokeTitle: () => "",
      confirmRevokeMessage: "",
      createTitle: "",
      createDescription: "",
      nameLabel: "",
      namePlaceholder: "",
      createScopesLabel: "",
      noScopesAvailable: "",
      create: "",
      working: "",
      oneTimeTitle: "",
      oneTimeDescription: "",
      oneTimeAnnouncement: "",
      apiKeyLabel: "",
      copy: "",
      copied: "",
      copyFailed: "",
      dismissSecret: "",
      createdAt: "",
      keysTitle: "",
      keysEmpty: "",
      revoke: "",
      rotate: "",
      rotateKeyLabel: () => "",
      revokeKeyLabel: () => "",
      requestsTitle: "",
      requestsDescription: "",
      reviewRequests: "",
      requestsLoading: "",
      requestsUnavailable: "",
      requestsLoadFailed: "",
      requestsStale: "",
      requestsEmpty: "",
      requestNamed: () => "",
      requestCount: () => "",
      expiresAt: "",
      observedExecutable: "",
      observedProcess: "",
      submittedAt: "",
      observedCallerIsInformational: "",
      requestScopesLabel: "",
      requestUnavailableScopeHelp: "",
      administratorHolderScope: "",
      approve: "",
      deny: "",
      denyReasonLabel: "",
      requestActionFailed: "",
      requestApproved: "",
      requestDenied: "",
      requestDeniedWithReason: () => "",
      requestExpired: "",
      requestOutcome: () => "",
      denialReasons: { "not-approved": "", unsupported: "", duplicate: "", "not-needed": "" },
      scopeLabels: {
        read: "",
        "metrics.read": "",
        "processes.read": "",
        session: "",
        "adapters.register.self": "",
        "desktop.publish": "",
        "gpu.resolve": "",
        "gpu.report": "",
        "keys.manage": "",
        "machine.write": "",
        "injection.configure": ""
      },
      unknownScope: ""
    }
  },
  debug: {
    debugModeTitle: "",
    debugModeDescription: "",
    debugLogTitle: "",
    debugLogDescription: "",
    hostManagerSmartCoordinatorScoreOnlyTitle: "",
    hostManagerSmartCoordinatorScoreOnlyDescription: "",
    hostManagerSmartCoordinatorPerformanceLogTitle: "",
    hostManagerSmartCoordinatorPerformanceLogDescription: ""
  },
  hardwareControl: { keepAliveTitle: "", keepAliveDescription: "", reassertionTitle: "", reassertionDescription: "" },
  performanceOverlay: {
    intro: "",
    loading: "",
    loadFailed: "",
    emptyTitle: "",
    emptyDetail: () => "",
    itemCount: () => "",
    position: () => "",
    open: "",
    openLabel: () => "",
    softwareMissing: ""
  },
  credits: {
    heroTitle: "",
    heroBody: "",
    dependencyListTitle: "",
    dependencyListBody: "",
    groups: { project: "", runtime: "", windows: "", data: "", build: "", hardware: "" },
    roles: {
      project: "",
      frontendFramework: "",
      icons: "",
      serialization: "",
      charts: "",
      webView: "",
      database: "",
      nativeCompiler: "",
      softwareCatalog: "",
      buildToolchain: "",
      typeSystem: "",
      buildRuntime: "",
      dotnetRuntime: "",
      hookLibrary: "",
      startupInjectionLibrary: "",
      traceLibrary: "",
      windowsManagement: "",
      nvidiaTelemetry: "",
      nvidiaExtension: "",
      amdTelemetry: "",
      signedHardwareAccess: "",
      hardwareBridge: "",
      externalReference: "",
      deviceIdDatabase: ""
    },
    linkLabels: {
      official: "",
      github: "",
      docs: "",
      nuget: "",
      npm: "",
      license: ""
    },
    sourceLabel: "",
    dependencyColumns: { name: "", version: "", license: "", source: "" }
  },
  uiStyleOptions: [],
  animationStyleOptions: [],
  barColorOptions: [],
  byteUnitOptions: [],
  fontSmoothingOptions: [],
  creditGroups: []
};

export const fallbackSettingsText = createSettingsTextBundle("zh-CN", createSettingsLocale("zh-CN"));

const bundles = createLanguageBundleLoader<SettingsTextBundle>((concreteLanguage) =>
  localeLoaders[concreteLanguage]()
    .then((module) => createSettingsTextBundle(concreteLanguage, module.default))
    .catch((error: unknown) => {
      console.error(`[i18n] Settings language bundle failed: ${concreteLanguage}`, error);
      throw error;
    })
);

export function loadSettingsText(language?: string | null): Promise<SettingsTextBundle> {
  return bundles.load(resolveLanguageMode(language));
}

function createSettingsTextBundle(language: ConcreteAppLanguageMode, copy: SettingsCopy): SettingsTextBundle {
  return {
    ...copy,
    language,
    uiStyleOptions: uiStyles.map((style) => ({ id: style.id, label: copy.appearance.uiStyleOptions[style.id] })),
    animationStyleOptions: animationStyles.map((style) => ({ id: style.id, ...copy.appearance.animationStyleOptions[style.id] })),
    barColorOptions: (Object.entries(copy.appearance.barColorOptions) as Array<[AppBarColorMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    byteUnitOptions: (Object.entries(copy.appearance.byteUnitOptions) as Array<[ByteUnitMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    fontSmoothingOptions: (Object.entries(copy.appearance.fontSmoothingOptions) as Array<[AppFontSmoothing, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    creditGroups: createCreditGroups(copy)
  };
}

/** 加载中的占位文案：形状同给定的文案，文字为空，函数返回空串（显式栈遍历，不递归）。 */
function blankCopy<T extends object>(source: T): T {
  const root = Array.isArray(source) ? [] : {} as Record<string, unknown>;
  const pending: Array<{ from: Record<string, unknown>; to: Record<string, unknown> }> = [
    { from: source as Record<string, unknown>, to: root as Record<string, unknown> }];
  while (pending.length > 0) {
    const { from, to } = pending.pop()!;
    for (const [key, value] of Object.entries(from)) {
      if (typeof value === "function") to[key] = () => "";
      else if (typeof value === "string") to[key] = "";
      else if (value !== null && typeof value === "object") {
        const next = Array.isArray(value) ? [] : {};
        to[key] = next;
        pending.push({ from: value as Record<string, unknown>, to: next as Record<string, unknown> });
      } else to[key] = value;
    }
  }
  return root as T;
}
