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
  // 录制列表和报告在有进行中的录制时每 2 s 刷新一次。每个检查窗口原来真等 2250 ms；现在把页面时钟推进
  // 2250 ms（runFor 依次触发这段时间里到期的全部页面计时器，与真等时页面会做的事相同），再等本进程收到
  // 页面在窗口里发出的每个请求，然后做原来的断言。窗口长度、断言和请求计数方式都不变，只是不再真等。
  const pollWindowMs = 2250;
  const observedRequestTimeoutMs = 5000;
  const requestWaiters = new Set();
  page.on("request", () => { for (const check of [...requestWaiters]) check(); });
  await page.clock.install();
  await page.addInitScript(() => {
    // 页面里同步记下每个 GET：推进时钟结束时，页面发出的请求数就是确定的。
    const issued = { list: 0, reports: {} };
    window.__targetedRecordingRequestsIssued = issued;
    const pageFetch = window.fetch.bind(window);
    window.fetch = (input, init) => {
      const href = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
      const method = (init?.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
      if (method === "GET") {
        const path = new URL(href, location.href).pathname;
        if (path === "/api/targeted-recordings") issued.list++;
        const report = path.match(/^\/api\/targeted-recordings\/([^/]+)$/);
        if (report) issued.reports[report[1]] = (issued.reports[report[1]] ?? 0) + 1;
      }
      return pageFetch(input, init);
    };
  });
  const passPollWindow = async () => {
    await page.clock.runFor(pollWindowMs);
    const issued = await page.evaluate(() => structuredClone(window.__targetedRecordingRequestsIssued));
    const caughtUp = () => listReads >= issued.list
      && Object.entries(issued.reports).every(([id, count]) => (reportReads.get(id) ?? 0) >= count);
    if (caughtUp()) return;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        requestWaiters.delete(check);
        reject(new Error(`Requests the page issued in the poll window never reached the journey within ${observedRequestTimeoutMs} ms: ${JSON.stringify({ issued, listReads, reportReads: Object.fromEntries(reportReads) })}`));
      }, observedRequestTimeoutMs);
      const check = () => {
        if (!caughtUp()) return;
        clearTimeout(timer);
        requestWaiters.delete(check);
        resolve();
      };
      requestWaiters.add(check);
    });
  };
  const app = await loadAppCopy("zh-CN");
  let unrelatedRecording = true;
  const recordingList = async route => {
    if (route.request().method() !== "GET" || !unrelatedRecording) return route.continue();
    return route.fulfill({ json: [{
      id: "unrelated-active-recording", softwareId: "another-software", softwareName: "Other software",
      startedAt: new Date().toISOString(), endedAt: null, maximumDurationSeconds: 600,
      status: "recording", stopReason: null, incomplete: false
    }] });
  };
  await page.route("**/api/targeted-recordings", recordingList);
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const navigation = page.locator(".shell-pages");
  await navigation.getByRole("button", { name: app.page.components, exact: true }).click();
  const categoryNavigation = page.getByRole("tablist", { name: app.management.categoryNav });
  await categoryNavigation.getByRole("tab", { name: /一般应用/ }).click();
  const software = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
  await software.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
  const detail = page.getByRole("dialog", { name: "示例软件", exact: true });
  await detail.locator(".targeted-recording-control").waitFor({ state: "visible", timeout: 5000 });
  const startButton = detail.getByRole("button", { name: app.targetedReport.start, exact: true });
  await startButton.waitFor({ state: "visible", timeout: 5000 });
  await detail.getByRole("status").filter({ hasText: app.targetedReport.oneAtATime }).waitFor();
  assert(await startButton.isDisabled(), "Another software recording must block start");
  const conflictReads = listReads;
  unrelatedRecording = false;
  await page.waitForFunction((label) => Array.from(document.querySelectorAll(
    ".targeted-recording-control button")).some((button) =>
    button.textContent?.trim() === label && !button.disabled), app.targetedReport.start,
  { timeout: 5000 });
  assert(listReads > conflictReads, "Another software finishing must release the start button without a manual refresh");
  await page.unroute("**/api/targeted-recordings", recordingList);
  const idleReadCount = listReads;
  await passPollWindow();
  assert(listReads === idleReadCount,
    `The shared recording list polled without an active recording: ${idleReadCount} -> ${listReads}`);

  await detail.locator(".targeted-recording-actions input").fill("15");
  await startButton.click();
  await detail.locator(".targeted-recording-actions [role='status']")
    .waitFor({ state: "visible", timeout: 5000 });
  // start() 先提交录制状态再刷新列表；等刷新结束才推进轮询时钟，避免把尚在飞行的请求算进窗口。
  await page.waitForFunction(() => {
    const stop = document.querySelector(".targeted-recording-actions button");
    return stop && !stop.disabled;
  }, null, { timeout: 5000 });
  const readsAtStart = listReads;
  await passPollWindow();
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
  await passPollWindow();
  assert(listReads === readsBeforeHidden,
    `The mounted recording control kept polling while the document was hidden: ${readsBeforeHidden} -> ${listReads}`);
  assert(await control.isVisible(), "The recording control was unmounted for the hidden-page check");
  // Count from before visibility returns: the resume read starts at once and may be observed before this line continues.
  const readsBeforeVisible = listReads;
  await page.evaluate(() => window.setDocumentVisibilityForJourney(true));
  await passPollWindow();
  assert(listReads > readsBeforeVisible,
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
  await passPollWindow();
  assert((reportReads.get(id) ?? 0) >= 2,
    `The selected active report did not refresh: ${reportReads.get(id) ?? 0} reads`);

  await navigation.getByRole("button", { name: app.page.monitor, exact: true }).click();
  const listReadsBeforeHidden = listReads;
  const reportReadsBeforeHidden = reportReads.get(id) ?? 0;
  await passPollWindow();
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
  // 夹具录制原来在开始 15 s 后由服务端计时器以「达到最长时长」结束；现在由旅程在同一处立即触发同一个结束。
  const completion = await fetch(`${baseUrl}/__test/targeted-recordings/complete-pending`, { method: "POST" });
  assert(completion.ok, `The fixture recording could not be completed: ${completion.status}`);
  await passPollWindow();
  await detailPanel.locator(".targeted-report-note")
    .filter({ hasText: app.targetedReport.stopReasons.maxDuration })
    .waitFor({ state: "visible", timeout: 10000 });
  // List and detail publish independently; both must observe completion before polling can stop.
  await reopenedRow.locator(".targeted-report-open span")
    .filter({ hasText: app.targetedReport.stopReasons.maxDuration })
    .waitFor({ state: "visible", timeout: 5000 });
  const listReadsAfterCompletion = listReads;
  const reportReadsAfterCompletion = reportReads.get(id) ?? 0;
  await passPollWindow();
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
