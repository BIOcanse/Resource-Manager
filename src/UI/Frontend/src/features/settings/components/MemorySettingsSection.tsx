import { defaultAppSettings } from "../../../stores/settingsStore";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppPerformanceSettings } from "../../../types";

interface MemorySettingsSectionProps {
  settings: AppPerformanceSettings | undefined;
  text: SettingsTextBundle;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
}

/** 设置 → 内存：紧急清理阈值和内存优化目标。值存在设置的 performance 里。 */
export function MemorySettingsSection(props: MemorySettingsSectionProps) {
  // 占位值与「恢复为默认」同源，不另写一份默认值。
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
    </div>
  );
}
