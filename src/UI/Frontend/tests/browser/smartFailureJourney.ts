import type { ConcreteAppLanguageMode } from "../../src/i18n/settingsTypes.ts";
import type { Page, Locator } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

// 性能优化页 → 调度段失败（GET /api/optimization/smart/status 与其推送同形，后端 tests/App 的 smart-status 夹具三例）。
// - 调度未运行但 failure 为 null：只显示“调度未运行”，没有失败提示。
// - cycle-failed：失败类型的文字和问号说明；没有计划状态，没有排除。
// - native-plan-failed：失败类型、计划状态码的文字、排除总数、按（数据项，原因）的汇总，以及明细对话框里的进程与对象标识；
//   GPU 排除另有一行适配器标识，原样显示；事实和原因都取文字，页面上不出现码。
// - 故障关闭原因是另一件事：和失败提示同时出现时照旧显示，文字不变。
// - exceptionType 只用于诊断，任何语言下都不出现在页面上。
// 中英两种语言各走一遍；状态都由推送带来，不刷新页面。
const pushTimeout = 8_000;
const faultReason = "effect-scope-load-failed:UnauthorizedAccessException";

export default async function smartFailureJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("console", message => { if (message.type() === "error") errors.push(`console: ${message.text()}`); });
  const scenario = async (value: Record<string, unknown>) => {
    const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: value });
    assert.ok(response.ok(), `scenario ${JSON.stringify(value)}: ${response.status()}`);
  };

  await page.setViewportSize({ width: 1280, height: 800 });
  await seedFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  try {
    for (const language of ["zh-CN", "en-US"] as const) {
      await scenario({
        capabilities: "ready",
        settings: "ready",
        optimizationRuntime: true,
        smartStatusSegment: "success",
        smartStatus: { mode: "limited", schedulerRunning: false }
      });
      if (language !== "zh-CN") await writeFrontendPreferences(page, value => { value.appearance.language = language; });
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const app = await loadAppCopy(language as ConcreteAppLanguageMode);
      const text = app.smartFailure;
      await page.getByRole("navigation").getByRole("button", { name: app.page.optimization, exact: true }).click();
      const modeStatus = page.locator("#optimizationPage .optimization-mode-status");
      const notice = page.locator("#optimizationPage .optimization-smart-failure");
      const faultNotice = page.getByRole("alert").filter({ hasText: app.optimization.runtimeFaultClosedTitle });

      // 调度未运行、没有失败段：不推断失败。
      await modeStatus.getByText(app.optimization.smartSchedulerStopped, { exact: true }).waitFor({ timeout: pushTimeout });
      assert.equal(await notice.count(), 0, `${language}: schedulerRunning=false with failure null shows no failure`);

      // cycle-failed：只有失败类型和说明。
      await scenario({ smartStatusSegment: "cycle-failed" });
      const cycle = page.locator('#optimizationPage .optimization-smart-failure[data-failure-kind="cycle-failed"]');
      await cycle.waitFor({ timeout: pushTimeout });
      assert.equal((await cycle.locator("strong").first().textContent())!.trim(), text.title["cycle-failed"], `${language}: cycle-failed title`);
      assert.equal(await cycle.locator(".optimization-smart-failure-plan").count(), 0, `${language}: cycle-failed has no plan status`);
      assert.equal(await cycle.locator(".optimization-smart-failure-exclusions").count(), 0, `${language}: cycle-failed has no exclusions`);
      await cycle.getByRole("button", { name: app.common.helpHint, exact: true }).hover();
      await page.getByText(text.hint["cycle-failed"], { exact: true }).waitFor({ timeout: 2_000 });
      await page.mouse.move(0, 0);
      assert.equal(await modeStatus.textContent!(), app.optimization.smartSchedulerStopped, `${language}: status line unchanged by the failure`);

      // 故障关闭原因与失败同时存在：两者各自显示，故障关闭的文字不变。
      await scenario({ smartStatus: { mode: "limited", schedulerRunning: false, faultClosedReason: faultReason } });
      await faultNotice.waitFor({ timeout: pushTimeout });
      assert.equal((await faultNotice.locator("span").textContent())!.trim(), app.optimization.runtimeFaultClosedDetail(faultReason),
        `${language}: fault-closed detail unchanged`);
      assert.equal(await cycle.count(), 1, `${language}: the failure notice stays beside the fault-closed notice`);
      await scenario({ smartStatus: { mode: "limited", schedulerRunning: false } });
      await faultNotice.waitFor({ state: "detached", timeout: pushTimeout });

      // native-plan-failed：计划状态、排除总数、汇总和明细。
      await scenario({ smartStatusSegment: "native-plan-failed" });
      const native = page.locator('#optimizationPage .optimization-smart-failure[data-failure-kind="native-plan-failed"]');
      await native.waitFor({ timeout: pushTimeout });
      const capacity = text.nativePlanStatuses["capacity-exceeded"];
      assert.equal((await native.locator("strong").first().textContent())!.trim(), text.title["native-plan-failed"], `${language}: native title`);
      assert.equal((await native.locator(".optimization-smart-failure-plan").textContent())!.trim(), text.nativePlanStatus(capacity),
        `${language}: native plan status text`);
      const total = native.locator(".optimization-smart-failure-exclusion-total > span").first();
      assert.equal((await total.textContent())!.trim(), text.exclusionTotal(3), `${language}: exclusion total as sent`);
      const groups = native.locator(".optimization-smart-failure-counts li");
      assert.equal(await groups.count(), 3, `${language}: one summary row per (fact, reason)`);
      const expectedGroups = [
        ["inventory", "inventory-overflow", `${text.facts.inventory} · ${text.reasons["inventory-overflow"]}`],
        ["gpu-usage", "gpu-row-missing-or-invalid", `${text.facts["gpu-usage"]} · ${text.reasons["gpu-row-missing-or-invalid"]}`],
        ["native-plan", "capacity-exceeded", `${text.facts["native-plan"]} · ${capacity}`]
      ];
      assert.equal(text.reasons["capacity-exceeded"], capacity, `${language}: a native plan reason uses the plan status text`);
      for (const [index, [fact, reason, label]] of expectedGroups.entries()) {
        const row = groups.nth(index);
        assert.equal(await row.getAttribute("data-fact"), fact);
        assert.equal(await row.getAttribute("data-reason"), reason);
        assert.equal((await row.locator("span").textContent())!.trim(), label, `${language}: ${fact}/${reason} summary label`);
        assert.equal((await row.locator("strong").textContent())!.trim(), "1", `${language}: ${fact}/${reason} count`);
      }
      await native.getByRole("button", { name: app.common.helpHint, exact: true }).nth(1).hover();
      await page.getByText(text.exclusionsHint, { exact: true }).waitFor({ timeout: 2_000 });
      await page.mouse.move(0, 0);

      await native.getByRole("button", { name: text.viewExclusions, exact: true }).click();
      const dialog = page.getByRole("dialog", { name: text.detailTitle, exact: true });
      await dialog.waitFor({ timeout: 2_000 });
      assert.equal((await dialog.locator(".user-details-summary").textContent())!.trim(), text.detailSummary(3, 3), `${language}: dialog summary`);
      const sections = dialog.locator(".user-details-section");
      assert.equal(await sections.count(), 3, `${language}: one section per listed exclusion`);
      assert.equal((await sections.nth(0).locator("h3").textContent())!.trim(), expectedGroups[0][2]);
      assert.deepEqual(await rows(sections.nth(0)), [
        [text.field.processId, "43003"],
        [text.field.processStartKey, "9007199254740993"],
        [text.field.targetId, "process:43003:9007199254740993"]
      ], `${language}: process exclusion identities as sent, no adapter row without an adapter key`);
      assert.equal((await sections.nth(1).locator("h3").textContent())!.trim(), expectedGroups[1][2]);
      assert.deepEqual(await rows(sections.nth(1)), [
        [text.field.processId, "42"],
        [text.field.processStartKey, "9007199254740993"],
        [text.field.adapterKey, "9007199254740995"]
      ], `${language}: GPU exclusion shows its adapter key as its own field, exact text`);
      assert.equal((await sections.nth(2).locator("h3").textContent())!.trim(), expectedGroups[2][2]);
      assert.deepEqual(await rows(sections.nth(2)), [[text.field.target, text.noTarget]], `${language}: segment-wide exclusion`);
      await page.keyboard.press("Escape");
      await dialog.waitFor({ state: "detached", timeout: 2_000 });

      const pageText = await page.locator("#optimizationPage").innerText();
      for (const exceptionType of ["IOException", "InvalidDataException"]) {
        assert.ok(!pageText.includes(exceptionType), `${language}: exceptionType ${exceptionType} is never shown`);
      }
      for (const code of ["gpu-usage", "gpu-row-missing-or-invalid", "inventory-overflow", "capacity-exceeded"]) {
        assert.ok(!pageText.includes(code), `${language}: code ${code} is shown as text, not as the code`);
      }

      // 下一段成功：失败提示消失。
      await scenario({ smartStatusSegment: "success" });
      await notice.waitFor({ state: "detached", timeout: pushTimeout });
    }
  } finally {
    await scenario({ smartStatusSegment: "success", smartStatus: {} });
  }
  assert.deepEqual(errors, []);
}

async function rows(section: Locator) {
  return section.locator(".user-details-row").evaluateAll((nodes: HTMLElement[]) => nodes.map(node => [
    node.querySelector<HTMLElement>("dt")!?.textContent!?.trim() ?? "",
    node.querySelector<HTMLElement>("dd")!?.textContent!?.trim() ?? ""
  ]));
}
