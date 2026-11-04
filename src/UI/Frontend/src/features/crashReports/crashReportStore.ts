import { createEffect, createMemo, createSignal, onCleanup, type Accessor } from "solid-js";
import type { CurrentValueSource } from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { bindPushSource } from "../../frontendRuntime/push/usePushSource.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  acknowledgeCrashReport, deleteCrashReport, packageCrashReport,
  type CrashReport, type CrashReportList
} from "../../data/crashReports/crashReports.ts";
import { requestShellOpen, type ShellOpenRequest } from "../../host/shellOpen.ts";
import { currentSettingsText } from "../../i18n/appTextStore.ts";
import { userFacingDateTime, userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import type { ConfirmDialogRequest } from "../../ui/patterns/ConfirmDialog.tsx";

/**
 * Crash record store: subscribes to the backend list while the UI is visible; actions only call the backend,
 * and the window process selects files on user action. History clear uses the shared useReportHistoryClear.
 *
 * The only local state is the records whose dialog was closed in this UI instance (dismissed), so a failed
 * acknowledgement does not reopen the dialog; the next UI instance follows the backend's acknowledged flag.
 */
export interface CrashReportStore {
  readonly list: Accessor<CrashReportList | undefined>;
  /** Include unread records that have not been dismissed in this UI instance. */
  readonly unacknowledged: Accessor<readonly CrashReport[]>;
  readonly busy: (id: string) => boolean;
  readonly reveal: (path: string) => Promise<void>;
  readonly buildPackage: (report: CrashReport) => Promise<void>;
  /** Crash dialog View and Close: mark every listed record read; records stay under Manage > Crash reports. */
  readonly acknowledge: (reports: readonly CrashReport[]) => Promise<void>;
  readonly remove: (report: CrashReport) => Promise<void>;
}

export function createCrashReportStore(options: {
  readonly source: CurrentValueSource<CrashReportList>;
  readonly active: Accessor<boolean>;
  readonly requestClient: Pick<RequestClient, "request">;
  readonly confirm: (request: ConfirmDialogRequest) => Promise<boolean>;
  readonly notifyError: (message: string) => void;
  readonly open?: (request: ShellOpenRequest) => Promise<void>;
}): CrashReportStore {
  const open = options.open ?? ((request: ShellOpenRequest) => requestShellOpen(request));
  const [list, setList] = createSignal<CrashReportList>();
  const [busyIds, setBusyIds] = createSignal<ReadonlySet<string>>(new Set());
  const [dismissed, setDismissed] = createSignal<ReadonlySet<string>>(new Set());
  createEffect(() => {
    if (!options.active()) { setDismissed(new Set<string>()); return; }
    const unsubscribe = bindPushSource(options.source, { active: true }, (snapshot) => {
      if (snapshot.status === "ready" && snapshot.data) setList(snapshot.data);
    });
    onCleanup(unsubscribe);
  });
  const unacknowledged = createMemo(() => (list()?.reports ?? [])
    .filter(report => !report.acknowledged && !dismissed().has(report.id)));
  const copy = () => currentSettingsText().crashDumps;

  async function withBusy(id: string, work: () => Promise<void>): Promise<void> {
    if (busyIds().has(id)) return;
    setBusyIds(current => new Set([...current, id]));
    try { await work(); }
    catch (error) { options.notifyError(userFacingErrorMessage(error, copy().actionFailed)); }
    finally { setBusyIds(current => { const next = new Set(current); next.delete(id); return next; }); }
  }

  async function reveal(path: string) {
    try { await open({ kind: "file", path }); }
    catch { options.notifyError(copy().revealFailed); }
  }

  async function acknowledge(reports: readonly CrashReport[]) {
    if (reports.length === 0) return;
    setDismissed(current => new Set([...current, ...reports.map(report => report.id)]));
    const pending = reports.filter(report => !report.acknowledged);
    const results = await Promise.allSettled(pending.map(report => acknowledgeCrashReport(options.requestClient, report.id)));
    if (results.some(result => result.status === "rejected")) options.notifyError(copy().actionFailed);
  }

  return {
    list,
    unacknowledged,
    busy: (id) => busyIds().has(id),
    reveal,
    buildPackage: (report) => withBusy(report.id, async () => {
      await packageCrashReport(options.requestClient, report.id);
    }),
    acknowledge,
    remove: (report) => withBusy(report.id, async () => {
      const text = copy();
      if (!await options.confirm({ title: text.deleteConfirmTitle, tone: "warning", confirmLabel: text.delete,
        message: text.deleteConfirmMessage(report.program, userFacingDateTime(report.occurredAt)) })) return;
      await deleteCrashReport(options.requestClient, report.id);
    })
  };
}
