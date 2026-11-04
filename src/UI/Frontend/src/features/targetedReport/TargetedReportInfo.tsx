import { For, Show } from "solid-js";
import { currentLanguage, uiText } from "../../text.ts";
import { itemName, itemReasonText, sourceName } from "../itemSelection/itemNames.ts";
import type { RecordingGap, RecordingHeader } from "./targetedRecordingApi.ts";

/* Show recording metadata only when the report provides it; preserve reports without item metadata. */
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

// Show unknown interval-source codes unchanged.
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

// Express gap ranges relative to recording start.
// Share source and reader reason labels with overlays; preserve unknown codes.
export function gapText(gap: RecordingGap) {
  const copy = uiText.targetedReport.reportInfo;
  const seconds = (ms: number) => (ms / 1000).toFixed(1);
  const reasons = copy.gapReasons;
  const reason = gap.reason === "item-unavailable" ? reasons.itemUnavailable
    : gap.reason === "value-unavailable" ? reasons.valueUnavailable
    : gap.reason === "size-limit" ? reasons.sizeLimit
    : gap.reason === "frames-incomplete" ? reasons.framesIncomplete
    : gap.reason === "frames-window-skipped" ? reasons.framesWindowSkipped
    : itemReasonText(gap.reason);
  return `${copy.gapRange(seconds(gap.fromMs), seconds(gap.toMs))} · ${reason}`;
}

// Show storage in KiB; a zero cap denotes a report recorded without a size limit.
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
