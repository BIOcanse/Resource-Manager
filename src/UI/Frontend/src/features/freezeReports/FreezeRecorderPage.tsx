import { createMemo, createResource, createSignal, createUniqueId, For, Index, onMount, Show } from "solid-js";
import {
  exportFreezeReview,
  freezeReviewsInDisplayOrder,
  readFreezeReviewDetails,
  type FreezeReviewSummary
} from "../../data/freezeReports/freezeRecorder.ts";
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
import { FreezeCurveChart, FreezeRawEvent } from "./FreezeReportParts.tsx";
import {
  freezeCurves,
  freezeLastRecordSections,
  freezeRecorderStateView,
  freezeRecorderSummary,
  freezeReviewEventFacts,
  freezeReviewEventTitle,
  freezeReviewFacts,
  freezeReviewOverview,
  freezeReviewTitle
} from "./freezeReportsPresentation.ts";

/* Subscribe while visible. Fetch review details on entry and whenever detailsRevision changes; all reviews are persisted history. */
export function FreezeRecorderPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.freeze-recorder";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsFreezeRecorder]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const source = usePushSource(frontendRuntime.sources.freezeRecorder, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsFreezeRecorder)
  })).snapshot;
  const status = createMemo(() => {
    const snapshot = source();
    return sourceCanRender(snapshot) ? snapshot.data : null;
  });
  const stateView = createMemo(() => freezeRecorderStateView(source()));
  const reviews = createMemo(() => freezeReviewsInDisplayOrder(status()?.reviews ?? []));
  const copy = () => uiText.freezeReports.recorder;
  const historyCount = () => status()?.reviews.length ?? 0;
  const history = useReportHistoryClear({ ...props, kind: "freezeReviews", historyCount });
  const [view, setView] = createSignal<{ readonly kind: "list" } | { readonly kind: "review"; readonly id: string } | { readonly kind: "settings" }>({ kind: "list" });
  const detailReview = createMemo(() => {
    const current = view();
    return current.kind === "review" ? reviews().find((review) => review.id === current.id) ?? null : null;
  });
  const page = createMemo(() => view().kind === "settings" ? "settings" : detailReview() ? "review" : "list");
  let stage: HTMLDivElement | undefined;
  useContentTransition({
    name: "假死记录子页",
    key: () => page() === "review" ? `review:${detailReview()!.id}` : page(),
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
      {...frontendVisibilitySurface("visible.details.freeze-recorder.surface", [demandId])}
      class="freeze-report"
      aria-label={pageName()}
    >
      <div ref={stage} class="freeze-report-stage">
        <Show when={page() === "list"} fallback={
          <Show when={page() === "review" && detailReview()} fallback={
            <FreezeSettingsPage part="freezeRecorder" onBack={() => backToList("[data-freeze-settings-button]")} />
          }>
            {(review) => <FreezeReviewPage review={review()} onActionError={props.onActionError}
              onBack={() => backToList(`[data-freeze-review="${review().id}"] .details-button`)} />}
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
          <dl class="freeze-report-summary" aria-busy={status() ? undefined : "true"}>
            <For each={freezeRecorderSummary(status())}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
          </dl>
          <Show when={status()?.failure}>
            {(failure) => <p class="freeze-report-note warning" role="status">{copy().failure(failure())}</p>}
          </Show>
          <Show when={status() && !status()!.running && !status()!.failure}>
            <p class="freeze-report-note">{copy().stoppedNote}</p>
          </Show>
          <Show when={(status()?.gaps.length ?? 0) > 0}>
            <div class="freeze-report-gaps" role="group" aria-label={copy().gapsTitle}>
              <p class="freeze-report-gaps-title">{copy().gapsTitle}</p>
              <ul role="list">
                <For each={status()!.gaps}>{(gap) => <li data-freeze-gap={gap}>{copy().gap[gap]}</li>}</For>
              </ul>
            </div>
          </Show>
          <Show when={stateView()}>
            {(state) => <ContentState kind={state().kind} title={state().title} detail={state().detail} />}
          </Show>
          <Show when={reviews().length > 0}>
            <ul class="freeze-report-items" role="list" aria-label={copy().reviews}>
              <For each={reviews()}>{(review) => <FreezeReviewCard review={review} onDetails={() => setView({ kind: "review", id: review.id })} />}</For>
            </ul>
          </Show>
        </Show>
      </div>
    </section>
  );
}

function FreezeReviewCard(props: { readonly review: FreezeReviewSummary; readonly onDetails: () => void }) {
  const copy = () => uiText.freezeReports.recorder;
  return (
    <li class="freeze-report-item history" data-freeze-review={props.review.id}>
      <header class="freeze-report-item-header">
        <div class="freeze-report-item-title">
          <strong>{freezeReviewTitle(props.review)}</strong>
        </div>
        <span class="freeze-report-item-tags">
          <button class="secondary details-button" type="button" onClick={() => props.onDetails()}>{copy().details.button}</button>
        </span>
      </header>
      <Show when={props.review.tags.length > 0} fallback={<p class="freeze-report-note">{copy().noTags}</p>}>
        <ul class="freeze-report-tag-list" role="list" aria-label={copy().tagsTitle}>
          <For each={props.review.tags}>{(tag) => <li class="freeze-report-tag" data-freeze-tag={tag}>{copy().tag[tag].name}</li>}</For>
        </ul>
      </Show>
      <dl class="freeze-report-facts">
        <For each={freezeReviewFacts(props.review)}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
      </dl>
      <Show when={!props.review.inputsComplete}>
        <p class="freeze-report-note warning">{copy().inputsIncomplete}</p>
      </Show>
    </li>
  );
}

/** Keep the final section layout while details load; export the complete review JSON. */
function FreezeReviewPage(props: {
  readonly review: FreezeReviewSummary;
  readonly onBack: () => void;
  readonly onActionError: ReportHistoryClearFeedback["onActionError"];
}) {
  const runtime = useFrontendRuntime();
  const copy = () => uiText.freezeReports.recorder;
  const [details] = createResource(
    () => ({ id: props.review.id, revision: props.review.detailsRevision }),
    ({ id }) => readFreezeReviewDetails(runtime.requestClient, id));
  const loaded = () => details.error ? null : details.latest ?? null;
  const overview = createMemo(() => freezeReviewOverview(loaded()));
  const curves = createMemo(() => freezeCurves(loaded()?.records ?? []));
  const lastRecord = createMemo(() => freezeLastRecordSections(loaded()?.records.at(-1)));
  const [exporting, setExporting] = createSignal(false);
  const exportReview = async () => {
    if (exporting()) return;
    setExporting(true);
    try {
      await exportFreezeReview(runtime.requestClient, props.review.id);
    } catch (error) {
      props.onActionError(error, copy().details.exportFailed);
    } finally {
      setExporting(false);
    }
  };
  const titleId = `freeze-review-title-${createUniqueId()}`;
  let title: HTMLHeadingElement | undefined;
  onMount(() => title?.focus());
  return (
    <article class="freeze-report-subpage" aria-labelledby={titleId} aria-busy={details.loading ? "true" : undefined}>
      <div class="panel-header freeze-report-subpage-header">
        <CurrentPageHeading />
        <button class="secondary freeze-report-back" type="button" onClick={() => props.onBack()}>{copy().details.back}</button>
        <h3 ref={title} id={titleId} class="freeze-report-subpage-title" tabindex="-1">{freezeReviewTitle(props.review)}</h3>
        <div class="freeze-report-header-actions">
          <button class="secondary panel-refresh-button" type="button" disabled={exporting()} onClick={() => void exportReview()}>
            <StableLabel labels={{ idle: copy().details.export, busy: copy().details.exporting }} current={exporting() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <Show when={details.error}>
        <ContentState kind="unavailable" title={copy().details.readFailed} />
      </Show>
      <Show when={!details.error}>
        <DetailSections sections={overview()} idPrefix={`${titleId}-overview`} />
        <section class="user-details-section" aria-labelledby={`${titleId}-tags`}>
          <h4 id={`${titleId}-tags`}>{copy().tagsTitle}</h4>
          <Show when={props.review.tags.length > 0} fallback={<p class="freeze-report-note">{copy().noTags}</p>}>
            <dl class="freeze-report-tag-details">
              <For each={props.review.tags}>{(tag) => <div data-freeze-tag={tag}><dt>{copy().tag[tag].name}</dt><dd>{copy().tag[tag].description}</dd></div>}</For>
            </dl>
          </Show>
        </section>
        <section class="user-details-section" aria-labelledby={`${titleId}-curves`}>
          <h4 id={`${titleId}-curves`}>{copy().details.curvesTitle}</h4>
          <div class="freeze-curves">
            <For each={curves()}>{(curve) => <FreezeCurveChart curve={curve} />}</For>
          </div>
        </section>
        <Show when={loaded()} fallback={<p class="freeze-report-note">{copy().details.loading}</p>}>
          {(current) => <>
            <Show when={current().records.length === 0}>
              <p class="freeze-report-note">{copy().details.noRecords}</p>
            </Show>
            <DetailSections sections={lastRecord()} idPrefix={`${titleId}-last`} />
            <section class="user-details-section" aria-labelledby={`${titleId}-events`}>
              <h4 id={`${titleId}-events`}>{copy().details.eventsTitle(current().events.length)}</h4>
              <Show when={current().events.length > 0} fallback={<p class="freeze-report-note">{copy().details.noEvents}</p>}>
                <ul class="freeze-report-events" role="list">
                  <For each={current().events}>
                    {(event) => <FreezeRawEvent raw={event.raw} title={freezeReviewEventTitle(event)} facts={freezeReviewEventFacts(event)} />}
                  </For>
                </ul>
              </Show>
            </section>
          </>}
        </Show>
      </Show>
    </article>
  );
}

function DetailSections(props: { readonly sections: readonly { readonly title?: string; readonly items: readonly { label: string; value: string }[] }[]; readonly idPrefix: string }) {
  return (
    <Index each={props.sections}>
      {(section, index) => (
        <section class="user-details-section" aria-labelledby={`${props.idPrefix}-${index}`}>
          <h4 id={`${props.idPrefix}-${index}`}>{section().title}</h4>
          <dl>
            <Index each={section().items}>
              {(row) => <div class="user-details-row"><dt>{row().label}</dt><dd>{row().value}</dd></div>}
            </Index>
          </dl>
        </section>
      )}
    </Index>
  );
}
