import assert from "node:assert/strict";

export default async function controlSlowRequestJourney(page, baseUrl) {
  let objects = [];
  const writes = [];
  const delay = () => new Promise(resolve => setTimeout(resolve, 12_000));
  const state = () => ({ desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } });
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: [{
      id: "slow", kind: "fan", reportedName: "Slow Fan", isControllable: true,
      platform: { operatingSystem: "windows", vendor: "test" }, terms: ["curve-firmware"],
      capabilities: [{ id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }]
    }]
  } }));
  await page.route("**/api/control/objects/slow/curve", async route => {
    await delay();
    return route.fulfill({ json: [{ temperatureCelsius: 40, percent: 30 }, { temperatureCelsius: 95, percent: 100 }] });
  });
  await page.route("**/api/control/state**", async route => {
    if (route.request().method() !== "PUT") return route.fulfill({ json: state() });
    const settings = route.request().postDataJSON();
    await delay();
    writes.push(settings);
    objects = [{ objectId: "slow", settings }];
    return route.fulfill({ json: state() });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  const card = page.locator(".control-object").filter({ hasText: "Slow Fan" });
  await card.getByRole("radio", { name: "软件管理", exact: true }).click();
  await card.getByRole("checkbox", { name: "转速曲线", exact: true }).check();
  const highPoint = card.getByRole("slider", { name: "100 °C", exact: true });
  await highPoint.waitFor({ timeout: 20_000 });
  assert.equal(await highPoint.getAttribute("aria-valuenow"), "100", "Slow read must map the high-temperature tail to the 100°C grid endpoint");
  await highPoint.press("ArrowDown");
  const completedWrite = page.waitForResponse(response => response.request().method() === "PUT"
    && response.url().endsWith("/api/control/state/slow"), { timeout: 20_000 });
  await card.getByRole("button", { name: "应用", exact: true }).click();
  assert.equal((await completedWrite).status(), 200);
  await card.getByText("已改动，未应用", { exact: true }).waitFor({ state: "hidden", timeout: 3000 });
  assert.equal(writes.length, 1, "Slow write must finish rather than be canceled at ten seconds");
  assert.deepEqual(writes[0][0].curve.at(-1), { temperatureCelsius: 100, percent: 99 });
  assert.equal(await card.getByRole("button", { name: "应用", exact: true }).isDisabled(), true);
  return { readDelayMs: 12_000, writeDelayMs: 12_000, highTemperatureGridPoint: 100 };
}
