import assert from "node:assert/strict";
import { mock } from "node:test";
import { createRoot } from "solid-js";
import { frontendWorkIds } from "../src/frontendWork/frontendWorkIds.ts";
import { frontendVisibilitySurfaceSelector } from "../src/frontendWork/frontendVisibilitySurface.ts";

/*
 * 回归（10-30，已安装候选 .4）：监视页的“软件资源占用”和“资源列表”一直显示正在加载，订阅从没开。
 * 浏览器的相交观察只在相交状态变化时报告一个界面；宿主或文档不可见时送来的那一次被丢掉，
 * 之后不会再报，那块区域就一直算作不可见。可见恢复时，被丢掉的界面要重新观察，浏览器才会重报。
 */

class FakeSurface {
  readonly isConnected = true;
  constructor(private readonly attributes: Record<string, string>) {}
  getAttribute(name: string) { return this.attributes[name] ?? null; }
  getClientRects() { throw new Error("synchronous geometry read"); }
  getBoundingClientRect() { throw new Error("synchronous geometry read"); }
}
const surfaces = [
  new FakeSurface({ "data-frontend-visibility-surface": "bars", "data-frontend-visibility-demands": "monitor.bars" })
];
const root = {
  querySelectorAll(selector: string) {
    assert.equal(selector, frontendVisibilitySurfaceSelector);
    return surfaces;
  },
  getBoundingClientRect() { throw new Error("synchronous geometry read"); }
};
class InertObserver { observe() {} unobserve() {} disconnect() {} }
let intersections!: BrowserLikeIntersections;
// 和浏览器一样：只给新观察的界面报一次初始结果，之后只在变化时报。
class BrowserLikeIntersections {
  targets = new Set<FakeSurface>();
  pending = new Set<FakeSurface>();
  observeCalls = 0;
  constructor(readonly callback: (entries: unknown[]) => void) { intersections = this; }
  observe(target: FakeSurface) { this.observeCalls += 1; this.targets.add(target); this.pending.add(target); }
  unobserve(target: FakeSurface) { this.targets.delete(target); this.pending.delete(target); }
  disconnect() { this.targets.clear(); this.pending.clear(); }
  takeRecords() { return []; }
  deliverInitial(visible: boolean) {
    const entries = [...this.pending].map((target) => ({ target, isIntersecting: visible }));
    this.pending.clear();
    if (entries.length) this.callback(entries);
  }
}
let documentVisibilityChanged = () => {};
Object.assign(globalThis, {
  document: {
    visibilityState: "hidden",
    querySelector: (selector: string) => selector === ".app-shell" ? root : null,
    addEventListener(name: string, listener: () => void) { if (name === "visibilitychange") documentVisibilityChanged = listener; },
    removeEventListener() {}
  },
  window: Object.assign(globalThis.window ?? {}, {
    innerWidth: 1000, innerHeight: 1000,
    setInterval: () => 0, clearInterval: () => undefined
  }),
  IntersectionObserver: BrowserLikeIntersections,
  MutationObserver: InertObserver
});

mock.timers.enable({ apis: ["setTimeout"] });
const { useFrontendWorkController } = await import("../src/frontendWork/useFrontendWorkController.ts");

let controller!: ReturnType<typeof useFrontendWorkController>;
const dispose = createRoot((disposeRoot) => {
  controller = useFrontendWorkController();
  controller.registerVisibilityDemand("monitor.bars", [frontendWorkIds.monitorBars]);
  return disposeRoot;
});
await flush();
assert.equal(intersections.observeCalls, 1);

// 页面在窗口还不可见时完成布局：初始结果送到时被丢掉。
intersections.deliverInitial(true);
assert.equal(controller.isNeeded(frontendWorkIds.monitorBars), false, "a hidden document cannot start work");

// 窗口显示出来：被丢掉的界面重新观察，浏览器重报，工作开始。
Object.assign(document, { visibilityState: "visible" });
documentVisibilityChanged();
assert.equal(intersections.observeCalls, 2, "the surface whose entry was dropped is observed again");
intersections.deliverInitial(true);
await flush();
assert.equal(controller.isNeeded(frontendWorkIds.monitorBars), true, "the repeated initial entry starts the work");

// 已经得到结果的界面不再重新观察：再隐藏、再显示而没有丢掉结果时，不多观察一次。
Object.assign(document, { visibilityState: "hidden" });
documentVisibilityChanged();
Object.assign(document, { visibilityState: "visible" });
documentVisibilityChanged();
assert.equal(intersections.observeCalls, 2, "only surfaces with a dropped entry are observed again");

dispose();
mock.timers.reset();
console.log("frontend work remeasure: ok");

async function flush() {
  await Promise.resolve();
  await Promise.resolve();
}
