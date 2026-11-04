import { For, Show } from "solid-js";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import type { CrashReport } from "../../data/crashReports/crashReports.ts";
import { currentSettingsText } from "../../i18n/appTextStore.ts";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { useCrashReports } from "./CrashReportContext.tsx";
import type { CrashReportStore } from "./crashReportStore.ts";
import { crashFaultSummary, crashReportDumpText, crashReportLocalFile } from "./crashReportPresentation.ts";

/** A local file path; clicking it asks the window process to select the file in Explorer. */
function CrashReportPathButton(props: { readonly path: string; readonly onReveal: (path: string) => Promise<void> }) {
  return (
    <button type="button" class="crash-report-path" onClick={() => void props.onReveal(props.path)}>
      {props.path}
    </button>
  );
}

/**
 * Crash records under Manage > Crash reports, newest first. Each shows time, program, version, fault summary,
 * dump path and size (or that the system rotated it away) and package size; its buttons only call the backend.
 * Clear history confirms, then removes acknowledged records with their dumps and packages; unread ones stay.
 * Acknowledged records count as history; the button is disabled when there are none.
 */
export function CrashReportRecords(props: ReportHistoryClearFeedback) {
  const store = useCrashReports();
  const pageName = useCurrentPageName();
  const copy = () => currentSettingsText().crashDumps;
  const reports = () => store?.list()?.reports ?? [];
  const historyCount = () => reports().filter(report => report.acknowledged).length;
  const history = useReportHistoryClear({ ...props, kind: "crashReports", historyCount });
  return (
    <section class="crash-report-records" aria-label={pageName()}>
      <div class="panel-header crash-report-records-header">
        <CurrentPageHeading />
        <div class="crash-report-records-actions">
          <button class="secondary panel-refresh-button" type="button"
            disabled={historyCount() === 0 || history.clearing()} onClick={() => void history.clear()}>
            <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
              current={history.clearing() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <p class="crash-report-records-note">{copy().historyDescription}</p>
      <Show when={store && reports().length > 0} fallback={<p class="crash-report-empty">{copy().historyEmpty}</p>}>
        <ul class="crash-report-list">
          <For each={reports()}>{(report) => <CrashReportItem store={store!} report={report} />}</For>
        </ul>
      </Show>
    </section>
  );
}

function CrashReportItem(props: { readonly store: CrashReportStore; readonly report: CrashReport }) {
  const copy = () => currentSettingsText().crashDumps;
  const report = () => props.report;
  const dump = () => crashReportDumpText(report(), copy());
  const file = () => crashReportLocalFile(report());
  const busy = () => props.store.busy(report().id);
  const dumpSize = () => report().dumpRemoved ? null : report().dump;
  return (
    <li class="crash-report-item" aria-busy={busy()}>
      <strong>{copy().program(report().program, copy().roles[report().role])}</strong>
      <dl class="crash-report-facts">
        <dt>{copy().timeLabel}</dt><dd>{userFacingDateTime(report().occurredAt)}</dd>
        <dt>{copy().versionLabel}</dt><dd>{report().version ?? copy().versionUnknown}</dd>
        <dt>{copy().faultLabel}</dt><dd>{crashFaultSummary(report().fault, copy())}</dd>
        <dt>{copy().dumpLabel}</dt>
        <dd>
          <Show when={dump().path}>{(path) => report().dumpStatus === "saved"
            ? <CrashReportPathButton path={path()} onReveal={props.store.reveal} />
            : <code class="crash-report-code">{path()}</code>}</Show>
          <Show when={dump().note}>{(note) => <span>{note()}</span>}</Show>
        </dd>
        <Show when={dumpSize()}>{(item) => <><dt>{copy().sizeLabel}</dt><dd>{formatBytes(item().sizeBytes, "storage")}</dd></>}</Show>
        <dt>{copy().packageLabel}</dt>
        <dd>
          <Show when={report().package} fallback={<span>{copy().packageNone}</span>}>{(item) => <>
            <CrashReportPathButton path={item().path} onReveal={props.store.reveal} />
            <span> · {formatBytes(item().sizeBytes, "storage")}</span>
          </>}</Show>
        </dd>
      </dl>
      <div class="crash-report-actions">
        <button type="button" class="secondary" disabled={busy() || file() === null}
          onClick={() => { const path = file(); if (path) void props.store.reveal(path); }}>{copy().openFolder}</button>
        <button type="button" class="secondary" disabled={busy()} onClick={() => void props.store.buildPackage(report())}>{copy().buildPackage}</button>
        <button type="button" class="secondary" disabled={busy()} onClick={() => void props.store.remove(report())}>{copy().delete}</button>
      </div>
    </li>
  );
}
