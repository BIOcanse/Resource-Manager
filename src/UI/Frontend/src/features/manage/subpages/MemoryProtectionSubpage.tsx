import { uiText } from "../../../text.ts";
import { MemoryProtectionReportPage } from "../../memoryProtection/MemoryProtectionReportPage.tsx";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Compose backend memory-protection status, settings, and records. */
export function MemoryProtectionSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <SettingsPanel panel="memoryProtection" />
      <ManageRecords name={() => uiText.memoryProtection.reportTab}>
        <MemoryProtectionReportPage {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
