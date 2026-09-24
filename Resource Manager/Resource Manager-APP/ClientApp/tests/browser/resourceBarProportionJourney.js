import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

export default async function resourceBarProportionJourney(page, baseUrl) {
  const results = [];
  for (const width of [390, 1100]) {
    await page.setViewportSize({ width, height: 844 });
    for (const value of [1, 25, 95, 99, 99.9, 100]) {
      await page.request.post(`${baseUrl}/__test/scenario`, {
        data: { resourceCpuValue: value, resourceSegmentCount: 1, settingsTheme: "dark" }
      });
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const canvas = page.locator('.resource-bar-track[data-metric-id="cpu.usage"] canvas');
      await canvas.waitFor({ state: "visible" });
      await page.waitForTimeout(600);
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
      data: { resourceSharedMemory: true, resourceSegmentCount: 1, settingsTheme: "dark" }
    });
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    const canvas = page.locator('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    await canvas.waitFor({ state: "visible" });
    await page.waitForTimeout(600);
    results.push(await canvas.evaluate(canvas => {
      const pixels = canvas.getContext("2d").getImageData(0, Math.floor(canvas.height / 4), canvas.width, 1).data;
      const yellow = [];
      for (let x = 0; x < canvas.width; x++) {
        if (pixels[x * 4] === 211 && pixels[x * 4 + 1] === 171 && pixels[x * 4 + 2] === 48) yellow.push(x);
      }
      const expectedStart = Math.floor(canvas.width * .125);
      const expectedEnd = Math.floor(canvas.width * .25);
      if (!yellow.length || Math.abs(yellow[0] - expectedStart) > 1 || Math.abs(yellow.at(-1) + 1 - expectedEnd) > 1) {
        throw new Error(`Shared fraction painted incorrectly: ${yellow[0]}..${yellow.at(-1)} / ${canvas.width}`);
      }
      return { sharedStart: yellow[0], sharedEnd: yellow.at(-1) + 1, width: canvas.width };
    }));
    const output = resolve("../../../.codex/local-runs/numeric-audit-20260920/screenshots");
    mkdirSync(output, { recursive: true });
    await page.screenshot({ path: resolve(output, `shared-${width}.png`) });
  }
  return results;
}
