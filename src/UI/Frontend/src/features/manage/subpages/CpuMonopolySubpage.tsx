import { uiText } from "../../../text.ts";
import { CpuMonopolyRecordsPage } from "../../cpuMonopoly/CpuMonopolyRecordsPage.tsx";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Compose CPU protection status, settings, and records. */
export function CpuMonopolySubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <SettingsPanel panel="cpuMonopolyProtection" />
      <ManageRecords name={() => uiText.cpuMonopoly.records.tab}>
        <CpuMonopolyRecordsPage {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
