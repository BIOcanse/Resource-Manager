import { SettingsPanel } from "../../settings/SettingsPanel.tsx";

/** Show the read-only overlay overview without save actions. */
export function PerformanceOverlaySubpage() {
  return (
    <div class="manage-subpage">
      <SettingsPanel panel="performanceOverlay" />
    </div>
  );
}
