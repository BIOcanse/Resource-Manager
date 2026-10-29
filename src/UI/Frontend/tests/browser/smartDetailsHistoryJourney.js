import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/**
 * 详细信息 → 报告：智能调度记录的历史（docs/report_lifecycle_2026-10-28.md“智能调度记录”）。
 * 1. 账本里带序号的记录按序号排在前面并标出序号，没有序号的按更新时间排在后面。
 * 2. 历史按序号从新到旧分页：先读 50 条，“加载更早的记录”读下一页，读完按钮消失；每条写恢复时间和恢复结果，
 *    恢复失败的结果标成危险色；详细信息里有序号、应用、记录、恢复时间和恢复结果。
 * 3. 带序号的记录离开账本（恢复了）后，历史从最新重读，条数保持已显示的条数。
 * 4. 清空历史：确认后发一次 POST，提示后端回复的删除条数，列表变成“暂无历史记录”，按钮不可用。
 * 5. 保留开关在设置 → 运行策略，默认关；关着时历史区说明记录恢复后不再保留，打开并保存后说明消失，保存的是
 *    performance.keepSmartRecordHistory。
 * 6. 九种语言：历史区的标题、说明、结果和按钮来自该语言的文案，面板不横向溢出。
 */
export default async function smartDetailsHistoryJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const at = (minute) => new Date(Date.UTC(2026, 9, 28, 9, minute)).toISOString();
  const results = ["restored", "targetExited", "ownershipLost", "failed", "unknown"];
  const historyEntry = (sequence) => ({
    sequence, state: "history", kind: sequence % 2 ? "cpu_affinity" : "gpu_preference", recordId: `record-${sequence}`,
    targetId: `software:frontend-harness.software:${sequence}`, softwareId: "frontend-harness.software",
    resourceKind: sequence % 2 ? "cpu" : "gpu", appliedAt: at(sequence % 60), processId: 2000 + sequence,
    processStartKey: `13370000000000${String(sequence).padStart(4, "0")}`, processName: `Harness ${sequence}`,
    recordedAt: at(sequence % 60), restoredAt: at((sequence + 1) % 60), restoreResult: results[sequence % results.length]
  });
  const historyRange = (from, to) => Array.from({ length: from - to + 1 }, (_, index) => historyEntry(from - index));
  // 账本：一条带序号 61（更新得早），一条没有序号（更新得晚）。
  const placement = (name, minute, record) => ({
    targetId: `software:frontend-harness.software:${name}`, processName: name, processId: 900 + minute,
    processStartKey: `1337000000000099${minute}`, softwareId: "frontend-harness.software", resourceKind: "cpu",
    records: [{ kind: "cpu_affinity", recordId: `active-${name}`, ...record }], appliedAt: at(minute), updatedAt: at(minute)
  });
  const kept = placement("Kept", 1, { sequence: 61, state: "active", recordedAt: at(1) });
  const unkept = placement("Unkept", 30, {});
  const scenario = async (value) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert.equal(response.status, 200, `scenario ${JSON.stringify(Object.keys(value))}`);
  };
  const historyRequests = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/optimization/smart/records/history")) historyRequests.push(`${request.method()} ${new URL(request.url()).pathname}${new URL(request.url()).search}`);
  });
  const settingsWrites = [];
  page.on("request", request => {
    if (new URL(request.url()).pathname === "/api/settings/app" && ["PATCH", "PUT"].includes(request.method())) {
      settingsWrites.push(request.postDataJSON());
    }
  });

  await scenario({ optimizationRuntime: true, smartPlacements: [kept, unkept], smartRecordHistory: historyRange(60, 1) });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await writeFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  const zh = await loadAppCopy("zh-CN");
  const copy = zh.smartReport.history;
  const report = await openReport(page, zh);
  // 账本记录的列表直接在报告区里；历史记录的列表在历史区里。
  const activeRows = report.locator(":scope > .smart-details-records > .smart-details-record");
  const historySection = report.getByRole("region", { name: copy.title, exact: true });
  const historyRows = historySection.locator(".smart-details-record.history");

  // 1. 账本记录：带序号的在前。
  await activeRows.nth(1).waitFor();
  const activeNames = (await activeRows.locator(".smart-details-record-main strong").allTextContents()).map(text => text.trim());
  assert.ok(activeNames[0].startsWith("Kept") && activeNames[1].startsWith("Unkept"), `records with a sequence come first: ${activeNames}`);
  assert.equal((await activeRows.first().locator(".smart-details-sequence").textContent()).trim(), "#61");
  assert.equal(await activeRows.nth(1).locator(".smart-details-sequence").count(), 0, "a record without a sequence shows none");

  // 2. 历史分页。
  await historyRows.nth(49).waitFor();
  assert.equal(await historyRows.count(), 50, "the first history page holds 50 records");
  assert.equal(await historyRows.first().getAttribute("data-smart-record-sequence"), "60");
  await historySection.getByText(copy.retentionOff, { exact: true }).waitFor();
  const loadMore = historySection.getByRole("button", { name: copy.loadMore, exact: true });
  await loadMore.click();
  await historyRows.nth(59).waitFor();
  assert.equal(await historyRows.count(), 60, "loading more appends the older page");
  await loadMore.waitFor({ state: "detached" });
  assert.deepEqual(historyRequests.slice(0, 2), [
    "GET /api/optimization/smart/records/history?limit=50",
    "GET /api/optimization/smart/records/history?limit=50&beforeSequence=11"
  ]);
  const failedRow = historySection.locator('[data-smart-record-sequence="3"]');
  assert.equal(await failedRow.locator(".smart-details-record-metrics span.danger").textContent(), copy.restoreResult.failed);
  assert.equal(await historySection.locator('[data-smart-record-sequence="5"] .smart-details-record-metrics span:not(.smart-details-sequence)').textContent(),
    copy.restoreResult.restored);
  await historySection.locator('[data-smart-record-sequence="4"]').getByRole("button", { name: zh.smartReport.details, exact: true }).click();
  const detail = page.getByRole("dialog");
  const detailText = await detail.textContent();
  for (const label of [copy.field.sequence, copy.field.appliedAt, copy.field.recordedAt, copy.field.restoredAt, copy.field.restoreResult]) {
    assert.ok(detailText.includes(label), `history details name ${label}`);
  }
  assert.ok(detailText.includes(copy.restoreResult.unknown), "history details give the restore result");
  await detail.getByRole("button", { name: zh.misc.close, exact: true }).last().click();
  await detail.waitFor({ state: "detached" });

  // 3. 序号 61 恢复：离开账本，进入历史；历史从最新重读，仍显示 60 条。
  const requestsBefore = historyRequests.length;
  await scenario({ smartPlacements: [unkept], smartRecordHistory: [historyEntry(61), ...historyRange(60, 1)] });
  await page.waitForFunction(() => document.querySelector(".smart-details-history .smart-details-record.history")?.getAttribute("data-smart-record-sequence") === "61");
  assert.equal(historyRequests[requestsBefore], "GET /api/optimization/smart/records/history?limit=60");
  assert.equal(await historyRows.count(), 60, "the re-read keeps the number of shown records");
  await loadMore.waitFor();

  // 4. 清空历史。
  const clear = historySection.getByRole("button", { name: zh.reportHistory.clear, exact: true });
  const confirm = page.getByRole("dialog", { name: zh.reportHistory.confirmTitle, exact: true });
  await clear.click();
  const confirmText = await confirm.textContent();
  assert.ok(confirmText.includes(zh.reportHistory.confirmMessage.smartRecords)
    && zh.reportHistory.details.smartRecords.every(line => confirmText.includes(line)), `clear confirmation: ${confirmText}`);
  await confirm.getByRole("button", { name: zh.reportHistory.confirm, exact: true }).click();
  await page.locator(".toast.success:not([data-exit-ghost])").filter({ hasText: zh.reportHistory.cleared("61") }).waitFor();
  await historySection.getByText(copy.empty, { exact: true }).waitFor();
  assert.equal(await clear.isDisabled(), true, "clear history is unavailable with no history");
  assert.equal(historyRequests.filter(entry => entry === "POST /api/optimization/smart/records/history/clear").length, 1);
  assert.equal(await activeRows.count(), 1, "clearing the history leaves the ledger records");

  // 5. 保留开关。
  const settingsText = await loadSettingsText("zh-CN");
  await page.getByRole("navigation").getByRole("button", { name: zh.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settingsText.sections.runtimePolicy, exact: true }).click();
  const keep = page.getByRole("radiogroup", { name: settingsText.performance.keepSmartRecordHistoryTitle, exact: true });
  assert.equal(await keep.getByRole("radio", { name: settingsText.performance.keepSmartRecordHistoryOptions.off.label, exact: true }).getAttribute("aria-checked"), "true",
    "keeping the history is off by default");
  await keep.getByRole("radio", { name: settingsText.performance.keepSmartRecordHistoryOptions.on.label, exact: true }).click();
  await page.getByRole("button", { name: settingsText.actions.save, exact: true }).click();
  await assertEventually(() => settingsWrites.some(body => JSON.stringify(body).includes('"keepSmartRecordHistory":true')),
    `saving writes performance.keepSmartRecordHistory: ${JSON.stringify(settingsWrites)}`);
  await openReport(page, zh);
  await historySection.getByText(copy.empty, { exact: true }).waitFor();
  assert.equal(await historySection.getByText(copy.retentionOff, { exact: true }).count(), 0, "the retention notice goes away once history is kept");

  // 6. 九种语言。
  const layouts = [];
  await scenario({ smartPlacements: [kept, unkept], smartRecordHistory: historyRange(12, 1) });
  for (const option of languageOptions.filter(item => item.id !== "system")) {
    const app = await loadAppCopy(option.id);
    await writeFrontendPreferences(page, value => { value.appearance.language = option.id; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const localized = await openReport(page, app);
    const section = localized.getByRole("region", { name: app.smartReport.history.title, exact: true });
    await section.locator(".smart-details-record.history").nth(11).waitFor();
    await section.getByRole("button", { name: app.reportHistory.clear, exact: true }).waitFor();
    await section.getByText(app.smartReport.history.restoreResult.failed, { exact: true }).first().waitFor();
    for (const size of [{ width: 1280, height: 900 }, { width: 760, height: 520 }]) {
      await page.setViewportSize(size);
      const overflow = await localized.evaluate(element => element.scrollWidth - element.clientWidth);
      assert.ok(overflow <= 1, `${option.id} ${size.width}×${size.height}: the report overflows by ${overflow}px`);
      layouts.push(`${option.id}@${size.width}`);
    }
    await page.setViewportSize({ width: 1280, height: 900 });
  }
  assert.deepEqual(errors, []);
  return { historyPages: true, rereadAfterRestore: true, cleared: true, retentionSetting: true, layouts: layouts.length };
}

async function openReport(page, app) {
  await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
  await page.getByRole("tab", { name: app.misc.detailsTab.report, exact: true }).click();
  const report = page.locator(".smart-details-report");
  await report.waitFor();
  return report;
}

async function assertEventually(predicate, message) {
  const deadline = Date.now() + 5000;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 50));
  }
}
