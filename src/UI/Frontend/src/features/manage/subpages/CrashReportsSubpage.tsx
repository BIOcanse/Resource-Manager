import { CrashDumpRequestCard } from "../../crashReports/CrashDumpRequestCard.tsx";
import { CrashReportRecords } from "../../crashReports/CrashReportRecords.tsx";
import { currentSettingsText } from "../../../i18n/appTextStore.ts";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/**
 * Manage > Crash reports. Status: the dump-mode request card while the backend mode is undecided;
 * settings: crash dump settings; records: all crash records, with the shared Clear history in the header.
 */
export function CrashReportsSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <CrashDumpRequestCard diagnostics={props.settings.crashDumps()} onChoose={props.settings.chooseCrashDumpMode} />
      <SettingsPanel panel="crashDumps" />
      <ManageRecords name={() => currentSettingsText().crashDumps.historyTitle}>
        <CrashReportRecords {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
