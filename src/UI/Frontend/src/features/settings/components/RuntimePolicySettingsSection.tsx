import { Show } from "solid-js";
import { defaultAppSettings } from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppPerformanceSettings } from "../../../types";
import { SegmentedControl } from "./SettingsControls";
import { uiText } from "../../../text.ts";

type GpuSchedulingMode = "basic" | "precise";

interface RuntimePolicySettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  preciseGpuPlacementAvailable: boolean;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onGpuOverflowThresholdsChange: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  onRealtimeActionBudgetChange: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
}

/** 设置 → 运行策略：调度怎么选卡、什么时候溢出到集显、每轮实时动作的上限。值存在设置的 performance 里。 */
export function RuntimePolicySettingsSection(props: RuntimePolicySettingsSectionProps) {
  // 占位值与「恢复为默认」同源，不另写一份默认值。
  const performance = (): AppPerformanceSettings => props.settings ?? defaultAppSettings().performance!;
  const gpuSchedulingMode = (): GpuSchedulingMode => performance().preciseGpuPlacementEnabled ? "precise" : "basic";
  // 设置页的两态开关一律「开启在前、关闭在后」。
  const gpuSchedulingModeOptions = (): Array<{ id: GpuSchedulingMode; label: string; description: string }> => [
    { id: "precise", ...props.text.performance.preciseGpuPlacementModeOptions.precise },
    { id: "basic", ...props.text.performance.preciseGpuPlacementModeOptions.basic }
  ];
  const updateGpuOverflowThreshold = (field: "usage" | "dedicated", value: string) => {
    const number = Number(value);
    const usage = field === "usage" ? number : performance().gpuOverflowUsagePercent;
    const dedicated = field === "dedicated" ? number : performance().gpuOverflowDedicatedMemoryPercent;
    props.onGpuOverflowThresholdsChange(usage, dedicated);
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.performance.preciseGpuPlacementTitle}</strong>
          <span>{props.text.performance.preciseGpuPlacementDescription}</span>
          <Show when={!props.preciseGpuPlacementAvailable}><span role="note">{uiText.misc.precisePlacementDisabled}</span></Show>
        </div>
        <SegmentedControl
          value={props.preciseGpuPlacementAvailable ? gpuSchedulingMode() : "basic"}
          options={gpuSchedulingModeOptions()}
          ariaLabel={props.text.performance.preciseGpuPlacementTitle}
          disabled={!props.preciseGpuPlacementAvailable}
          onChange={(mode) => props.onPreciseGpuPlacementChange(mode === "precise")}
        />
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
    </div>
  );
}
