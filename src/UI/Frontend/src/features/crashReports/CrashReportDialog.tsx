import { createMemo, For, Show } from "solid-js";
import { DialogActions, DialogBody, DialogHeader, DialogRoot } from "../../ui/primitives/Dialog.tsx";
import { currentSettingsText } from "../../i18n/appTextStore.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import type { CrashReportStore } from "./crashReportStore.ts";
import { crashFaultSummary, crashReportDumpText, crashReportLocalFile } from "./crashReportPresentation.ts";

/** 一个本地文件路径，点开时由窗口进程在资源管理器里选中它。 */
export function CrashReportPathButton(props: { readonly path: string; readonly onReveal: (path: string) => Promise<void> }) {
  return (
    <button type="button" class="crash-report-path" onClick={() => void props.onReveal(props.path)}>
      {props.path}
    </button>
  );
}

/**
 * 崩溃提示弹窗（docs/crash_dumps_2026-10-31.md“前端”）：界面打开着、后端列表里有未读记录时，一个弹窗列出全部未读记录：
 * 程序、时间、故障摘要；每条一个 GitHub 链接（点开时向后端要预填的 issue 地址并在浏览器打开）和一个本地文件路径
 * （有报告包时是报告包，否则是转储文件；点开在资源管理器里选中）。每条的“在 GitHub 报告”生成报告包、打开预填页面、
 * 选中报告包并置已读；“稍后”和关闭弹窗把列出的记录都置已读，记录留在设置 · 系统集成。
 */
export function CrashReportDialog(props: { readonly store: CrashReportStore; readonly active: boolean }) {
  const copy = () => currentSettingsText().crashDumps;
  const reports = createMemo(() => props.store.unacknowledged());
  const open = () => props.active && reports().length > 0;
  const later = () => void props.store.acknowledge(reports());
  return (
    <DialogRoot open={open()} labelledBy="crashReportDialogTitle" describedBy="crashReportDialogIntro"
      class="crash-report-dialog" dismissOnBackdrop={false} onDismiss={later}>
      <DialogHeader title={copy().dialogTitle} titleId="crashReportDialogTitle" onDismiss={later} />
      <DialogBody class="crash-report-dialog-body">
        <p id="crashReportDialogIntro">{copy().dialogIntro}</p>
        <ul class="crash-report-dialog-list">
          <For each={reports()}>{(report) => {
            const file = () => crashReportLocalFile(report);
            const busy = () => props.store.busy(report.id);
            return (
              <li class="crash-report-dialog-item" aria-busy={busy()}>
                <strong>{copy().dialogCrashed(copy().program(report.program, copy().roles[report.role]), userFacingDateTime(report.occurredAt))}</strong>
                <span>{crashFaultSummary(report.fault, copy())}</span>
                <button type="button" class="crash-report-link" disabled={busy()} onClick={() => void props.store.openIssueLink(report)}>
                  {copy().dialogIssueLink}
                </button>
                <span class="crash-report-file">
                  <span class="crash-report-file-label">{copy().dialogLocalPath}</span>
                  <Show when={file()} fallback={<span>{crashReportDumpText(report, copy()).note}</span>}>
                    {(path) => <CrashReportPathButton path={path()} onReveal={props.store.reveal} />}
                  </Show>
                </span>
                <div class="crash-report-dialog-item-actions">
                  <button type="button" disabled={busy()} onClick={() => void props.store.reportOnGitHub(report)}>
                    {copy().reportOnGitHub}
                  </button>
                </div>
              </li>
            );
          }}</For>
        </ul>
      </DialogBody>
      <DialogActions>
        <button type="button" class="secondary" onClick={later}>{copy().dialogLater}</button>
      </DialogActions>
    </DialogRoot>
  );
}
