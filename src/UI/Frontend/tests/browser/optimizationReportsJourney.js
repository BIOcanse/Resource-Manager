import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
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
  const statusReads = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/optimization/reports")) {
      reportRequests.push({ path, method: request.method() });
    }
    if (path === "/api/optimization/smart/status" && request.method() === "GET") {
      statusReads.push(path);
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
    "An invalid refresh response discarded the committed last evaluation");

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

  for (const [state, message] of [
    ["evaluation-failed", "无法完成报告评估，原因：IOException。"],
    ["partial", "部分规则不可用（1 条）；其余规则的报告仍可查看。"]
  ]) {
    await postScenario({ optimizationReports: state });
    await page.getByRole("button", { name: "刷新", exact: true }).click();
    await page.getByText(message, { exact: true }).waitFor({ state: "visible", timeout: 5000 });
    assert(await page.getByText("暂时没有需要处理的报告。", { exact: true }).count() === 0,
      `${state} must not show a healthy empty state`);
  }
  await postScenario({ optimizationReports: "ready" });
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByText("暂时没有需要处理的报告。", { exact: true }).waitFor();

  // 调度状态跟随后端：不离开页面、不点刷新，后端的模式、运行与已优化对象数也会推到页面上。
  const modeStatus = page.locator(".optimization-mode-status");
  const statusReadsBeforePush = statusReads.length;
  await postScenario({
    smartStatus: { mode: "limited", schedulerRunning: true, appliedTargetCount: 92 }
  });
  await modeStatus.getByText("调度运行中 · 92 个对象已优化", { exact: true })
    .waitFor({ state: "visible", timeout: 8000 });
  assert(await memoryMode.getAttribute("aria-pressed") === "true",
    "The pushed backend mode did not select Memory scheduling");
  assert(statusReads.length === statusReadsBeforePush,
    "The status update must arrive by push, not by an extra read");

  // 运行时故障关闭：状态如实显示未运行，并给出故障提示和原因码。
  await postScenario({
    smartStatus: {
      mode: "limited",
      schedulerRunning: false,
      faultClosedReason: "effect-scope-load-failed:UnauthorizedAccessException"
    }
  });
  const faultNotice = page.getByRole("alert").filter({ hasText: "优化运行时已故障关闭" });
  await faultNotice.waitFor({ state: "visible", timeout: 8000 });
  assert((await faultNotice.textContent()).includes(
    "effect-scope-load-failed:UnauthorizedAccessException"),
  "The fault-closed notice must show the backend reason code");
  assert(await modeStatus.textContent() === "调度未运行",
    `A fault-closed runtime must not read as running: ${await modeStatus.textContent()}`);

  // 刷新按钮同时重新读取调度状态：暂停推送后，只有刷新能带来后端切回普通模式的结果。
  await postScenario({
    pausedSubscriptionPaths: ["/api/optimization/smart/status/subscribe"],
    smartStatus: { mode: "normal", pendingChangeCount: 136, appliedTargetCount: 1 }
  });
  const statusReadsBeforeRefresh = statusReads.length;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await modeStatus.getByText("136 项调整待确认 · 1 个对象已优化", { exact: true })
    .waitFor({ state: "visible", timeout: 5000 });
  assert(statusReads.length === statusReadsBeforeRefresh + 1,
    `Refresh did not read the scheduling status once: ${statusReads.length - statusReadsBeforeRefresh}`);
  assert(await normalMode.getAttribute("aria-pressed") === "true",
    "Refreshed backend mode did not select Normal mode");
  assert(await faultNotice.count() === 0,
    "The fault-closed notice must disappear once the backend clears the fault");
  await postScenario({ pausedSubscriptionPaths: [], smartStatus: {} });
  // A persisted trust must remain visible and removable after its report clears.
  let trusted = true;
  let deleteCount = 0;
  const overview = await (await fetch(`${baseUrl}/api/optimization/reports`)).json();
  const identity = { id: "hm-trust-editor", targetType: "Software", targetKey: "software:editor",
    displayName: "Editor", reportType: "background-memory", resourceKind: "Memory",
    trustedAt: overview.capturedAt, trustedReason: "", createdFromReportId: "former-report",
    lastVerifiedAt: overview.capturedAt, state: "Active" };
  const fulfillOverview = route => route.fulfill({ json: { ...overview, reports: [],
    trustedTargets: trusted ? [identity] : [], status: { ...overview.status, trustedCount: trusted ? 1 : 0 } } });
  await page.route("**/api/optimization/reports", fulfillOverview);
  await page.route("**/api/optimization/reports/refresh", fulfillOverview);
  await page.route("**/api/optimization/trust/hm-trust-editor", async route => {
    assert(route.request().method() === "DELETE", "Trust removal must use DELETE");
    deleteCount++; trusted = false; await route.fulfill({ status: 204 });
  });
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByRole("radio", { name: /已信任.*1/ }).click();
  await page.locator(".optimization-trusted-targets").getByText("Editor", { exact: true }).waitFor();
  await page.getByRole("button", { name: "移出信任", exact: true }).click();
  await page.locator(".optimization-trusted-targets").getByText("Editor", { exact: true }).waitFor({ state: "detached" });
  assert(deleteCount === 1, "Inactive trust was not removed exactly once");
  await page.unroute("**/api/optimization/reports", fulfillOverview);
  await page.unroute("**/api/optimization/reports/refresh", fulfillOverview);
  let strayTrusted = false;
  const strayTarget = { targetType: "process_instance", targetKey: "process:424242:134000000000000000", displayName: "pwsh",
    softwareId: "tool", softwareName: "PowerShell", softwareKind: "other", displayKind: "other", processNames: ["pwsh"], processIds: [424242], driveLetter: null };
  const strayTrust = { ...identity, id: "hm-trust-stray", ...strayTarget, reportType: "suspected_stray_process", createdFromReportId: "stray-report" };
  const strayReport = { id: "stray-report", type: "suspected_stray_process", state: "Active", severity: "Info", confidence: "High",
    createdAt: overview.capturedAt, updatedAt: overview.capturedAt, firstObservedAt: overview.capturedAt, lastObservedAt: overview.capturedAt,
    title: "BACKEND_PROSE", message: "BACKEND_PROSE", target: strayTarget, suggestedActions: [],
    context: { contextKind: "Normal", foregroundProcessId: null, foregroundProcessName: null, foregroundExecutablePath: null,
      foregroundSoftwareId: null, foregroundSoftwareName: null, confidence: "High", evidence: [] },
    evidence: { resourceKind: "Memory", averageValue: 52428800, peakValue: 52428800, currentValue: 52428800, valueUnit: "B",
      activeSampleCount: 1, sampleCount: 2, durationSeconds: 0, details: [],
      process: { processId: 424242, processStartKey: "134000000000000000", name: "pwsh", memoryBytes: 52428800,
        idleDurationSeconds: 900, commandLine: 'pwsh -File "stray-check.ps1"' } } };
  let strayEvaluationFailed = false;
  const fulfillStray = route => route.fulfill({ json: { ...overview,
    reports: [{ ...strayReport, state: strayTrusted ? "TrustedSuppressed" : "Active" }], trustedTargets: strayTrusted ? [strayTrust] : [],
    status: { ...overview.status, activeReportCount: 1, trustedCount: strayTrusted ? 1 : 0,
      evaluationState: strayEvaluationFailed ? "failed" : "completed",
      evaluationFailureReason: strayEvaluationFailed ? "IOException: fixture failure" : null } } });
  await page.route("**/api/optimization/reports", fulfillStray);
  await page.route("**/api/optimization/reports/refresh", fulfillStray);
  await page.route("**/api/optimization/reports/stray-report/dismiss", async route => {
    assert(route.request().method() === "POST", "Stray trust must use report dismiss");
    strayTrusted = true; await route.fulfill({ json: strayTrust });
  });
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByRole("radio", { name: /未信任.*1/ }).click();
  const strayCard = page.locator(".optimization-report").filter({ hasText: "疑似游离程序：pwsh" });
  await strayCard.waitFor({ state: "visible", timeout: 5000 });
  assert((await strayCard.textContent()).includes("15") && !(await strayCard.textContent()).includes("BACKEND_PROSE"),
    "Stray report must render localized structured evidence");
  strayEvaluationFailed = true;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByText("无法完成报告评估，原因：IOException: fixture failure。", { exact: true }).waitFor();
  assert(await strayCard.isVisible(), "Evaluation failure discarded the committed report row");
  assert(await strayCard.getByRole("button", { name: "忽略", exact: true }).isEnabled(),
    "Unavailable evaluation must not block trusting a committed report");
  strayEvaluationFailed = false;
  await page.getByRole("button", { name: "刷新", exact: true }).click();
  await page.getByText("无法完成报告评估，原因：IOException: fixture failure。", { exact: true }).waitFor({ state: "detached" });
  await strayCard.getByRole("button", { name: "详细信息", exact: true }).click();
  const detail = page.getByRole("dialog");
  await detail.getByText('pwsh -File "stray-check.ps1"', { exact: true }).waitFor();
  await detail.getByText("424242", { exact: true }).waitFor();
  await detail.getByRole("button", { name: "关闭", exact: true }).last().click();
  await strayCard.getByRole("button", { name: "忽略", exact: true }).click();
  await page.getByRole("radio", { name: /已信任.*1/ }).click();
  await page.locator(".optimization-trusted-targets").getByText("pwsh", { exact: true }).waitFor();
  assert(strayTrusted, "Stray process target was not trusted");
  await page.unroute("**/api/optimization/reports");
  await page.unroute("**/api/optimization/reports/refresh");
  const availabilityLanguages = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const app = await loadAppCopy(option.id);
    const settingsLocale = async route => {
      const response = await route.fetch();
      const result = await response.json();
      result.settings.appearance.language = option.id;
      await route.fulfill({ json: result });
    };
    await page.route("**/api/settings/app", settingsLocale);
    await postScenario({ optimizationReports: "ready" });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    await page.getByRole("navigation").getByRole("button", { name: app.page.optimization, exact: true }).click();
    await page.getByText(app.optimization.empty, { exact: true }).waitFor();
    for (const [state, message] of [
      ["evaluation-failed", app.optimization.evaluationFailed("IOException")],
      ["partial", app.optimization.rulesUnavailable(1)]
    ]) {
      await postScenario({ optimizationReports: state });
      await page.getByRole("button", { name: app.optimization.refresh, exact: true }).click();
      await page.getByText(message, { exact: true }).waitFor();
      assert(await page.getByText(app.optimization.empty, { exact: true }).count() === 0,
        `${option.id} ${state} incorrectly claimed no problems`);
    }
    availabilityLanguages.push(option.id);
    await page.unroute("**/api/settings/app", settingsLocale);
  }
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    availabilityLanguages,
    initialSourceGetCount: initialReadCount,
    manualRefreshPostCount: 2,
    statusFollowsBackendPush: true,
    faultClosedNoticeShown: true,
    refreshReadsSchedulingStatus: true,
    failedRefreshPreservedLastGood: true,
    retiredRecorderStateAbsent: true,
    strayStructuredEvidenceAndInstanceTrust: true,
    pageErrors: failures
  };
}
