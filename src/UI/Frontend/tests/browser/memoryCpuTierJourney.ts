import type { Page, Locator } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { waitForContentSettled } from "./contentSettled.ts";
import { NOT_GHOST } from "./liveLocator.ts";

// 设置 → 内存与 CPU 取舍（docs/memory_cpu_tiers_2026-10-30.md）：选项表全按模拟后端的 GET 回复显示。
// 切档先看到未保存的预览，保存后重读后端；单独设置保存后标“你的设置”，清除后回到档位值；
// 重启项同时显示“现在在用”和“重启后”；未知名称标“未知选项”；坏回复和读取失败显示读取失败。
// 期望值按 zh-CN 和默认字节模式（内存二进制）手写，不调用被测的格式化函数。
export default async function memoryCpuTierJourney(page: Page, baseUrl: string) {
  const errors : string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const settingsText = await loadSettingsText("zh-CN");
  const copy = app.memoryCpuTier;
  const patches: { changes: { memoryCpuTier: { tier: number; overrides: Record<string, unknown> } } }[] = [];
  let reads = 0;
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "PATCH" && path === "/api/settings/app") patches.push(request.postDataJSON());
    if (request.method() === "GET" && path === "/api/settings/memory-cpu-tier") reads++;
  });
  await page.setViewportSize({ width: 1280, height: 900 });

  const scenario = async (value: string) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ memoryCpuTier: value }) });
    assert.equal(response.ok, true, `scenario ${value} rejected`);
  };
  // 换分区时旧分区留一份退场副本（data-exit-ghost），只看在场的那份。
  const panel = () => page.locator(".memory-cpu-tier-settings").and(page.locator(NOT_GHOST));
  const option = (name: string) => panel().locator(`[data-memory-cpu-tier-option="${name}"]`);
  // 等到元素文字正好是期望值：保存、切档后的列表在下一次渲染才换，直接读会读到旧值。
  const exact = (text: string) => new RegExp(`^${text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`);
  const expectIn = (locator: Locator, text: string) => locator.filter({ hasText: exact(text) }).waitFor();
  const expectValue = (name: string, text: string) => expectIn(option(name).locator("[data-memory-cpu-tier-value]"), text);
  const runningText = (name: string) => option(name).locator("[data-memory-cpu-tier-running]");
  const expectRunning = (name: string, text: string) => expectIn(runningText(name), text);
  const expectBadge = (name: string, kind: string, text: string) => expectIn(option(name).locator(`[data-memory-cpu-tier-${kind}]`), text);
  const expectSaved = (tier: number) => expectIn(panel().locator("[data-memory-cpu-tier-saved]"), `${copy.savedTier(tier)} · ${copy.defaultTier(3)}`);
  const tierRadio = (tier: number) => panel().getByRole("radio", { name: copy.tierLabel(tier), exact: true });
  const previewNote = () => panel().locator(".memory-cpu-tier-preview-note");
  const save = async () => {
    const reread = page.waitForResponse(response => new URL(response.url()).pathname === "/api/settings/memory-cpu-tier"
      && response.request().method() === "GET");
    await page.getByRole("button", { name: settingsText.actions.save, exact: true }).click();
    await reread;
    await previewNote().waitFor({ state: "detached" });
    await waitForContentSettled(page);
  };
  const open = async () => {
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settingsText.sections.memoryCpuTier, exact: true }).click();
    await panel().getByText(copy.intro, { exact: true }).waitFor();
  };
  const covered : string[] = [];

  // 读到的默认表：第 3 档，五档“尚未实测”，每项按单位格式化，重启项待重启时两个值都在。
  await open();
  await option("gcGen0MaxBudget").waitFor();
  await waitForContentSettled(page);
  assert.equal(await tierRadio(3).getAttribute("aria-checked"), "true");
  await expectSaved(3);
  assert.equal(await panel().locator("[data-memory-cpu-tier-measured]").count(), 5);
  for (let tier = 1; tier <= 5; tier++) {
    assert.match(await panel().locator(`[data-memory-cpu-tier-measured="${tier}"]`).innerText(), new RegExp(copy.notMeasured));
  }
  assert.equal(await panel().locator("[data-memory-cpu-tier-option]").count(), 6, "only the options the endpoint returns render");
  await expectRunning("gcGen0MaxBudget", "现在在用：2.00 MiB");
  await expectValue("gcGen0MaxBudget", "重启后：4.00 MiB");
  await expectBadge("gcGen0MaxBudget", "apply", "重启后生效");
  await expectBadge("gcGen0MaxBudget", "category", "比例项");
  await expectBadge("gcGen0MaxBudget", "source", "来自档位");
  assert.equal(await runningText("gcConserveMemory").count(), 0, "a restart option already running its value shows one value");
  await expectValue("gcConserveMemory", "5");
  await expectValue("browserRuntimeSnapshotSeconds", "30 秒");
  await expectBadge("browserRuntimeSnapshotSeconds", "apply", "立即生效");
  await expectValue("singleRequestStopBufferMs", "10000 毫秒");
  await expectValue("rendererAbsenceEntries", "256");
  await expectValue("ledgerMemoryModeFloor", "自动");
  await option("gcGen0MaxBudget").getByText(copy.options.gcGen0MaxBudget.description, { exact: true }).waitFor();
  covered.push("read");

  // 切到第 5 档：保存前就看到按 tiers[] 算出的预览，不发补丁。
  await tierRadio(5).click();
  await previewNote().waitFor();
  await panel().getByRole("heading", { name: copy.optionsTitle(5), exact: true }).waitFor();
  await expectValue("browserRuntimeSnapshotSeconds", "300 秒");
  await expectValue("gcGen0MaxBudget", "重启后：不设");
  await expectBadge("gcGen0MaxBudget", "category", "激进项");
  await expectValue("ledgerMemoryModeFloor", "不限制（不主动归还）");
  assert.equal(patches.length, 0, "switching tier only changes the draft");
  covered.push("preview");

  // 保存：补丁带整节 memoryCpuTier，重读后显示后端的值。
  await save();
  assert.deepEqual(patches.at(-1)!.changes.memoryCpuTier, { tier: 5, overrides: {} });
  await expectSaved(5);
  await expectValue("browserRuntimeSnapshotSeconds", "300 秒");
  await expectBadge("gcGen0MaxBudget", "category", "激进项");
  await expectRunning("gcGen0MaxBudget", "现在在用：2.00 MiB");
  await expectValue("gcGen0MaxBudget", "重启后：不设");
  covered.push("save");

  // 单独设置：改一项保存后标“你的设置”；范围外的值不会发出；清除后回到档位值。
  const retention = option("browserRuntimeSnapshotSeconds").getByRole("textbox", { name: `${copy.options.browserRuntimeSnapshotSeconds.title} · ${copy.valueLabel}`, exact: true });
  await retention.fill("45");
  await retention.blur();
  await previewNote().waitFor();
  await expectBadge("browserRuntimeSnapshotSeconds", "source", "你的设置");
  await save();
  assert.deepEqual(patches.at(-1)!.changes.memoryCpuTier, { tier: 5, overrides: { browserRuntimeSnapshotSeconds: 45 } });
  await expectValue("browserRuntimeSnapshotSeconds", "45 秒");
  await expectBadge("browserRuntimeSnapshotSeconds", "source", "你的设置");
  await retention.fill("9999");
  await retention.blur();
  assert.equal(await retention.inputValue(), "600", "an out-of-range entry snaps to the endpoint's range");
  const clear = option("browserRuntimeSnapshotSeconds").getByRole("button", { name: `${copy.options.browserRuntimeSnapshotSeconds.title} · ${copy.clear}`, exact: true });
  await clear.click();
  assert.equal(await clear.count(), 0);
  await expectBadge("browserRuntimeSnapshotSeconds", "source", "来自档位");
  await expectValue("browserRuntimeSnapshotSeconds", "300 秒");
  await save();
  assert.deepEqual(patches.at(-1)!.changes.memoryCpuTier, { tier: 5, overrides: {} }, "clearing removes the key");
  assert.ok(patches.every(patch => JSON.stringify(patch).indexOf("9999") < 0), "out-of-range values are not sent");
  await expectBadge("browserRuntimeSnapshotSeconds", "source", "来自档位");
  covered.push("override-clear");

  // 选项项和“不设”：在草稿里改，切档不清单独设置；恢复默认值回到第 3 档、无单独设置。
  await option("ledgerMemoryModeFloor").getByRole("combobox", { name: `${copy.options.ledgerMemoryModeFloor.title} · ${copy.choiceLabel}`, exact: true }).click();
  await page.getByRole("option", { name: copy.choices.auto, exact: true }).click();
  await expectValue("ledgerMemoryModeFloor", "自动");
  await expectBadge("ledgerMemoryModeFloor", "source", "你的设置");
  await tierRadio(2).click();
  await expectValue("ledgerMemoryModeFloor", "自动");
  await expectValue("browserRuntimeSnapshotSeconds", "10 秒");
  await page.getByRole("button", { name: settingsText.actions.restoreDefaults, exact: true }).click();
  assert.equal(await tierRadio(3).getAttribute("aria-checked"), "true");
  await expectBadge("ledgerMemoryModeFloor", "source", "来自档位");
  const unset = option("gcGen0MaxBudget").getByRole("checkbox", { name: `${copy.options.gcGen0MaxBudget.title} · ${copy.unset}`, exact: true });
  await unset.check();
  await expectValue("gcGen0MaxBudget", "重启后：不设");
  assert.equal(await option("gcGen0MaxBudget").getByRole("textbox").isDisabled(), true);
  await unset.uncheck();
  await expectValue("gcGen0MaxBudget", "重启后：256 KiB");
  await option("gcGen0MaxBudget").getByRole("button", { name: `${copy.options.gcGen0MaxBudget.title} · ${copy.clear}`, exact: true }).click();
  await save();
  assert.deepEqual(patches.at(-1)!.changes.memoryCpuTier, { tier: 3, overrides: {} });
  await expectValue("gcGen0MaxBudget", "重启后：4.00 MiB");
  await expectRunning("gcGen0MaxBudget", "现在在用：2.00 MiB");
  covered.push("choice-unset-restore");

  // 前端不认识的名称：原名加“未知选项”，没有说明。
  await scenario("unknown-option");
  await open();
  await option("futureTradeOff").waitFor();
  await option("futureTradeOff").getByText(copy.unknownOption, { exact: true }).waitFor();
  await option("futureTradeOff").locator("strong").getByText("futureTradeOff", { exact: true }).waitFor();
  assert.equal(await option("futureTradeOff").locator(".memory-cpu-tier-option-description").count(), 0);
  covered.push("unknown-option");

  // 坏回复和读取失败：显示读取失败，不显示选项；恢复后重试能读到。
  for (const failing of ["malformed", "error"]) {
    await scenario(failing);
    await open();
    await panel().getByRole("alert").getByText(copy.loadFailed, { exact: true }).waitFor();
    assert.equal(await panel().locator("[data-memory-cpu-tier-option]").count(), 0);
    covered.push(failing);
  }
  await scenario("ready");
  await panel().getByRole("button", { name: copy.retry, exact: true }).click();
  await option("gcGen0MaxBudget").waitFor();
  assert.equal(await panel().getByRole("alert").count(), 0);
  covered.push("retry");

  assert.deepEqual(errors, []);
  return { covered, patches: patches.length, reads };
}
