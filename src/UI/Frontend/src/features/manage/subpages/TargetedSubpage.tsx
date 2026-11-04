import { uiText } from "../../../text.ts";
import { TargetedReportsPage } from "../../targetedReport/TargetedReportsPage.tsx";
import { ManageRecords, reportFeedback, type ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Compose recording controls, profiles, and records; navigation requires writable persistence. */
export function TargetedSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <ManageRecords name={() => uiText.targetedReport.tab}>
        <TargetedReportsPage {...reportFeedback(props)} />
      </ManageRecords>
    </div>
  );
}
