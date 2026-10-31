import { createSignal, For, Show } from "solid-js";
import type { AppCrashDumpMode, AppCrashDumpSettings, CrashDumpDiagnostics } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { useCrashReports } from "../../crashReports/CrashReportContext.tsx";
import { CrashReportPathButton } from "../../crashReports/CrashReportDialog.tsx";
import {
  crashDumpModeView, crashDumpStatusView, crashFaultSummary, crashReportDumpText, crashReportLocalFile
} from "../../crashReports/crashReportPresentation.ts";
import { formatBytes } from "../../../presentation/byteUnits.ts";
import { userFacingDateTime } from "../../../presentation/userFacingText.ts";

/**
 * 设置 · 系统集成 · 崩溃转储（docs/crash_dumps_2026-10-31.md）：模式、转储类型、保留份数改设置草稿，随设置页保存；
 * 实际生效的模式和来源、期望与实际的差别来自后端诊断。beta 版默认开启时给一个立即生效的“关闭崩溃转储”。
 * 下面是全部历史记录（后端订阅），按钮只调用后端，打开和选中由窗口进程做。
 */
export function CrashDumpSettings(props: {
  settings: AppCrashDumpSettings;
  diagnostics: CrashDumpDiagnostics | undefined;
  text: SettingsTextBundle;
  onChange: (crashDumps: AppCrashDumpSettings) => void;
  onChooseMode: (mode: AppCrashDumpMode) => Promise<void>;
}) {
  const copy = () => props.text.crashDumps;
  const current = () => props.settings;
  const modeView = () => crashDumpModeView(props.diagnostics, copy());
  const statusView = () => crashDumpStatusView(props.diagnostics, copy());
  const modeOptions = () => (["default", "ask", "on", "off"] as const).map(id => ({ id, ...copy().modeOptions[id] }));
  const typeOptions = () => (["mini", "full"] as const).map(id => ({ id, ...copy().dumpTypeOptions[id] }));
  const [turningOff, setTurningOff] = createSignal(false);
  const [turnOffFailed, setTurnOffFailed] = createSignal(false);
  async function turnOff() {
    setTurningOff(true);
    setTurnOffFailed(false);
    try { await props.onChooseMode("off"); }
    catch { setTurnOffFailed(true); }
    finally { setTurningOff(false); }
  }
  const reports = useCrashReports();

  return (
    <section class="crash-dump-settings" aria-labelledby="crashDumpSettingsTitle">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong id="crashDumpSettingsTitle">{copy().title}</strong>
          <span>{copy().description}</span>
        </div>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{copy().modeTitle}</strong>
          <span>{copy().modeDescription}</span>
          <Show when={modeView()}>{(view) => <span class="crash-dump-effective" role="status">{view().effective}</span>}</Show>
        </div>
        <SegmentedControl value={current().mode} options={modeOptions()} ariaLabel={copy().modeTitle}
          onChange={(mode) => props.onChange({ ...current(), mode })} />
      </div>
      <Show when={modeView()?.offerTurnOff}>
        <div class="settings-row">
          <div class="settings-row-copy">
            <span>{copy().turnOffDescription}</span>
            <Show when={turnOffFailed()}><span role="alert">{copy().requestFailed}</span></Show>
          </div>
          <button type="button" class="secondary" disabled={turningOff()} onClick={() => void turnOff()}>{copy().turnOff}</button>
        </div>
      </Show>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{copy().dumpTypeTitle}</strong>
          <span>{copy().dumpTypeDescription}</span>
        </div>
        <SegmentedControl value={current().dumpType} options={typeOptions()} ariaLabel={copy().dumpTypeTitle}
          onChange={(dumpType) => props.onChange({ ...current(), dumpType })} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <span>{copy().keepCountDescription}</span>
        </div>
        <NumberField label={copy().keepCountTitle} value={current().keepCount} min={1}
          max={props.diagnostics?.keepCountMaximum ?? current().keepCount} step={1}
          disabled={props.diagnostics === undefined}
          onChange={(keepCount) => props.onChange({ ...current(), keepCount })} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy" role="status">
          <strong>{copy().statusTitle}</strong>
          <span>{statusView().status}</span>
          <For each={statusView().reasons}>{(reason) => <span>{reason}</span>}</For>
          <Show when={props.diagnostics}>{(diagnostics) =>
            <span><span class="crash-report-file-label">{copy().dumpFolderLabel}</span> <code class="crash-report-code">{diagnostics().dumpFolder}</code></span>}</Show>
          <For each={statusView().images}>{(image) =>
            <span><code class="crash-report-code">{image.image}</code> {image.state}{image.detail ? ` · ${image.detail}` : ""}</span>}</For>
        </div>
      </div>
      <Show when={reports}>{(store) => <CrashReportHistory store={store()} text={props.text} />}</Show>
    </section>
  );
}

function CrashReportHistory(props: { store: NonNullable<ReturnType<typeof useCrashReports>>; text: SettingsTextBundle }) {
  const copy = () => props.text.crashDumps;
  const list = () => props.store.list();
  const limit = () => formatBytes(list()?.issueAttachmentLimitBytes ?? 0, "storage");
  return (
    <div class="crash-report-history">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{copy().historyTitle}</strong>
          <span>{copy().historyDescription}</span>
        </div>
      </div>
      <Show when={(list()?.reports.length ?? 0) > 0} fallback={<p class="crash-report-empty">{copy().historyEmpty}</p>}>
        <ul class="crash-report-list">
          <For each={list()?.reports ?? []}>{(report) => {
            const dump = () => crashReportDumpText(report, copy());
            const file = () => crashReportLocalFile(report);
            const busy = () => props.store.busy(report.id);
            return (
              <li class="crash-report-item" aria-busy={busy()}>
                <strong>{copy().program(report.program, copy().roles[report.role])}</strong>
                <dl class="crash-report-facts">
                  <dt>{copy().timeLabel}</dt><dd>{userFacingDateTime(report.occurredAt)}</dd>
                  <dt>{copy().versionLabel}</dt><dd>{report.version ?? copy().versionUnknown}</dd>
                  <dt>{copy().faultLabel}</dt><dd>{crashFaultSummary(report.fault, copy())}</dd>
                  <dt>{copy().dumpLabel}</dt>
                  <dd>
                    <Show when={dump().path}>{(path) => report.dumpStatus === "saved"
                      ? <CrashReportPathButton path={path()} onReveal={props.store.reveal} />
                      : <code class="crash-report-code">{path()}</code>}</Show>
                    <Show when={dump().note}>{(note) => <span>{note()}</span>}</Show>
                  </dd>
                  <Show when={report.dump}>{(item) => <><dt>{copy().sizeLabel}</dt><dd>{formatBytes(item().sizeBytes, "storage")}</dd></>}</Show>
                  <dt>{copy().reportedLabel}</dt><dd>{report.reported ? copy().reported : copy().notReported}</dd>
                  <Show when={report.package}>{(item) => <>
                    <dt>{copy().packageLabel}</dt>
                    <dd>
                      <CrashReportPathButton path={item().path} onReveal={props.store.reveal} />
                      <span> · {formatBytes(item().sizeBytes, "storage")}</span>
                      <Show when={item().exceedsIssueLimit}>
                        <span class="crash-report-warning" role="note">{copy().packageExceeds(formatBytes(item().sizeBytes, "storage"), limit())}</span>
                      </Show>
                    </dd>
                  </>}</Show>
                </dl>
                <div class="crash-report-actions">
                  <button type="button" class="secondary" disabled={busy() || file() === null}
                    onClick={() => { const path = file(); if (path) void props.store.reveal(path); }}>{copy().openFolder}</button>
                  <button type="button" class="secondary" disabled={busy()} onClick={() => void props.store.buildPackage(report)}>{copy().buildPackage}</button>
                  <button type="button" class="secondary" disabled={busy()} onClick={() => void props.store.reportOnGitHub(report)}>{copy().reportOnGitHub}</button>
                  <button type="button" class="secondary" disabled={busy()} onClick={() => void props.store.remove(report)}>{copy().delete}</button>
                </div>
              </li>
            );
          }}</For>
        </ul>
      </Show>
    </div>
  );
}
