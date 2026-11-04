import { Show } from "solid-js";
import { defaultAppSettings } from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppPerformanceSettings } from "../../../types";
import { SegmentedControl } from "./SettingsControls";
import { uiText } from "../../../text.ts";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

type GpuSchedulingMode = "basic" | "precise";

interface RuntimePolicySettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  preciseGpuPlacementAvailable: boolean;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onGpuOverflowThresholdsChange: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  onRealtimeActionBudgetChange: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
  onKeepSmartRecordHistoryChange: (enabled: boolean) => void;
}

/** Edit scheduler placement, action limits, and record-retention choices in performance settings. */
export function RuntimePolicySettingsSection(props: RuntimePolicySettingsSectionProps) {
  // Share placeholder values with reset defaults.
  const performance = (): AppPerformanceSettings => props.settings ?? defaultAppSettings().performance!;
  const gpuSchedulingMode = (): GpuSchedulingMode => performance().preciseGpuPlacementEnabled ? "precise" : "basic";
  // Order enabled before disabled in binary selectors.
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
          <div class="settings-row-title">
            <strong>{props.text.performance.preciseGpuPlacementTitle}</strong>
            <HelpHint label={props.text.performance.preciseGpuPlacementTitle}>{props.text.performance.preciseGpuPlacementDescription}</HelpHint>
          </div>
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
      {/* Either threshold triggers overflow through the same placement plan. */}
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.performance.gpuOverflowTitle}</strong>
            <HelpHint label={props.text.performance.gpuOverflowTitle}>{props.text.performance.gpuOverflowDescription}</HelpHint>
          </div>
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
          <div class="settings-row-title">
            <strong>{props.text.performance.realtimeActionBudgetTitle}</strong>
            <HelpHint label={props.text.performance.realtimeActionBudgetTitle}>{props.text.performance.realtimeActionBudgetDescription}</HelpHint>
          </div>
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
      {/* Disabling history retention preserves existing history. */}
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.performance.keepSmartRecordHistoryTitle}</strong>
            <HelpHint label={props.text.performance.keepSmartRecordHistoryTitle}>{props.text.performance.keepSmartRecordHistoryDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl<"on" | "off">
          value={performance().keepSmartRecordHistory ? "on" : "off"}
          options={[
            { id: "on", ...props.text.performance.keepSmartRecordHistoryOptions.on },
            { id: "off", ...props.text.performance.keepSmartRecordHistoryOptions.off }
          ]}
          ariaLabel={props.text.performance.keepSmartRecordHistoryTitle}
          onChange={(value) => props.onKeepSmartRecordHistoryChange(value === "on")}
        />
      </div>
    </div>
  );
}
