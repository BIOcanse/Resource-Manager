import type { ControlSetting } from "../../src/features/control/controlTypes.ts";
import type { Page } from "playwright-core";
import { fanModeFacts } from "./fanModeFacts.ts";
import assert from "node:assert/strict";

export default async function controlNumberInitialJourney(page: Page, baseUrl: string) {
  await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ controlActualValues: [{ objectId: "A", capabilityId: "fan.duty", number: 47, unit: "%" }] }) });
  const writes: { id: string; settings: ControlSetting[] }[] = [];
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: ["A", "B", "Fixed"].map(id => ({
      id, ...fanModeFacts(["fan.duty"], {firmware: false, software: id !== "Fixed"}), kind: "fan", reportedName: `Fan ${id}`, isControllable: id !== "Fixed",
      platform: { operatingSystem: "windows", vendor: "test" },
      capabilities: [{ id: "fan.duty", valueKind: "number", supported: id !== "Fixed",
        unavailableReason: id === "Fixed" ? { code: "control-range-fixed" } : null,
        unavailableKind: id === "Fixed" ? "platform" : null,
        requiredAccessLevel: "normal", range: { minimum: 0, maximum: id === "Fixed" ? 0 : 100,
          step: 1, unit: "%", defaultValue: null } }]
    }))
  } }));
  await page.route("**/api/control/state/**", route => {
    writes.push({ id: decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop()!),
      settings: route.request().postDataJSON() });
    return route.fulfill({ json: { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } } });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  const a = page.locator(".control-object").filter({ hasText: "Fan A" });
  const b = page.locator(".control-object").filter({ hasText: "Fan B" });
  const fixed = page.locator(".control-object").filter({ hasText: "Fan Fixed" });
  await fixed.waitFor();
  assert.equal(await page.locator(".control-object").count(), 3,
    "An unsupported fixed (0,0) range must not reject the entire catalog");
  assert.equal(await fixed.getByRole("radio", { name: "软件管理", exact: true }).isDisabled(), true);
  assert.equal(await fixed.getByRole("checkbox", { name: "固定转速", exact: true }).count(), 0,
    "An unsupported capability remains read-only when software ownership is selected");
  for (const card of [a, b]) {
    await card.getByRole("radio", { name: "软件管理", exact: true }).click();
    // 实际读数 47 画在滑轨刻度上、显示在数字框里；数字框已经显示它，旁边不再写第二遍。
    if (card === a) await a.locator('.control-editor-actual-mark[aria-label*="47"]').first().waitFor();
    await card.getByRole("checkbox", { name: "固定转速", exact: true }).check();
  }
  assert.equal(await a.locator(".control-capability-actual").filter({ hasText: "47" }).count(), 0,
    "The actual value the number box already shows must not be repeated beside it");
  assert.equal(await a.getByRole("spinbutton").inputValue(), "47");
  assert.equal(await b.getByRole("spinbutton").inputValue(), "");
  assert.equal(await b.getByRole("button", { name: "应用", exact: true }).isEnabled(), false);
  await a.getByRole("button", { name: "应用", exact: true }).click();
  await page.waitForFunction(() => !document.querySelector<HTMLElement>(".control-workspace")!?.hasAttribute("aria-busy"));
  assert.equal(writes[0].settings[0].number, 47);
  await b.getByRole("spinbutton").fill("60");
  await b.getByRole("spinbutton").press("Enter");
  const bApplied = page.waitForEvent("requestfinished", {
    predicate: request => request.method() === "PUT" && new URL(request.url()).pathname === "/api/control/state/B",
    timeout: 5000
  });
  await b.getByRole("button", { name: "应用", exact: true }).click();
  await bApplied;
  assert.equal(writes[1].settings[0].number, 60);
  assert.deepEqual(writes.map(write => write.id), ["A", "B"], "Only editable objects are submitted");
  return { actualInitial: 47, unknownInitial: "", userEntered: 60, fixedRangeRetainsUsableCatalog: true };
}
