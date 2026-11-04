import { createMemo, createSignal, createUniqueId, For, Index, onMount, Show } from "solid-js";
import { kernelLeakItemKey, type KernelLeakItem } from "../../data/kernelLeaks/kernelLeakReport.ts";
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
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { uiText } from "../../text.ts";
import {
  kernelLeakActionReasons,
  kernelLeakActionViews,
  kernelLeakCapturedAt,
  kernelLeakEvidence,
  kernelLeakHolderTitle,
  kernelLeakImagePath,
  kernelLeakItemCounts,
  kernelLeakItemDetails,
  kernelLeakItemsInDisplayOrder,
  kernelLeakObservationProgress,
  kernelLeakPreviousRunFailure,
  kernelLeakProductName,
  kernelLeakRestartNote,
  kernelLeakServices,
  kernelLeakStateView
} from "./kernelLeakPresentation.ts";

/* Visibility owns the report subscription, not backend observation. Actions show backend availability and risks; clearing removes history and details follow entry identity. */
export function KernelLeakReportPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.kernel-leaks";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsKernelLeaks]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const source = usePushSource(frontendRuntime.sources.kernelLeakReport, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsKernelLeaks)
  })).snapshot;
  const report = createMemo(() => {
    const snapshot = source();
    return sourceCanRender(snapshot) ? snapshot.data : null;
  });
  // Show entries independently of observation status; suspected entries are the result.
  const stateView = createMemo(() => kernelLeakStateView(source()));
  const items = createMemo(() => kernelLeakItemsInDisplayOrder(report()?.items ?? []));
  const copy = () => uiText.kernelLeakReport;
  const historyCount = () => report()?.items.filter((item) => item.state === "history").length ?? 0;
  const history = useReportHistoryClear({ ...props, kind: "kernelLeaks", historyCount });
  // Return to the list if the selected entry disappears.
  const [detailKey, setDetailKey] = createSignal<string | null>(null);
  const detailItem = createMemo(() => items().find((item) => kernelLeakItemKey(item) === detailKey()) ?? null);
  let stage: HTMLDivElement | undefined;
  useContentTransition({
    name: "内核泄漏详细信息",
    key: () => detailItem() ? `item:${detailKey()}` : "list",
    order: () => detailItem() ? 1 : 0,
    stage: () => stage,
    snapshot: () => "move"
  });
  const openDetails = (item: KernelLeakItem) => setDetailKey(kernelLeakItemKey(item));
  // Restore focus to the originating details button if the entry still exists.
  const closeDetails = () => {
    const key = detailKey();
    setDetailKey(null);
    queueMicrotask(() => {
      const button = [...(stage?.querySelectorAll<HTMLElement>("[data-kernel-leak-item]") ?? [])]
        .find((element) => element.dataset.kernelLeakItem === key)?.querySelector<HTMLElement>(".details-button");
      button?.focus();
    });
  };

  return (
    <section
      {...frontendVisibilitySurface("visible.details.kernel-leaks.surface", [demandId])}
      class="kernel-leak-report"
      aria-label={pageName()}
    >
      <div ref={stage} class="kernel-leak-stage">
        <Show when={detailItem()} fallback={<>
            <div class="panel-header kernel-leak-header">
              <CurrentPageHeading />
              <div class="kernel-leak-header-actions">
                <button class="secondary panel-refresh-button" type="button"
                  disabled={historyCount() === 0 || history.clearing()} onClick={() => void history.clear()}>
                  <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
                    current={history.clearing() ? "busy" : "idle"} />
                </button>
              </div>
            </div>
            <p class="kernel-leak-note">{copy().intro}</p>
            <Show when={report()}>
              {(current) => (
                <dl class="kernel-leak-summary">
                  <Show when={kernelLeakCapturedAt(current())}>
                    {(time) => <div><dt>{copy().capturedAt}</dt><dd>{time()}</dd></div>}
                  </Show>
                  <Show when={current().items.length > 0 || current().unrecordedItemCount > 0}>
                    <For each={kernelLeakItemCounts(current())}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
                  </Show>
                </dl>
              )}
            </Show>
            <Show when={report() && kernelLeakObservationProgress(report()!)}>
              {(progress) => (
                <dl class="kernel-leak-observation" aria-label={copy().observation.title}>
                  <For each={progress()}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
                </dl>
              )}
            </Show>
            <Show when={report()?.observation.previousRunFailure}>
              {(failure) => (
                <div class="kernel-leak-previous-run" role="group" aria-label={copy().previousRun.title}>
                  <p class="kernel-leak-previous-run-title">{copy().previousRun.title}</p>
                  <dl>
                    <For each={kernelLeakPreviousRunFailure(failure())}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
                  </dl>
                </div>
              )}
            </Show>
            <Show when={report() && kernelLeakRestartNote(report()!)}>
              {(note) => <p class="kernel-leak-note">{note()}</p>}
            </Show>
            <Show when={(report()?.unavailableHolderCount ?? 0) > 0}>
              <p class="kernel-leak-note warning" role="status">{copy().unavailableHolders(report()!.unavailableHolderCount)}</p>
            </Show>
            <Show when={stateView()}>
              {(view) => <ContentState kind={view().kind} title={view().title} detail={view().detail} />}
            </Show>
            <Show when={items().length > 0}>
              <ul class="kernel-leak-items" role="list" aria-label={copy().items}>
                <For each={items()}>{(item) => <KernelLeakItemCard item={item} onDetails={() => openDetails(item)} />}</For>
              </ul>
            </Show>
        </>}>
          {(item) => <KernelLeakItemPage item={item()} onBack={closeDetails} />}
        </Show>
      </div>
    </section>
  );
}

/** Show all backend detail fields and follow pushes; focus the heading on entry. */
function KernelLeakItemPage(props: { readonly item: KernelLeakItem; readonly onBack: () => void }) {
  const copy = () => uiText.kernelLeakReport;
  const details = createMemo(() => kernelLeakItemDetails(props.item));
  const titleId = `kernel-leak-item-page-title-${createUniqueId()}`;
  let title: HTMLHeadingElement | undefined;
  onMount(() => title?.focus());
  return (
    <article class="kernel-leak-item-page" aria-labelledby={titleId}>
      <div class="panel-header kernel-leak-item-page-header">
        <CurrentPageHeading />
        <button class="secondary kernel-leak-back" type="button" onClick={() => props.onBack()}>{copy().details.back}</button>
        <h3 ref={title} id={titleId} class="kernel-leak-item-page-title" tabindex="-1">{details().title}</h3>
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
    </article>
  );
}

function KernelLeakItemCard(props: { readonly item: KernelLeakItem; readonly onDetails: () => void }) {
  const copy = () => uiText.kernelLeakReport;
  const holder = () => props.item.holder;
  const services = () => kernelLeakServices(holder().services);
  return (
    <li class="kernel-leak-item" classList={{ history: props.item.state === "history" }} data-kernel-leak-item={kernelLeakItemKey(props.item)}>
      <header class="kernel-leak-item-header">
        <div class="kernel-leak-item-title">
          <strong classList={{ "metadata-state": holder().image.status !== "Current" }}>{kernelLeakHolderTitle(holder().image)}</strong>
          <span>{copy().holder.processId} {holder().processId}</span>
        </div>
        <span class="kernel-leak-item-tags">
          <Show when={props.item.state === "history"}>
            <span class="kernel-leak-history">{copy().historyEntry}</span>
          </Show>
          <span class="kernel-leak-kind">{copy().resourceKind[props.item.resourceKind]}</span>
          <button class="secondary details-button" type="button" onClick={() => props.onDetails()}>{copy().details.button}</button>
        </span>
      </header>
      <dl class="kernel-leak-holder">
        <div>
          <dt>{copy().holder.productName}</dt>
          <dd classList={{ "metadata-state": holder().image.status !== "Current" }}>{kernelLeakProductName(holder().image)}</dd>
        </div>
        <div>
          <dt>{copy().holder.services}</dt>
          <dd>
            {(() => {
              const value = services();
              return "names" in value
                ? <span class="kernel-leak-service-list"><For each={value.names}>{(name) => <span class="kernel-leak-service">{name}</span>}</For></span>
                : <span class="metadata-state">{value.text}</span>;
            })()}
          </dd>
        </div>
        <div class="kernel-leak-path">
          <dt>{copy().holder.imagePath}</dt>
          <dd classList={{ "metadata-state": holder().image.status !== "Current" }}>{kernelLeakImagePath(holder().image)}</dd>
        </div>
      </dl>
      <dl class="kernel-leak-evidence">
        <For each={kernelLeakEvidence(props.item)}>
          {(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}
        </For>
      </dl>
      <div class="kernel-leak-actions" role="group" aria-label={copy().actions.title}>
        <For each={kernelLeakActionViews(props.item)}>
          {(action) => (
            <span class="kernel-leak-action" data-kernel-leak-action={action.action} data-risk-level={action.riskLevel}>
              <button type="button" class="kernel-leak-action-button" disabled>{action.name}</button>
              <HelpHint label={action.riskLabel}>{action.riskText}</HelpHint>
            </span>
          )}
        </For>
        <For each={kernelLeakActionReasons(props.item)}>
          {(reason) => <span class="kernel-leak-action-reason">{reason}</span>}
        </For>
      </div>
    </li>
  );
}
