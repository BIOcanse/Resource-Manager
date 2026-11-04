import { createEffect, createMemo, createSignal, onCleanup, type Accessor } from "solid-js";
import type { CurrentValueSource } from "../../frontendRuntime/push/PushValueSourceFamily.ts";
import { bindPushSource } from "../../frontendRuntime/push/usePushSource.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  acknowledgeCrashReport, deleteCrashReport, packageCrashReport, requestCrashReportIssueLink,
  type CrashReport, type CrashReportList
} from "../../data/crashReports/crashReports.ts";
import { requestShellOpen, type ShellOpenRequest } from "../../host/shellOpen.ts";
import { currentSettingsText } from "../../i18n/appTextStore.ts";
import { userFacingDateTime, userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import type { ConfirmDialogRequest } from "../../ui/patterns/ConfirmDialog.tsx";

/** The backend owns records and acknowledgement state. Local dismissal prevents repeated prompts after acknowledgement fails and lasts only for this UI instance. */
export interface CrashReportStore {
  readonly list: Accessor<CrashReportList | undefined>;
  /** Include unread records that have not been dismissed in this UI instance. */
  readonly unacknowledged: Accessor<readonly CrashReport[]>;
  readonly busy: (id: string) => boolean;
  readonly reveal: (path: string) => Promise<void>;
  readonly buildPackage: (report: CrashReport) => Promise<void>;
  /** Package the report, open its issue URL, reveal the package, then acknowledge it. */
  readonly reportOnGitHub: (report: CrashReport) => Promise<void>;
  /** Open the prefilled issue URL, then acknowledge the record. */
  readonly openIssueLink: (report: CrashReport) => Promise<void>;
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

  async function openUrl(url: string) {
    try { await open({ kind: "url", url }); return true; }
    catch { options.notifyError(copy().openFailed); return false; }
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
    reportOnGitHub: (report) => withBusy(report.id, async () => {
      const receipt = await packageCrashReport(options.requestClient, report.id);
      const link = await requestCrashReportIssueLink(options.requestClient, report.id);
      if (!await openUrl(link.url)) return;
      await reveal(receipt.package.path);
      await acknowledge([link.report]);
    }),
    openIssueLink: (report) => withBusy(report.id, async () => {
      const link = await requestCrashReportIssueLink(options.requestClient, report.id);
      if (!await openUrl(link.url)) return;
      await acknowledge([link.report]);
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
