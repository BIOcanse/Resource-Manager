import type { AnimationStyleId } from "../../src/presentation/animationStyle.ts";
type MotionEvent = { moving: boolean; at: number };
type MotionProbe = { track: HTMLElement; events: MotionEvent[]; observer: MutationObserver | null };
type ProbeWindow = typeof window & { __resourceLayoutMotionProbe?: MotionProbe };
import type { Page } from "playwright-core";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

const normalSettleMinimumMs = 400;
const disabledMotionObservationMs = 600;

export default async function resourceLayoutTaskJourney(page: Page, baseUrl = "http://127.0.0.1:4177") {
  const pageErrors: string[] = [];
  const consoleErrors: string[] = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });

  const assert = (condition: unknown, message: string) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const setScenario = async (patch: Record<string, unknown>) => {
    const result = await page.evaluate(async (value: Record<string, unknown>) => {
      const response = await fetch("/__test/scenario", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(value)
      });
      return { ok: response.ok, status: response.status };
    }, patch);
    assert(result.ok, `Unable to set frontend scenario: ${JSON.stringify(result)}`);
  };
  const waitForCpuTotal = (displayValue: string) => page.waitForFunction((expected) => {
    const item = document.querySelector<HTMLElement>('.resource-bar-track[data-metric-id="cpu.usage"]')!
      ?.closest(".resource-breakdown-item");
    const value = item?.querySelector(".resource-breakdown-header span")?.textContent?.trim();
    return Number.parseFloat(value ?? "") === Number.parseFloat(expected);
  }, displayValue, { timeout: 6000 });
  const installMotionObserver = async () => page.evaluate(() => {
    const track = document.querySelector<HTMLElement>('[data-metric-id="cpu.usage"].resource-bar-track')!;
    if (!(track instanceof HTMLElement)) {
      throw new Error("CPU resource track is unavailable.");
    }
    const events: MotionEvent[] = [];
    const probe: MotionProbe = { track, events, observer: null };
    const observer = new MutationObserver(() => {
      events.push({ moving: track.classList.contains("moving"), at: performance.now() });
    });
    observer.observe(track, { attributes: true, attributeFilter: ["class"] });
    probe.observer = observer;
    (window as ProbeWindow).__resourceLayoutMotionProbe = probe;
  });
  const readMotion = () => page.evaluate(() => {
    const probe = (window as ProbeWindow).__resourceLayoutMotionProbe;
    return {
      events: probe?.events ?? [],
      moving: probe?.track?.classList.contains("moving") ?? null
    };
  });
  // 动画风格是前端偏好：像用户保存过一样写前端缓存，随后 reload 生效。这里用桌面常规（固定时长）。
  const setAnimations = (style: AnimationStyleId) => writeFrontendPreferences(page, (value) => {
    value.appearance.animationStyle = style;
  });
  const disposeMotionObserver = () => page.evaluate(() => {
    (window as ProbeWindow).__resourceLayoutMotionProbe?.observer?.disconnect();
    delete (window as ProbeWindow).__resourceLayoutMotionProbe;
  });

  await page.setViewportSize({ width: 1000, height: 700 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setScenario({
    capabilities: "ready",
    settings: "ready",
    resourceBreakdownGeneration: 0
  });
  await setAnimations("desktop");
  await page.reload({ waitUntil: "domcontentloaded" });

  const track = page.locator('[data-metric-id="cpu.usage"].resource-bar-track');
  await track.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.body.dataset.animations === "desktop");
  await installMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 1 });
  await waitForCpuTotal("38%");
  await page.waitForFunction(() =>
    (window as ProbeWindow).__resourceLayoutMotionProbe?.events.some((event) => event.moving === true),
  null,
  { timeout: 3000 });
  await page.waitForFunction(() => {
    const events = (window as ProbeWindow).__resourceLayoutMotionProbe?.events ?? [];
    const movingIndex = events.findIndex((event) => event.moving === true);
    return movingIndex >= 0
      && events.slice(movingIndex + 1).some((event) => event.moving === false);
  }, null, { timeout: 3000 });
  const normal = await readMotion();
  const movingEvent = normal.events.find((event) => event.moving === true);
  const settledEvent = normal.events.find(
    (event) => movingEvent && event.at > movingEvent.at && event.moving === false);
  assert(normal.moving === false
    && movingEvent
    && settledEvent
    && settledEvent.at - movingEvent.at >= normalSettleMinimumMs,
  `Normal layout transition did not settle through the task lifecycle: ${JSON.stringify(normal)}`);
  await disposeMotionObserver();

  await setScenario({ resourceBreakdownGeneration: 2 });
  await setAnimations("none");
  await page.reload({ waitUntil: "domcontentloaded" });
  await track.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => document.body.dataset.animations === "none");
  await installMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 3 });
  await waitForCpuTotal("55%");
  // Observe the full negative animation window. The MutationObserver must catch
  // any transient moving=true; current moving=false would permit early pass.
  await page.waitForTimeout(disabledMotionObservationMs);
  const disabled = await readMotion();
  assert(disabled.moving === false
    && disabled.events.every((event) => event.moving === false),
  `Disabled motion entered a moving state: ${JSON.stringify(disabled)}`);
  await disposeMotionObserver();
  await setScenario({ resourceBreakdownGeneration: 0 });

  assert(pageErrors.length === 0 && consoleErrors.length === 0,
    `Resource layout browser errors: ${JSON.stringify({ pageErrors, consoleErrors })}`);
  return { normal, disabled, pageErrors, consoleErrors };
}
