import { defaultAppSettings } from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppPerformanceSettings } from "../../../types";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

interface MemorySettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
}

/** Edit emergency thresholds and optimization targets in performance settings. */
export function MemorySettingsSection(props: MemorySettingsSectionProps) {
  // Share placeholder values with reset defaults.
  const performance = (): AppPerformanceSettings => props.settings ?? defaultAppSettings().performance!;
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

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.performance.automaticMemoryCleanupTitle}</strong>
            <HelpHint label={props.text.performance.automaticMemoryCleanupTitle}>{props.text.performance.automaticMemoryCleanupDescription}</HelpHint>
          </div>
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
          <div class="settings-row-title">
            <strong>{props.text.performance.memoryOptimizationTargetTitle}</strong>
            <HelpHint label={props.text.performance.memoryOptimizationTargetTitle}>{props.text.performance.memoryOptimizationTargetDescription}</HelpHint>
          </div>
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
    </div>
  );
}
