import { spawn, execFileSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import os from "node:os";
import { basename, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createServer } from "node:net";
import { gzipSync } from "node:zlib";

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const repositoryRoot = resolve(projectRoot, "../../..");
const artifactBase = resolve(repositoryRoot, ".artifacts/tests/frontend-performance");
const quick = process.argv.includes("--quick");
const profile = Object.freeze(quick ? {
  id: "frontend-framework-mock-dev-v1",
  coldStarts: 2,
  routeCycles: 3,
  stableSeconds: 10,
  segmentCounts: [1, 333],
  tableRowCounts: [1, 1000],
  samplesPerCardinality: 2,
  memoryWarmupCycles: 2,
  memoryCycles: 5
} : {
  id: "frontend-framework-mock-v1",
  coldStarts: 10,
  routeCycles: 30,
  stableSeconds: 60,
  segmentCounts: [1, 100, 333, 1000],
  tableRowCounts: [1, 80, 500, 1000],
  samplesPerCardinality: 5,
  memoryWarmupCycles: 5,
  memoryCycles: 50
});
const thresholds = Object.freeze({
  routeSecondFrameP95Ms: 100,
  routeSecondFrameMaximumMs: 250,
  longTaskThresholdMs: 50,
  severeFrameIntervalMs: 100,
  slowFrameIntervalMs: 50,
  maximumTableDomRows: 60,
  maximumHeapGrowthBytes: 8 * 1024 * 1024,
  maximumHeapGrowthRatio: 0.10,
  maximumDomGrowthNodes: 32,
  maximumDomGrowthRatio: 0.05
});
const runId = `${formatRunTimestamp(new Date())}-${profile.id}-${randomBytes(6).toString("hex")}`;
const runRoot = resolve(artifactBase, runId);
await mkdir(artifactBase, { recursive: true });
await mkdir(runRoot, { recursive: false });

const scenario = {
  schemaVersion: 1,
  profile,
  thresholds,
  viewport: { width: 1280, height: 800 },
  runtimeEventCapacity: 50_000,
  pageOrder: ["components", "optimization", "details", "settings", "monitor"]
};
await writeJson("scenario.json", scenario);

let browser;
let server;
let serverOutput = "";
let environment = collectStaticEnvironment();
let completed = false;

try {
  const assets = await collectFrontendAssets();
  await writeJson("assets.json", assets);
  const port = await reservePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  server = spawn(process.execPath, [resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
    cwd: projectRoot,
    env: { ...process.env, RM_FRONTEND_HARNESS_PORT: String(port) },
    stdio: ["ignore", "pipe", "pipe"]
  });
  server.stdout.on("data", (chunk) => { serverOutput += chunk; });
  server.stderr.on("data", (chunk) => { serverOutput += chunk; });
  await waitForServer(baseUrl, server, () => serverOutput);
  await setScenario(baseUrl, {
    optimizationRuntime: true,
    animations: "normal",
    resourceSegmentCount: 1,
    resourceTableRowCount: 1,
    resourceBreakdownGeneration: 1
  });

  const { chromium } = await loadPlaywright();
  browser = await launchBrowser(chromium);
  environment = {
    ...environment,
    browserVersion: browser.version(),
    baseUrl
  };
  await writeJson("environment.json", environment);

  const diagnostics = createDiagnostics();
  const coldStarts = await measureColdStarts(browser, baseUrl, diagnostics);
  const context = await createMeasuredContext(browser, diagnostics);
  const page = await context.newPage();
  const session = await context.newCDPSession(page);
  await session.send("Performance.enable");
  await session.send("HeapProfiler.enable");
  await openApplication(page, baseUrl);

  const routeResult = await measureRoutes(page);
  const cardinalityResult = await measureSegmentCardinalities(page, session, baseUrl);
  const tableResult = await measureTableCardinalities(page, session, baseUrl);
  const stableResult = await measureStableMonitor(page, session, baseUrl);
  const memoryResult = await measureMemoryCycles(page, session);
  const finalRuntime = await readRuntimeSnapshot(page);
  const requestTelemetry = await fetchJson(`${baseUrl}/__test/requests`);

  await context.close();
  const runtimeEvidence = {
    schemaVersion: 1,
    coldStarts: coldStarts.runtime,
    route: routeResult.runtime,
    segmentCardinality: cardinalityResult.runtime,
    tableCardinality: tableResult.runtime,
    stable: stableResult.runtime,
    memory: memoryResult.runtime,
    final: finalRuntime
  };
  const browserMetrics = {
    schemaVersion: 1,
    coldStarts: coldStarts.measurements,
    route: routeResult.measurements,
    segmentCardinality: cardinalityResult.measurements,
    tableCardinality: tableResult.measurements,
    stable: stableResult.measurements,
    memory: memoryResult.measurements
  };
  const network = {
    schemaVersion: 1,
    diagnostics: diagnostics.network,
    consoleErrors: diagnostics.consoleErrors,
    pageErrors: diagnostics.pageErrors,
    serverTelemetry: requestTelemetry
  };
  await writeJson("runtime-events.json", runtimeEvidence);
  await writeJson("browser-metrics.json", browserMetrics);
  await writeJson("network.json", network);
  await writeJson("process-metrics.json", {
    schemaVersion: 1,
    scope: "mock-production-bundle",
    collected: false,
    reason: "Headless browser process totals are not attributed to the product; CDP main-thread metrics are authoritative for the mock profile and exact process metrics belong to the live Native UI profile."
  });

  const assessment = assess({
    coldStarts,
    routeResult,
    cardinalityResult,
    tableResult,
    stableResult,
    memoryResult,
    diagnostics,
    requestTelemetry,
    assets
  });
  await writeJson("assessment.json", assessment);
  await writeFile(resolve(runRoot, "report.md"), renderReport(assessment), "utf8");
  await sealEvidence();
  completed = true;
  process.stdout.write(`${JSON.stringify({ runRoot, runId, passed: assessment.passed, assessment }, null, 2)}\n`);
  if (!assessment.passed) {
    process.exitCode = 1;
  }
} catch (error) {
  await writeJson("failure.json", {
    schemaVersion: 1,
    failedAt: new Date().toISOString(),
    error: error instanceof Error
      ? { name: error.name, message: error.message, stack: error.stack ?? null }
      : { name: "Unknown", message: String(error), stack: null },
    serverOutput
  });
  await sealEvidence();
  throw error;
} finally {
  await browser?.close();
  if (server) {
    await stopChildProcess(server);
  }
  if (!completed) {
    process.stderr.write(`Frontend performance attempt preserved at ${runRoot}\n`);
  }
}

async function measureColdStarts(browserHandle, baseUrl, diagnostics) {
  const measurements = [];
  const runtime = [];
  for (let index = 0; index < profile.coldStarts; index += 1) {
    const context = await createMeasuredContext(browserHandle, diagnostics);
    const page = await context.newPage();
    await openApplication(page, baseUrl);
    const result = await page.evaluate(() => {
      const navigation = performance.getEntriesByType("navigation")[0];
      const paint = performance.getEntriesByType("paint");
      const probe = globalThis.__rmFrontendPerformanceProbe;
      return {
        navigation: navigation ? {
          responseEnd: navigation.responseEnd,
          domContentLoaded: navigation.domContentLoadedEventEnd,
          loadEventEnd: navigation.loadEventEnd
        } : null,
        firstContentfulPaint: paint.find((entry) => entry.name === "first-contentful-paint")?.startTime ?? null,
        milestones: probe?.milestones ?? null
      };
    });
    measurements.push({ iteration: index + 1, ...result });
    runtime.push(await readRuntimeSnapshot(page));
    await context.close();
  }
  return { measurements, runtime };
}

async function measureRoutes(page) {
  await resetRuntimeSnapshot(page);
  const measurements = [];
  for (let cycle = 0; cycle < profile.routeCycles; cycle += 1) {
    for (const pageId of scenario.pageOrder) {
      measurements.push({ cycle: cycle + 1, ...(await navigateToPage(page, pageId)) });
    }
  }
  return { measurements, runtime: await readRuntimeSnapshot(page) };
}

async function measureSegmentCardinalities(page, session, baseUrl) {
  await navigateToPage(page, "monitor");
  const measurements = [];
  const runtime = [];
  let generation = 20;
  for (const segmentCount of profile.segmentCounts) {
    await resetRuntimeSnapshot(page);
    const before = await captureCdpMetrics(session);
    const publicationBaseline = await readResourcePublicationCount(baseUrl, "bars");
    await setScenario(baseUrl, {
      resourceSegmentCount: segmentCount,
      resourceBreakdownGeneration: ++generation
    });
    await waitForSegmentCount(page, segmentCount);
    await waitForResourcePublications(
      baseUrl,
      "bars",
      publicationBaseline + profile.samplesPerCardinality);
    await activateResourceInteraction(page);
    const after = await captureCdpMetrics(session);
    const dom = await readResourceDom(page);
    const snapshot = await readRuntimeSnapshot(page);
    measurements.push({ segmentCount, dom, cdpDelta: subtractMetrics(after.performance, before.performance) });
    runtime.push({ segmentCount, snapshot });
  }
  return { measurements, runtime };
}

async function measureTableCardinalities(page, session, baseUrl) {
  const measurements = [];
  const runtime = [];
  let generation = 100;
  for (const rowCount of profile.tableRowCounts) {
    await resetRuntimeSnapshot(page);
    const before = await captureCdpMetrics(session);
    await setScenario(baseUrl, {
      resourceTableRowCount: rowCount,
      resourceBreakdownGeneration: ++generation
    });
    await page.waitForFunction((expected) =>
      document.querySelector(".resource-table-frame")?.getAttribute("aria-rowcount") === String(expected + 1),
    rowCount, { timeout: 20_000 });
    const after = await captureCdpMetrics(session);
    const dom = await readResourceDom(page);
    const snapshot = await readRuntimeSnapshot(page);
    measurements.push({ rowCount, dom, cdpDelta: subtractMetrics(after.performance, before.performance) });
    runtime.push({ rowCount, snapshot });
  }
  return { measurements, runtime };
}

async function measureStableMonitor(page, session, baseUrl) {
  await setScenario(baseUrl, {
    resourceSegmentCount: 333,
    resourceTableRowCount: 1000,
    resourceBreakdownGeneration: 200
  });
  await waitForSegmentCount(page, 333);
  await page.waitForFunction(() =>
    document.querySelector(".resource-table-frame")?.getAttribute("aria-rowcount") === "1001",
  null, { timeout: 20_000 });
  await resetRuntimeSnapshot(page);
  await page.evaluate(() => globalThis.__rmFrontendPerformanceProbe?.startFrames());
  const before = await captureCdpMetrics(session);
  const startedWallClockMs = Date.now();
  const performanceTimeOrigin = await page.evaluate(() => performance.timeOrigin);
  const startedAt = await page.evaluate(() => performance.now());
  await delay(profile.stableSeconds * 1000);
  const finishedAt = await page.evaluate(() => performance.now());
  const finishedWallClockMs = Date.now();
  const frameIntervals = await page.evaluate(() =>
    globalThis.__rmFrontendPerformanceProbe?.stopFrames() ?? []);
  const after = await captureCdpMetrics(session);
  const runtime = await readRuntimeSnapshot(page);
  const requestTelemetry = await fetchJson(`${baseUrl}/__test/requests`);
  return {
    measurements: {
      startedAt,
      finishedAt,
      startedWallClockMs,
      finishedWallClockMs,
      performanceTimeOrigin,
      elapsedMs: finishedAt - startedAt,
      frameIntervals,
      cdpDelta: subtractMetrics(after.performance, before.performance),
      domBefore: before.dom,
      domAfter: after.dom,
      requestTelemetry
    },
    runtime
  };
}

async function measureMemoryCycles(page, session) {
  await navigateToPage(page, "monitor");
  const warmupRouteMeasurements = [];
  for (let cycle = 0; cycle < profile.memoryWarmupCycles; cycle += 1) {
    for (const pageId of scenario.pageOrder) {
      warmupRouteMeasurements.push({
        cycle: cycle + 1,
        ...(await navigateToPage(page, pageId))
      });
    }
  }
  await resetRuntimeSnapshot(page);
  const before = await captureCollectedMemory(session);
  const routeMeasurements = [];
  for (let cycle = 0; cycle < profile.memoryCycles; cycle += 1) {
    for (const pageId of scenario.pageOrder) {
      routeMeasurements.push({ cycle: cycle + 1, ...(await navigateToPage(page, pageId)) });
    }
  }
  const after = await captureCollectedMemory(session);
  return {
    measurements: { before, after, warmupRouteMeasurements, routeMeasurements },
    runtime: await readRuntimeSnapshot(page)
  };
}

function assess(input) {
  const routeDurations = input.routeResult.measurements.map((item) => item.secondFrameMs);
  const routeSummary = summarize(routeDurations);
  const stableEvents = input.stableResult.runtime.events ?? [];
  const longTasks = stableEvents.filter((event) => event.kind === "long-task");
  const requestEvents = stableEvents.filter((event) => event.kind === "request");
  const monitorSubscription = input.stableResult.measurements.requestTelemetry["/api/resource-monitor/subscribe"];
  const monitorSnapshot = input.stableResult.measurements.requestTelemetry["/api/resource-monitor/snapshot"];
  const stablePublicationSeries = Object.values(
    monitorSubscription?.publicationTimesByQuery ?? {})
    .map((times) => times.filter((time) =>
      time >= input.stableResult.measurements.startedWallClockMs
      && time <= input.stableResult.measurements.finishedWallClockMs));
  const stablePublicationCount = stablePublicationSeries
    .reduce((sum, times) => sum + times.length, 0);
  const consecutiveSampleLongTasks = Math.max(
    0,
    ...stablePublicationSeries.map((times) => maximumConsecutiveAffectedSamples(
      times.map((time) => ({
        finishedAt: time - input.stableResult.measurements.performanceTimeOrigin
      })),
      longTasks)));
  const frameIntervals = input.stableResult.measurements.frameIntervals;
  const frameSummary = summarize(frameIntervals);
  const stableElapsedSeconds = input.stableResult.measurements.elapsedMs / 1000;
  const stableCdp = input.stableResult.measurements.cdpDelta;
  const proxyCounts = input.cardinalityResult.measurements.map((item) => item.dom.interactionProxyCount);
  const tooltipCounts = input.cardinalityResult.measurements.map((item) => item.dom.tooltipCount);
  const tableDomRows = input.tableResult.measurements.map((item) => item.dom.tableDomRowCount);
  const memoryBefore = input.memoryResult.measurements.before;
  const memoryAfter = input.memoryResult.measurements.after;
  const heapGrowthBytes = memoryAfter.heap.usedSize - memoryBefore.heap.usedSize;
  const heapGrowthRatio = memoryBefore.heap.usedSize > 0
    ? heapGrowthBytes / memoryBefore.heap.usedSize
    : 0;
  const domGrowthNodes = memoryAfter.dom.nodes - memoryBefore.dom.nodes;
  const domGrowthRatio = memoryBefore.dom.nodes > 0
    ? domGrowthNodes / memoryBefore.dom.nodes
    : 0;
  const sourceSingleFlightViolations = findSourceSingleFlightViolations([
    ...input.cardinalityResult.runtime.map((item) => item.snapshot),
    ...input.tableResult.runtime.map((item) => item.snapshot),
    input.stableResult.runtime,
    input.memoryResult.runtime
  ]);
  const monitorQueryConcurrency = Object.values(
    monitorSubscription?.maxInFlightByQuery ?? {}).map(Number);
  const droppedEventCount = sumDropped([
    ...input.coldStarts.runtime,
    input.routeResult.runtime,
    ...input.cardinalityResult.runtime.map((item) => item.snapshot),
    ...input.tableResult.runtime.map((item) => item.snapshot),
    input.stableResult.runtime,
    input.memoryResult.runtime
  ]);
  const unexpectedRequestFailures = input.diagnostics.network.requestFailures.filter(
    (item) => !String(item.errorText).includes("ERR_ABORTED"));
  const gates = {
    routeSecondFrameP95: gate(
      routeSummary.p95 <= thresholds.routeSecondFrameP95Ms,
      routeSummary.p95,
      `<= ${thresholds.routeSecondFrameP95Ms} ms`),
    routeSecondFrameMaximum: gate(
      routeSummary.max <= thresholds.routeSecondFrameMaximumMs,
      routeSummary.max,
      `<= ${thresholds.routeSecondFrameMaximumMs} ms`),
    recurringMonitorLongTask: gate(
      consecutiveSampleLongTasks < 3,
      consecutiveSampleLongTasks,
      "< 3 consecutive samples affected"),
    sourceSingleFlight: gate(
      sourceSingleFlightViolations.length === 0
        && (monitorSubscription?.started ?? 0) > 0
        && monitorQueryConcurrency.every((count) => count <= 1)
        && (monitorSnapshot?.started ?? 0) === 0,
      {
        sourceSingleFlightViolations,
        subscriptionStarted: monitorSubscription?.started ?? 0,
        maximumInFlightByQuery: monitorSubscription?.maxInFlightByQuery ?? {},
        snapshotRequests: monitorSnapshot?.started ?? 0
      },
      "one active push stream per canonical query and no snapshot GET"),
    boundedSegmentDom: gate(
      Math.max(...proxyCounts) - Math.min(...proxyCounts) <= 2
        && Math.max(...tooltipCounts) - Math.min(...tooltipCounts) <= 2,
      { proxyCounts, tooltipCounts },
      "node counts do not scale with 1..1000 segments"),
    boundedTableDom: gate(
      Math.max(...tableDomRows) <= thresholds.maximumTableDomRows,
      tableDomRows,
      `<= ${thresholds.maximumTableDomRows} DOM rows`),
    settledHeapGrowth: gate(
      heapGrowthBytes <= Math.max(
        thresholds.maximumHeapGrowthBytes,
        memoryBefore.heap.usedSize * thresholds.maximumHeapGrowthRatio),
      { heapGrowthBytes, heapGrowthRatio },
      "<= max(8 MiB, 10%)"),
    settledDomGrowth: gate(
      domGrowthNodes <= Math.max(
        thresholds.maximumDomGrowthNodes,
        memoryBefore.dom.nodes * thresholds.maximumDomGrowthRatio),
      { domGrowthNodes, domGrowthRatio },
      "<= max(32 nodes, 5%)"),
    diagnosticsIntegrity: gate(
      input.diagnostics.consoleErrors.length === 0
        && input.diagnostics.pageErrors.length === 0
        && input.diagnostics.network.badResponses.length === 0
        && unexpectedRequestFailures.length === 0,
      {
        consoleErrors: input.diagnostics.consoleErrors.length,
        pageErrors: input.diagnostics.pageErrors.length,
        badResponses: input.diagnostics.network.badResponses.length,
        unexpectedRequestFailures: unexpectedRequestFailures.length
      },
      "all zero"),
    runtimeBufferIntegrity: gate(droppedEventCount === 0, droppedEventCount, "0 dropped events")
  };
  const entryFiles = input.assets.files.filter((file) =>
    input.assets.entryScripts.includes(`/${file.path}`));
  const performanceMonitorFiles = input.assets.files.filter((file) =>
    file.path.includes("FrontendPerformanceMonitor-"));
  return {
    schemaVersion: 1,
    profileId: profile.id,
    runId,
    passed: Object.values(gates).every((item) => item.passed),
    claimBoundary: "SolidJS/Vite application main-thread cadence and Native browser behavior; not display-completion FPS, dropped frames, GPU-ready time, game performance, scheduler benefit, or photon latency.",
    bundle: {
      buildIdentity: input.assets.buildIdentity,
      entryRawBytes: entryFiles.reduce((sum, file) => sum + file.bytes, 0),
      entryGzipBytes: entryFiles.reduce((sum, file) => sum + file.gzipBytes, 0),
      allAssetRawBytes: input.assets.files.reduce((sum, file) => sum + file.bytes, 0),
      performanceMonitorLazyChunkRawBytes: performanceMonitorFiles.reduce((sum, file) => sum + file.bytes, 0),
      performanceMonitorLazyChunkGzipBytes: performanceMonitorFiles.reduce((sum, file) => sum + file.gzipBytes, 0)
    },
    startup: summarizeStartup(input.coldStarts.measurements),
    routeSecondFrameMs: routeSummary,
    stableMonitor: {
      elapsedMs: input.stableResult.measurements.elapsedMs,
      pushPublicationSamples: stablePublicationCount,
      pushPublicationSamplesByQuery: stablePublicationSeries.map((times) => times.length),
      longTaskCount: longTasks.length,
      longTaskDurationMs: summarize(longTasks.map((item) => item.durationMs)),
      maximumConsecutiveSamplesAffectedByLongTasks: consecutiveSampleLongTasks,
      frameIntervalsMs: frameSummary,
      frameIntervalsOver50Ms: frameIntervals.filter((value) => value > thresholds.slowFrameIntervalMs).length,
      frameIntervalsOver100Ms: frameIntervals.filter((value) => value > thresholds.severeFrameIntervalMs).length,
      requestDurationMs: summarize(requestEvents.map((item) => item.durationMs)),
      requestOutcomes: countBy(requestEvents, (item) => item.outcome),
      mainThreadTaskPercent: stableElapsedSeconds > 0
        ? stableCdp.TaskDuration / stableElapsedSeconds * 100
        : null,
      scriptPercent: stableElapsedSeconds > 0
        ? stableCdp.ScriptDuration / stableElapsedSeconds * 100
        : null,
      layoutPercent: stableElapsedSeconds > 0
        ? stableCdp.LayoutDuration / stableElapsedSeconds * 100
        : null,
      recalcStylePercent: stableElapsedSeconds > 0
        ? stableCdp.RecalcStyleDuration / stableElapsedSeconds * 100
        : null,
      cdpDelta: stableCdp
    },
    cardinality: {
      segments: input.cardinalityResult.measurements,
      table: input.tableResult.measurements
    },
    memory: {
      before: memoryBefore,
      after: memoryAfter,
      heapGrowthBytes,
      heapGrowthRatio,
      domGrowthNodes,
      domGrowthRatio
    },
    gates
  };
}

function renderReport(assessment) {
  const gateRows = Object.entries(assessment.gates)
    .map(([name, value]) => `| ${name} | ${value.passed ? "PASS" : "FAIL"} | ${escapeCell(JSON.stringify(value.actual))} | ${escapeCell(value.expected)} |`)
    .join("\n");
  return `# 新前端框架性能测试报告\n\n- Run: \`${assessment.runId}\`\n- Profile: \`${assessment.profileId}\`\n- Result: **${assessment.passed ? "PASS" : "FAIL"}**\n- Scope: ${assessment.claimBoundary}\n\n## 关键统计\n\n- 默认入口 bundle：${formatBytes(assessment.bundle.entryRawBytes)} raw / ${formatBytes(assessment.bundle.entryGzipBytes)} gzip；测试启用时才加载的性能监控 chunk：${formatBytes(assessment.bundle.performanceMonitorLazyChunkRawBytes)} raw / ${formatBytes(assessment.bundle.performanceMonitorLazyChunkGzipBytes)} gzip。\n- 页面选择至第二个绘制帧：p50 ${formatNumber(assessment.routeSecondFrameMs.p50)} ms，p95 ${formatNumber(assessment.routeSecondFrameMs.p95)} ms，p99 ${formatNumber(assessment.routeSecondFrameMs.p99)} ms，max ${formatNumber(assessment.routeSecondFrameMs.max)} ms。\n- 稳定 Monitor：${formatNumber(assessment.stableMonitor.elapsedMs / 1000)} s，${assessment.stableMonitor.pushPublicationSamples} 个资源推送发布样本，${assessment.stableMonitor.longTaskCount} 个 >=50 ms long task；主线程 TaskDuration 占窗口 ${formatNumber(assessment.stableMonitor.mainThreadTaskPercent)}%，Script ${formatNumber(assessment.stableMonitor.scriptPercent)}%，Layout ${formatNumber(assessment.stableMonitor.layoutPercent)}%，RecalcStyle ${formatNumber(assessment.stableMonitor.recalcStylePercent)}%。\n- request 耗时：p50 ${formatNumber(assessment.stableMonitor.requestDurationMs.p50)} ms，p95 ${formatNumber(assessment.stableMonitor.requestDurationMs.p95)} ms，max ${formatNumber(assessment.stableMonitor.requestDurationMs.max)} ms。\n- rAF 主线程间隔：p95 ${formatNumber(assessment.stableMonitor.frameIntervalsMs.p95)} ms，p99 ${formatNumber(assessment.stableMonitor.frameIntervalsMs.p99)} ms，max ${formatNumber(assessment.stableMonitor.frameIntervalsMs.max)} ms；>50 ms ${assessment.stableMonitor.frameIntervalsOver50Ms}，>100 ms ${assessment.stableMonitor.frameIntervalsOver100Ms}。\n- 回收后 heap 变化：${formatBytes(assessment.memory.heapGrowthBytes)}（${formatNumber(assessment.memory.heapGrowthRatio * 100)}%）；DOM node 变化 ${assessment.memory.domGrowthNodes}（${formatNumber(assessment.memory.domGrowthRatio * 100)}%）。\n\n## 独立门\n\n| Gate | Result | Actual | Expected |\n| --- | --- | --- | --- |\n${gateRows}\n\n## 结论边界\n\n本报告测量应用 Present 之前的浏览器主线程、推送发布、DOM/heap 和 production bundle 行为。它不是显示完成 FPS、显示掉帧、GPU-ready、游戏性能、调度收益或光子延迟证据。\n`;
}

async function createMeasuredContext(browserHandle, diagnostics) {
  const context = await browserHandle.newContext({ viewport: scenario.viewport });
  await context.addInitScript(({ capacity }) => {
    globalThis.__resourceManagerFrontendPerformanceBootstrap = {
      schemaVersion: 1,
      enabled: true,
      capacity
    };
    const probe = {
      milestones: {},
      frameIntervals: [],
      frameActive: false,
      lastFrameAt: null,
      startFrames() {
        this.frameIntervals = [];
        this.frameActive = true;
        this.lastFrameAt = null;
        requestAnimationFrame((at) => this.onFrame(at));
      },
      onFrame(at) {
        if (!this.frameActive) return;
        if (this.lastFrameAt !== null) this.frameIntervals.push(at - this.lastFrameAt);
        this.lastFrameAt = at;
        requestAnimationFrame((nextAt) => this.onFrame(nextAt));
      },
      stopFrames() {
        this.frameActive = false;
        return [...this.frameIntervals];
      }
    };
    globalThis.__rmFrontendPerformanceProbe = probe;
    const scan = () => {
      const at = performance.now();
      if (probe.milestones.firstMetricAt === undefined
        && document.querySelector(".metric-value")) {
        probe.milestones.firstMetricAt = at;
      }
      if (probe.milestones.firstResourceCanvasAt === undefined
        && document.querySelector("[data-resource-paint-plane]")) {
        probe.milestones.firstResourceCanvasAt = at;
      }
      if (probe.milestones.firstResourceTableAt === undefined
        && document.querySelector(".resource-table-row")) {
        probe.milestones.firstResourceTableAt = at;
      }
      if (Object.keys(probe.milestones).length === 3) observer.disconnect();
    };
    const observer = new MutationObserver(scan);
    observer.observe(document, { subtree: true, childList: true, characterData: true });
    addEventListener("DOMContentLoaded", scan, { once: true });
  }, { capacity: scenario.runtimeEventCapacity });
  context.on("page", (page) => attachDiagnostics(page, diagnostics));
  return context;
}

function attachDiagnostics(page, diagnostics) {
  page.on("console", (message) => {
    if (message.type() === "error") diagnostics.consoleErrors.push({ text: message.text(), url: page.url() });
  });
  page.on("pageerror", (error) => diagnostics.pageErrors.push({ message: String(error), url: page.url() }));
  page.on("requestfailed", (request) => diagnostics.network.requestFailures.push({
    url: redactUrl(request.url()),
    method: request.method(),
    errorText: request.failure()?.errorText ?? "unknown"
  }));
  page.on("response", (response) => {
    if (new URL(response.url()).pathname.startsWith("/api/") && !response.ok()) {
      diagnostics.network.badResponses.push({
        url: redactUrl(response.url()),
        status: response.status(),
        method: response.request().method()
      });
    }
  });
}

async function openApplication(page, baseUrl) {
  await page.goto(`${baseUrl}/`, { waitUntil: "load", timeout: 30_000 });
  await page.waitForFunction(() => document.body.dataset.page === "monitor", null, { timeout: 15_000 });
  await page.locator(".metric-value").first().waitFor({ state: "visible", timeout: 15_000 });
  await page.locator("[data-resource-paint-plane]").first().waitFor({ state: "visible", timeout: 15_000 });
  await page.locator(".resource-table-frame").waitFor({ state: "visible", timeout: 15_000 });
  await page.waitForFunction(() => Boolean(globalThis.__resourceManagerFrontendPerformance), null, { timeout: 5_000 });
  await page.evaluate(() => new Promise((resolveFrame) =>
    requestAnimationFrame(() => requestAnimationFrame(resolveFrame))));
}

async function navigateToPage(page, pageId) {
  if (await page.evaluate((expected) => document.body.dataset.page === expected, pageId)) {
    return { pageId, secondFrameMs: 0, routeId: null, noOp: true };
  }
  const beforeRouteId = await page.evaluate(() => {
    const events = globalThis.__resourceManagerFrontendPerformance?.snapshot().events ?? [];
    return Math.max(0, ...events.filter((event) => event.kind === "route").map((event) => event.routeId));
  });
  await page.getByRole("button", { name: pageLabel(pageId), exact: true }).click();
  await page.waitForFunction((expected) => document.body.dataset.page === expected, pageId, { timeout: 10_000 });
  await page.waitForFunction((routeId) => {
    const events = globalThis.__resourceManagerFrontendPerformance?.snapshot().events ?? [];
    return events.some((event) => event.kind === "route"
      && event.routeId > routeId
      && event.phase === "frame-2");
  }, beforeRouteId, { timeout: 10_000 });
  return page.evaluate((routeId) => {
    const events = globalThis.__resourceManagerFrontendPerformance.snapshot().events;
    const event = [...events].reverse().find((candidate) => candidate.kind === "route"
      && candidate.routeId > routeId
      && candidate.phase === "frame-2");
    return {
      pageId: event.page,
      secondFrameMs: event.elapsedFromIntentMs,
      routeId: event.routeId,
      noOp: false
    };
  }, beforeRouteId);
}

function pageLabel(pageId) {
  switch (pageId) {
    case "monitor": return "监视控制台";
    case "components": return "组件与软件";
    case "optimization": return "性能优化";
    case "details": return "详细信息";
    case "settings": return "设置";
    default: throw new Error(`Unknown page id: ${pageId}`);
  }
}

async function waitForSegmentCount(page, count) {
  try {
    await page.waitForFunction((expected) => {
      const canvas = document.querySelector(
        ".resource-bar-track[data-metric-id='cpu.usage'] [data-resource-paint-plane]");
      return canvas?.getAttribute("data-resource-segment-count") === String(expected);
    }, count, { timeout: 20_000 });
  } catch (error) {
    const observed = await page.evaluate(() =>
      [...document.querySelectorAll(".resource-bar-track[data-metric-id]")].map((track) => ({
        metricId: track.getAttribute("data-metric-id"),
        segmentCount: track.querySelector("[data-resource-paint-plane]")
          ?.getAttribute("data-resource-segment-count") ?? null
      })));
    throw new Error(
      `Timed out waiting for CPU segment count ${count}; observed ${JSON.stringify(observed)}.`,
      { cause: error });
  }
}

async function waitForResourcePublications(baseUrl, scope, expected) {
  const deadline = Date.now() + Math.max(20_000, profile.samplesPerCardinality * 3_000);
  let actual = await readResourcePublicationCount(baseUrl, scope);
  while (actual < expected && Date.now() < deadline) {
    await delay(50);
    actual = await readResourcePublicationCount(baseUrl, scope);
  }
  if (actual < expected) {
    throw new Error(
      `Timed out waiting for ${scope} push publications: expected ${expected}, received ${actual}.`);
  }
}

async function readResourcePublicationCount(baseUrl, scope) {
  const telemetry = await fetchJson(`${baseUrl}/__test/requests`);
  const publications = telemetry["/api/resource-monitor/subscribe"]?.publicationsByQuery ?? {};
  return Object.entries(publications)
    .filter(([query]) => new URLSearchParams(query).get("scope") === scope)
    .reduce((sum, [, count]) => sum + Number(count), 0);
}

async function activateResourceInteraction(page) {
  const track = page.locator(".resource-bar-track").first();
  const box = await track.boundingBox();
  if (!box) throw new Error("Resource bar track has no interaction geometry.");
  await page.mouse.move(box.x + Math.max(2, box.width * 0.05), box.y + box.height / 2);
  await page.waitForFunction(() =>
    document.querySelectorAll(".resource-segment-interaction-proxy").length === 1,
  null, { timeout: 5_000 });
}

async function readResourceDom(page) {
  return page.evaluate(() => ({
    totalNodes: document.getElementsByTagName("*").length,
    paintPlaneCount: document.querySelectorAll("[data-resource-paint-plane]").length,
    paintedSegmentCounts: [...document.querySelectorAll("[data-resource-paint-plane]")]
      .map((node) => Number(node.getAttribute("data-resource-segment-count"))),
    interactionLayerCount: document.querySelectorAll(".resource-segment-interaction-layer").length,
    interactionProxyCount: document.querySelectorAll(".resource-segment-interaction-proxy").length,
    tooltipCount: document.querySelectorAll(".resource-tooltip, [role='tooltip']").length,
    tableAriaRowCount: Number(document.querySelector(".resource-table-frame")?.getAttribute("aria-rowcount")),
    tableDomRowCount: document.querySelectorAll(".resource-table-row").length
  }));
}

async function readRuntimeSnapshot(page) {
  return page.evaluate(() => globalThis.__resourceManagerFrontendPerformance?.snapshot() ?? null);
}

async function resetRuntimeSnapshot(page) {
  await page.evaluate(() => globalThis.__resourceManagerFrontendPerformance?.reset());
}

async function captureCdpMetrics(session) {
  const [{ metrics }, dom] = await Promise.all([
    session.send("Performance.getMetrics"),
    session.send("Memory.getDOMCounters")
  ]);
  return { performance: Object.fromEntries(metrics.map((item) => [item.name, item.value])), dom };
}

async function captureCollectedMemory(session) {
  await session.send("HeapProfiler.collectGarbage");
  await delay(100);
  const [heap, dom] = await Promise.all([
    session.send("Runtime.getHeapUsage"),
    session.send("Memory.getDOMCounters")
  ]);
  return { heap, dom };
}

function subtractMetrics(after, before) {
  const names = [
    "TaskDuration",
    "ScriptDuration",
    "LayoutDuration",
    "RecalcStyleDuration",
    "LayoutCount",
    "RecalcStyleCount",
    "JSHeapUsedSize",
    "Nodes",
    "Documents"
  ];
  return Object.fromEntries(names.map((name) => [name, (after[name] ?? 0) - (before[name] ?? 0)]));
}

function findSourceSingleFlightViolations(snapshots) {
  const violations = [];
  for (const snapshot of snapshots.filter(Boolean)) {
    const active = new Map();
    for (const event of snapshot.events.filter((item) => item.kind === "source")) {
      const key = `${event.sourceKey}\u0000${event.generation}\u0000${event.attempt}`;
      if (event.phase === "started") {
        const count = (active.get(event.sourceKey) ?? 0) + 1;
        active.set(event.sourceKey, count);
        if (count > 1) violations.push({ sourceKey: event.sourceKey, sequence: event.sequence, count });
        active.set(key, event.sourceKey);
      } else if (active.has(key)) {
        const sourceKey = active.get(key);
        active.set(sourceKey, Math.max(0, (active.get(sourceKey) ?? 1) - 1));
        active.delete(key);
      }
    }
  }
  return violations;
}

function maximumConsecutiveAffectedSamples(samples, longTasks) {
  let current = 0;
  let maximum = 0;
  for (const sample of samples) {
    const affected = longTasks.some((task) =>
      task.at <= sample.finishedAt + 250
      && task.at + task.durationMs >= sample.finishedAt - 250);
    current = affected ? current + 1 : 0;
    maximum = Math.max(maximum, current);
  }
  return maximum;
}

function summarizeStartup(measurements) {
  return {
    domContentLoadedMs: summarize(measurements.map((item) => item.navigation?.domContentLoaded).filter(Number.isFinite)),
    loadEventEndMs: summarize(measurements.map((item) => item.navigation?.loadEventEnd).filter(Number.isFinite)),
    firstContentfulPaintMs: summarize(measurements.map((item) => item.firstContentfulPaint).filter(Number.isFinite)),
    firstMetricMs: summarize(measurements.map((item) => item.milestones?.firstMetricAt).filter(Number.isFinite)),
    firstResourceCanvasMs: summarize(measurements.map((item) => item.milestones?.firstResourceCanvasAt).filter(Number.isFinite)),
    firstResourceTableMs: summarize(measurements.map((item) => item.milestones?.firstResourceTableAt).filter(Number.isFinite))
  };
}

function summarize(values) {
  const sorted = values.filter(Number.isFinite).sort((left, right) => left - right);
  if (sorted.length === 0) return { count: 0, mean: null, p50: null, p95: null, p99: null, max: null };
  return {
    count: sorted.length,
    mean: sorted.reduce((sum, value) => sum + value, 0) / sorted.length,
    p50: percentile(sorted, 0.50),
    p95: percentile(sorted, 0.95),
    p99: percentile(sorted, 0.99),
    max: sorted.at(-1)
  };
}

function countBy(values, selectKey) {
  const counts = Object.create(null);
  for (const value of values) {
    const key = String(selectKey(value));
    counts[key] = (counts[key] ?? 0) + 1;
  }
  return counts;
}

function percentile(sorted, probability) {
  if (sorted.length === 1) return sorted[0];
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  const weight = position - lower;
  return sorted[lower] * (1 - weight) + sorted[upper] * weight;
}

function gate(passed, actual, expected) {
  return { passed: Boolean(passed), actual, expected };
}

function sumDropped(snapshots) {
  return snapshots.filter(Boolean).reduce((sum, snapshot) => sum + (snapshot.droppedEventCount ?? 0), 0);
}

function createDiagnostics() {
  return {
    consoleErrors: [],
    pageErrors: [],
    network: { requestFailures: [], badResponses: [] }
  };
}

function collectStaticEnvironment() {
  const git = (args) => execFileSync("git", args, { cwd: repositoryRoot, encoding: "utf8" }).trim();
  return {
    schemaVersion: 1,
    capturedAt: new Date().toISOString(),
    commit: git(["rev-parse", "HEAD"]),
    branch: git(["branch", "--show-current"]),
    dirtyState: git(["status", "--porcelain"]).split(/\r?\n/).filter(Boolean),
    node: process.version,
    platform: `${os.platform()} ${os.release()} ${os.arch()}`,
    cpuModel: os.cpus()[0]?.model ?? null,
    logicalCpuCount: os.cpus().length,
    totalMemoryBytes: os.totalmem(),
    freeMemoryBytesAtAdmission: os.freemem(),
    viewport: scenario.viewport
  };
}

async function collectFrontendAssets() {
  const root = resolve(projectRoot, "../wwwroot");
  const manifestPath = resolve(root, "frontend-build.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const assetPaths = (await listFiles(resolve(root, "assets")))
    .map((path) => relative(root, path).replaceAll("\\", "/"));
  const paths = ["frontend-build.json", "index.html", ...assetPaths];
  const files = [];
  for (const relativePath of paths) {
    const content = await readFile(resolve(root, relativePath));
    files.push({
      path: relativePath.replaceAll("\\", "/"),
      bytes: content.length,
      gzipBytes: gzipSync(content, { level: 9 }).length,
      sha256: sha256(content)
    });
  }
  return {
    schemaVersion: 1,
    buildIdentity: manifest.buildIdentity,
    entryScripts: manifest.entryScripts,
    stylesheets: manifest.stylesheets,
    files
  };
}

async function setScenario(baseUrl, patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(`Scenario update failed: ${response.status}`);
}

async function fetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Fetch failed (${response.status}): ${url}`);
  return response.json();
}

async function loadPlaywright() {
  const candidates = [
    process.env.RM_PLAYWRIGHT_MODULE,
    resolve(projectRoot, "node_modules/playwright/index.mjs"),
  ].filter(Boolean);
  const modulePath = candidates.find((candidate) => existsSync(candidate));
  if (!modulePath) throw new Error("Playwright is unavailable.");
  return import(pathToFileURL(modulePath).href);
}

async function launchBrowser(chromium) {
  try {
    return await chromium.launch({ headless: true });
  } catch (error) {
    if (!String(error).includes("Executable doesn't exist")) throw error;
  }
  const executablePath = [
    process.env.RM_PLAYWRIGHT_BROWSER,
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Google/Chrome/Application/chrome.exe"
  ].filter(Boolean).find((candidate) => existsSync(candidate));
  if (!executablePath) throw new Error("No supported Edge/Chrome executable is available.");
  return chromium.launch({ headless: true, executablePath });
}

async function reservePort() {
  const socket = createServer();
  await new Promise((resolveListen, rejectListen) => {
    socket.once("error", rejectListen);
    socket.listen(0, "127.0.0.1", resolveListen);
  });
  const address = socket.address();
  const port = typeof address === "object" && address ? address.port : 0;
  await new Promise((resolveClose, rejectClose) => socket.close((error) => error ? rejectClose(error) : resolveClose()));
  if (!port) throw new Error("Unable to reserve a frontend performance port.");
  return port;
}

async function waitForServer(baseUrl, processHandle, readOutput) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    if (processHandle.exitCode !== null) throw new Error(`Mock server exited early.\n${readOutput()}`);
    try {
      const response = await fetch(`${baseUrl}/__test/scenario`);
      if (response.ok) return;
    } catch {
      // Server is still binding.
    }
    await delay(100);
  }
  throw new Error(`Mock server readiness timed out.\n${readOutput()}`);
}

async function stopChildProcess(processHandle) {
  if (processHandle.exitCode === null) {
    const exited = new Promise((resolveExit) => processHandle.once("exit", resolveExit));
    processHandle.kill();
    await Promise.race([exited, delay(3000)]);
    if (processHandle.exitCode === null) {
      processHandle.kill("SIGKILL");
      await Promise.race([exited, delay(1000)]);
    }
  }
  processHandle.stdout.destroy();
  processHandle.stderr.destroy();
}

async function writeJson(name, value) {
  await writeFile(resolve(runRoot, name), `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

async function sealEvidence() {
  const files = await listFiles(runRoot);
  const lines = [];
  for (const path of files.filter((path) => basename(path) !== "manifest.sha256").sort()) {
    const content = await readFile(path);
    lines.push(`${sha256(content)}  ${relative(runRoot, path).replaceAll("\\", "/")}`);
  }
  await writeFile(resolve(runRoot, "manifest.sha256"), `${lines.join("\n")}\n`, "utf8");
}

async function listFiles(root) {
  const result = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = resolve(root, entry.name);
    if (entry.isDirectory()) result.push(...await listFiles(path));
    else if (entry.isFile() && (await stat(path)).size >= 0) result.push(path);
  }
  return result;
}

function sha256(content) {
  return createHash("sha256").update(content).digest("hex").toUpperCase();
}

function stripLeadingSlash(value) {
  return String(value).replace(/^[/\\]+/, "");
}

function redactUrl(value) {
  try {
    const url = new URL(value);
    return url.pathname;
  } catch {
    return "invalid-url";
  }
}

function formatRunTimestamp(date) {
  return date.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z");
}

function escapeCell(value) {
  return String(value).replaceAll("|", "\\|");
}

function formatNumber(value) {
  return Number.isFinite(value) ? value.toFixed(2) : "n/a";
}

function formatBytes(value) {
  if (!Number.isFinite(value)) return "n/a";
  return `${(value / 1024 / 1024).toFixed(2)} MiB`;
}

function delay(milliseconds) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}
