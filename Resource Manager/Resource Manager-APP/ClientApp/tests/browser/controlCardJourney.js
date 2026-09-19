import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";

export default async function controlCardJourney(page, baseUrl) {
  let objects = [];
  const writes = [];
  let holdWrite;
  let releaseWrite;
  const state = () => ({ desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } });
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: ["A", "B"].map(id => ({
      id, kind: "fan", displayName: `Fan ${id}`, isControllable: true,
      platform: { operatingSystem: "windows", vendor: "test" },
      terms: ["curve-firmware", "curve-software"],
      capabilities: [{ id: "fan.curve", label: "Curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }]
    }))
  } }));
  await page.route("**/api/control/objects/*/curve", route => route.fulfill({ json:
    [40,45,50,55,60,65,70,75,80,85].map((temperatureCelsius, i) => ({ temperatureCelsius, percent: 10 + i * 10 }))
  }));
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
    await card.getByRole("checkbox", { name: "Curve", exact: true }).check();
    await card.locator(".fan-curve-chart").waitFor();
  }
  assert.equal(await a.locator(".fan-curve-grid").count(), 22);
  assert.equal(await a.locator(".control-curve-execution .settings-segment").count(), 2);
  assert.equal(await a.locator(".control-curve-execution [aria-checked=true]").count(), 1);
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll(".control-object-actions button:disabled").length < 4);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].id, "A");
  assert.equal(await b.getByRole("button", { name: "应用", exact: true }).isEnabled(), true);
  await b.getByRole("button", { name: "撤销改动", exact: true }).click();
  assert.equal(await b.getByRole("radio", { name: "固件自动管理", exact: true }).getAttribute("aria-checked"), "true");
  assert.equal(await a.getByRole("checkbox", { name: "Curve", exact: true }).isChecked(), true);
  const slider = a.getByRole("slider", { name: "60 °C", exact: true });
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
    const selector = await a.locator('.control-curve-execution').boundingBox();
    assert.ok(selector.y + selector.height <= box.y, "Selector stays above chart");
    assert.ok(box.width > 100 && box.width <= width);
    const overflow = await chart.evaluate(el => el.getBoundingClientRect().right > document.documentElement.clientWidth);
    assert.equal(overflow, false);
    await mkdir("../../../.codex/local-runs/control-card-curve-20260919/screenshots", { recursive: true });
    await a.screenshot({ path: `../../../.codex/local-runs/control-card-curve-20260919/screenshots/card-${width}.png` });
  }
  return { cardScopedWrites: writes.length, retainedConcurrentEdit: true, gridLines: 22, viewports: [1440, 390] };
}
