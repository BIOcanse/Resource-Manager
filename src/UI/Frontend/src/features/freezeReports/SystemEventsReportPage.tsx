import { createMemo, createSignal, createUniqueId, For, Index, onMount, Show } from "solid-js";
import type { SystemEventEntry } from "../../data/freezeReports/systemEvents.ts";
import { systemEventEntriesInDisplayOrder } from "../../data/freezeReports/systemEvents.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { useContentTransition } from "../../interactions/solid/contentTransition.ts";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { uiText } from "../../text.ts";
import { FreezeSettingsPage } from "./FreezeSettingsPage.tsx";
import { FreezeRawEvent } from "./FreezeReportParts.tsx";
import {
  systemEventEntryDetails,
  systemEventEntryFacts,
  systemEventsNewestFirst,
  systemEventsStateView,
  systemEventsSummary,
  systemEventTargetText
} from "./freezeReportsPresentation.ts";

/* Subscribe to backend reports while visible. Clearing removes history and retains active entries; details and settings remain within this page. */
export function SystemEventsReportPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.system-events";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsSystemEvents]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const source = usePushSource(frontendRuntime.sources.systemEvents, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsSystemEvents)
  })).snapshot;
  const report = createMemo(() => {
    const snapshot = source();
    return sourceCanRender(snapshot) ? snapshot.data : null;
  });
  const stateView = createMemo(() => systemEventsStateView(source()));
  const items = createMemo(() => systemEventEntriesInDisplayOrder(report()?.items ?? []));
  const copy = () => uiText.freezeReports.systemEvents;
  const historyCount = () => report()?.items.filter((item) => item.state === "history").length ?? 0;
  const history = useReportHistoryClear({ ...props, kind: "systemEvents", historyCount });
  // Return to the list if the selected entry disappears.
  const [view, setView] = createSignal<{ readonly kind: "list" } | { readonly kind: "item"; readonly id: string } | { readonly kind: "settings" }>({ kind: "list" });
  const detailItem = createMemo(() => {
    const current = view();
    return current.kind === "item" ? items().find((item) => item.id === current.id) ?? null : null;
  });
  const page = createMemo(() => view().kind === "settings" ? "settings" : detailItem() ? "item" : "list");
  let stage: HTMLDivElement | undefined;
  useContentTransition({
    name: "系统事件子页",
    key: () => page() === "item" ? `item:${detailItem()!.id}` : page(),
    order: () => page() === "list" ? 0 : 1,
    stage: () => stage,
    snapshot: () => "move"
  });
  const backToList = (focusSelector: string) => {
    setView({ kind: "list" });
    queueMicrotask(() => stage?.querySelector<HTMLElement>(focusSelector)?.focus());
  };

  return (
    <section
      {...frontendVisibilitySurface("visible.details.system-events.surface", [demandId])}
      class="freeze-report"
      aria-label={pageName()}
    >
      <div ref={stage} class="freeze-report-stage">
        <Show when={page() === "list"} fallback={
          <Show when={page() === "item" && detailItem()} fallback={
            <FreezeSettingsPage part="systemEvents" onBack={() => backToList("[data-freeze-settings-button]")} />
          }>
            {(item) => <SystemEventItemPage item={item()} onBack={() => backToList(`[data-system-event-item="${item().id}"] .details-button`)} />}
          </Show>
        }>
          <div class="panel-header freeze-report-header">
            <CurrentPageHeading />
            <div class="freeze-report-header-actions">
              <button class="secondary panel-refresh-button" type="button" data-freeze-settings-button
                onClick={() => setView({ kind: "settings" })}>{uiText.freezeReports.settings.open}</button>
              <button class="secondary panel-refresh-button" type="button"
                disabled={historyCount() === 0 || history.clearing()} onClick={() => void history.clear()}>
                <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
                  current={history.clearing() ? "busy" : "idle"} />
              </button>
            </div>
          </div>
          <p class="freeze-report-note">{copy().intro}</p>
          {/* Reserve summary-cell geometry before data arrives. */}
          <dl class="freeze-report-summary" aria-busy={report() ? undefined : "true"}>
            <For each={systemEventsSummary(report())}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
          </dl>
          <Show when={report()?.failure}>
            {(failure) => <p class="freeze-report-note warning" role="status">{copy().failure(failure())}</p>}
          </Show>
          <Show when={report()?.liveKernelFailure}>
            {(failure) => <p class="freeze-report-note warning" role="status">{copy().liveKernelFailure(failure())}</p>}
          </Show>
          <Show when={(report()?.unrecordedItemCount ?? 0) > 0}>
            <p class="freeze-report-note warning" role="status">{copy().unrecorded(report()!.unrecordedItemCount)}</p>
          </Show>
          <Show when={stateView()}>
            {(state) => <ContentState kind={state().kind} title={state().title} detail={state().detail} />}
          </Show>
          <Show when={items().length > 0}>
            <ul class="freeze-report-items" role="list" aria-label={copy().items}>
              <For each={items()}>{(item) => <SystemEventItemCard item={item} onDetails={() => setView({ kind: "item", id: item.id })} />}</For>
            </ul>
          </Show>
        </Show>
      </div>
    </section>
  );
}

function SystemEventItemCard(props: { readonly item: SystemEventEntry; readonly onDetails: () => void }) {
  const copy = () => uiText.freezeReports.systemEvents;
  return (
    <li class="freeze-report-item" classList={{ history: props.item.state === "history" }} data-system-event-item={props.item.id}>
      <header class="freeze-report-item-header">
        <div class="freeze-report-item-title">
          <strong>{systemEventTargetText(props.item)}</strong>
        </div>
        <span class="freeze-report-item-tags">
          <Show when={props.item.state === "history"}>
            <span class="freeze-report-tag">{copy().historyEntry}</span>
          </Show>
          <span class="freeze-report-tag">{copy().category[props.item.category]}</span>
          <button class="secondary details-button" type="button" onClick={() => props.onDetails()}>{copy().detailsButton}</button>
        </span>
      </header>
      <dl class="freeze-report-facts">
        <For each={systemEventEntryFacts(props.item)}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
      </dl>
    </li>
  );
}

/** Keep details current with backend pushes and focus the heading on entry. */
function SystemEventItemPage(props: { readonly item: SystemEventEntry; readonly onBack: () => void }) {
  const copy = () => uiText.freezeReports.systemEvents;
  const details = createMemo(() => systemEventEntryDetails(props.item));
  const events = createMemo(() => systemEventsNewestFirst(props.item.events));
  const titleId = `system-event-item-title-${createUniqueId()}`;
  let title: HTMLHeadingElement | undefined;
  onMount(() => title?.focus());
  return (
    <article class="freeze-report-subpage" aria-labelledby={titleId}>
      <div class="panel-header freeze-report-subpage-header">
        <CurrentPageHeading />
        <button class="secondary freeze-report-back" type="button" onClick={() => props.onBack()}>{copy().back}</button>
        <h3 ref={title} id={titleId} class="freeze-report-subpage-title" tabindex="-1">{details().title}</h3>
      </div>
      <Index each={details().sections}>
        {(section, index) => (
          <section class="user-details-section" aria-labelledby={`${titleId}-${index}`}>
            <h4 id={`${titleId}-${index}`}>{section().title}</h4>
            <dl>
              <Index each={section().items}>
                {(row) => <div class="user-details-row"><dt>{row().label}</dt><dd>{row().value}</dd></div>}
              </Index>
            </dl>
          </section>
        )}
      </Index>
      <section class="user-details-section" aria-labelledby={`${titleId}-events`}>
        <h4 id={`${titleId}-events`}>{copy().eventsTitle(props.item.events.length)}</h4>
        <ul class="freeze-report-events" role="list">
          <For each={events()}>{(event) => <FreezeRawEvent raw={event} />}</For>
        </ul>
      </section>
    </article>
  );
}
