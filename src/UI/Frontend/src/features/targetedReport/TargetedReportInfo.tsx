import { For, Show } from "solid-js";
import { currentLanguage, uiText } from "../../text.ts";
import { itemName, sourceName } from "../itemSelection/itemNames.ts";
import type { RecordingGap, RecordingHeader } from "./targetedRecordingApi.ts";

/*
 * 报告详情里的录制信息（docs/targeted_recording_views_2026-10-30.md §3.4）：用的配置、每个录制项选的与实际的来源和间隔、
 * 同步精度、点数、空缺及原因；录到的进程；报告占用的存储和上限。
 * 只有带录制项信息的报告（新版后端写的）才有这些字段；旧报告没有，整块不显示，详情的其余部分照旧。
 */
export function TargetedReportInfo(props: { recording: RecordingHeader }) {
  const copy = () => uiText.targetedReport.reportInfo;
  const items = () => props.recording.items ?? [];
  const processes = () => props.recording.processes ?? [];
  return (
    <div class="targeted-report-info">
      <dl class="targeted-report-info-facts">
        <div>
          <dt>{copy().profile}</dt>
          <dd>{props.recording.profileName ?? copy().profileNone}</dd>
        </div>
        <Show when={props.recording.storedBytes !== undefined}>
          <div>
            <dt>{copy().storage}</dt>
            <dd>{storageText(props.recording.storedBytes ?? 0, props.recording.maximumReportKib ?? 0)}</dd>
          </div>
        </Show>
      </dl>
      <section class="targeted-report-info-section" aria-label={copy().items}>
        <h4>{copy().items}</h4>
        <Show when={items().length > 0} fallback={<p class="targeted-report-note">{copy().none}</p>}>
          <div class="targeted-report-table-scroll">
            <table class="targeted-report-table">
              <thead>
                <tr>
                  <th scope="col">{copy().columns.name}</th>
                  <th scope="col">{copy().columns.unit}</th>
                  <th scope="col">{copy().columns.selectedSource}</th>
                  <th scope="col">{copy().columns.actualSources}</th>
                  <th scope="col">{copy().columns.selectedInterval}</th>
                  <th scope="col">{copy().columns.actualInterval}</th>
                  <th scope="col">{copy().columns.syncPrecision}</th>
                  <th scope="col">{copy().columns.points}</th>
                  <th scope="col">{copy().columns.gaps}</th>
                </tr>
              </thead>
              <tbody>
                <For each={items()}>
                  {(item) => (
                    <tr>
                      <th scope="row">
                        <span class="targeted-report-table-name">{itemName(item.kind, item.id)}</span>
                        <span class="targeted-report-table-id">{uiText.targetedReport.itemPicker.groups[item.kind]} · {item.id}</span>
                      </th>
                      <td>{item.unit || copy().none}</td>
                      <td>{sourceName(item.selectedSource)}</td>
                      <td>{item.actualSources.length > 0 ? item.actualSources.map(sourceName).join(", ") : copy().none}</td>
                      <td>{intervalText(item.selectedIntervalMs)}</td>
                      <td>{item.intervalOrigin === "per-present" ? originText(item.intervalOrigin)
                        : `${intervalText(item.actualIntervalMs)} · ${originText(item.intervalOrigin)}`}</td>
                      <td>{uiText.targetedReport.itemPicker.precisions[item.syncPrecision]}</td>
                      <td>{item.pointCount}</td>
                      <td>
                        <Show when={item.gaps.length > 0} fallback={copy().none}>
                          <ul class="targeted-report-gaps">
                            <For each={item.gaps}>{(gap) => <li>{gapText(gap)}</li>}</For>
                          </ul>
                        </Show>
                      </td>
                    </tr>
                  )}
                </For>
              </tbody>
            </table>
          </div>
        </Show>
      </section>
      <section class="targeted-report-info-section" aria-label={copy().processes}>
        <h4>{copy().processes}</h4>
        <Show when={processes().length > 0} fallback={<p class="targeted-report-note">{copy().noProcesses}</p>}>
          <div class="targeted-report-table-scroll">
            <table class="targeted-report-table">
              <thead>
                <tr>
                  <th scope="col">{copy().processColumns.name}</th>
                  <th scope="col">{copy().processColumns.pid}</th>
                  <th scope="col">{copy().processColumns.firstSeen}</th>
                  <th scope="col">{copy().processColumns.exited}</th>
                </tr>
              </thead>
              <tbody>
                <For each={processes()}>
                  {(process) => (
                    <tr>
                      <th scope="row">{process.name}</th>
                      <td>{process.processId}</td>
                      <td>{timeText(process.firstSeenAt)}</td>
                      <td>{process.exitedAt === null ? copy().processRunning : timeText(process.exitedAt)}</td>
                    </tr>
                  )}
                </For>
              </tbody>
            </table>
          </div>
        </Show>
      </section>
    </div>
  );
}

function intervalText(ms: number | null) {
  const copy = uiText.targetedReport.reportInfo;
  return ms === null ? copy.intervalEmpty : copy.intervalValue(ms);
}

// 间隔来源。认不出的原样显示代码。
function originText(origin: string) {
  const values = uiText.targetedReport.reportInfo.intervalOrigins;
  switch (origin) {
    case "item": return values.item;
    case "natural": return values.natural;
    case "profile-default": return values.profileDefault;
    case "per-present": return values.perPresent;
    default: return origin;
  }
}

// 空缺：相对录制开始的秒数范围和原因。认不出的原因原样显示代码。
export function gapText(gap: RecordingGap) {
  const copy = uiText.targetedReport.reportInfo;
  const seconds = (ms: number) => (ms / 1000).toFixed(1);
  const reasons = copy.gapReasons;
  const reason = gap.reason === "item-unavailable" ? reasons.itemUnavailable
    : gap.reason === "value-unavailable" ? reasons.valueUnavailable
    : gap.reason === "size-limit" ? reasons.sizeLimit
    : gap.reason === "frames-incomplete" ? reasons.framesIncomplete
    : gap.reason === "frames-window-skipped" ? reasons.framesWindowSkipped
    : gap.reason;
  return `${copy.gapRange(seconds(gap.fromMs), seconds(gap.toMs))} · ${reason}`;
}

// 已用存储按 KiB 显示一位小数；上限为 0 表示录制时没有大小上限（旧报告）。
function storageText(storedBytes: number, maximumKib: number) {
  const copy = uiText.targetedReport.reportInfo;
  const used = (storedBytes / 1024).toFixed(1);
  return maximumKib > 0 ? copy.storageUsed(used, String(maximumKib)) : copy.storageNoCap(used);
}

function timeText(value: string) {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? new Intl.DateTimeFormat(currentLanguage(), {
    hour: "2-digit", minute: "2-digit", second: "2-digit"
  }).format(timestamp) : value;
}
