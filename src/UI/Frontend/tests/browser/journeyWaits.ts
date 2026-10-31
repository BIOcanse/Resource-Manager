// Rendering barriers are preparation only: callers keep their original assertions.
// Repeated checks advance on animation-frame events rather than a fixed sleep.
import type { Page } from "./journeyTypes.ts";

export interface JourneyWaitOptions { timeoutMs?: number }
export interface JourneyConditionOptions extends JourneyWaitOptions { description?: string }
export interface JourneyRenderingOptions extends JourneyWaitOptions { stableFrames?: number; includeCanvas?: boolean }
type PaintProbeWindow = typeof window & { [key: `__journeyPaint${number}`]: number | undefined };
type RenderingProbeWindow = typeof window & { [key: `__journeyRendering${number}`]: { signature: string; frames: number } | undefined };
const DEFAULT_WAIT_TIMEOUT_MS = 5_000;
const DEFAULT_STABLE_FRAMES = 3;
const MAX_CONDITION_CHECKS = 10_000;
const MAX_RENDER_ELEMENTS = 5_000;
const MAX_CANVAS_PIXELS = 4_000_000;
let probeSequence = 0;

export async function waitForNextPaint(page: Page, { timeoutMs = DEFAULT_WAIT_TIMEOUT_MS }: JourneyWaitOptions = {}) {
  const key = `__journeyPaint${++probeSequence}` as const;
  try {
    const handle = await page.waitForFunction(key => {
      const probeWindow = window as PaintProbeWindow;
      const frames = (probeWindow[key] ?? 0) + 1;
      probeWindow[key] = frames;
      return frames >= 2;
    }, key, { polling: "raf", timeout: timeoutMs });
    await handle.dispose();
  } finally {
    await page.evaluate(key => { delete (window as PaintProbeWindow)[key]; }, key).catch(() => undefined);
  }
}

export async function waitForCondition<T>(page: Page, check: () => T | Promise<T>, {
  timeoutMs = DEFAULT_WAIT_TIMEOUT_MS,
  description = "journey condition"
}: JourneyConditionOptions = {}): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expired = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`Timed out after ${timeoutMs} ms waiting for ${description}`)), timeoutMs);
  });
  try {
    let checks = 0;
    while (++checks <= MAX_CONDITION_CHECKS) {
      const result = await Promise.race([Promise.resolve().then(check), expired]);
      if (result) return result;
      const remaining = deadline - Date.now();
      if (remaining <= 0) throw new Error(`Timed out after ${timeoutMs} ms waiting for ${description}`);
      await Promise.race([waitForNextPaint(page, { timeoutMs: remaining }), expired]);
    }
    throw new Error(`Exceeded ${MAX_CONDITION_CHECKS} checks waiting for ${description}`);
  } finally {
    clearTimeout(timer);
  }
}

export async function waitForStableRendering(page: Page, selectors: string | readonly string[], {
  timeoutMs = DEFAULT_WAIT_TIMEOUT_MS,
  stableFrames = DEFAULT_STABLE_FRAMES,
  includeCanvas = false
}: JourneyRenderingOptions = {}) {
  const selected = typeof selectors === "string" ? [selectors] : selectors;
  if (!Array.isArray(selected) || selected.length === 0) throw new Error("Rendering waits require an explicit selector");
  const key = `__journeyRendering${++probeSequence}` as const;
  try {
    const handle = await page.waitForFunction(({ selected, key, stableFrames, includeCanvas, maxElements, maxPixels }) => {
      const probeWindow = window as RenderingProbeWindow;
      const pending = () => { delete probeWindow[key]; return false; };
      if (document.fonts.status !== "loaded") return pending();
      const roots: Element[] = [];
      for (const selector of selected) {
        // Content transitions retain inert copies with the original classes;
        // permanent secondary-tab groups also remain mounted while hidden.
        // Neither can establish that the requested current surface exists.
        const matches = [...document.querySelectorAll(selector)].filter(element =>
          !element.closest("[hidden], [inert]") && element.getClientRects().length > 0
          && getComputedStyle(element).visibility === "visible");
        if (matches.length === 0) return pending();
        roots.push(...matches);
      }
      const elements = new Set<Element>();
      const ancestors = new Set<Element>();
      let animated = false;
      for (const root of roots) {
        elements.add(root);
        for (const element of root.querySelectorAll("*")) elements.add(element);
        const animations = root.getAnimations({ subtree: true });
        // The page/settings stage owns JS-drawn transform and opacity. Child
        // geometry alone misses its fade, and getAnimations cannot see Lodestone.
        let ancestor = root.parentElement;
        while (ancestor) {
          ancestors.add(ancestor);
          if (ancestors.size > maxElements) throw new Error(`Rendering probe exceeds ${maxElements} ancestors`);
          ancestor = ancestor.parentElement;
        }
        // Selection indicators are siblings of their options, not descendants.
        // Only include the group that owns an explicitly requested option.
        if (root.matches(".page-tab, .segment")) {
          const indicator = root.closest(".has-selection-indicator")?.querySelector(":scope > .selection-indicator");
          if (indicator) {
            elements.add(indicator);
            animations.push(...indicator.getAnimations());
          }
        }
        for (const animation of animations) {
          const timing = animation.effect?.getComputedTiming();
          if (animation.playState === "running" && timing?.iterations !== Infinity) animated = true;
        }
      }
      for (const ancestor of ancestors) {
        elements.add(ancestor);
        for (const animation of ancestor.getAnimations()) {
          const timing = animation.effect?.getComputedTiming();
          if (animation.playState === "running" && timing?.iterations !== Infinity) animated = true;
        }
      }
      if (elements.size > maxElements) throw new Error(`Rendering probe exceeds ${maxElements} elements; narrow the selectors`);
      const samples: unknown[] = [];
      let pixels = 0;
      for (const element of elements) {
        const rect = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        samples.push([element.tagName, element.getAttribute("class"), rect.x, rect.y, rect.width, rect.height,
          element.scrollLeft, element.scrollTop, element.scrollWidth, element.scrollHeight,
          style.transform, style.translate, style.rotate, style.scale,
          style.opacity, style.color, style.backgroundColor, style.borderTopColor,
          element.childElementCount === 0 ? element.textContent : null,
          element instanceof HTMLInputElement ? [element.value, element.checked, element.disabled] : null]);
        if (includeCanvas && element instanceof HTMLCanvasElement) {
          pixels += element.width * element.height;
          if (pixels > maxPixels) throw new Error(`Rendering probe exceeds ${maxPixels} canvas pixels`);
          const context = element.getContext("2d");
          if (context && element.width > 0 && element.height > 0) {
            const data = context.getImageData(0, 0, element.width, element.height).data;
            let hash = 2166136261;
            for (let index = 0; index < data.length; index++) hash = Math.imul(hash ^ data[index], 16777619);
            samples.push([element.width, element.height, hash]);
          }
        }
      }
      const signature = JSON.stringify(samples);
      const previous = probeWindow[key];
      const frames = !animated && previous?.signature === signature ? previous.frames + 1 : 0;
      probeWindow[key] = { signature, frames };
      return frames >= stableFrames;
    }, { selected, key, stableFrames, includeCanvas, maxElements: MAX_RENDER_ELEMENTS, maxPixels: MAX_CANVAS_PIXELS },
    { polling: "raf", timeout: timeoutMs });
    await handle.dispose();
  } finally {
    await page.evaluate(key => { delete (window as RenderingProbeWindow)[key]; }, key).catch(() => undefined);
  }
}
