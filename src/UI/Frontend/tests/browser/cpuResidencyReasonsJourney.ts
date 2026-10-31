import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.ts";
import { waitForCondition } from "./journeyWaits.ts";

// 详细信息 → CPU 调度模型：CPU 驻留推送是快照或带原因的不可用值（GET 同形）。
// 六个原因逐个推来时，拓扑页头显示该原因的名称、问号里是说明，执行时长显示 --；
// 推回快照后原因消失、执行时长恢复，订阅通道不重开。中英两种语言各走一遍。
const reasons = [
  "cpu-residency-not-requested",
  "cpu-residency-window-warming",
  "cpu-residency-events-lost",
  "cpu-residency-session-restarted",
  "cpu-residency-session-unavailable",
  "cpu-residency-capture-failed"
] as const;
const residencyPath = "/api/cpu/residency/subscribe";

export default async function cpuResidencyReasonsJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  await setReason(page, baseUrl, reasons[0]);
  await page.setViewportSize({ width: 1280, height: 800 });
  await seedFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });

  for (const language of ["zh-CN", "en-US"] as const) {
    if (language !== "zh-CN") await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    await setReason(page, baseUrl, reasons[0]);
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    const app = await loadAppCopy(language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    await page.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
    // The fixture topology arrives on its first aligned publication.
    await page.locator("#pageStage .cpu-core").first().waitFor({ timeout: 65_000 });
    const streamsBefore = await streamStarts(page);

    for (const reason of reasons) {
      await setReason(page, baseUrl, reason);
      const expected = app.resourceTable.unavailableReasons[reason];
      assert.ok(expected, `${language}: ${reason} has copy`);
      const state = page.locator(`#pageStage .cpu-topology-residency-state[data-reason="${reason}"]`);
      await state.waitFor({ timeout: 6_500 });
      assert.equal((await state.evaluate(node => node.firstChild?.textContent ?? "")).trim(), expected.label, `${language}: ${reason} label`);
      const coreTimes = await page.locator("#pageStage .cpu-logical-chip small").allTextContents();
      assert.ok(coreTimes.length > 0 && coreTimes.every(text => text.trim() === "--"), `${language}: ${reason} leaves execution time empty`);
      if (reason === reasons[2]) {
        await state.getByRole("button").hover();
        await page.getByText(expected.hint, { exact: true }).waitFor({ timeout: 2_000 });
        await page.mouse.move(0, 0);
      }
    }

    await setReason(page, baseUrl, null);
    await page.locator("#pageStage .cpu-topology-residency-state").waitFor({ state: "detached", timeout: 6_500 });
    await waitForCondition(page, async () => {
      const times = await page.locator("#pageStage .cpu-logical-chip small").allTextContents();
      return times.length > 0 && times.some(text => text.trim() !== "--");
    }, { timeoutMs: 6_500, description: `${language}: execution time after recovery` });
    assert.equal(await streamStarts(page), streamsBefore, `${language}: reasons and recovery arrive on the open subscription channel`);
    const residency = await telemetry(page, residencyPath);
    assert.equal(residency.logicalInFlight, 1, `${language}: one residency subscription stays open`);
  }
  assert.deepEqual(errors, []);
}

async function setReason(page: Page, baseUrl: string, reason: typeof reasons[number] | null) {
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: { cpuResidencyUnavailableReason: reason } });
  assert.ok(response.ok(), `scenario ${reason}`);
}

async function telemetry(page: Page, path: string) {
  return page.evaluate(async requestPath => {
    const response = await fetch("/__test/requests");
    return (await response.json() as Record<string, { physicalStarted: number; logicalInFlight: number }>)[requestPath] ?? { physicalStarted: 0, logicalInFlight: 0 };
  }, path);
}

async function streamStarts(page: Page) {
  return (await telemetry(page, "/api/subscriptions/stream")).physicalStarted;
}
