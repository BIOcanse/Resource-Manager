import { retryableLocaleImport } from "./retryableLocaleImport.ts";
import type { AppAnimationMode, AppBarColorMode, AppFontSmoothing, ByteUnitMode } from "../types.ts";
import { uiStyles } from "../presentation/uiStyles.ts";
import { createCreditGroups } from "./settingsCredits.ts";
import { resolveLanguageMode } from "./settingsLanguages.ts";
import { createSettingsLocale } from "./settingsLocaleFactory.ts";
import { loadingUpdatesCopy } from "./settingsUpdateCopy.ts";
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
  language: "zh-CN",
  navigationLabel: "",
  sections: { performance: "", appearance: "", systemIntegration: "", updates: "", debug: "", credits: "" },
  saveState: { saving: "", saved: "", constrained: "", partial: "", error: "", dirty: "", conflict: "" },
  loadState: {
    loading: "",
    errorTitle: "",
    errorDescription: "",
    staleTitle: "",
    staleDescription: ""
  },
  actions: { save: "", restoreDefaults: "", retry: "", reload: "", reapply: "" },
  performance: {
    smartMonitoringTitle: "",
    smartMonitoringDescription: "",
    adaptiveBooleanModeOptions: [],
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
    animationTitle: "",
    animationDescription: "",
    resourceBarFrameRateTitle: "",
    resourceBarFrameRateDescription: "",
    resourceBarFrameRateModeOptions: [],
    resourceBarFrameRateCustomLabel: "",
    barColorTitle: "",
    byteUnitTitle: "",
    byteUnitDescription: "",
    readingSourceTitle: "",
    readingSourceDescription: "",
    readingSourceModeOptions: [],
    barColorDescription: "",
    fontSmoothingTitle: "",
    fontSmoothingDescription: "",
    languageTitle: "",
    languageDescription: "",
    settingsLanguageTitle: "",
    settingsLanguageDescription: "",
    languageSelectLabel: "",
    uiStyleOptions: { standard: "", soft: "", outline: "", terminal: "" },
    themeOptions: {
      system: "", light: "", dark: "", lowContrast: "",
      paper: "", graphite: "", blueprint: "",
      phosphor: "", amber: "", ice: ""
    },
    animationOptions: {
      auto: { label: "", description: "" },
      normal: { label: "", description: "" },
      none: { label: "", description: "" },
      ultra: { label: "", description: "" }
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
    taskManagerTitle: "",
    taskManagerDescription: "",
    forceTerminateTitle: "",
    forceTerminateDescription: "",
    forceTerminateWarning: "",
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
    aiGatewayLoadFailed: ""
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
    notes: {
      windowsFoundation: "",
      microsoftTools: "",
      managedServices: "",
      nativeToolchain: "",
      buildContributors: "",
      testContributors: "",
      upstreamContributors: "",
      resourceManager: "",
      solid: "",
      lucide: "",
      seroval: "",
      webView2: "",
      sqlite: "",
      zig: "",
      softwareCatalog: "",
      vite: "",
      typescript: "",
      node: "",
      dotnet: "",
      minHook: "",
      detours: "",
      traceEvent: "",
      systemManagement: "",
      nvml: "",
      nvapi: "",
      adlx: "",
      pawnIo: "",
      pawnIoModules: "",
      libreHardwareMonitor: "",
      blacktempelToolkits: "",
      hidSharp: "",
      monoPosix: "",
      usbIds: "",
      pciIds: ""
    },
    linkLabels: {
      official: "",
      github: "",
      docs: "",
      license: "",
      nuget: "",
      gpuOpen: "",
      eula: "",
      runtime: "",
      aspnet: "",
      vite: "",
      solidPlugin: ""
    }
  },
  uiStyleOptions: [],
  animationOptions: [],
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
    animationOptions: (Object.entries(copy.appearance.animationOptions) as Array<[AppAnimationMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    barColorOptions: (Object.entries(copy.appearance.barColorOptions) as Array<[AppBarColorMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    byteUnitOptions: (Object.entries(copy.appearance.byteUnitOptions) as Array<[ByteUnitMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    fontSmoothingOptions: (Object.entries(copy.appearance.fontSmoothingOptions) as Array<[AppFontSmoothing, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    creditGroups: createCreditGroups(copy)
  };
}
