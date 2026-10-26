import { For, Match, Show, Switch } from "solid-js";
import type { PhysicalMemoryResidualFrame } from "../../types.ts";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { byteQuantityKindForMetric } from "../../presentation/metricLabels.ts";
import { formatObservationTimestamp } from "../../presentation/observationTimestamp.ts";
import { physicalMemoryResidualView, type PhysicalMemoryResidualView } from "../../presentation/physicalMemoryResidual.ts";
import { uiText } from "../../text.ts";
import { formatPercent } from "../../utils.ts";

type ReadyView = Extract<PhysicalMemoryResidualView, { state: "ready" }>;
type UnavailableView = Extract<PhysicalMemoryResidualView, { state: "unavailable" }>;

const bytes = (value: number) => formatBytes(value, byteQuantityKindForMetric("memory.usage"));

/*
 * 物理内存“系统/驱动保留”展开后的页帧明细。显示内容由 physicalMemoryResidualView 从帧得出（三种状态、行、
 * 名称都在那里），这里只负责排版：标题和采集时间、分项表（类别、大小、占保留量比例，末行是保留量合计）、
 * 已用物理内存与已归属的进程私有内存，以及“各类别加未分类等于保留量”的说明。
 */
export function PhysicalMemoryResidualDetails(props: { frame: PhysicalMemoryResidualFrame | undefined }) {
  const copy = () => uiText.physicalMemoryResidual.panel;
  const view = () => physicalMemoryResidualView(props.frame);
  const capturedAt = () => { const current = view(); return current.state === "not-collected" ? null : current.capturedAt; };
  return (
    <section class="resource-residual-details" aria-label={copy().title} data-residual-state={view().state}>
      <div class="resource-residual-details-header">
        <strong>{copy().title}</strong>
        <Show when={capturedAt()}>
          {(time) => <span>{copy().capturedAt(formatObservationTimestamp(time()))}</span>}
        </Show>
      </div>
      <Switch fallback={<p class="resource-residual-details-state">{copy().notCollected}</p>}>
        <Match when={view().state === "unavailable" ? view() as UnavailableView : undefined}>
          {(current) => (
            <div class="resource-residual-details-state">
              <strong>{copy().unavailable}</strong>
              <p>{current().reason}</p>
              <p class="resource-residual-details-code">{copy().reasonCode(current().reasonCode)}</p>
            </div>
          )}
        </Match>
        <Match when={view().state === "ready" ? view() as ReadyView : undefined}>
          {(current) => <ReadyDetails view={current()} />}
        </Match>
      </Switch>
    </section>
  );
}

function ReadyDetails(props: { view: ReadyView }) {
  const copy = () => uiText.physicalMemoryResidual.panel;
  return (
    <>
      <table class="resource-residual-details-table">
        <thead>
          <tr>
            <th scope="col">{copy().categoryColumn}</th>
            <th scope="col">{copy().bytesColumn}</th>
            <th scope="col">{copy().shareColumn}</th>
          </tr>
        </thead>
        <tbody>
          <For each={props.view.rows}>
            {(row) => row.type === "group"
              ? <tr class="resource-residual-details-group" data-group={row.group}><th scope="rowgroup" colSpan={3}>{row.label}</th></tr>
              : <tr data-kind={row.kind} classList={{ grouped: row.grouped }}>
                <th scope="row">{row.label}</th>
                <td>{bytes(row.bytes)}</td>
                <td>{row.residualPercent === null ? "-" : formatPercent(row.residualPercent)}</td>
              </tr>}
          </For>
        </tbody>
        <tfoot>
          <tr data-kind="residual">
            <th scope="row">{copy().residualRow}</th>
            <td>{bytes(props.view.residualBytes)}</td>
            <td />
          </tr>
        </tfoot>
      </table>
      <dl class="resource-residual-details-totals">
        <dt>{copy().usedLabel}</dt>
        <dd data-total="used">{bytes(props.view.usedBytes)}</dd>
        <dt>{copy().attributedPrivateLabel}</dt>
        <dd data-total="attributed-private">{bytes(props.view.attributedProcessPrivateBytes)}</dd>
      </dl>
      <p class="resource-residual-details-note">{copy().identity}</p>
      <Show when={props.view.reason}>
        {(reason) => (
          <p class="resource-residual-details-note" data-reason-code={reason().reasonCode}>
            {reason().text} {copy().reasonCode(reason().reasonCode)}
          </p>
        )}
      </Show>
    </>
  );
}
