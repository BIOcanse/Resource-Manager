import { createEffect, on, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { Definition } from "../protocol/definition.ts";
import { attachDom, expectRelease } from "../protocol/domInput.ts";
import { Runtime } from "../protocol/runtime.ts";
import type { AnimationMode, Composite, Host, Instance, Platform, Presentation } from "../protocol/runtime.ts";
import { Trace } from "../protocol/trace.ts";
import type { Value } from "../protocol/vec.ts";

/*
 * 磁石（Lodestone）交互协议在产品里的接入（协议第 4.4 节；引擎在 ../protocol，版本见 CommonAssets/Tools/Lodestone/README.md）。
 *
 * 整个应用一个运行时，在 window 的捕获阶段接浏览器事件，组件自己不挂监听。
 * 帧按需：运行时要下一帧时排一个 requestAnimationFrame，回调里推进一帧；空闲时没有帧。
 * 系统参数第一版不读：给 null，系统项取协议默认，参数来源标“系统读取失败”。C# 宿主读到后改用 setSystem。
 * 日志没有订阅，写下的条目不留。执行方式是发行用的“优化”（协议第 4.7 节）。
 * 运行时在第一个组件挂上或第一次设置动画档位时建立，之后随页面存在。
 */

let runtime: Runtime | null = null;
let frameRequested = false;

const platform: Platform = {
  contains: (element, target) => element instanceof Node && target instanceof Node && element.contains(target),
  rect: (element) => {
    if (!(element instanceof Element)) return null;
    const r = element.getBoundingClientRect();
    return { left: r.left, top: r.top, right: r.right, bottom: r.bottom };
  },
  // 指针已经不在时捕获失败：之后的输入由输入层校正。
  capture: (element, pointerId) => { try { (element as HTMLElement).setPointerCapture(pointerId); } catch { /* 见上 */ } },
  release: (element, pointerId) => {
    const target = element as HTMLElement;
    if (!target.hasPointerCapture(pointerId)) return;
    expectRelease(pointerId);
    target.releasePointerCapture(pointerId);
  },
  focus: (element) => (element as HTMLElement).focus({ preventScroll: true }),
  responseZone: () => ({ left: 0, top: 0, right: window.innerWidth, bottom: window.innerHeight }),
  requestFrame: () => {
    if (frameRequested) return;
    frameRequested = true;
    requestAnimationFrame(() => {
      frameRequested = false;
      interactionRuntime().frame(performance.now());
    });
  }
};

export function interactionRuntime(): Runtime {
  if (runtime === null) {
    const created = new Runtime(null, new Trace(), platform, { mode: "optimized" });
    attachDom(window, (raw) => created.feed(raw));
    runtime = created;
  }
  return runtime;
}

/** 动画档位沿用 body[data-animations]，由 App 按外观设置解析后交给这里。 */
export function setInteractionAnimationMode(mode: AnimationMode): void {
  interactionRuntime().setAnimationMode(mode, performance.now());
}

/** 挂上的组件实例：挂载后才有，卸载后回到 null。组合的区域路由用它找子组件。 */
export interface InteractionHandle<V extends Value = Value> {
  readonly instance: Instance<V> | null;
}

/**
 * 把一份定义、一个呈现层和一个宿主挂到组件上：组件挂载后加入运行时，卸载时移除。
 * 在 Solid 组件的初始化里调用；view 在挂载时才建，因为它要读已经渲染好的元素。
 */
export function useInteraction<V extends Value>(name: string, definition: Definition<V>, view: () => Presentation<V>, host: Host<V>): InteractionHandle<V> {
  const handle: { instance: Instance<V> | null } = { instance: null };
  onMount(() => {
    const instance = interactionRuntime().add(name, definition, view(), host);
    handle.instance = instance;
    onCleanup(() => {
      handle.instance = null;
      interactionRuntime().remove(instance, performance.now());
    });
  });
  return handle;
}

/**
 * 组合（协议第 4.2 节）：一组子组件和它们的区域路由。挂载后加入运行时，卸载时移除。
 * children 每次路由时现读，子组件各自挂上、移除。route 收到指针的视口坐标，返回交给哪个子组件和理由，不交返回 null。
 */
export function useInteractionComposite(
  name: string,
  element: () => Element,
  children: () => readonly Instance[],
  route: (position: { x: number; y: number }) => { child: Instance; why: string } | null
): void {
  onMount(() => {
    const composite: Composite = {
      name,
      element: element(),
      get children() { return children(); },
      overrides: {},
      route
    };
    interactionRuntime().addComposite(composite);
    onCleanup(() => interactionRuntime().remove(composite, performance.now()));
  });
}

/**
 * Solid 状态做宿主：值和禁用读 accessor，调用交给 call。值变了通知运行时（不触发调用，协议第 4.2 节“不回声”）。
 * 在 Solid 组件的初始化里调用。
 */
export function solidHost<V extends Value>(options: {
  value: Accessor<V>;
  enabled: Accessor<boolean>;
  call: (port: string, args: readonly unknown[]) => void;
}): Host<V> {
  const listeners = new Set<(old: V, next: V, cause: string) => void>();
  createEffect(on(options.value, (next, old) => {
    if (old === undefined) return;
    for (const listener of listeners) listener(old, next, "宿主值变化");
  }));
  return {
    value: options.value,
    enabled: options.enabled,
    call: (port, args) => options.call(port, args),
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  };
}
