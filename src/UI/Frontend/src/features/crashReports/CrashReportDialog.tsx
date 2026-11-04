import { createMemo, For } from "solid-js";
import { DialogActions, DialogBody, DialogHeader, DialogRoot } from "../../ui/primitives/Dialog.tsx";
import { currentSettingsText } from "../../i18n/appTextStore.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import type { CrashReportStore } from "./crashReportStore.ts";
import { crashFaultSummary } from "./crashReportPresentation.ts";

/**
 * Crash dialog: while the UI is open and the backend has unread records, lists them all with the program,
 * exit time and fault summary, and says the reports are saved under Manage > Crash reports.
 * View opens that subpage; Close (and the title bar close) only closes the dialog. Both mark the listed records read.
 * The dialog offers no external address.
 */
export function CrashReportDialog(props: {
  readonly store: CrashReportStore;
  readonly active: boolean;
  /** Opens Manage > Crash reports. */
  readonly onView: () => void;
}) {
  const copy = () => currentSettingsText().crashDumps;
  const reports = createMemo(() => props.store.unacknowledged());
  const open = () => props.active && reports().length > 0;
  const close = () => void props.store.acknowledge(reports());
  const view = () => {
    close();
    props.onView();
  };
  return (
    <DialogRoot open={open()} labelledBy="crashReportDialogTitle" describedBy="crashReportDialogIntro"
      class="crash-report-dialog" dismissOnBackdrop={false} onDismiss={close}>
      <DialogHeader title={copy().dialogTitle} titleId="crashReportDialogTitle" onDismiss={close} />
      <DialogBody class="crash-report-dialog-body">
        <p id="crashReportDialogIntro">{copy().dialogIntro}</p>
        <ul class="crash-report-dialog-list">
          <For each={reports()}>{(report) => (
            <li class="crash-report-dialog-item">
              <strong>{copy().dialogCrashed(copy().program(report.program, copy().roles[report.role]), userFacingDateTime(report.occurredAt))}</strong>
              <span>{crashFaultSummary(report.fault, copy())}</span>
            </li>
          )}</For>
        </ul>
        <p class="crash-report-dialog-saved">{copy().dialogSaved}</p>
      </DialogBody>
      <DialogActions>
        <button type="button" class="secondary" onClick={close}>{copy().dialogClose}</button>
        <button type="button" onClick={view}>{copy().dialogView}</button>
      </DialogActions>
    </DialogRoot>
  );
}
