import { seedFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForStableRendering } from "./journeyWaits.js";

export default async function resourceBarColorModesJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const setup = await page.request.post(`${baseUrl}/__test/scenario`, {
    data: { resourceSegmentCount: 8, resourceDetailsDelayMs: 650 }
  });
  assert(setup.ok(), `Fixture setup failed: ${setup.status()}`);
  // 配色是前端偏好：首次加载前写入浅色。
  await seedFrontendPreferences(page, (value) => { value.appearance.theme = "light"; });
  await page.setViewportSize({ width: 1100, height: 760 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const typeBefore = await readMemoryBar(page);
  assert(typeBefore.mode === "type", "Default bar mode was not type");
  assert(typeBefore.uniqueColors === 1,
    `Same-type software did not share one color: ${JSON.stringify(typeBefore)}`);
  assert(typeBefore.dividerCount === 7,
    `Fixed-type software boundaries are missing: ${JSON.stringify(typeBefore)}`);
  const unchangedCallbacks = await checkUnchangedBoundaries(page);

  await selectMode(page, "异色区分", "distinct");
  const distinct = await readMemoryBar(page);
  assert(distinct.uniqueColors >= 4,
    `Distinct mode did not repaint software: ${JSON.stringify(distinct)}`);

  await page.reload({ waitUntil: "domcontentloaded" });
  const distinctReloaded = await readMemoryBar(page);
  assert(distinctReloaded.mode === "distinct"
    && JSON.stringify(distinctReloaded.colors) === JSON.stringify(distinct.colors),
  `Reload lost the saved color mode: ${JSON.stringify(distinctReloaded)}`);

  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await page.setViewportSize({ width: 1100, height: 520 });
  // 把资源条滚到视口顶端，保证在页面滚离顶部的状态下测点击（卡片区高度变化不影响这个前提）。
  await track.evaluate((element) => element.scrollIntoView({ block: "start" }));
  const beforeClick = await page.locator("main.app-shell").evaluate((element) => element.scrollTop);
  assert(beforeClick > 0, "Segment interaction must be tested below the top of the page");
  const bounds = await track.boundingBox();
  assert(bounds !== null, "Memory bar bounds are missing");
  await startSelectionFrameCheck(page);
  await track.click({ position: { x: bounds.width / 64, y: bounds.height / 2 } });
  const processPanel = page.locator('.resource-breakdown-item:has(.resource-bar-track[data-metric-id="memory.usage"]) .resource-process-panel');
  await processPanel
    .waitFor({ state: "visible", timeout: 5000 });
  const afterClick = await page.locator("main.app-shell").evaluate((element) => element.scrollTop);
  assert(Math.abs(afterClick - beforeClick) <= 1,
    `Selecting a resource segment moved the page: ${beforeClick} -> ${afterClick}`);
  await processPanel.locator('.resource-process-track[role="listbox"]').waitFor({ state: "visible" });
  await track.click({ position: { x: bounds.width * 3 / 64, y: bounds.height / 2 } });
  await processPanel.getByText("示例软件 1", { exact: true }).waitFor();
  await processPanel.locator('.resource-process-track[role="listbox"]').waitFor({ state: "visible" });
  const selectionFrames = await finishSelectionFrameCheck(page);
  assert(selectionFrames.emptyFrames === 0 && selectionFrames.replacedCanvases === 0,
    `Selecting another software blanked or replaced the bars: ${JSON.stringify(selectionFrames)}`);
  assert(selectionFrames.frames > 5 && selectionFrames.pendingFrames > 0,
    `Selection check did not cover pending process-detail frames: ${JSON.stringify(selectionFrames)}`);

  await selectMode(page, "类型固定色", "type");
  const typeAfter = await readMemoryBar(page);
  assert(typeAfter.uniqueColors === 1
    && typeAfter.dividerCount === 7
    && JSON.stringify(typeAfter.colors) === JSON.stringify(typeBefore.colors),
  `Switching back did not restore fixed-type rendering: ${JSON.stringify(typeAfter)}`);
  assert(errors.length === 0, `Page errors: ${JSON.stringify(errors)}`);
  return { typeBefore, distinct, distinctReloaded, typeAfter, unchangedCallbacks, selectionFrames, beforeClick, afterClick, errors };
}

async function startSelectionFrameCheck(page) {
  await page.evaluate(() => {
    const selector = '.resource-bar-track[data-metric-id="memory.usage"] canvas';
    const canvas = document.querySelector(selector);
    const result = { frames: 0, emptyFrames: 0, replacedCanvases: 0, pendingFrames: 0, running: true };
    window.__selectionFrames = result;
    const deadline = performance.now() + 12_000;
    const sample = () => {
      if (!result.running || performance.now() > deadline) return;
      const current = document.querySelector(selector);
      result.frames++;
      if (!current || current.width <= 0 || Number(current.dataset.resourceSegmentCount) !== 8
        || document.querySelector('.resource-breakdown-panel .content-state')) result.emptyFrames++;
      if (current !== canvas) result.replacedCanvases++;
      if (document.querySelector('.resource-process-panel[aria-busy="true"]')) result.pendingFrames++;
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  });
}

async function finishSelectionFrameCheck(page) {
  return page.evaluate(async () => {
    await new Promise(requestAnimationFrame);
    const result = window.__selectionFrames;
    result.running = false;
    return result;
  });
}

async function selectMode(page, label, value) {
  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "外观", exact: true }).click();
  await page.getByRole("radio", { name: label, exact: true }).click();
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await page.waitForFunction((mode) => document.body.dataset.barColor === mode, value);
  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "监视控制台", exact: true }).click();
}

async function readMemoryBar(page) {
  const canvas = page.locator('.resource-bar-track[data-metric-id="memory.usage"] canvas');
  await canvas.waitFor({ state: "visible", timeout: 7000 });
  await page.waitForFunction(() => {
    const element = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return element instanceof HTMLCanvasElement && element.width > 100
      && Number(element.dataset.resourceSegmentCount) === 8
      && !element.closest(".resource-bar-track").classList.contains("moving")
      && element.getContext("2d").getImageData(0, Math.floor(element.height / 4), 1, 1).data[3] > 0;
  }, null, { timeout: 7000 });
  // .moving belongs to the layout-settle task; canvas motion itself runs in JS,
  // so also compare actual painted pixels across frames before the original probes.
  await waitForStableRendering(page, ['.resource-bar-track[data-metric-id="memory.usage"]'], { timeoutMs: 7000, includeCanvas: true });
  return canvas.evaluate((element) => {
    const context = element.getContext("2d");
    if (!context) throw new Error("Resource bar has no 2D context");
    const ratio = element.width / element.getBoundingClientRect().width;
    const overscan = Number.parseFloat(getComputedStyle(element.parentElement)
      .getPropertyValue("--segment-paint-overscan")) * ratio;
    const width = element.width;
    const y = Math.floor(element.height - overscan - 3 * ratio);
    // Eight equal software values occupy 25% of physical memory in this fixture.
    const colors = Array.from({ length: 8 }, (_, index) => {
      const x = Math.floor(width * 0.25 * (index + 0.5) / 8);
      return [...context.getImageData(x, y, 1, 1).data];
    });
    if (colors.some((color) => color[3] !== 255)) {
      throw new Error(`Resource bar has blank software pixels: ${JSON.stringify(colors)}`);
    }
    return {
      mode: document.body.dataset.barColor,
      colors,
      dividerCount: Array.from({ length: 7 }, (_, index) => {
        const x = Math.floor(width * 0.25 * (index + 1) / 8) - 1;
        const boundary = [...context.getImageData(x, y, 1, 1).data].join(",");
        const interior = [...context.getImageData(x - 2, y, 1, 1).data].join(",");
        return boundary !== interior;
      }).filter(Boolean).length,
      uniqueColors: new Set(colors.map((color) => color.join(","))).size
    };
  });
}

async function checkUnchangedBoundaries(page) {
  return page.evaluate(async () => {
    const telemetry = async () => (await (await fetch("/__test/requests")).json())
      ["/api/resource-monitor/subscribe"].publications;
    const before = await telemetry();
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    const context = canvas.getContext("2d");
    let samples = 0;
    const end = performance.now() + 2_200;
    while (performance.now() < end) {
      await new Promise(requestAnimationFrame);
      const y = Math.floor(canvas.height / 4);
      for (let index = 0; index < 7; index += 1) {
        const x = Math.floor(canvas.width * (index + 1) / 32) - 1;
        const boundary = [...context.getImageData(x, y, 1, 1).data].join(",");
        const interior = [...context.getImageData(x - 2, y, 1, 1).data].join(",");
        if (boundary === interior) {
          throw new Error(`Unchanged callback removed divider ${index} at frame ${samples}`);
        }
      }
      samples += 1;
    }
    const after = await telemetry();
    if (samples < 2 || after <= before) {
      throw new Error(`Boundary check did not span real fixture callbacks: ${samples}/${before}/${after}`);
    }
    return { samples, publicationsBefore: before, publicationsAfter: after };
  });
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
