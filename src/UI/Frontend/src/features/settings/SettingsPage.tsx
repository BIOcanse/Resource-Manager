import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { For, Show } from "solid-js";
import { ReportRuleSettings } from "./components/ReportRuleSettings";
import { MemoryProtectionSettingsSection } from "./components/MemoryProtectionSettingsSection";
import { HardwareControlSettings } from "./components/HardwareControlSettings";
import { AppearanceSettingsSection } from "./components/AppearanceSettingsSection";
import { CreditsSettingsSection } from "./components/CreditsSettingsSection";
import { DebugSettingsSection } from "./components/DebugSettingsSection";
import { PerformanceOverlaySettingsSection } from "./components/PerformanceOverlaySettingsSection";
import { MemorySettingsSection } from "./components/MemorySettingsSection";
import { PublicServicesSettingsSection } from "./components/PublicServicesSettingsSection";
import { RefreshCadenceSetting } from "./components/RefreshCadenceSetting";
import { RuntimePolicySettingsSection } from "./components/RuntimePolicySettingsSection";
import { SystemIntegrationSettingsSection } from "./components/SystemIntegrationSettingsSection";
import { TrustedAccessManager } from "./components/TrustedAccessManager";
import { UpdatesSettingsSection } from "./components/UpdatesSettingsSection";
import { uiText, currentSettingsText, isRightToLeftLanguage } from "../../text.ts";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";
import { useContentTransition } from "../../interactions/solid/contentTransition.ts";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import type {
  AppBarColorMode,
  ByteUnitMode,
  AppEditableHotkeySettings,
  AppFontSmoothing,
  AppLanguageMode,
  AppLogicRefreshIntervalKey,
  AppPresetNumericSetting,
  AppPerformanceSettings,
  AppSettings,
  AppPalette,
  AppResourceBarFrameRateMode,
  AppUiStyle,
  SettingsSection,
  SoftwareRecord
} from "../../types";

export { MultiSegmentedControl, SegmentedControl } from "./components/SettingsControls";

interface SettingsPageProps {
  settings: AppSettings;
  onTrustedAccessHeadingChange?: (heading: HTMLElement | undefined) => void;
  loadState: "loading" | "refreshing" | "ready" | "stale" | "error";
  activeSection: SettingsSection;
  saveState: "idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict";
  runtimeCapabilityConstrainedPaths: readonly string[];
  runtimeFailureCode: string | null;
  onReportsChange: (reports: NonNullable<AppSettings["reports"]>) => void;
  onMemoryProtectionChange: (memoryProtection: NonNullable<AppSettings["memoryProtection"]>) => void;
  /** 已保存的内存保护设置：分区据此判断有没有未保存的更改，保存提交后重新读后端状态。 */
  committedMemoryProtection: AppSettings["memoryProtection"];
  preciseGpuPlacementAvailable: boolean;
  publicServicesAvailable: boolean;
  optimizationRuntimeAvailable: boolean;
  isDirty: boolean;
  onSectionChange: (section: SettingsSection) => void;
  onReload: () => void;
  onReapply: () => void;
  onSave: () => void;
  /** 恢复为默认：只恢复这个分区自己的字段。 */
  onRestoreDefaults: (section: SettingsSection) => void;
  onResetFrontendPreferences: () => void;
  onControlChange: (control: NonNullable<AppSettings["control"]>) => void;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onGpuOverflowThresholdsChange: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  onRealtimeActionBudgetChange: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
  onUiStyleChange: (uiStyle: AppUiStyle) => void;
  onThemeChange: (theme: AppPalette) => void;
  onAnimationStyleChange: (style: AnimationStyleId) => void;
  onAutoDisableAnimationsChange: (enabled: boolean) => void;
  onResourceBarFrameRateModeChange: (mode: AppResourceBarFrameRateMode) => void;
  onResourceBarFrameRateChange: (framesPerSecond: number) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onFontSmoothingChange: (fontSmoothing: AppFontSmoothing) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onServiceRecoveryChange: (enabled: boolean, delay: number, count: number) => void;
  serviceRecovery?: import("../../types").ServiceRecoveryDiagnostics;
  productUpdates: import("../../stores/productUpdateStore").ProductUpdateStore;
  /** 已保存的更新设置：下次检查时间这一行按它说明“未开启”，草稿里未保存的改动不改变后端状态。 */
  committedUpdateSettings: import("../../types").AppUpdateSettings | undefined;
  onUpdateModeChange: (mode: import("../../types").AppUpdateMode) => void;
  onPeriodicUpdateCheckChange: (enabled: boolean) => void;
  onCheckIntervalChange: (hours: number) => void;
  onNotificationSwitchChange: (path: keyof import("../../types").AppNotificationSettings, enabled: boolean) => void;
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
  /** 性能浮层分区：软件详细信息里能不能编辑叠加层。 */
  overlayEditingAvailable: boolean;
  /** 性能浮层分区：软件详细信息对话框是否开着。 */
  softwareDetailOpen: boolean;
  /** 性能浮层分区：打开这个软件的详细信息，停在叠加层分页。 */
  onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
}

const settingSections: SettingsSection[] = [
  "runtimePolicy", "memory", "memoryProtection", "hardwareControl", "reportRules", "appearance", "performanceOverlay",
  "systemIntegration", "publicServices", "trustedAccess", "updates", "debug", "credits"
];
// 只读的分区（性能浮层总览、可信访问、致谢）不改设置草稿，没有重新加载、恢复为默认和保存；
// 可信访问的改动在它自己的控件里立即生效。
const readOnlySections: ReadonlySet<SettingsSection> = new Set(["performanceOverlay", "trustedAccess", "credits"]);

export function SettingsPage(props: SettingsPageProps) {
  // 界面语言只有一个所有者（appTextStore）：设置页的文案跟着它走，
  // 不再自己按草稿里的语言另外载入一份，否则设置页会先于其他页面变语言。
  const text = () => currentSettingsText();
  const sectionLabel = (section: SettingsSection) => text().sections[section] ?? section;
  // 导航的指示条和分区内容的进出（磁石，docs/page_motion_2026-10-25.md）：序号是导航里的次序，竖排，往下的分区从下面进来。
  let nav: HTMLElement | undefined;
  let navIndicator: HTMLElement | undefined;
  let content: HTMLDivElement | undefined;
  let sectionStage: HTMLFieldSetElement | undefined;
  const sectionOrder = () => Math.max(0, settingSections.indexOf(props.activeSection));
  useSelectionIndicator({
    name: "设置导航",
    container: () => nav,
    indicator: () => navIndicator,
    selected: () => nav?.querySelector<HTMLElement>(".settings-nav-item.active") ?? null,
    items: () => [...(nav?.querySelectorAll<HTMLElement>(".settings-nav-item") ?? [])],
    order: sectionOrder
  });
  useContentTransition({
    name: "设置分区",
    key: () => props.activeSection,
    order: sectionOrder,
    stage: () => sectionStage,
    snapshot: () => "move",
    axis: "y",
    distance: 5
  });
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
      <nav ref={nav} class="settings-nav has-selection-indicator" aria-label={text().navigationLabel}>
        <span ref={navIndicator} class="selection-indicator page-tab-indicator" aria-hidden="true" />
        <For each={settingSections}>
          {(section) => (
            <button
              type="button"
              class="settings-nav-item page-tab"
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
        ref={content}
        class="settings-content"
        aria-busy={props.loadState === "loading" || props.loadState === "refreshing"}
      >
        <div class="settings-content-header">
          <h2>{sectionLabel(props.activeSection)}</h2>
          <div class="settings-header-actions">
            <Show when={settingsReady() && !readOnlySections.has(props.activeSection)}>
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
                onClick={() => props.onRestoreDefaults(props.activeSection)}
              >
                {text().actions.restoreDefaults}
              </button>
              <button
                type="button"
                disabled={props.saveState === "saving" || props.saveState === "conflict" || !props.isDirty}
                onClick={props.onSave}
              >
                <StableLabel labels={{ idle: text().actions.save, busy: text().saveState.saving }} current={props.saveState === "saving" ? "busy" : "idle"} />
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
            ref={sectionStage}
            class="settings-readonly-boundary"
            disabled={!settingsReady()}
            aria-disabled={!settingsReady()}
            aria-describedby={props.loadState === "stale" ? "settingsStaleNotice" : undefined}
          >
          <Show when={props.activeSection === "runtimePolicy"}>
            <RuntimePolicySettingsSection
              settings={props.settings.performance}
              text={text()}
              preciseGpuPlacementAvailable={props.preciseGpuPlacementAvailable}
              onPreciseGpuPlacementChange={props.onPreciseGpuPlacementChange}
              onGpuOverflowThresholdsChange={props.onGpuOverflowThresholdsChange}
              onRealtimeActionBudgetChange={props.onRealtimeActionBudgetChange}
            />
          </Show>
          <Show when={props.activeSection === "memory"}>
            <MemorySettingsSection
              settings={props.settings.performance}
              text={text()}
              onAutomaticMemoryCleanupLinesChange={props.onAutomaticMemoryCleanupLinesChange}
              onMemoryOptimizationTargetLinesChange={props.onMemoryOptimizationTargetLinesChange}
            />
          </Show>
          <Show when={props.activeSection === "memoryProtection"}>
            <MemoryProtectionSettingsSection
              settings={props.settings.memoryProtection}
              committed={props.committedMemoryProtection}
              runtimeFailureCode={props.runtimeFailureCode}
              onChange={props.onMemoryProtectionChange}
            />
          </Show>
          <Show when={props.activeSection === "hardwareControl"}>
            <HardwareControlSettings control={props.settings.control!} text={text()} onChange={props.onControlChange} />
          </Show>
          <Show when={props.activeSection === "reportRules"}>
            <ReportRuleSettings reports={props.settings.reports!} onChange={props.onReportsChange}
              runtimeFailureCode={props.runtimeFailureCode} />
          </Show>
          <Show when={props.activeSection === "appearance"}>
            <AppearanceSettingsSection
              onResetPreferences={props.onResetFrontendPreferences}
              settings={props.settings.appearance}
              text={text()}
              onUiStyleChange={props.onUiStyleChange}
              onThemeChange={props.onThemeChange}
              onAnimationStyleChange={props.onAnimationStyleChange}
              onAutoDisableAnimationsChange={props.onAutoDisableAnimationsChange}
              onResourceBarFrameRateModeChange={props.onResourceBarFrameRateModeChange}
              onResourceBarFrameRateChange={props.onResourceBarFrameRateChange}
              onBarColorModeChange={props.onBarColorModeChange}
              onByteUnitModeChange={props.onByteUnitModeChange}
              onFontSmoothingChange={props.onFontSmoothingChange}
              onLanguageChange={props.onLanguageChange}
            >
              <RefreshCadenceSetting
                settings={props.settings.performance}
                text={text()}
                onLogicRefreshIntervalChange={props.onLogicRefreshIntervalChange}
              />
            </AppearanceSettingsSection>
          </Show>
          <Show when={props.activeSection === "performanceOverlay"}>
            <PerformanceOverlaySettingsSection
              text={text()}
              overlayEditingAvailable={props.overlayEditingAvailable}
              softwareDetailOpen={props.softwareDetailOpen}
              onOpenOverlay={props.onOpenSoftwareOverlay}
            />
          </Show>
          <Show when={props.activeSection === "systemIntegration"}>
            <SystemIntegrationSettingsSection
              settings={props.settings.systemIntegration}
              text={text()}
              onTaskManagerShortcutReplacementChange={props.onTaskManagerShortcutReplacementChange}
              onAutoStartChange={props.onAutoStartChange}
              onServiceRecoveryChange={props.onServiceRecoveryChange}
              serviceRecovery={props.serviceRecovery}
              onEditableHotkeyChange={props.onEditableHotkeyChange}
            />
          </Show>
          <Show when={props.activeSection === "publicServices"}>
            <PublicServicesSettingsSection
              publicServiceSettings={props.settings.publicService}
              aiModelServiceSettings={props.settings.aiModelService}
              text={text()}
              publicServicesAvailable={props.publicServicesAvailable}
              onLocalPublicServiceChange={props.onLocalPublicServiceChange}
              onPublicFileIndexChange={props.onPublicFileIndexChange}
              onPublicDatabaseServiceChange={props.onPublicDatabaseServiceChange}
              onPublicAiModelServiceChange={props.onPublicAiModelServiceChange}
              onLmStudioEndpointChange={props.onLmStudioEndpointChange}
              onLmStudioAutoStartChange={props.onLmStudioAutoStartChange}
            />
          </Show>
          <Show when={props.activeSection === "trustedAccess"}>
            <div class="settings-section-panel">
              <TrustedAccessManager copy={text().systemIntegration.trustedAccess}
                onHeadingChange={props.onTrustedAccessHeadingChange} />
            </div>
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
          <Show when={props.activeSection === "updates"}>
            <UpdatesSettingsSection
              settings={props.settings.update}
              committedSettings={props.committedUpdateSettings}
              notifications={props.settings.notifications}
              productUpdates={props.productUpdates}
              text={text()}
              onModeChange={props.onUpdateModeChange}
              onPeriodicCheckChange={props.onPeriodicUpdateCheckChange}
              onCheckIntervalChange={props.onCheckIntervalChange}
              onNotificationSwitchChange={props.onNotificationSwitchChange}
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
