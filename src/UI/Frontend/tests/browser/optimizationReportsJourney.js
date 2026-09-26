export default async function optimizationReportsJourney(page, baseUrl) {
  const failures = [];
  const reportRequests = [];
  const gpuScoreRequests = [];
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  page.on("console", (message) => {
    if (message.type() === "error") {
      failures.push(`console: ${message.text()}`);
    }
  });
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/optimization/reports")) {
      reportRequests.push({ path, method: request.method() });
    }
    if (path.startsWith("/api/gpu/performance-")) {
      gpuScoreRequests.push(path);
    }
  });

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const postScenario = async (value) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    assert(response.ok, `Unable to update report scenario: ${response.status}`);
  };

  await page.setViewportSize({ width: 960, height: 680 });
  await postScenario({
    capabilities: "ready",
    settings: "ready",
    optimizationRuntime: true,
    optimizationReports: "ready",
    unknownGpuScore: true
  });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "性能优化", exact: true }).click();
  await page.getByRole("heading", { name: "优化报告", exact: true })
    .waitFor({ state: "visible", timeout: 5000 });
  await page.getByText("显卡型号未匹配", { exact: true })
    .waitFor({ state: "visible", timeout: 5000 });
  assert(await page.getByText(/GPU0 Unlisted GPU 未匹配内置性能分/).isVisible(),
    "Unknown GPU model must be identified on the report page");
  assert(gpuScoreRequests.includes("/api/gpu/performance-scores")
    && !gpuScoreRequests.includes("/api/gpu/performance-overrides"),
  `Report warning must read scores independently of overrides: ${JSON.stringify(gpuScoreRequests)}`);
  const modeGroup = page.locator(".optimization-mode-group");
  assert(await modeGroup.getByRole("button").count() === 4,
    "Scheduling mode does not expose Normal, Memory, CPU, and GPU choices");
  assert(await modeGroup.getByRole("button", { name: "普通模式" }).getAttribute("aria-pressed") === "true",
    "Normal mode is not the exclusive default");
  const normalMode = modeGroup.getByRole("button", { name: "普通模式" });
  const memoryMode = modeGroup.getByRole("button", { name: "内存调度" });
  const cpuMode = modeGroup.getByRole("button", { name: "CPU 调度" });
  await cpuMode.click();
  assert(await cpuMode.getAttribute("aria-pressed") === "true",
    "CPU scheduling did not become selected");
  await memoryMode.click();
  assert(await cpuMode.getAttribute("aria-pressed") === "true"
    && await memoryMode.getAttribute("aria-pressed") === "true",
    "Selecting Memory cleared the selected CPU domain");
  await normalMode.click();
  assert(await normalMode.getAttribute("aria-pressed") === "true"
    && await cpuMode.getAttribute("aria-pressed") === "false"
    && await memoryMode.getAttribute("aria-pressed") === "false",
    "Normal mode did not clear the selected scheduling domains");
  const observationStatus = page.getByText(/持续观察 ·/);
  assert(await observationStatus.count() === 0,
    "Internal report-rule availability must not be displayed");
  await page.getByText("暂时没有需要处理的报告。", { exact: true })
    .waitFor({ state: "visible", timeout: 5000 });
  const initialReadCount = reportRequests.filter((request) =>
    request.path === "/api/optimization/reports" && request.method === "GET").length;
  assert(initialReadCount >= 1,
    `Report Source did not perform its initial GET: ${JSON.stringify(reportRequests)}`);
  assert(reportRequests.every((request) => request.path !== "/api/optimization/reports"
    || request.method === "GET"),
  `Report read endpoint received a non-GET request: ${JSON.stringify(reportRequests)}`);
  assert(await page.getByText(/正在监控 ·/).count() === 0,
    "Retired recorder-running presentation is still visible");

  await postScenario({ optimizationReports: "invalid" });
  const invalidRefresh = page.waitForResponse((response) =>
    new URL(response.url()).pathname === "/api/optimization/reports/refresh");
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await invalidRefresh;
  await page.waitForFunction(() => {
    const button = Array.from(document.querySelectorAll("button"))
      .find((candidate) => candidate.textContent?.trim() === "刷新");
    return button instanceof HTMLButtonElement && !button.disabled;
  });
  assert(await observationStatus.count() === 0,
    "Report refresh must not restore internal rule-status copy");
  assert(await page.getByText("暂时没有需要处理的报告。", { exact: true }).isVisible(),
    "A failed report refresh discarded the last-good overview");

  await postScenario({ optimizationReports: "ready", unknownGpuScore: false });
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.waitForFunction(() => {
    const button = Array.from(document.querySelectorAll("button"))
      .find((candidate) => candidate.textContent?.trim() === "刷新");
    return button instanceof HTMLButtonElement && !button.disabled;
  });
  assert(await observationStatus.count() === 0,
    "Report refresh must not restore internal rule-status copy");
  await page.getByText("显卡型号未匹配", { exact: true })
    .waitFor({ state: "detached", timeout: 5000 });
  const refreshPosts = reportRequests.filter((request) =>
    request.path === "/api/optimization/reports/refresh"
      && request.method === "POST").length;
  assert(refreshPosts === 2,
    `Manual refresh did not publish through POST twice: ${JSON.stringify(reportRequests)}`);
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    initialSourceGetCount: initialReadCount,
    manualRefreshPostCount: 2,
    failedRefreshPreservedLastGood: true,
    retiredRecorderStateAbsent: true,
    pageErrors: failures
  };
}
