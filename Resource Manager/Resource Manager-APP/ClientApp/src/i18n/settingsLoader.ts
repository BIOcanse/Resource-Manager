import type { AppAnimationMode, AppBarColorMode, AppFontSmoothing, AppGpuPerformanceUseCase, AppThemeMode, ByteUnitMode } from "../types.ts";
import { createCreditGroups } from "./settingsCredits.ts";
import { resolveLanguageMode } from "./settingsLanguages.ts";
import { createSettingsLocale } from "./settingsLocaleFactory.ts";
import type { ConcreteAppLanguageMode, SettingsCopy, SettingsTextBundle } from "./settingsTypes.ts";

type SettingsLocaleModule = { default: SettingsCopy };
type SettingsLocaleLoader = () => Promise<SettingsLocaleModule>;

const localeLoaders = {
  "zh-CN": () => import("./settingsLocales/zh-CN.ts"),
  "zh-TW": () => import("./settingsLocales/zh-TW.ts"),
  "en-US": () => import("./settingsLocales/en-US.ts"),
  "ja-JP": () => import("./settingsLocales/ja-JP.ts"),
  "ko-KR": () => import("./settingsLocales/ko-KR.ts"),
  "fr-FR": () => import("./settingsLocales/fr-FR.ts"),
  "de-DE": () => import("./settingsLocales/de-DE.ts"),
  "es-ES": () => import("./settingsLocales/es-ES.ts"),
  "es-MX": () => import("./settingsLocales/es-MX.ts"),
  "pt-BR": () => import("./settingsLocales/pt-BR.ts"),
  "pt-PT": () => import("./settingsLocales/pt-PT.ts"),
  "ru-RU": () => import("./settingsLocales/ru-RU.ts"),
  "uk-UA": () => import("./settingsLocales/uk-UA.ts"),
  "pl-PL": () => import("./settingsLocales/pl-PL.ts"),
  "tr-TR": () => import("./settingsLocales/tr-TR.ts"),
  "it-IT": () => import("./settingsLocales/it-IT.ts"),
  "nl-NL": () => import("./settingsLocales/nl-NL.ts"),
  "sv-SE": () => import("./settingsLocales/sv-SE.ts"),
  "fi-FI": () => import("./settingsLocales/fi-FI.ts"),
  "da-DK": () => import("./settingsLocales/da-DK.ts"),
  "nb-NO": () => import("./settingsLocales/nb-NO.ts"),
  "cs-CZ": () => import("./settingsLocales/cs-CZ.ts"),
  "hu-HU": () => import("./settingsLocales/hu-HU.ts"),
  "ro-RO": () => import("./settingsLocales/ro-RO.ts"),
  "el-GR": () => import("./settingsLocales/el-GR.ts"),
  "he-IL": () => import("./settingsLocales/he-IL.ts"),
  "ar-SA": () => import("./settingsLocales/ar-SA.ts"),
  "hi-IN": () => import("./settingsLocales/hi-IN.ts"),
  "id-ID": () => import("./settingsLocales/id-ID.ts"),
  "vi-VN": () => import("./settingsLocales/vi-VN.ts"),
  "th-TH": () => import("./settingsLocales/th-TH.ts")
} satisfies Record<ConcreteAppLanguageMode, SettingsLocaleLoader>;

const localeCache = new Map<ConcreteAppLanguageMode, Promise<SettingsTextBundle>>();

export const loadingSettingsText: SettingsTextBundle = {
  language: "zh-CN",
  navigationLabel: "",
  sections: { performance: "", appearance: "", systemIntegration: "", debug: "", credits: "" },
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
    smartMonitoringDescription: () => "",
    adaptiveBooleanModeOptions: [],
    gpuPerformanceUseCasesTitle: "",
    gpuPerformanceUseCasesDescription: "",
    automaticSchedulingOptimizationsTitle: "",
    automaticSchedulingOptimizationsDescription: "",
    onOffOptions: [],
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
    pauseHiddenTitle: "",
    pauseHiddenDescription: "",
    frontendHiddenRefreshModeOptions: [],
    refreshCadenceTitle: "",
    refreshCadenceDescription: "",
    presetNumericModeOptions: [],
    refreshCadencePresetLabel: "",
    refreshCadenceCustomLabel: "",
    refreshCadencePresetOptions: [],
    refreshCadenceItems: {
      monitor: { label: "", description: "" },
      resourceTable: { label: "", description: "" },
      management: { label: "", description: "" },
      discovery: { label: "", description: "" },
      optimization: { label: "", description: "" },
      localSystem: { label: "", description: "" }
    }
  },
  appearance: {
    themeTitle: "",
    themeDescription: "",
    animationTitle: "",
    animationDescription: "",
    resourceBarHardwareAccelerationTitle: "",
    resourceBarHardwareAccelerationDescription: "",
    resourceBarHardwareAccelerationModeOptions: [],
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
    themeOptions: { system: "", light: "", dark: "", lowContrast: "" },
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
    gpuPerformanceUseCaseOptions: {
      general: { label: "", description: "" },
      ai: { label: "", description: "" },
      gaming: { label: "", description: "" }
    }
  },
  systemIntegration: {
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
      etwToolkit: "",
      nvidiaTelemetry: "",
      nvidiaExtension: "",
      amdTelemetry: "",
      amdCpuSdk: "",
      intelTelemetry: "",
      amdSmuBoundary: "",
      hardwareBridge: "",
      notebookEc: "",
      externalReference: "",
      latencyDiagnostics: "",
      deviceIdDatabase: ""
    },
    notes: {
      windowsFoundation: "",
      microsoftTools: "",
      managedServices: "",
      nativeToolchain: "",
      buildContributors: "",
      testContributors: "",
      openHardwareMonitor: "",
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
      wpt: "",
      nvml: "",
      nvapi: "",
      adlx: "",
      ryzenMaster: "",
      intelPcm: "",
      pawnIo: "",
      libreHardwareMonitor: "",
      notebookFanControl: "",
      afterburner: "",
      latencyMon: "",
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
  themeOptions: [],
  animationOptions: [],
  barColorOptions: [],
  byteUnitOptions: [],
  fontSmoothingOptions: [],
  gpuPerformanceUseCaseOptions: [],
  creditGroups: []
};

export const fallbackSettingsText = createSettingsTextBundle("zh-CN", createSettingsLocale("zh-CN"));

export function loadSettingsText(language?: string | null): Promise<SettingsTextBundle> {
  const concreteLanguage = resolveLanguageMode(language);
  const cached = localeCache.get(concreteLanguage);
  if (cached) {
    return cached;
  }

  const loading = localeLoaders[concreteLanguage]()
    .then((module) => createSettingsTextBundle(concreteLanguage, module.default))
    .catch(() => {
      if (concreteLanguage === "zh-CN") {
        return fallbackSettingsText;
      }

      return loadSettingsText("zh-CN");
    });
  localeCache.set(concreteLanguage, loading);
  return loading;
}

function createSettingsTextBundle(language: ConcreteAppLanguageMode, copy: SettingsCopy): SettingsTextBundle {
  return {
    ...copy,
    language,
    themeOptions: (Object.entries(copy.appearance.themeOptions) as Array<[AppThemeMode, string]>)
      .map(([id, label]) => ({ id, label })),
    animationOptions: (Object.entries(copy.appearance.animationOptions) as Array<[AppAnimationMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    barColorOptions: (Object.entries(copy.appearance.barColorOptions) as Array<[AppBarColorMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    byteUnitOptions: (Object.entries(copy.appearance.byteUnitOptions) as Array<[ByteUnitMode, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    fontSmoothingOptions: (Object.entries(copy.appearance.fontSmoothingOptions) as Array<[AppFontSmoothing, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    gpuPerformanceUseCaseOptions: (Object.entries(copy.appearance.gpuPerformanceUseCaseOptions) as Array<[AppGpuPerformanceUseCase, { label: string; description: string }]>)
      .map(([id, option]) => ({ id, label: option.label, description: option.description })),
    creditGroups: createCreditGroups(copy)
  };
}
