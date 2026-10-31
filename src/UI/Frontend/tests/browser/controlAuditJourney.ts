import type { Page } from "playwright-core";
type Setting = { capabilityId: string; number?: number; unit?: string; curveExecution?: string; curve?: { temperatureCelsius: number; percent: number }[] };
type Telemetry = Record<string, { inFlight: number }>;
import { fanModeFacts } from "./fanModeFacts.ts";
import assert from "node:assert/strict";
import { ResourceManager } from "../../src/preferences/generated/frontendPoints.values.ts";

export default async function controlAuditJourney(page: Page, baseUrl: string) {
  let stateAvailable = false;
  let curveAvailable = false;
  let reads = 0;
  const writes: { id: string; settings: Setting[] }[] = [];
  let objects: { objectId: string; settings: Setting[] }[] = [{ objectId: "A", settings: [
    { capabilityId: "fan.duty", number: 42, unit: "%" },
    { capabilityId: "fan.curve", curveExecution: "software", curve: [
      { temperatureCelsius: 40, percent: 30 }, { temperatureCelsius: 80, percent: 80 }
    ] }
  ] }];
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: ["A", "B"].map(id => ({
      id, ...fanModeFacts(["fan.curve", "fan.duty"]), kind: "fan", reportedName: `Fan ${id}`, isControllable: true,
      platform: { operatingSystem: "windows", vendor: "oem" }, terms: [],
      capabilities: [
        { id: "fan.duty", valueKind: "number", supported: true, requiredAccessLevel: "normal",
          range: { minimum: 0, maximum: 100, step: 1, unit: "%" } },
        { id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }
      ]
    }))
  } }));
  await page.route("**/api/control/objects/*/curve", route => curveAvailable
    ? route.fulfill({ json: [{ temperatureCelsius: 40, percent: 35 }, { temperatureCelsius: 80, percent: 85 }] })
    : route.fulfill({ status: 204 }));
  await page.route("**/api/control/state**", route => {
    if (route.request().method() === "PUT") {
      const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop()!);
      const settings: Setting[] = route.request().postDataJSON();
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
  assert.deepEqual(writes as readonly { id: string; settings: Setting[] }[], []);
  stateAvailable = true;
  await page.getByRole("button", { name: "重新读取", exact: true }).click();
  const a = page.locator(".control-object").filter({ hasText: "Fan A" });
  const b = page.locator(".control-object").filter({ hasText: "Fan B" });
  await a.locator(".fan-curve-chart").waitFor();
  await a.getByRole("slider", { name: /^60 °C，\d+%$/ }).press("ArrowUp");
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => !document.querySelector<HTMLElement>(".control-workspace")!?.hasAttribute("aria-busy"));
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
  assert.equal(await b.getByRole("slider", { name: /^60 °C，\d+%$/ }).getAttribute("aria-valuenow"), "60");
  await page.waitForFunction(async () => {
    const telemetry: Telemetry = await (await fetch("/__test/requests")).json();
    return telemetry["/api/control/actual/subscribe"]?.inFlight === 1;
  });
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await page.waitForTimeout(ResourceManager.FrontendPoints.VisibilityCooldownMs + 50);
  await page.waitForFunction(async () => {
    const telemetry: Telemetry = await (await fetch("/__test/requests")).json();
    return telemetry["/api/control/actual/subscribe"]?.inFlight === 0;
  });
  const hiddenReads = reads;
  const resumedState = page.waitForResponse(response => response.request().method() === "GET"
    && new URL(response.url()).pathname === "/api/control/state");
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await (await resumedState).finished();
  await a.locator(".fan-curve-chart").waitFor();
  assert.ok(reads > hiddenReads, "Becoming visible reloads authoritative state");
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "硬件控制", exact: true }).click();
  const interval = page.getByRole("textbox", { name: "重新应用间隔（秒）", exact: true });
  await interval.waitFor();
  assert.equal(await interval.inputValue(), "120");
  // 间隔只在保活维持设置打开时有意义：关着时显示数值但不可编辑，打开后才能改。
  assert.equal(await interval.isDisabled(), true, "The interval follows the keep-alive switch");
  await page.getByRole("checkbox", { name: "保活维持设置", exact: true }).check();
  await interval.fill("337");
  await interval.press("Tab");
  assert.equal(await interval.inputValue(), "337", "The interval is an editable Settings value");
  // Failure states must remain visible and must never invent acknowledgement or access.
  let noticeAvailable = false;
  let noticeWrites = 0;
  let accessAvailable = false;
  await page.route("**/api/control/notice", route => {
    if (route.request().method() !== "GET") noticeWrites++;
    return noticeAvailable ? route.fulfill({ json: { acknowledged: false } })
      : route.fulfill({ status: 503, json: { error: "notice-read-failed" } });
  });
  await page.route("**/api/control/access-level", route => accessAvailable
    ? route.fulfill({ json: { level: "root" } })
    : route.fulfill({ status: 503, json: { error: "access-read-failed" } }));
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "硬件控制", exact: true }).click();
  const access = page.locator(".control-access-level");
  await access.getByRole("alert").filter({ hasText: "无法读取调节权限" }).waitFor();
  assert.equal(await access.locator('[role="radio"][aria-checked="true"]').count(), 0);
  for (const radio of await access.getByRole("radio").all()) assert.equal(await radio.isDisabled(), true);
  accessAvailable = true;
  await access.getByRole("button", { name: "重新读取", exact: true }).click();
  await page.waitForFunction(() => document.querySelector<HTMLElement>('.control-access-level [role="radio"][aria-checked="true"]')!?.textContent!?.includes("root"));
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: "无法读取控制须知状态" }).waitFor();
  assert.equal(await page.locator(".control-first-run-notice").count(), 0);
  const beforeNoticeWrites = writes.length;
  noticeAvailable = true;
  await page.getByRole("button", { name: "重新读取", exact: true }).click();
  await page.locator(".control-first-run-notice").waitFor();
  // 须知经 Portal 挂到 body：对话框之外的应用根也必须设为 inert，读屏和指针都到不了背后的页面。
  const inertState = await page.evaluate(() => ({
    root: document.getElementById("root")?.inert === true,
    noticeInert: Boolean(document.querySelector<HTMLElement>(".control-first-run-notice")!?.closest<HTMLElement>("[inert]")!),
    rootContainsNotice: Boolean(document.getElementById("root")?.querySelector<HTMLElement>(".control-first-run-notice")!)
  }));
  assert.deepEqual(inertState, { root: true, noticeInert: false, rootContainsNotice: false },
    `The portal notice left the application root interactive: ${JSON.stringify(inertState)}`);
  // 键盘按“下一步”换到第二屏后，焦点落在第二屏的按钮上，而不是随第一屏的按钮掉到 body。
  await page.locator(".control-first-run-notice").getByRole("button", { name: "下一步", exact: true }).focus();
  await page.keyboard.press("Enter");
  await page.waitForFunction(() => document.activeElement?.closest<HTMLElement>(".control-first-run-notice")!
    && document.activeElement!.textContent!?.trim() === "我已知悉", null, { timeout: 3000 });
  assert.equal(noticeWrites, 0, "The journey never acknowledges the warranty notice");
  assert.equal(writes.length, beforeNoticeWrites, "Retrying reads never applies a draft");
  return { unavailableBaselineBlocked: true, siblingSettingRetained: true, failedCurveUnavailable: true,
    visibilityLeaseReleased: true, accessReadFailureUnselectedAndDisabled: true, noticeReadFailureNotSeen: true };
}
