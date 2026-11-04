import { uiText } from "../../../text.ts";
import { KernelLeakReportPage } from "../../kernelLeaks/KernelLeakReportPage.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** The report includes observation state; omit settings absent from the backend contract. */
export function KernelLeaksSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <ManageRecords name={() => uiText.kernelLeakReport.tab}>
        <KernelLeakReportPage {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
