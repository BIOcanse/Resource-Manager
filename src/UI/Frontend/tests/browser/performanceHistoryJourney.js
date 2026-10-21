import assert from "node:assert/strict";

export default async function performanceHistoryJourney(page, baseUrl) {
  await page.addInitScript(() => {
    const prototype = CanvasRenderingContext2D.prototype;
    const original = { beginPath: prototype.beginPath, moveTo: prototype.moveTo,
      lineTo: prototype.lineTo, stroke: prototype.stroke };
    const paths = new WeakMap();
    window.__rmCanvasPaths = [];
    prototype.beginPath = function (...args) { paths.set(this, []); return original.beginPath.apply(this, args); };
    for (const method of ["moveTo", "lineTo"]) {
      prototype[method] = function (...args) { paths.get(this)?.push(args); return original[method].apply(this, args); };
    }
    prototype.stroke = function (...args) {
      if (this.strokeStyle === "#0f8f7b") {
        window.__rmCanvasPaths.push(paths.get(this)?.slice() ?? []);
        if (window.__rmCanvasPaths.length > 200) window.__rmCanvasPaths.shift();
      }
      return original.stroke.apply(this, args);
    };
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("radio", { name: "性能", exact: true }).click();
  await page.locator(".resource-performance-panel canvas").waitFor();
  await page.waitForFunction(() => window.__rmCanvasPaths.some(path => path.length >= 3
    && new Set(path.map(point => point[0])).size >= 3), { timeout: 10000 });
  const points = await page.evaluate(() => Math.max(...window.__rmCanvasPaths.map(path => path.length)));
  assert.ok(points >= 3, "Successive published samples must draw an actual history line");
  return { drawnHistoryPoints: points };
}
