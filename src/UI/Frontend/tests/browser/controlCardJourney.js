import { fanModeFacts } from "./fanModeFacts.js";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

export default async function controlCardJourney(page, baseUrl) {
  const screenshotDirectory = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/control-card/", import.meta.url));
  let objects = [];
  const writes = [];
  let holdWrite;
  let releaseWrite;
  const state = () => ({ desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } });
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: ["A", "B"].map(id => ({
      id, ...fanModeFacts(["fan.curve", "fan.duty"]), kind: "fan", reportedName: `Fan ${id}`, isControllable: true,
      platform: { operatingSystem: "windows", vendor: "oem" },
      terms: [],
      capabilities: [{ id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }]
    }))
  } }));
  await page.route("**/api/control/objects/*/curve", route => {
    const curve = [40,45,50,55,60,65,70,75,80,85].map((temperatureCelsius, i) => ({ temperatureCelsius, percent: 10 + i * 10 }));
    if (route.request().url().includes("/A/")) curve.push({ temperatureCelsius: 95, percent: 100 });
    else curve.push({ temperatureCelsius: 120, percent: 100 });
    return route.fulfill({ json: curve });
  });
  await page.route("**/api/control/state**", async route => {
    if (route.request().method() !== "PUT") return route.fulfill({ json: state() });
    const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop());
    const settings = route.request().postDataJSON();
    writes.push({ id, settings });
    if (holdWrite) await holdWrite;
    objects = [...objects.filter(item => item.objectId !== id), ...(settings.length ? [{ objectId: id, settings }] : [])];
    return route.fulfill({ json: state() });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  const a = page.locator(".control-object").filter({ hasText: "Fan A" });
  const b = page.locator(".control-object").filter({ hasText: "Fan B" });
  for (const card of [a, b]) {
    await card.getByRole("radio", { name: "软件管理", exact: true }).click();
    await card.getByRole("checkbox", { name: "转速曲线", exact: true }).check();
    await card.locator(".fan-curve-chart").waitFor();
  }
  assert.equal(await a.locator(".fan-curve-grid").count(), 22);
  assert.equal(await a.locator(".control-curve-execution").count(), 0);
  assert.equal(await a.locator(".control-ownership-choice [aria-checked=true]").count(), 1);
  const highPoint = b.getByRole("slider", { name: /^100 °C，\d+%$/ });
  const highX = await highPoint.locator(".fan-curve-point").getAttribute("cx");
  assert.ok(Number(highX) >= 10 && Number(highX) <= 95, "A loaded 120°C tail maps to the visible 100°C grid endpoint");
  await highPoint.press("ArrowDown");
  assert.equal(await highPoint.getAttribute("aria-valuenow"), "99");
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll(".control-object-actions button:disabled").length < 4);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].id, "A");
  assert.deepEqual(writes[0].settings[0].curve.at(-1), { temperatureCelsius: 100, percent: 100 }, "A loaded 95°C tail maps to the 100°C grid endpoint on Apply");
  assert.equal(await b.getByRole("button", { name: "应用", exact: true }).isEnabled(), true);
  await b.getByRole("button", { name: "撤销改动", exact: true }).click();
  assert.equal(await b.getByRole("radio", { name: "固件管理", exact: true }).getAttribute("aria-checked"), "true");
  assert.equal(await a.getByRole("checkbox", { name: "转速曲线", exact: true }).isChecked(), true);
  const slider = a.getByRole("slider", { name: /^60 °C，\d+%$/ });
  await slider.press("ArrowUp");
  holdWrite = new Promise(resolve => { releaseWrite = resolve; });
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForTimeout(100);
  await slider.press("ArrowUp");
  releaseWrite();
  await page.waitForFunction(() => !document.querySelector(".control-object-actions button:last-child").disabled);
  assert.equal(await slider.getAttribute("aria-valuenow"), "52", "In-flight edit retained");
  await a.getByRole("button", { name: "撤销改动", exact: true }).click();
  assert.equal(await slider.getAttribute("aria-valuenow"), "51");
  await slider.press("Home");
  assert.equal(await slider.getAttribute("aria-valuenow"), "0");
  await slider.press("End");
  assert.equal(await slider.getAttribute("aria-valuenow"), "100");
  await a.getByRole("button", { name: "撤销改动", exact: true }).click();
  // Margin and secondary-button gestures cannot modify the curve.
  const chart = a.locator(".fan-curve-chart");
  const point = await chart.evaluate(el => {
    const p = new DOMPoint(61, 30.8).matrixTransform(el.getScreenCTM());
    return { x: p.x, y: p.y };
  });
  await page.mouse.click(point.x, point.y);
  assert.equal(await slider.getAttribute("aria-valuenow"), "40");
  await a.getByRole("button", { name: "撤销改动", exact: true }).click();
  await chart.click({ position: { x: 5, y: 5 } });
  await chart.click({ button: "right" });
  assert.equal(await a.getByRole("button", { name: "应用", exact: true }).isDisabled(), true);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const box = await chart.boundingBox();
    const selector = await a.locator('.control-ownership-choice').boundingBox();
    assert.ok(selector.y + selector.height <= box.y, "Selector stays above chart");
    assert.ok(box.width > 100 && box.width <= width);
    const overflow = await chart.evaluate(el => el.getBoundingClientRect().right > document.documentElement.clientWidth);
    assert.equal(overflow, false);
    await mkdir(screenshotDirectory, { recursive: true });
    await a.screenshot({ path: join(screenshotDirectory, `card-${width}.png`) });
  }
  await b.getByRole("radio", { name: "软件管理", exact: true }).click();
  await b.getByRole("checkbox", { name: "转速曲线", exact: true }).check();
  await highPoint.waitFor();
  await highPoint.press("ArrowDown");
  await b.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => !document.querySelector(".control-workspace")?.hasAttribute("aria-busy"));
  assert.deepEqual(writes.at(-1), { id: "B", settings: [{ capabilityId: "fan.curve", curveExecution: "software",
    curve: [
      { temperatureCelsius: 30, percent: 10 }, { temperatureCelsius: 35, percent: 10 },
      { temperatureCelsius: 40, percent: 10 }, { temperatureCelsius: 45, percent: 20 },
      { temperatureCelsius: 50, percent: 30 }, { temperatureCelsius: 55, percent: 40 },
      { temperatureCelsius: 60, percent: 50 }, { temperatureCelsius: 65, percent: 60 },
      { temperatureCelsius: 70, percent: 70 }, { temperatureCelsius: 75, percent: 80 },
      { temperatureCelsius: 80, percent: 90 }, { temperatureCelsius: 85, percent: 100 },
      { temperatureCelsius: 90, percent: 100 }, { temperatureCelsius: 95, percent: 100 },
      { temperatureCelsius: 100, percent: 99 }
    ] }] }, "The loaded curve is resampled to the software grid and the visible 100°C endpoint reaches Apply");
  return { cardScopedWrites: writes.length, retainedConcurrentEdit: true, gridLines: 22,
    highTemperaturePoint: 100, viewports: [1440, 390] };
}
