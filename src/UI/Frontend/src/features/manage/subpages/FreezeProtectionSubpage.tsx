import { uiText } from "../../../text.ts";
import { SystemEventsReportPage } from "../../freezeReports/SystemEventsReportPage.tsx";
import { FreezeRecorderPage } from "../../freezeReports/FreezeRecorderPage.tsx";
import { DeviceActionRecordsPage } from "../../deviceActions/DeviceActionRecordsPage.tsx";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Keep recorder status and backend-document settings within their report components. */
export function FreezeProtectionSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <SettingsPanel panel="crashDumpKeys" />
      <ManageRecords name={() => uiText.freezeReports.systemEvents.tab}>
        <SystemEventsReportPage {...reportFeedback(props)} />
      </ManageRecords>
      <ManageRecords name={() => uiText.freezeReports.recorder.tab}>
        <FreezeRecorderPage {...reportFeedback(props)} />
      </ManageRecords>
      <ManageRecords name={() => uiText.deviceActions.records.tab}>
        <DeviceActionRecordsPage {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
