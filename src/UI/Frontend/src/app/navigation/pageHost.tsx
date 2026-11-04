import { Show, createContext, createEffect, createMemo, on, onCleanup, useContext } from "solid-js";
import type { Accessor, JSX } from "solid-js";
import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type { PageId } from "../../types.ts";

/* Each page has a persistent host; only the active host is visible. refresh-kept_pages retains visited pages and their DOM state; other pages unmount on leave. Hidden hosts use aria-hidden and content-visibility: hidden to skip layout, painting, and interaction without propagating inert styles. Visibility-bound subscriptions release while hidden. Retained pages must use onPageLeave for cleanup, avoid global listeners, and pause hidden animations until shown. */

/** Pages retained by refresh-kept_pages. */
export const keptPages: ReadonlySet<PageId> = new Set<PageId>(ResourceManager.FrontendPoints.KeptPages);

const PageActiveContext = createContext<Accessor<boolean>>();

export function PageHost(props: { readonly page: PageId; readonly active: boolean; readonly children: JSX.Element }) {
  const kept = keptPages.has(props.page);
  // Retained pages stay mounted after first use; other pages exist only while active.
  const built = createMemo<boolean>((wasBuilt) => props.active || (kept && wasBuilt), false);
  const active = () => props.active;
  let host: HTMLDivElement | undefined;
  if (kept) {
    // Pause hidden retained pages' animations and resume them when shown.
    // Defer animation discovery until the next frame so style calculation stays out of navigation.
    // This shares the frame's existing style calculation.
    let paused: readonly Animation[] = [];
    let pausing = 0;
    createEffect(on(active, (now) => {
      cancelAnimationFrame(pausing);
      pausing = 0;
      if (!host) return;
      const element = host;
      if (now) {
        for (const animation of paused) if (animation.playState === "paused") animation.play();
        paused = [];
      } else {
        pausing = requestAnimationFrame(() => {
          pausing = 0;
          paused = pauseHiddenAnimations(element);
        });
      }
    }, { defer: true }));
    onCleanup(() => cancelAnimationFrame(pausing));
  }
  return (
    <div ref={host} class="page-host" data-page-id={props.page} aria-hidden={props.active ? undefined : "true"}>
      <PageActiveContext.Provider value={active}>
        <Show when={built()}>{props.children}</Show>
      </PageActiveContext.Provider>
    </div>
  );
}

/** Pause hidden animations and cancel finished transitions so they cannot keep producing empty frames. Return paused animations for resumption without rescanning on show. */
function pauseHiddenAnimations(host: Element): Animation[] {
  const paused: Animation[] = [];
  for (const animation of host.getAnimations({ subtree: true })) {
    if (animation.playState === "running") {
      animation.pause();
      paused.push(animation);
    } else if (animation.playState === "finished" && animation instanceof CSSTransition) {
      animation.cancel();
    }
  }
  return paused;
}

/** Register during page initialization; call on hide or unmount, including outside a PageHost. */
export function onPageLeave(leave: () => void): void {
  const active = useContext(PageActiveContext);
  // Pages mount only while active.
  let shown = true;
  const left = () => {
    if (!shown) return;
    shown = false;
    leave();
  };
  if (active) {
    createEffect(on(active, (now) => {
      if (now) shown = true;
      else left();
    }, { defer: true }));
  }
  onCleanup(left);
}
