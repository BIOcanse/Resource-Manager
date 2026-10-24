import assert from "node:assert/strict";

export default async function controlAuditJourney(page, baseUrl) {
  let stateAvailable = false;
  let curveAvailable = false;
  let reads = 0;
  const writes = [];
  let objects = [{ objectId: "A", settings: [
    { capabilityId: "fan.duty", number: 42, unit: "%" },
    { capabilityId: "fan.curve", curveExecution: "firmware", curve: [
      { temperatureCelsius: 40, percent: 30 }, { temperatureCelsius: 80, percent: 80 }
    ] }
  ] }];
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: ["A", "B"].map(id => ({
      id, kind: "fan", displayName: `Fan ${id}`, isControllable: true,
      platform: { operatingSystem: "windows", vendor: "test" }, terms: ["curve-firmware"],
      capabilities: [
        { id: "fan.duty", label: "Duty", valueKind: "number", supported: true, requiredAccessLevel: "normal",
          range: { minimum: 0, maximum: 100, step: 1, unit: "%" } },
        { id: "fan.curve", label: "Curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }
      ]
    }))
  } }));
  await page.route("**/api/control/objects/*/curve", route => curveAvailable
    ? route.fulfill({ json: [{ temperatureCelsius: 40, percent: 35 }, { temperatureCelsius: 80, percent: 85 }] })
    : route.fulfill({ status: 204 }));
  await page.route("**/api/control/state**", route => {
    if (route.request().method() === "PUT") {
      const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop());
      const settings = route.request().postDataJSON();
      writes.push({ id, settings });
      objects = [...objects.filter(item => item.objectId !== id), { objectId: id, settings }];
    } else {
      reads++;
      if (!stateAvailable) return route.fulfill({ status: 503, json: { error: "fixture-unavailable" } });
    }
    return route.fulfill({ json: { desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } } });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: "尚未读到权威控制设置" }).waitFor();
  assert.equal(await page.locator(".control-object").count(), 0, "No empty baseline cards after a state read failure");
  assert.deepEqual(writes, []);
  stateAvailable = true;
  await page.getByRole("button", { name: "重新读取", exact: true }).click();
  const a = page.locator(".control-object").filter({ hasText: "Fan A" });
  const b = page.locator(".control-object").filter({ hasText: "Fan B" });
  await a.locator(".fan-curve-chart").waitFor();
  await a.getByRole("slider", { name: "60 °C", exact: true }).press("ArrowUp");
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => !document.querySelector(".control-workspace")?.hasAttribute("aria-busy"));
  assert.ok(writes[0].settings.some(setting => setting.capabilityId === "fan.duty" && setting.number === 42),
    "Applying the curve retains the other authoritative setting");
  await b.getByRole("radio", { name: "软件管理", exact: true }).click();
  await b.getByRole("checkbox", { name: "转速曲线", exact: true }).check();
  await b.getByRole("alert").filter({ hasText: "无法读取当前固件曲线" }).waitFor();
  assert.equal(await b.locator(".fan-curve-chart").count(), 0);
  assert.equal(await b.getByRole("button", { name: "应用", exact: true }).isDisabled(), true);
  curveAvailable = true;
  await b.locator(".control-curve button").click();
  await b.locator(".fan-curve-chart").waitFor();
  assert.equal(await b.getByRole("slider", { name: "60 °C", exact: true }).getAttribute("aria-valuenow"), "60");
  await page.waitForFunction(async () => {
    const telemetry = await (await fetch("/__test/requests")).json();
    return telemetry["/api/control/actual/subscribe"]?.inFlight === 1;
  });
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await page.waitForFunction(async () => {
    const telemetry = await (await fetch("/__test/requests")).json();
    return telemetry["/api/control/actual/subscribe"]?.inFlight === 0;
  });
  const hiddenReads = reads;
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await a.locator(".fan-curve-chart").waitFor();
  assert.ok(reads > hiddenReads, "Becoming visible reloads authoritative state");
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  const interval = page.getByRole("textbox", { name: "重新应用间隔（秒）", exact: true });
  await interval.waitFor();
  assert.equal(await interval.inputValue(), "120");
  await interval.fill("337");
  await interval.press("Tab");
  assert.equal(await interval.inputValue(), "337", "The interval is an editable Settings value");
  return { unavailableBaselineBlocked: true, siblingSettingRetained: true, failedCurveUnavailable: true, visibilityLeaseReleased: true };
}
