import { DiskUsageWorkspace } from "../../diskUsage/DiskUsageWorkspace";
import type { ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Compose disk usage, result details, and health. */
export function DiskManagementSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <DiskUsageWorkspace onActionError={(error, fallback) => props.onActionError(error, fallback)} />
    </div>
  );
}
