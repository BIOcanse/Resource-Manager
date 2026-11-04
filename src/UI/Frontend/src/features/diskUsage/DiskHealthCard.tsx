import { createMemo, For, Show } from "solid-js";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { uiText } from "../../text.ts";
import { diskHealthFieldNames, diskHealthRows } from "./diskHealth.ts";

/** Subscribe to the backend field catalog and health values only while the page is visible; cache only published facts and preserve loading row size. */
export function DiskHealthCard(props: { active: boolean }) {
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const catalog = usePushSource(runtime.sources.metricFieldCatalog, () => ({
    active: props.active, intervalMs: readIntervals.current()?.monitor
  })).snapshot;
  const values = usePushSource(runtime.sources.metricFields, () => ({
    active: props.active,
    intervalMs: readIntervals.current()?.monitor,
    query: { fields: diskHealthFieldNames }
  })).snapshot;
  const text = uiText.diskUsage.health;
  const rows = createMemo(() => diskHealthRows(catalog().data?.fields ?? null, values().data));
  const catalogFailed = () => rows() === null && (catalog().status === "error" || catalog().status === "unavailable");

  return (
    <section class="panel disk-health" aria-labelledby="disk-health-title">
      <div class="panel-header">
        <div class="optimization-heading">
          <h2 id="disk-health-title">{text.title}</h2>
          <span>{text.intro}</span>
        </div>
      </div>
      <Show when={!catalogFailed()} fallback={<p class="disk-health-failed" role="status">{uiText.observationNotice.unavailable(text.title)}</p>}>
        <Show
          when={rows()}
          fallback={
            <>
              {/* Reserve one NVMe-sized row until the device count is known. */}
              <p class="sr-only" role="status">{uiText.observationNotice.loading(text.title)}</p>
              <ul class="disk-health-list" aria-hidden="true">
                <li class="disk-health-row pending">
                  <span class="disk-health-name">--</span>
                  <dl class="disk-health-items">
                    <For each={[0, 1, 2, 3]}>
                      {() => <div class="disk-health-item"><dt>--</dt><dd>--</dd></div>}
                    </For>
                  </dl>
                </li>
              </ul>
            </>
          }
        >
          {(list) => (
            <Show when={list().length > 0} fallback={<p class="disk-health-empty">{text.noDrives}</p>}>
              <ul class="disk-health-list">
                <For each={list()}>
                  {(row) => (
                    <li class="disk-health-row" data-device-identity={row.deviceIdentity} data-health-kind={row.kind}>
                      <span class="disk-health-name" data-full-content={row.name}>{row.name}</span>
                      <Show when={row.items.length > 0} fallback={<p class="disk-health-not-reported">{text.notReported}</p>}>
                        <dl class="disk-health-items">
                          <For each={row.items}>
                            {(item) => (
                              <div class="disk-health-item" data-field={item.field}
                                data-full-content={item.hint ? `${item.label}\n${item.hint}` : item.label}>
                                <dt>{item.label}</dt>
                                <dd aria-description={item.hint ?? undefined}>{item.value ?? "--"}</dd>
                              </div>
                            )}
                          </For>
                        </dl>
                      </Show>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          )}
        </Show>
      </Show>
    </section>
  );
}
