import { createEffect, createSignal, For, onCleanup, Show } from "solid-js";
import { AppearanceSettingsSection } from "./settings/AppearanceSettingsSection";
import { CreditsSettingsSection } from "./settings/CreditsSettingsSection";
import { DebugSettingsSection } from "./settings/DebugSettingsSection";
import { PerformanceSettingsSection } from "./settings/PerformanceSettingsSection";
import { SystemIntegrationSettingsSection } from "./settings/SystemIntegrationSettingsSection";
import { uiText, currentSettingsText, isRightToLeftLanguage } from "../text.ts";
import type {
  AppAdaptiveBooleanMode,
  AppBarColorMode,
  ByteUnitMode,
  AppEditableHotkeySettings,
  AppFrontendHiddenRefreshMode,
  AppGpuPerformanceUseCase,
  AppLanguageMode,
  AppLogicRefreshIntervalKey,
  AppPresetNumericSetting,
  AppSettings,
  AppThemeMode,
  SettingsSection
} from "../types";

export { MultiSegmentedControl, SegmentedControl } from "./settings/SettingsControls";

interface SettingsPageProps {
  settings: AppSettings;
  loadState: "loading" | "refreshing" | "ready" | "stale" | "error";
  activeSection: SettingsSection;
  saveState: "idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict";
  runtimeCapabilityConstrainedPaths: readonly string[];
  preciseGpuPlacementAvailable: boolean;
  publicServicesAvailable: boolean;
  optimizationRuntimeAvailable: boolean;
  isDirty: boolean;
  onSectionChange: (section: SettingsSection) => void;
  onReload: () => void;
  onReapply: () => void;
  onSave: () => void;
  onRestoreDefaults: () => void;
  onSmartMonitoringModeChange: (mode: AppAdaptiveBooleanMode) => void;
  onAutomaticSchedulingOptimizationsChange: (enabled: boolean) => void;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onGpuPerformanceUseCasesChange: (useCases: AppGpuPerformanceUseCase[]) => void;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onFrontendHiddenRefreshModeChange: (mode: AppFrontendHiddenRefreshMode) => void;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
  onThemeChange: (theme: AppThemeMode) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
  onLocalPublicServiceChange: (enabled: boolean) => void;
  onPublicFileIndexChange: (enabled: boolean) => void;
  onPublicDatabaseServiceChange: (enabled: boolean) => void;
  onPublicAiModelServiceChange: (enabled: boolean) => void;
  onLmStudioEndpointChange: (endpoint: string) => void;
  onLmStudioAutoStartChange: (enabled: boolean) => void;
  onDebugModeChange: (enabled: boolean) => void;
  onDebugLogChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorScoreOnlyChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorPerformanceLogChange: (enabled: boolean) => void;
}

const settingSections: SettingsSection[] = ["performance", "appearance", "systemIntegration", "debug", "credits"];

export function SettingsPage(props: SettingsPageProps) {
  // 界面语言只有一个所有者（appTextStore）：设置页的文案跟着它走，
  // 不再自己按草稿里的语言另外载入一份，否则设置页会先于其他页面变语言。
  const text = () => currentSettingsText();
  const sectionLabel = (section: SettingsSection) => text().sections[section] ?? section;
  const statusText = () => {
    if (props.saveState === "saving") {
      return text().saveState.saving;
    }
    if (props.saveState === "saved") {
      return text().saveState.saved;
    }
    if (props.saveState === "partial") {
      return text().saveState.partial;
    }
    if (props.saveState === "constrained") {
      return text().saveState.constrained;
    }
    if (props.saveState === "error") {
      return text().saveState.error;
    }
    if (props.saveState === "conflict") {
      return text().saveState.conflict;
    }
    if (props.isDirty) {
      return text().saveState.dirty;
    }

    return "";
  };
  const settingsReady = () => props.loadState === "ready";
  const canRenderSettings = () => props.loadState === "ready"
    || props.loadState === "refreshing"
    || props.loadState === "stale";
  const loadStateTitle = () => props.loadState === "stale"
    ? text().loadState.staleTitle
    : text().loadState.errorTitle;
  const loadStateDescription = () => props.loadState === "stale"
    ? text().loadState.staleDescription
    : text().loadState.errorDescription;

  return (
    <section
      class="settings-page page active-page"
      aria-label={sectionLabel(props.activeSection)}
      lang={text().language}
      dir={isRightToLeftLanguage(text().language) ? "rtl" : "ltr"}
    >
      <nav class="settings-nav" aria-label={text().navigationLabel}>
        <For each={settingSections}>
          {(section) => (
            <button
              type="button"
              class="settings-nav-item"
              classList={{ active: props.activeSection === section }}
              disabled={!canRenderSettings()}
              aria-current={props.activeSection === section ? "page" : undefined}
              onClick={() => props.onSectionChange(section)}
            >
              {sectionLabel(section)}
            </button>
          )}
        </For>
      </nav>
      <div
        class="settings-content"
        aria-busy={props.loadState === "loading" || props.loadState === "refreshing"}
      >
        <div class="settings-content-header">
          <h2>{sectionLabel(props.activeSection)}</h2>
          <div class="settings-header-actions">
            <Show when={settingsReady() && props.activeSection !== "credits"}>
              <span
                class="settings-save-state"
                classList={{
                  error: props.saveState === "error" || props.saveState === "conflict",
                  warning: props.saveState === "partial" || props.saveState === "constrained"
                }}
                role={props.saveState === "error" || props.saveState === "conflict" ? "alert" : "status"}
                title={props.saveState === "constrained"
                  ? props.runtimeCapabilityConstrainedPaths.join(", ")
                  : undefined}
              >
                {statusText()}
              </span>
              <Show when={props.saveState === "conflict"}>
                <button class="secondary" type="button" onClick={props.onReload}>
                  {text().actions.reload}
                </button>
              </Show>
              <Show when={props.saveState === "partial"}>
                <button class="secondary" type="button" onClick={props.onReapply}>
                  {text().actions.reapply}
                </button>
              </Show>
              <Show when={props.saveState !== "conflict"}>
                <button
                  class="secondary"
                  type="button"
                  disabled={props.saveState === "saving" || props.isDirty}
                  title={props.isDirty ? uiText.misc.saveOrRevertFirst : undefined}
                  onClick={props.onReload}
                >
                  {text().actions.reload}
                </button>
              </Show>
              <button
                class="secondary"
                type="button"
                disabled={props.saveState === "saving" || props.saveState === "conflict"}
                onClick={props.onRestoreDefaults}
              >
                {text().actions.restoreDefaults}
              </button>
              <button
                type="button"
                disabled={props.saveState === "saving" || props.saveState === "conflict" || !props.isDirty}
                onClick={props.onSave}
              >
                {props.saveState === "saving" ? text().saveState.saving : text().actions.save}
              </button>
            </Show>
          </div>
        </div>
        <Show when={props.loadState === "stale"}>
          <div id="settingsStaleNotice" class="settings-load-state stale" role="alert">
            <div>
              <strong>{loadStateTitle()}</strong>
              <p>{loadStateDescription()}</p>
            </div>
            <button type="button" onClick={props.onReload}>
              {text().actions.retry}
            </button>
          </div>
        </Show>
        <Show
          when={canRenderSettings()}
          fallback={
            <div
              class="settings-load-state"
              role={props.loadState === "loading" ? "status" : "alert"}
            >
              <Show
                when={props.loadState !== "loading"}
                fallback={<strong>{text().loadState.loading}</strong>}
              >
                <strong>{loadStateTitle()}</strong>
                <p>{loadStateDescription()}</p>
                <button type="button" onClick={props.onReload}>
                  {text().actions.retry}
                </button>
              </Show>
            </div>
          }
        >
          <fieldset
            class="settings-readonly-boundary"
            disabled={!settingsReady()}
            aria-disabled={!settingsReady()}
            aria-describedby={props.loadState === "stale" ? "settingsStaleNotice" : undefined}
          >
          <Show when={props.activeSection === "performance"}>
            <PerformanceSettingsSection
              settings={props.settings.performance}
              text={text()}
              preciseGpuPlacementAvailable={props.preciseGpuPlacementAvailable}
              onSmartMonitoringModeChange={props.onSmartMonitoringModeChange}
              onAutomaticSchedulingOptimizationsChange={props.onAutomaticSchedulingOptimizationsChange}
              onPreciseGpuPlacementChange={props.onPreciseGpuPlacementChange}
              onGpuPerformanceUseCasesChange={props.onGpuPerformanceUseCasesChange}
              onAutomaticMemoryCleanupLinesChange={props.onAutomaticMemoryCleanupLinesChange}
              onMemoryOptimizationTargetLinesChange={props.onMemoryOptimizationTargetLinesChange}
              onFrontendHiddenRefreshModeChange={props.onFrontendHiddenRefreshModeChange}
              onLogicRefreshIntervalChange={props.onLogicRefreshIntervalChange}
            />
          </Show>
          <Show when={props.activeSection === "appearance"}>
            <AppearanceSettingsSection
              settings={props.settings.appearance}
              text={text()}
              onThemeChange={props.onThemeChange}
              onBarColorModeChange={props.onBarColorModeChange}
              onByteUnitModeChange={props.onByteUnitModeChange}
              onLanguageChange={props.onLanguageChange}
            />
          </Show>
          <Show when={props.activeSection === "systemIntegration"}>
            <SystemIntegrationSettingsSection
              settings={props.settings.systemIntegration}
              publicServiceSettings={props.settings.publicService}
              aiModelServiceSettings={props.settings.aiModelService}
              text={text()}
              publicServicesAvailable={props.publicServicesAvailable}
              onTaskManagerShortcutReplacementChange={props.onTaskManagerShortcutReplacementChange}
              onAutoStartChange={props.onAutoStartChange}
              onEditableHotkeyChange={props.onEditableHotkeyChange}
              onLocalPublicServiceChange={props.onLocalPublicServiceChange}
              onPublicFileIndexChange={props.onPublicFileIndexChange}
              onPublicDatabaseServiceChange={props.onPublicDatabaseServiceChange}
              onPublicAiModelServiceChange={props.onPublicAiModelServiceChange}
              onLmStudioEndpointChange={props.onLmStudioEndpointChange}
              onLmStudioAutoStartChange={props.onLmStudioAutoStartChange}
            />
          </Show>
          <Show when={props.activeSection === "debug"}>
            <DebugSettingsSection
              settings={props.settings.debug}
              text={text()}
              optimizationRuntimeAvailable={props.optimizationRuntimeAvailable}
              onDebugModeChange={props.onDebugModeChange}
              onDebugLogChange={props.onDebugLogChange}
              onHostManagerSmartCoordinatorScoreOnlyChange={props.onHostManagerSmartCoordinatorScoreOnlyChange}
              onHostManagerSmartCoordinatorPerformanceLogChange={props.onHostManagerSmartCoordinatorPerformanceLogChange}
            />
          </Show>
          <Show when={props.activeSection === "credits"}>
            <CreditsSettingsSection text={text()} />
          </Show>
          </fieldset>
        </Show>
      </div>
    </section>
  );
}
