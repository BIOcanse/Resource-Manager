import { createMemo, For, Show } from "solid-js";
import { kernelLeakItemKey, type KernelLeakItem } from "../../data/kernelLeaks/kernelLeakReport.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";
import { uiText } from "../../text.ts";
import {
  kernelLeakActionReasons,
  kernelLeakActionViews,
  kernelLeakCapturedAt,
  kernelLeakEvidence,
  kernelLeakHolderTitle,
  kernelLeakImagePath,
  kernelLeakItemCounts,
  kernelLeakItemsInDisplayOrder,
  kernelLeakObservationProgress,
  kernelLeakPreviousRunFailure,
  kernelLeakProductName,
  kernelLeakRestartNote,
  kernelLeakServices,
  kernelLeakStateView
} from "./kernelLeakPresentation.ts";

/*
 * 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md“前端：报告界面”）。
 * 观察由本地服务在整个运行期间进行（配置可关闭）；页面可见只决定是否订阅 /api/kernel-leaks/subscribe 收报告
 * （前端工作控制器的可见性规则，和其他报告分页一样），后端发什么显示什么：本轮观察进度、上一轮失败及其时间、疑似项。
 * 每项的两个处理操作（重启持有程序、强制释放句柄）在合同 v2 里是保留的：按钮不可用，问号里是后端给的风险说明，
 * 不可用的原因直接写在按钮旁边。执行入口属于第 3 期。
 */
export function KernelLeakReportPage() {
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
  // 本轮观察的内容状态（Suspected 时没有，活动条目就是结果）；条目列表在它下面，任何状态下有条目就显示。
  const stateView = createMemo(() => kernelLeakStateView(source()));
  const items = createMemo(() => kernelLeakItemsInDisplayOrder(report()?.items ?? []));
  const copy = () => uiText.kernelLeakReport;

  return (
    <section
      {...frontendVisibilitySurface("visible.details.kernel-leaks.surface", [demandId])}
      class="kernel-leak-report"
      aria-label={pageName()}
    >
      <CurrentPageHeading />
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
          <For each={items()}>{(item) => <KernelLeakItemCard item={item} />}</For>
        </ul>
      </Show>
    </section>
  );
}

function KernelLeakItemCard(props: { readonly item: KernelLeakItem }) {
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
