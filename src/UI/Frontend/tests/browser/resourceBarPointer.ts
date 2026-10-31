import type { Page } from "playwright-core";
import { NOT_GHOST } from "./liveLocator.ts";

/**
 * 资源条指针旅程的共用读法。
 * trackContent：轨道内容区（去掉边框）在视口里的位置，命中判断按它算。
 * pointedSegment：指针现在落到的段（交互层的 data-software-id）和它画出来的范围（交互层的 --segment-left/--segment-width，
 * 是布局的百分比，和画布画这一段用的是同一份布局），换算成视口横坐标。没有交互层时为 null。
 */
export async function trackContent(page: Page, metricId: string) {
  return page.evaluate((id) => {
    const element = document.querySelector<HTMLElement>(`.resource-bar-track[data-metric-id="${id}"]`)!;
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    const left = rect.left + Number.parseFloat(style.borderLeftWidth || "0");
    return { left, width: rect.right - Number.parseFloat(style.borderRightWidth || "0") - left, y: rect.top + rect.height / 2 };
  }, metricId);
}

export async function pointedSegment(page: Page, metricId: string) {
  return page.evaluate(({ id, notGhost }) => {
    const track = document.querySelector<HTMLElement>(`.resource-bar-track[data-metric-id="${id}"]`)!;
    const layer = track?.querySelector<HTMLElement>(`.resource-segment-interaction-layer${notGhost}`)!;
    const proxy = layer?.querySelector<HTMLElement>(".resource-segment-interaction-proxy")!;
    if (!layer || !proxy) return null;
    const rect = track!.getBoundingClientRect();
    const style = getComputedStyle(track!);
    const contentLeft = rect.left + Number.parseFloat(style.borderLeftWidth || "0");
    const contentWidth = rect.right - Number.parseFloat(style.borderRightWidth || "0") - contentLeft;
    const left = Number.parseFloat(layer.style.getPropertyValue("--segment-left"));
    const width = Number.parseFloat(layer.style.getPropertyValue("--segment-width"));
    return {
      id: proxy.getAttribute("data-software-id"),
      selected: proxy.getAttribute("aria-selected") === "true",
      pointer: proxy.classList.contains("pointer-active"),
      left: contentLeft + contentWidth * left / 100,
      right: contentLeft + contentWidth * (left + width) / 100
    };
  }, { id: metricId, notGhost: NOT_GHOST });
}

/** 当前选中的段（各条的选中项，按条的 metricId）。 */
export async function selectedSegments(page: Page) {
  return page.evaluate(() => Object.fromEntries([...document.querySelectorAll<HTMLElement>(".resource-bar-track")]
    .map((track): [string, string | null] => [track.getAttribute("data-metric-id")!,
      track.querySelector<HTMLElement>('[role="option"][aria-selected="true"]')?.getAttribute("data-software-id")! ?? null])
    .filter(([, id]) => id !== null)));
}

export async function waitFor(check: () => Promise<unknown>, what: string, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  let last: unknown;
  while (Date.now() < deadline) {
    last = await check().catch((error: { message: unknown }) => error.message);
    if (last === true) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`${what}: ${JSON.stringify(last)}`);
}
