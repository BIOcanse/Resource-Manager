import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { seedFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForStableRendering } from "./journeyWaits.js";

const DEFAULT_ARTIFACT_DIRECTORY = resolve("../../../.local/browser-gate/resource-bar-proportion");

export default async function resourceBarProportionJourney(page, baseUrl) {
  const results = [];
  // 配色是前端偏好：首次加载前写入深色。
  await seedFrontendPreferences(page, (value) => { value.appearance.theme = "dark"; });
  for (const width of [390, 1100]) {
    await page.setViewportSize({ width, height: 844 });
    for (const value of [1, 25, 95, 99, 99.9, 100]) {
      await page.request.post(`${baseUrl}/__test/scenario`, {
        data: { resourceCpuValue: value, resourceSegmentCount: 1 }
      });
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const canvas = page.locator('.resource-bar-track[data-metric-id="cpu.usage"] canvas');
      await canvas.waitFor({ state: "visible" });
      await waitForBarPaint(page, "cpu.usage");
      const result = await canvas.evaluate((canvas, value) => {
        const ctx = canvas.getContext("2d");
        const y = Math.floor(canvas.height / 4);
        const pixels = ctx.getImageData(0, y, canvas.width, 1).data;
        const reference = [...pixels.slice(0, 4)].join(",");
        let end = 0;
        while (end < canvas.width && [...pixels.slice(end * 4, end * 4 + 4)].join(",") === reference) end++;
        const expected = Math.floor(canvas.width * value / 100);
        if (Math.abs(end - expected) > 1) throw new Error(`Bar ${value}%: ${end}/${canvas.width}, expected ${expected}`);
        if (value < 100 && end === canvas.width) throw new Error("Non-full usage painted full");
        return { value, width: canvas.width, end, expected };
      }, value);
      results.push(result);
    }
  }
  for (const width of [390, 1100]) {
    await page.setViewportSize({ width, height: 844 });
    await page.request.post(`${baseUrl}/__test/scenario`, {
      data: { resourceSharedMemory: true, resourceSegmentCount: 1 }
    });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    const canvas = page.locator('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    await canvas.waitFor({ state: "visible" });
    await waitForBarPaint(page, "memory.usage");
    // 共享部分画在本段右端，是本段颜色提亮（--resource-shared-tint）：从段的颜色变亮处起，到下一次变色止。
    results.push(await canvas.evaluate(canvas => {
      const pixels = canvas.getContext("2d").getImageData(0, Math.floor(canvas.height / 4), canvas.width, 1).data;
      const at = (x) => [pixels[x * 4], pixels[x * 4 + 1], pixels[x * 4 + 2]];
      const same = (a, b) => a.every((value, index) => value === b[index]);
      const own = at(1);
      let sharedStart = 1;
      while (sharedStart < canvas.width && same(at(sharedStart), own)) sharedStart++;
      const shared = at(sharedStart);
      let sharedEnd = sharedStart;
      while (sharedEnd < canvas.width && same(at(sharedEnd), shared)) sharedEnd++;
      const expectedStart = Math.floor(canvas.width * .125);
      const expectedEnd = Math.floor(canvas.width * .25);
      if (Math.abs(sharedStart - expectedStart) > 1 || Math.abs(sharedEnd - expectedEnd) > 1) {
        throw new Error(`Shared fraction painted incorrectly: ${sharedStart}..${sharedEnd} / ${canvas.width}`);
      }
      if (!shared.every((value, index) => value >= own[index]) || shared.reduce((a, b) => a + b) - own.reduce((a, b) => a + b) < 60) {
        throw new Error(`Shared part is not a visibly lighter tint of its segment: ${own} -> ${shared}`);
      }
      return { sharedStart, sharedEnd, width: canvas.width, own, shared };
    }));
    const output = resolve(process.env.RM_BROWSER_ARTIFACT_DIR || DEFAULT_ARTIFACT_DIRECTORY);
    mkdirSync(output, { recursive: true });
    await page.screenshot({ path: resolve(output, `shared-${width}.png`) });
  }
  return results;
}

async function waitForBarPaint(page, metricId) {
  const selector = `.resource-bar-track[data-metric-id="${metricId}"]`;
  await page.waitForFunction((selector) => {
    const track = document.querySelector(selector);
    const canvas = track?.querySelector("canvas");
    return canvas instanceof HTMLCanvasElement && canvas.width > 0 && canvas.height > 0
      && Number(canvas.dataset.resourceSegmentCount) === 1 && !track.classList.contains("moving")
      && canvas.getContext("2d").getImageData(0, Math.floor(canvas.height / 4), 1, 1).data[3] > 0;
  }, selector, { timeout: 7000 });
  await waitForStableRendering(page, [selector], { timeoutMs: 7000, includeCanvas: true });
}
