import type { AppAdaptiveBooleanMode, AppAnimationMode, AppBarColorMode, AppFontSmoothing, AppFrontendHiddenRefreshMode, AppGpuPerformanceUseCase, AppLanguageMode, AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset, AppPresetNumericSettingMode, AppThemeMode, ByteUnitMode, SettingsSection } from "../types.ts";

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
  links: Array<{ label: keyof SettingsCopy["credits"]["linkLabels"]; href: string }>;
}

export interface SettingsCopy {
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
  performance: {
    smartMonitoringTitle: string;
    smartMonitoringDescription: (seconds: number) => string;
    adaptiveBooleanModeOptions: Array<{ id: AppAdaptiveBooleanMode; label: string; description: string }>;
    gpuPerformanceUseCasesTitle: string;
    gpuPerformanceUseCasesDescription: string;
    automaticSchedulingOptimizationsTitle: string;
    automaticSchedulingOptimizationsDescription: string;
    onOffOptions: Array<{ id: "on" | "off"; label: string; description: string }>;
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
    pauseHiddenTitle: string;
    pauseHiddenDescription: string;
    frontendHiddenRefreshModeOptions: Array<{ id: AppFrontendHiddenRefreshMode; label: string; description: string }>;
    refreshCadenceTitle: string;
    refreshCadenceDescription: string;
    presetNumericModeOptions: Array<{ id: AppPresetNumericSettingMode; label: string; description: string }>;
    refreshCadencePresetLabel: string;
    refreshCadenceCustomLabel: string;
    refreshCadencePresetOptions: Array<{ id: AppLogicRefreshIntervalPreset; label: string; description: string }>;
    refreshCadenceItems: Record<AppLogicRefreshIntervalKey, { label: string; description: string }>;
  };
  appearance: {
    themeTitle: string;
    themeDescription: string;
    animationTitle: string;
    animationDescription: string;
    resourceBarHardwareAccelerationTitle: string;
    resourceBarHardwareAccelerationDescription: string;
    resourceBarHardwareAccelerationModeOptions: Array<{ id: AppAdaptiveBooleanMode; label: string; description: string }>;
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
    themeOptions: Record<AppThemeMode, string>;
    animationOptions: Record<AppAnimationMode, { label: string; description: string }>;
    barColorOptions: Record<AppBarColorMode, { label: string; description: string }>;
    byteUnitOptions: Record<ByteUnitMode, { label: string; description: string }>;
    fontSmoothingOptions: Record<AppFontSmoothing, { label: string; description: string }>;
    gpuPerformanceUseCaseOptions: Record<AppGpuPerformanceUseCase, { label: string; description: string }>;
  };
  systemIntegration: {
    autoStartTitle: string;
    autoStartDescription: string;
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
      etwToolkit: string;
      nvidiaTelemetry: string;
      nvidiaExtension: string;
      amdTelemetry: string;
      amdCpuSdk: string;
      intelTelemetry: string;
      amdSmuBoundary: string;
      hardwareBridge: string;
      notebookEc: string;
      externalReference: string;
      latencyDiagnostics: string;
      deviceIdDatabase: string;
    };
    notes: {
      windowsFoundation: string;
      microsoftTools: string;
      managedServices: string;
      nativeToolchain: string;
      buildContributors: string;
      testContributors: string;
      openHardwareMonitor: string;
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
      wpt: string;
      nvml: string;
      nvapi: string;
      adlx: string;
      ryzenMaster: string;
      intelPcm: string;
      pawnIo: string;
      libreHardwareMonitor: string;
      notebookFanControl: string;
      afterburner: string;
      latencyMon: string;
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
  themeOptions: Array<{ id: AppThemeMode; label: string }>;
  animationOptions: Array<{ id: AppAnimationMode; label: string; description: string }>;
  barColorOptions: Array<{ id: AppBarColorMode; label: string; description: string }>;
  byteUnitOptions: Array<{ id: ByteUnitMode; label: string; description: string }>;
  fontSmoothingOptions: Array<{ id: AppFontSmoothing; label: string; description: string }>;
  gpuPerformanceUseCaseOptions: Array<{ id: AppGpuPerformanceUseCase; label: string; description: string }>;
  creditGroups: CreditGroup[];
}
