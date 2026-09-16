import { For } from "solid-js";
import { StandardSelect } from "../StandardSelect";
import {
  defaultLogicRefreshIntervalSetting,
  formatLogicRefreshIntervalPresetMs,
  logicRefreshIntervalKeys,
  normalizeLogicRefreshIntervalSetting
} from "../../stores/settingsStore";
import type { SettingsTextBundle } from "../../text.ts";
import type {
  AppAdaptiveBooleanMode,
  AppFrontendHiddenRefreshMode,
  AppGpuPerformanceUseCase,
  AppLogicRefreshIntervalKey,
  AppLogicRefreshIntervalPreset,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  AppPresetNumericSettingMode
} from "../../types";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { uiText } from "../../text.ts";

type GpuSchedulingMode = "basic" | "precise";

interface PerformanceSettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  preciseGpuPlacementAvailable: boolean;
  onSmartMonitoringModeChange: (mode: AppAdaptiveBooleanMode) => void;
  onAutomaticSchedulingOptimizationsChange: (enabled: boolean) => void;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onGpuPerformanceUseCasesChange: (useCases: AppGpuPerformanceUseCase[]) => void;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onFrontendHiddenRefreshModeChange: (mode: AppFrontendHiddenRefreshMode) => void;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
}

export function PerformanceSettingsSection(props: PerformanceSettingsSectionProps) {
  const performance = (): AppPerformanceSettings => props.settings ?? {
    smartMonitoringEnabled: true,
    monitoringIdleSeconds: 5,
    optimizationMode: "normal" as const,
    pauseFrontendRefreshWhenHiddenInNormalMode: true,
    preciseGpuPlacementEnabled: true,
    vramMoveDownPhysicalMemoryDangerPercent: 10,
    physicalMemoryMoveDownVirtualMemoryDangerPercent: 12,
    physicalMemoryAutomaticCleanupPercent: 6,
    virtualMemoryAutomaticCleanupPercent: 8,
    physicalMemoryOptimizationTargetUsagePercent: 70,
    virtualMemoryOptimizationTargetUsagePercent: 70,
    gpuPerformanceUseCases: ["general"],
    smartMonitoringMode: "auto",
    automaticSchedulingOptimizationsEnabled: true,
    frontendHiddenRefreshMode: "auto",
    monitorRefreshIntervalMs: defaultLogicRefreshIntervalSetting("monitor"),
    resourceTableRefreshIntervalMs: defaultLogicRefreshIntervalSetting("resourceTable"),
    managementRefreshIntervalMs: defaultLogicRefreshIntervalSetting("management"),
    discoveryRefreshIntervalMs: defaultLogicRefreshIntervalSetting("discovery"),
    optimizationRefreshIntervalMs: defaultLogicRefreshIntervalSetting("optimization"),
    localSystemRefreshIntervalMs: defaultLogicRefreshIntervalSetting("localSystem")
  };
  const gpuSchedulingMode = (): GpuSchedulingMode => performance().preciseGpuPlacementEnabled ? "precise" : "basic";
  // 设置页的两态开关一律「开启在前、关闭在后」，和按需监控、自动调度性能优化保持一致。
  const gpuSchedulingModeOptions = (): Array<{ id: GpuSchedulingMode; label: string; description: string }> => [
    { id: "precise", ...props.text.performance.preciseGpuPlacementModeOptions.precise },
    { id: "basic", ...props.text.performance.preciseGpuPlacementModeOptions.basic }
  ];
  const updateCleanupLine = (field: "physical" | "virtual", value: string) => {
    const number = Number(value);
    const physical = field === "physical" ? number : performance().physicalMemoryAutomaticCleanupPercent;
    const virtual = field === "virtual" ? number : performance().virtualMemoryAutomaticCleanupPercent;
    props.onAutomaticMemoryCleanupLinesChange(physical, virtual);
  };
  const updateTargetLine = (field: "physical" | "virtual", value: string) => {
    const number = Number(value);
    const physical = field === "physical" ? number : performance().physicalMemoryOptimizationTargetUsagePercent;
    const virtual = field === "virtual" ? number : performance().virtualMemoryOptimizationTargetUsagePercent;
    props.onMemoryOptimizationTargetLinesChange(physical, virtual);
  };
  const logicRefreshIntervalSetting = (key: AppLogicRefreshIntervalKey) => {
    const current = performance();
    const setting = key === "monitor"
      ? current.monitorRefreshIntervalMs
      : key === "resourceTable"
        ? current.resourceTableRefreshIntervalMs
        : key === "management"
          ? current.managementRefreshIntervalMs
          : key === "discovery"
            ? current.discoveryRefreshIntervalMs
            : key === "optimization"
              ? current.optimizationRefreshIntervalMs
              : current.localSystemRefreshIntervalMs;
    return normalizeLogicRefreshIntervalSetting(key, setting);
  };
  const updateLogicRefreshInterval = (
    key: AppLogicRefreshIntervalKey,
    patch: Partial<AppPresetNumericSetting>
  ) => {
    props.onLogicRefreshIntervalChange(
      key,
      normalizeLogicRefreshIntervalSetting(key, {
        ...logicRefreshIntervalSetting(key),
        ...patch
      }));
  };
  const selectLogicRefreshMode = (key: AppLogicRefreshIntervalKey, mode: AppPresetNumericSettingMode) => {
    const setting = logicRefreshIntervalSetting(key);
    updateLogicRefreshInterval(key, {
      mode,
      customValue: mode === "preset"
        ? formatLogicRefreshIntervalPresetMs(key, setting.preset)
        : setting.customValue
    });
  };
  const selectLogicRefreshPreset = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    updateLogicRefreshInterval(key, {
      mode: "preset",
      preset,
      customValue: formatLogicRefreshIntervalPresetMs(key, preset)
    });
  };
  const updateLogicRefreshCustomValue = (key: AppLogicRefreshIntervalKey, customValue: number) => {
    updateLogicRefreshInterval(key, { mode: "custom", customValue });
  };
  const presetOptionLabel = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    const option = props.text.performance.refreshCadencePresetOptions.find((item) => item.id === preset);
    return `${option?.label ?? preset} · ${formatMilliseconds(formatLogicRefreshIntervalPresetMs(key, preset))}`;
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.smartMonitoringTitle}</strong>
          <span>{props.text.performance.smartMonitoringDescription(performance().monitoringIdleSeconds)}</span>
        </div>
        <SegmentedControl
          value={performance().smartMonitoringMode ?? "auto"}
          options={props.text.performance.adaptiveBooleanModeOptions}
          ariaLabel={props.text.performance.smartMonitoringTitle}
          onChange={props.onSmartMonitoringModeChange}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.automaticSchedulingOptimizationsTitle}</strong>
          <span>{props.text.performance.automaticSchedulingOptimizationsDescription}</span>
        </div>
        <SegmentedControl
          value={performance().automaticSchedulingOptimizationsEnabled === false ? "off" : "on"}
          options={props.text.performance.onOffOptions}
          ariaLabel={props.text.performance.automaticSchedulingOptimizationsTitle}
          onChange={(value) => props.onAutomaticSchedulingOptimizationsChange(value === "on")}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.preciseGpuPlacementTitle}</strong>
          <span>{props.text.performance.preciseGpuPlacementDescription}</span>
        </div>
        <SegmentedControl
          value={props.preciseGpuPlacementAvailable ? gpuSchedulingMode() : "basic"}
          options={gpuSchedulingModeOptions()}
          ariaLabel={props.text.performance.preciseGpuPlacementTitle}
          disabled={!props.preciseGpuPlacementAvailable}
          disabledTitle={uiText.misc.precisePlacementDisabled}
          onChange={(mode) => props.onPreciseGpuPlacementChange(mode === "precise")}
        />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.automaticMemoryCleanupTitle}</strong>
          <span>{props.text.performance.automaticMemoryCleanupDescription}</span>
        </div>
        <div class="settings-number-pair">
          <label>
            <span>{props.text.performance.physicalMemoryAutomaticCleanupLabel}</span>
            <input
              type="number"
              min="0"
              max="95"
              step="1"
              value={performance().physicalMemoryAutomaticCleanupPercent}
              onChange={(event) => updateCleanupLine("physical", event.currentTarget.value)}
            />
          </label>
          <label>
            <span>{props.text.performance.virtualMemoryAutomaticCleanupLabel}</span>
            <input
              type="number"
              min="0"
              max="95"
              step="1"
              value={performance().virtualMemoryAutomaticCleanupPercent}
              onChange={(event) => updateCleanupLine("virtual", event.currentTarget.value)}
            />
          </label>
        </div>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.memoryOptimizationTargetTitle}</strong>
          <span>{props.text.performance.memoryOptimizationTargetDescription}</span>
        </div>
        <div class="settings-number-pair">
          <label>
            <span>{props.text.performance.physicalMemoryOptimizationTargetLabel}</span>
            <input type="number" min="5" max="95" step="1" value={performance().physicalMemoryOptimizationTargetUsagePercent} onChange={(event) => updateTargetLine("physical", event.currentTarget.value)} />
          </label>
          <label>
            <span>{props.text.performance.virtualMemoryOptimizationTargetLabel}</span>
            <input type="number" min="5" max="95" step="1" value={performance().virtualMemoryOptimizationTargetUsagePercent} onChange={(event) => updateTargetLine("virtual", event.currentTarget.value)} />
          </label>
        </div>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.pauseHiddenTitle}</strong>
          <span>{props.text.performance.pauseHiddenDescription}</span>
        </div>
        <SegmentedControl
          value={performance().frontendHiddenRefreshMode ?? "auto"}
          options={props.text.performance.frontendHiddenRefreshModeOptions}
          ariaLabel={props.text.performance.pauseHiddenTitle}
          onChange={props.onFrontendHiddenRefreshModeChange}
        />
      </div>
      <div class="settings-row settings-row-block">
        <div class="settings-row-copy">
          <strong>{props.text.performance.refreshCadenceTitle}</strong>
          <span>{props.text.performance.refreshCadenceDescription}</span>
        </div>
        <div class="settings-interval-grid">
          <For each={logicRefreshIntervalKeys}>
            {(key) => {
              const itemText = () => props.text.performance.refreshCadenceItems[key];
              const setting = () => logicRefreshIntervalSetting(key);
              return (
                <div class="settings-interval-item">
                  <div class="settings-interval-copy">
                    <strong>{itemText().label}</strong>
                    <span>{itemText().description}</span>
                  </div>
                  <SegmentedControl<AppPresetNumericSettingMode>
                    value={setting().mode}
                    options={props.text.performance.presetNumericModeOptions}
                    ariaLabel={itemText().label}
                    onChange={(mode) => selectLogicRefreshMode(key, mode)}
                  />
                  <label class="settings-select settings-inline-select">
                    <span>{props.text.performance.refreshCadencePresetLabel}</span>
                    <StandardSelect<AppLogicRefreshIntervalPreset>
                      value={setting().preset}
                      ariaLabel={`${itemText().label} ${props.text.performance.refreshCadencePresetLabel}`}
                      options={props.text.performance.refreshCadencePresetOptions.map((option) => ({
                        value: option.id,
                        label: presetOptionLabel(key, option.id)
                      }))}
                      onChange={(value) => selectLogicRefreshPreset(key, value)}
                    />
                  </label>
                  <label class="settings-number-field">
                    <span>{props.text.performance.refreshCadenceCustomLabel}</span>
                    <input
                      type="number"
                      min="500"
                      max="300000"
                      step="100"
                      value={setting().customValue}
                      aria-label={`${itemText().label} ${props.text.performance.refreshCadenceCustomLabel}`}
                      onInput={(event) => updateLogicRefreshCustomValue(key, Number(event.currentTarget.value))}
                    />
                  </label>
                </div>
              );
            }}
          </For>
        </div>
      </div>
    </div>
  );
}

function formatMilliseconds(milliseconds: number) {
  if (milliseconds >= 1000 && milliseconds % 1000 === 0) {
    return `${milliseconds / 1000}s`;
  }

  return `${milliseconds}ms`;
}
