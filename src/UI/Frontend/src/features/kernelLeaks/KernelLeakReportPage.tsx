import { createMemo, For, Show } from "solid-js";
import type { KernelLeakItem } from "../../data/kernelLeaks/kernelLeakReport.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import { uiText } from "../../text.ts";
import {
  kernelLeakCapturedAt,
  kernelLeakEvidence,
  kernelLeakHolderTitle,
  kernelLeakImagePath,
  kernelLeakItemKey,
  kernelLeakProductName,
  kernelLeakServices,
  kernelLeakStateView
} from "./kernelLeakPresentation.ts";

/*
 * 详细信息 → 内核泄漏报告（docs/kernel_leak_report_feature_2026-10-27.md“前端：报告界面”）。
 * 页面可见时订阅 /api/kernel-leaks/subscribe（前端工作控制器的可见性规则，和其他报告分页一样），后端发什么显示什么。
 * 处理动作（重启持有者、强制释放句柄）及其风险说明属于第 3 期，由本地服务的进程操作发出；
 * 在那之前每一项只占一个说明位置，前端不写动作名称、风险文字或动作逻辑。
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
  // 有疑似项时显示列表，其余情况显示一个内容状态。
  const stateView = createMemo(() => kernelLeakStateView(source()));
  const items = createMemo(() => {
    const current = report();
    return current?.status === "Suspected" ? current.items : [];
  });
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
            <Show when={current().status === "Suspected"}>
              <div><dt>{copy().suspectedItems}</dt><dd>{formatInterfaceNumber(items().length)}</dd></div>
            </Show>
          </dl>
        )}
      </Show>
      <Show when={(report()?.unavailableHolderCount ?? 0) > 0}>
        <p class="kernel-leak-note warning" role="status">{copy().unavailableHolders(report()!.unavailableHolderCount)}</p>
      </Show>
      <Show when={stateView()} fallback={
        <ul class="kernel-leak-items" role="list" aria-label={copy().suspectedItems}>
          <For each={items()}>{(item) => <KernelLeakItemCard item={item} />}</For>
        </ul>
      }>
        {(view) => <ContentState kind={view().kind} title={view().title} detail={view().detail} />}
      </Show>
    </section>
  );
}

function KernelLeakItemCard(props: { readonly item: KernelLeakItem }) {
  const copy = () => uiText.kernelLeakReport;
  const holder = () => props.item.holder;
  const services = () => kernelLeakServices(holder().services);
  return (
    <li class="kernel-leak-item" data-kernel-leak-item={kernelLeakItemKey(props.item)}>
      <header class="kernel-leak-item-header">
        <div class="kernel-leak-item-title">
          <strong classList={{ "metadata-state": holder().image.status !== "Current" }}>{kernelLeakHolderTitle(holder().image)}</strong>
          <span>{copy().holder.processId} {holder().processId}</span>
        </div>
        <span class="kernel-leak-kind">{copy().resourceKind[props.item.resourceKind]}</span>
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
      <p class="kernel-leak-actions-pending">{copy().actionsPending}</p>
    </li>
  );
}
