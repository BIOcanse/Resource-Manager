import { CrashDumpRequestCard } from "../../crashReports/CrashDumpRequestCard.tsx";
import { SettingsPanel } from "../../settings/SettingsPanel.tsx";
import type { ManageSubpageProps } from "./manageSubpageParts.tsx";

/** Compose the undecided-mode prompt, dump settings, and crash records. */
export function CrashReportsSubpage(props: ManageSubpageProps) {
  return (
    <div class="manage-subpage">
      <CrashDumpRequestCard diagnostics={props.settings.crashDumps()} onChoose={props.settings.chooseCrashDumpMode} />
      <SettingsPanel panel="crashDumps" />
    </div>
  );
}
