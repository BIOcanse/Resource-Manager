import { Show } from "solid-js";
import type { JSX } from "solid-js";
import { uiText } from "../../../text.ts";
import { OptimizationWorkspace } from "../../optimization/OptimizationWorkspace";
import { HostManagerSmartCoordinatorDetailsReport } from "../../details/HostManagerSmartCoordinatorDetailsReport";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Request tier choices and rule catalogs when this subpage becomes visible so their layout settles before lower reports enter the viewport. Hidden demand still releases both reads. */
export function SmartSchedulingSubpage(props: ManageSubpageProps) {
  const settings = (): JSX.Element => (
    <div class="manage-settings">
      <SettingsPanel panel="runtimePolicy" />
      <SettingsPanel panel="memory" />
      <SettingsPanel panel="memoryCpuTier" />
      <SettingsPanel panel="reportRules" />
    </div>
  );
  return (
    <div class="manage-subpage"
      {...frontendVisibilitySurface("manage.smart-scheduling.subpage",
        [frontendWorkIds.settingsMemoryCpuTier, frontendWorkIds.settingsReportRules])}>
      <Show when={props.optimizationEnabled} fallback={settings()}>
        <OptimizationWorkspace
          optimization={props.optimization}
          onInspectOptimizationTarget={props.onInspectOptimizationTarget}
          {...reportFeedback(props)}
          settings={settings()}
        />
        <ManageRecords name={() => uiText.misc.detailsTab.report}>
          <HostManagerSmartCoordinatorDetailsReport software={props.software}
            keepHistory={props.keepSmartRecordHistory} {...reportFeedback(props)} />
        </ManageRecords>
      </Show>
    </div>
  );
}
