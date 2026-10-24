import { useFrontendReadIntervals } from "../../../app/FrontendReadIntervalsContext.tsx";
import { For, Match, Show, Switch } from "solid-js";
import { StandardSelect } from "../../../components/StandardSelect";
import {
  defaultAppSettings,
  logicRefreshIntervalKeys,
  normalizeLogicRefreshIntervalSetting
} from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppAdaptiveBooleanMode,
  AppLogicRefreshIntervalKey,
  AppLogicRefreshIntervalPreset,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  AppPresetNumericSettingMode
} from "../../../types";
import { ControlAccessLevelSetting } from "./ControlAccessLevelSetting";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { uiText } from "../../../text.ts";

type GpuSchedulingMode = "basic" | "precise";

interface PerformanceSettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  preciseGpuPlacementAvailable: boolean;
  onSmartMonitoringModeChange: (mode: AppAdaptiveBooleanMode) => void;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onGpuOverflowThresholdsChange: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  onRealtimeActionBudgetChange: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
}

export function PerformanceSettingsSection(props: PerformanceSettingsSectionProps) {
  const publishedIntervals = useFrontendReadIntervals();
  const presetInterval = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) =>
    publishedIntervals.configuration()!.presets[key][preset];
  // 占位值与「恢复为默认」同源，不另写一份默认值。
  const performance = (): AppPerformanceSettings => props.settings ?? defaultAppSettings().performance!;
  const gpuSchedulingMode = (): GpuSchedulingMode => performance().preciseGpuPlacementEnabled ? "precise" : "basic";
  // 设置页的两态开关一律「开启在前、关闭在后」。
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
  const updateGpuOverflowThreshold = (field: "usage" | "dedicated", value: string) => {
    const number = Number(value);
    const usage = field === "usage" ? number : performance().gpuOverflowUsagePercent;
    const dedicated = field === "dedicated" ? number : performance().gpuOverflowDedicatedMemoryPercent;
    props.onGpuOverflowThresholdsChange(usage, dedicated);
  };
  const logicRefreshIntervalSetting = (key: AppLogicRefreshIntervalKey) => {
    const current = performance();
    const setting = key === "monitor"
      ? current.monitorRefreshIntervalMs
      : key === "resourceTable"
        ? current.resourceTableRefreshIntervalMs
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
        ? presetInterval(key, setting.preset)
        : setting.customValue
    });
  };
  const selectLogicRefreshPreset = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    updateLogicRefreshInterval(key, {
      mode: "preset",
      preset,
      customValue: presetInterval(key, preset)
    });
  };
  const updateLogicRefreshCustomValue = (key: AppLogicRefreshIntervalKey, customValue: number) => {
    updateLogicRefreshInterval(key, { mode: "custom", customValue });
  };
  const presetOptionLabel = (key: AppLogicRefreshIntervalKey, preset: AppLogicRefreshIntervalPreset) => {
    const option = props.text.performance.refreshCadencePresetOptions.find((item) => item.id === preset);
    return `${option?.label ?? preset} · ${formatMilliseconds(presetInterval(key, preset))}`;
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.smartMonitoringTitle}</strong>
          <span>{props.text.performance.smartMonitoringDescription}</span>
        </div>
        <SegmentedControl
          value={performance().smartMonitoringMode}
          options={props.text.performance.adaptiveBooleanModeOptions}
          ariaLabel={props.text.performance.smartMonitoringTitle}
          onChange={props.onSmartMonitoringModeChange}
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
      {/* 两项阈值由设置发布到同一放置计划，任一项达到即触发溢出。 */}
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.gpuOverflowTitle}</strong>
          <span>{props.text.performance.gpuOverflowDescription}</span>
        </div>
        <div class="settings-number-pair">
          <label>
            <span>{props.text.performance.gpuOverflowUsageLabel}</span>
            <input
              type="number"
              min="1"
              max="100"
              step="1"
              value={performance().gpuOverflowUsagePercent}
              onChange={(event) => updateGpuOverflowThreshold("usage", event.currentTarget.value)}
            />
          </label>
          <label>
            <span>{props.text.performance.gpuOverflowDedicatedMemoryLabel}</span>
            <input
              type="number"
              min="1"
              max="100"
              step="1"
              value={performance().gpuOverflowDedicatedMemoryPercent}
              onChange={(event) => updateGpuOverflowThreshold("dedicated", event.currentTarget.value)}
            />
          </label>
        </div>
      </div>
      <div class="settings-row settings-row-block">
        <div class="settings-row-copy">
          <strong>{props.text.performance.realtimeActionBudgetTitle}</strong>
          <span>{props.text.performance.realtimeActionBudgetDescription}</span>
        </div>
        <SegmentedControl<AppPerformanceSettings["realtimeActionBudgetMode"]>
          value={performance().realtimeActionBudgetMode}
          options={props.text.performance.realtimeActionBudgetModeOptions}
          ariaLabel={props.text.performance.realtimeActionBudgetTitle}
          onChange={(mode) => props.onRealtimeActionBudgetChange(mode, performance().realtimeActionBudgetCount, performance().realtimeActionBudgetPercent)}
        />
        <div class="settings-number-pair">
          <label>
            <span>{props.text.performance.realtimeActionBudgetCountLabel}</span>
            <input type="number" min="1" max="8192" step="1"
              value={performance().realtimeActionBudgetCount}
              onChange={(event) => props.onRealtimeActionBudgetChange(performance().realtimeActionBudgetMode, Number(event.currentTarget.value), performance().realtimeActionBudgetPercent)} />
          </label>
          <label>
            <span>{props.text.performance.realtimeActionBudgetPercentLabel}</span>
            <input type="number" min="1" max="100" step="1"
              value={performance().realtimeActionBudgetPercent}
              onChange={(event) => props.onRealtimeActionBudgetChange(performance().realtimeActionBudgetMode, performance().realtimeActionBudgetCount, Number(event.currentTarget.value))} />
          </label>
        </div>
      </div>
      <Show when={publishedIntervals.configuration()}>
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
                  {/* 只显示当前模式真正生效的输入：锁定预设显示预设下拉框，自定义显示毫秒输入框，自动调度两者都不显示。 */}
                  <Switch>
                    <Match when={setting().mode === "preset"}>
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
                    </Match>
                    <Match when={setting().mode === "custom"}>
                      <NumberField
                        label={props.text.performance.refreshCadenceCustomLabel}
                        ariaLabel={`${itemText().label} ${props.text.performance.refreshCadenceCustomLabel}`}
                        value={setting().customValue}
                        min={500}
                        max={300000}
                        step={100}
                        onChange={(value) => updateLogicRefreshCustomValue(key, value)}
                      />
                    </Match>
                  </Switch>
                </div>
              );
            }}
          </For>
        </div>
      </div>

      </Show>
      {/*
        调节权限档位。放在性能这一节，因为它管的就是控制页里那些调频调压的项。
        它不走这一页的草稿-保存：值存在控制面那边，选完就生效（见组件自己的说明）。
      */}
      <ControlAccessLevelSetting />
    </div>
  );
}

function formatMilliseconds(milliseconds: number) {
  if (milliseconds >= 1000 && milliseconds % 1000 === 0) {
    return `${milliseconds / 1000}s`;
  }

  return `${milliseconds}ms`;
}
