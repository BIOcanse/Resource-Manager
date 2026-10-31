type ActionFacts = { actionId: string; targetKind: "process" | "processTree"; name: string; processId: number | string; recordCount: number; bytesWritten: number };
type RequestTelemetry = Record<string, { logicalInFlight: number; logicalStarted: number; publications: number; queries: string[]; inFlightByQuery: Record<string, number> }>;
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

/*
 * 后端通知在界面里的提示，以及详细信息 → 内存保护记录（docs/notifications_2026-10-29.md“前端”，
 * docs/report_lifecycle_2026-10-28.md“主机内存保护的处理记录”）：
 * 1. 界面可见时订阅 /api/notifications/subscribe；第一帧只记下最新序号，打开界面前已有的通知不补显示。
 * 2. 之后的新通知按种类显示：内存保护一次处理是一条界面提示，写出对象、进程数和转储量；不认识的种类、事实写错的
 *    一条只跳过，同一帧里后面的通知照样显示，订阅不中断；同一条不显示两次。
 * 3. 提示里的“查看处理记录”打开详细信息的内存保护记录分页。分页列出后端发来的全部记录（最新在前），写明条数和容量；
 *    容量满了时照后端的未记下条数提示。
 * 4. 清空历史：确认框写明要删的条数和切片不删；取消不发请求；确认后提示后端回复的删除条数，列表变空。
 * 期望值按 zh-CN 取自文案；转储量的格式化不在这里核对，只核对它前后的文字。
 */
const notificationsPath = "/api/notifications/subscribe";

export default async function memoryProtectionNotificationsJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const clearRequests: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && new URL(request.url()).pathname === "/api/memory-protection/records/history/clear") {
      clearRequests.push(request.url());
    }
  });
  const app = await loadAppCopy("zh-CN");
  const copy = app.memoryProtection;
  const action = (sequence: number, facts: Partial<ActionFacts> = {}) => ({ sequence, kind: "memory-protection.action", occurredAt: "2026-10-29T10:00:00Z",
    facts: { actionId: `${sequence}`.padStart(32, "e"), targetKind: "process", name: "game.exe", processId: 4100 + sequence,
      recordCount: 1, bytesWritten: 16384, ...facts } });
  const notices = () => page.locator(".toast:not([data-exit-ghost])").filter({ hasText: copy.noticeTitle });
  // 转储量那一段换成占位，期望的是它前后的文字。
  const around = (text: string) => text.split("§");

  await setScenario(baseUrl, { memoryProtection: "active", backendNotifications: [action(1), action(2)] });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await writeFrontendPreferences(page, value => { value.appearance.language = "zh-CN"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitForTelemetry(baseUrl, t => t[notificationsPath]?.logicalInFlight === 1, "the visible interface subscribes");
  await page.waitForTimeout(400);
  assert.equal(await notices().count(), 0, "notifications from before the interface opened are not shown");

  // 新通知：不认识的种类和写错的事实只跳过，后面的照样显示。
  await setScenario(baseUrl, { backendNotifications: [action(1), action(2),
    { sequence: 3, kind: "backpack-overheat.action", occurredAt: "2026-10-29T10:00:01Z", facts: { level: 3 } },
    action(4, { processId: "4104" }),
    action(5, { targetKind: "processTree", name: "launcher", processId: 300, recordCount: 3 })] });
  await notices().first().waitFor();
  assert.equal(await notices().count(), 1, "one notice: the unknown kind and the bad facts are skipped");
  const treeText = ((await notices().first().textContent())!) ?? "";
  for (const part of around(copy.noticeTree(copy.recordProcess("launcher", 300), 3, "§"))) {
    assert(treeText.includes(part), `tree notice names the root, the process count and the dump size: ${treeText}`);
  }
  await setScenario(baseUrl, { backendNotifications: [action(2), action(5, { targetKind: "processTree", name: "launcher",
    processId: 300, recordCount: 3 }), action(6, { name: "worker.exe" })] });
  await page.waitForFunction(title => [...document.querySelectorAll<HTMLElement>(".toast:not([data-exit-ghost])")]
    .filter(toast => toast.textContent?.includes(title)).length === 2, copy.noticeTitle);
  const processText = ((await notices().first().textContent())!) ?? "";
  for (const part of around(copy.noticeProcess(copy.recordProcess("worker.exe", 4106), "§"))) {
    assert(processText.includes(part), `process notice names the process and the dump size: ${processText}`);
  }
  assert.equal(await notices().count(), 2, "a notification already shown is not shown again");

  // 入口：打开详细信息的内存保护记录分页。
  await setScenario(baseUrl, { memoryProtectionRecordCapacity: 2 });
  await setScenario(baseUrl, { memoryProtectionRecords: [
    { actionId: "1".repeat(32), processId: 61, name: "first" },
    { actionId: "2".repeat(32), processId: 62, name: "second" },
    { actionId: "3".repeat(32), processId: 63, name: "third" }] });
  await notices().first().getByRole("button", { name: copy.noticeOpenRecords, exact: true }).click();
  const tab = page.getByRole("tablist", { name: app.misc.detailsTab.tabsLabel, exact: true })
    .getByRole("tab", { name: copy.reportTab, exact: true });
  await tab.waitFor();
  assert.equal(await tab.getAttribute("aria-selected"), "true", "the notice opens the memory protection records tab");
  const report = page.getByRole("region", { name: copy.reportTab, exact: true });
  await report.getByText(copy.reportIntro, { exact: true }).waitFor();
  await report.getByText(copy.recordsCount("2", "2"), { exact: true }).waitFor();
  await report.getByText(copy.recordsUnrecorded(1), { exact: true }).waitFor();
  const names = async () => report.locator(".memory-protection-record .memory-protection-item-head strong")
    .evaluateAll(rows => rows.map(row => row.textContent?.trim()));
  assert.deepEqual(await names(), [copy.recordProcess("second", 62), copy.recordProcess("first", 61)],
    "all records newest first; the third action was not recorded");

  // 清空历史：取消不发请求；确认后提示删除条数，列表变空，未记下条数随之清零。
  await report.getByRole("button", { name: app.reportHistory.clear, exact: true }).click();
  const confirm = page.getByRole("dialog", { name: app.reportHistory.confirmTitle, exact: true });
  const confirmText = ((await confirm.textContent())!) ?? "";
  assert(confirmText.includes(app.reportHistory.confirmMessage.memoryProtection("2"))
    && app.reportHistory.details.memoryProtection.every(line => confirmText.includes(line)),
    `the confirmation gives the count and says slices stay: ${confirmText}`);
  await confirm.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
  await confirm.waitFor({ state: "detached" });
  assert.deepEqual(clearRequests, [], "cancelling sends nothing");
  await report.getByRole("button", { name: app.reportHistory.clear, exact: true }).click();
  await confirm.getByRole("button", { name: app.reportHistory.confirm, exact: true }).click();
  await page.locator(".toast.success:not([data-exit-ghost])").filter({ hasText: app.reportHistory.cleared("2") }).waitFor();
  await report.getByText(copy.recordsEmpty, { exact: true }).waitFor();
  assert.equal(await report.getByText(copy.recordsUnrecorded(1), { exact: true }).count(), 0);
  assert.equal(await report.getByRole("button", { name: app.reportHistory.clear, exact: true }).isDisabled(), true,
    "nothing left to clear");
  assert.equal(clearRequests.length, 1);

  assert.deepEqual(errors, []);
  return { notices: 2, cleared: 2 };
}

async function setScenario(baseUrl: string, patch: Record<string, unknown>) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(`Unable to set scenario ${JSON.stringify(patch)}: ${response.status} ${await response.text()}`);
}

async function waitForTelemetry(baseUrl: string, predicate: (telemetry: RequestTelemetry) => boolean, label: string) {
  const deadline = Date.now() + 12000;
  while (Date.now() < deadline) {
    if (predicate(await (await fetch(`${baseUrl}/__test/requests`)).json())) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Notification subscription telemetry did not reach: ${label}`);
}
