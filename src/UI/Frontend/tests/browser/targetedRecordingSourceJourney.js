import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function targetedRecordingSourceJourney(page, baseUrl) {
  const failures = [];
  let listReads = 0;
  let expectedDetailFailure = false;
  const reportReads = new Map();
  page.on("pageerror", error => failures.push(`pageerror: ${String(error)}`));
  page.on("console", message => {
    if (message.type() === "error" && !expectedDetailFailure) failures.push(`console: ${message.text()}`);
  });
  page.on("request", request => {
    if (request.method() !== "GET") return;
    const url = new URL(request.url());
    if (url.pathname === "/api/targeted-recordings") listReads++;
    const report = url.pathname.match(/^\/api\/targeted-recordings\/([^/]+)$/);
    if (report) reportReads.set(report[1], (reportReads.get(report[1]) ?? 0) + 1);
  });
  const assert = (condition, message) => {
    if (!condition) throw new Error(message);
  };
  const app = await loadAppCopy("zh-CN");
  let unrelatedRecording = true;
  await page.route("**/api/targeted-recordings", async route => {
    if (route.request().method() !== "GET") return route.continue();
    const response = await route.fetch();
    const records = await response.json();
    if (unrelatedRecording) records.push({
      id: "unrelated-active-recording", softwareId: "another-software", softwareName: "Other software",
      startedAt: new Date().toISOString(), endedAt: null, maximumDurationSeconds: 600,
      status: "recording", stopReason: null, incomplete: false
    });
    return route.fulfill({ json: records });
  });
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const navigation = page.locator(".shell-pages");
  await navigation.getByRole("button", { name: app.page.components, exact: true }).click();
  const categoryNavigation = page.getByRole("navigation", { name: app.management.categoryNav });
  await categoryNavigation.getByRole("button", { name: /一般应用/ }).click();
  const software = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
  await software.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const detail = page.getByRole("dialog", { name: "示例软件", exact: true });
  await detail.locator(".targeted-recording-control").waitFor({ state: "visible", timeout: 5000 });
  const startButton = detail.getByRole("button", { name: app.targetedReport.start, exact: true });
  await startButton.waitFor({ state: "visible", timeout: 5000 });
  await page.waitForFunction((label) => Array.from(document.querySelectorAll(
    ".targeted-recording-control button")).some((button) =>
    button.textContent?.trim() === label && !button.disabled), app.targetedReport.start,
  { timeout: 5000 });
  const idleReadCount = listReads;
  await page.waitForTimeout(2250);
  assert(listReads === idleReadCount,
    `The shared recording list polled without an active recording: ${idleReadCount} -> ${listReads}`);
  unrelatedRecording = false;

  await startButton.click();
  await detail.locator(".targeted-recording-actions [role='status']")
    .waitFor({ state: "visible", timeout: 5000 });
  const readsAtStart = listReads;
  await page.waitForTimeout(2250);
  assert(listReads > readsAtStart,
    "The shared recording list did not refresh while a recording was active");

  await page.evaluate(() => {
    let visible = true;
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      get: () => visible ? "visible" : "hidden"
    });
    window.setDocumentVisibilityForJourney = next => {
      visible = next;
      document.dispatchEvent(new Event("visibilitychange"));
    };
  });
  const control = detail.locator(".targeted-recording-control");
  await control.waitFor({ state: "visible", timeout: 3000 });
  const readsBeforeHidden = listReads;
  await page.evaluate(() => window.setDocumentVisibilityForJourney(false));
  assert(await page.evaluate(() => document.visibilityState) === "hidden",
    "The fixture did not hide document visibility");
  await page.waitForTimeout(2250);
  assert(listReads === readsBeforeHidden,
    `The mounted recording control kept polling while the document was hidden: ${readsBeforeHidden} -> ${listReads}`);
  assert(await control.isVisible(), "The recording control was unmounted for the hidden-page check");
  await page.evaluate(() => window.setDocumentVisibilityForJourney(true));
  const readsAfterVisible = listReads;
  await page.waitForTimeout(2250);
  assert(listReads > readsAfterVisible,
    "The recording list did not resume polling when document visibility returned");
  await detail.locator("button.icon-button[aria-label='关闭']").click();
  await detail.waitFor({ state: "hidden", timeout: 3000 });

  await navigation.getByRole("button", { name: app.page.details, exact: true }).click();
  await page.getByRole("tab", { name: app.targetedReport.tab, exact: true }).click();
  const row = page.locator(".targeted-report-list-row")
    .filter({ hasText: "示例软件" }).first();
  await row.waitFor({ state: "visible", timeout: 5000 });
  await row.locator(".targeted-report-open").click();
  const detailPanel = page.locator(".targeted-report-detail");
  await detailPanel.waitFor({ state: "visible", timeout: 5000 });
  const id = "recording-fixture-1";
  await page.waitForTimeout(2250);
  assert((reportReads.get(id) ?? 0) >= 2,
    `The selected active report did not refresh: ${reportReads.get(id) ?? 0} reads`);

  await navigation.getByRole("button", { name: app.page.monitor, exact: true }).click();
  const listReadsBeforeHidden = listReads;
  const reportReadsBeforeHidden = reportReads.get(id) ?? 0;
  await page.waitForTimeout(2250);
  assert(listReads === listReadsBeforeHidden,
    `The list source polled while the targeted report page was hidden: ${listReadsBeforeHidden} -> ${listReads}`);
  assert((reportReads.get(id) ?? 0) === reportReadsBeforeHidden,
    `The report source polled while its page was hidden: ${reportReadsBeforeHidden} -> ${reportReads.get(id) ?? 0}`);

  await navigation.getByRole("button", { name: app.page.details, exact: true }).click();
  await page.getByRole("tab", { name: app.targetedReport.tab, exact: true }).click();
  const reopenedRow = page.locator(".targeted-report-list-row")
    .filter({ hasText: "示例软件" }).first();
  await reopenedRow.waitFor({ state: "visible", timeout: 5000 });
  await reopenedRow.locator(".targeted-report-open").click();
  await detailPanel.waitFor({ state: "visible", timeout: 5000 });
  await detailPanel.locator(".targeted-report-note")
    .filter({ hasText: app.targetedReport.stopReasons.maxDuration })
    .waitFor({ state: "visible", timeout: 10000 });
  const listReadsAfterCompletion = listReads;
  const reportReadsAfterCompletion = reportReads.get(id) ?? 0;
  await page.waitForTimeout(2250);
  assert(listReads === listReadsAfterCompletion,
    `The list source kept polling after the recording completed: ${listReadsAfterCompletion} -> ${listReads}`);
  assert((reportReads.get(id) ?? 0) === reportReadsAfterCompletion,
    `The detail source kept polling after the recording completed: ${reportReadsAfterCompletion} -> ${reportReads.get(id) ?? 0}`);
  assert(reportReadsAfterCompletion >= 3,
    `Expected the selected report to be reread through active and terminal states: ${reportReadsAfterCompletion}`);
  await navigation.getByRole("button", { name: app.page.monitor, exact: true }).click();
  // Remounting releases the previous detail lease so this is a no-data error.
  expectedDetailFailure = true;
  const failDetail = route => route.fulfill({ status: 404, contentType: "text/plain", body: "recording not found" });
  await page.route("**/api/targeted-recordings/recording-fixture-1", failDetail);
  await navigation.getByRole("button", { name: app.page.details, exact: true }).click();
  await page.getByRole("tab", { name: app.targetedReport.tab, exact: true }).click();
  await page.locator(".targeted-report-list-row").filter({ hasText: "示例软件" }).first().locator(".targeted-report-open").click();
  const error = page.locator(".targeted-reports-page").getByRole("alert");
  await error.waitFor();
  assert(await page.locator(".targeted-reports-page").getByText(app.targetedReport.loading, { exact: true }).count() === 0,
    "A failed detail must stop loading");
  await page.unroute("**/api/targeted-recordings/recording-fixture-1", failDetail);
  await error.getByRole("button", { name: app.observationNotice.retry, exact: true }).click();
  await detailPanel.waitFor();
  await error.waitFor({ state: "detached" });
  expectedDetailFailure = false;
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    idleListReads: idleReadCount,
    listReadsWhileActive: listReads - readsAtStart,
    listReadsAfterHidden: readsBeforeHidden,
    activeReportReads: reportReadsAfterCompletion,
    completionStoppedPolling: true,
    pageErrors: failures
  };
}
