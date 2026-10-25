import assert from "node:assert/strict";
import { batch, createEffect, createRoot } from "solid-js";
import { frontendVisibilitySourceKinds, frontendWorkIds } from "../src/frontendWork/frontendWorkIds.ts";
import { frontendVisibilitySourceDemand, frontendVisibilitySurfaceSelector } from "../src/frontendWork/frontendVisibilitySurface.ts";

/*
 * 某项工作是否需要（isNeeded）只随这一项变化。
 * 回归：订阅消费端在 effect 里读 isNeeded(X)，以前它跟踪整份 activeWorkIds，
 * 别的工作变化或看板卡片重建（同一 ID 先注销再注册）都会让消费端退订再订阅，
 * 合并订阅通道因此整条重建，所有订阅（包括本程序调度、本机状态）一起断开重开。
 */

class FakeSurface {
  readonly isConnected = true;
  constructor(private readonly attributes: Record<string, string>) {}
  getAttribute(name: string) { return this.attributes[name] ?? null; }
  getClientRects() { return [{}]; }
  getBoundingClientRect() { return { top: 0, left: 0, right: 10, bottom: 10 }; }
}
// 卡片 ID 里带需要编码的字符：消费端拿到的必须是原样的卡片 ID。
const cardDemand = frontendVisibilitySourceDemand(frontendVisibilitySourceKinds.monitorCard, "gpu 0/usage");
const hiddenCardDemand = frontendVisibilitySourceDemand(frontendVisibilitySourceKinds.monitorCard, "memory");
const surfaces = [
  new FakeSurface({ "data-frontend-visibility-surface": "card", "data-frontend-visibility-demands": cardDemand.demandId }),
  new FakeSurface({ "data-frontend-visibility-surface": "table", "data-frontend-visibility-demands": "monitor.table" })
];
const root = {
  querySelectorAll(selector: string) {
    assert.equal(selector, frontendVisibilitySurfaceSelector);
    return surfaces;
  },
  getBoundingClientRect() { return { top: 0, left: 0, right: 1000, bottom: 1000 }; }
};
class InertObserver { observe() {} unobserve() {} disconnect() {} }
Object.assign(globalThis, {
  document: {
    visibilityState: "visible",
    querySelector: (selector: string) => selector === ".app-shell" ? root : null,
    addEventListener() {},
    removeEventListener() {}
  },
  window: Object.assign(globalThis.window ?? {}, {
    innerWidth: 1000, innerHeight: 1000,
    setInterval: () => 0, clearInterval: () => undefined
  }),
  IntersectionObserver: InertObserver,
  MutationObserver: InertObserver
});

const { useFrontendWorkController } = await import("../src/frontendWork/useFrontendWorkController.ts");

let metricsRuns = 0;
let metricsNeeded: boolean | null = null;
let controller!: ReturnType<typeof useFrontendWorkController>;
let unregisterCard = () => undefined as void;
let unregisterTable = () => undefined as void;
const dispose = createRoot((disposeRoot) => {
  controller = useFrontendWorkController();
  unregisterCard = controller.registerVisibilityDemand(cardDemand, [frontendWorkIds.monitorMetrics]);
  controller.registerVisibilityDemand(hiddenCardDemand, [frontendWorkIds.monitorMetrics]);
  unregisterTable = controller.registerVisibilityDemand("monitor.table", [frontendWorkIds.monitorTable]);
  createEffect(() => {
    metricsNeeded = controller.isNeeded(frontendWorkIds.monitorMetrics);
    metricsRuns += 1;
  });
  return disposeRoot;
});
await flush();
assert.equal(metricsNeeded, true, "visible card needs metrics");
assert.deepEqual(controller.activeVisibilitySourceIds(frontendVisibilitySourceKinds.monitorCard), ["gpu 0/usage"],
  "only the visible card is handed over, with its own id");
assert.deepEqual(controller.activeWorkIds(), [frontendWorkIds.monitorMetrics, frontendWorkIds.monitorTable]);
const baseline = metricsRuns;

// 无关工作（表格）不再需要：读监视指标需求的消费端不重跑。
unregisterTable();
await flush();
assert.deepEqual(controller.activeWorkIds(), [frontendWorkIds.monitorMetrics]);
assert.equal(metricsRuns, baseline, "an unrelated work change must not re-run a metrics consumer");

// 看板设置刷新后卡片重建：同一需求 ID 先注销再注册，需要始终成立。
batch(() => {
  unregisterCard();
  unregisterCard = controller.registerVisibilityDemand(cardDemand, [frontendWorkIds.monitorMetrics]);
});
await flush();
assert.equal(metricsNeeded, true);
assert.equal(metricsRuns, baseline, "re-creating a visible card must not re-run a metrics consumer");

// 真正不再需要时照常通知。
unregisterCard();
await flush();
assert.equal(metricsNeeded, false);
assert.equal(metricsRuns, baseline + 1);
assert.deepEqual(controller.activeVisibilitySourceIds(frontendVisibilitySourceKinds.monitorCard), []);

dispose();

async function flush() {
  await Promise.resolve();
  await Promise.resolve();
}
