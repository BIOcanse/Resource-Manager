import { execFileSync, spawn } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import os from "node:os";
import { basename, isAbsolute, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const clientRoot = fileURLToPath(new URL("../..", import.meta.url));
const repositoryRoot = resolve(clientRoot, "../../..");
const applicationRoot = resolve(repositoryRoot, "src/Core");
const endpoint = process.env.RM_NATIVE_UI_CDP_ENDPOINT?.trim() || "http://127.0.0.1:9333";
const expectedFrontendUrl = process.env.RM_EXPECTED_FRONTEND_URL?.trim() || "http://127.0.0.1:9321/";
const expectedCapacity = parseExpectedCapacity(
  process.env.RM_FRONTEND_PERFORMANCE_EXPECTED_CAPACITY?.trim() || "50000");
const quick = process.argv.includes("--quick");
const profile = Object.freeze(quick ? {
  id: "frontend-framework-live-webview2-dev-v1",
  routeCycles: 10,
  stableSeconds: 10,
  memoryWarmupCycles: 10,
  memoryCycles: 10
} : {
  id: "frontend-framework-live-webview2-v1",
  routeCycles: 30,
  stableSeconds: 60,
  memoryWarmupCycles: 10,
  memoryCycles: 50
});
const thresholds = Object.freeze({
  routeSecondFrameP95Ms: 100,
  routeSecondFrameMaximumMs: 250,
  minimumStableSourceSampleRatio: 0.75,
  maximumHeapGrowthBytes: 8 * 1024 * 1024,
  maximumHeapGrowthRatio: 0.10,
  maximumDomGrowthNodes: 32,
  maximumDomGrowthRatio: 0.05
});
const runId = process.env.RM_FRONTEND_PERFORMANCE_RUN_ID?.trim()
  || `${formatRunTimestamp(new Date())}-${profile.id}-${randomBytes(6).toString("hex")}`;
const runRoot = process.env.RM_FRONTEND_PERFORMANCE_RUN_ROOT?.trim()
  || resolve(repositoryRoot, ".artifacts/tests/frontend-performance", runId);
await mkdir(runRoot, { recursive: true });
await writeJson("scenario.json", {
  schemaVersion: 1,
  profile,
  thresholds,
  endpoint,
  expectedFrontendUrl,
  expectedCapacity,
  pageOrder: ["components", "optimization", "details", "settings", "monitor"]
});

const diagnostics = {
  consoleErrors: [],
  pageErrors: [],
  requestFailures: [],
  apiFailures: [],
  apiResponses: 0
};

try {
  const { chromium } = await loadPlaywright();
  const browser = await chromium.connectOverCDP(endpoint);
  const pages = browser.contexts().flatMap((context) => context.pages());
  const candidates = pages.filter((page) => page.url().startsWith(expectedFrontendUrl));
  assert(candidates.length === 1, `Expected one Resource Manager page; found ${candidates.length}.`);
  const page = candidates[0];
  attachDiagnostics(page);
  await page.waitForFunction(() => document.readyState === "complete");
  await page.waitForFunction(() => Boolean(globalThis.__resourceManagerFrontendPerformance), null, { timeout: 10_000 });
  await page.locator(".metric-value").first().waitFor({ state: "visible", timeout: 15_000 });
  await activateResourceMonitor(page);
  const session = await page.context().newCDPSession(page);
  await session.send("Performance.enable");
  await session.send("HeapProfiler.enable");

  const startup = await readStartup(page);
  assert(startup.runtime.schemaVersion === 1, "The frontend performance runtime schema is not v1.");
  assert(startup.runtime.enabled === true, "The frontend performance runtime is not enabled.");
  assert(startup.runtime.capacity === expectedCapacity,
    `Expected frontend performance capacity ${expectedCapacity}; received ${startup.runtime.capacity}.`);
  const runtimeIdentity = await readRuntimeIdentity(page);
  const topology = readProcessTopology(
    runtimeIdentity.processId,
    Number(new URL(endpoint).port),
    Number(new URL(expectedFrontendUrl).port));
  assert(topology.backend !== null, "The exact backend PID is absent from the live process topology.");
  assert(topology.nativeUi !== null, "The Native UI process is absent from the live process topology.");
  assert(topology.webView.length > 0, "No WebView2 descendant process was found.");
  assert(topology.listenerOwnedByWebView, "The CDP listener is not owned by the sealed WebView2 descendant chain.");
  assert(topology.backendListenerOwnedByRuntime,
    "The frontend HTTP listener is not owned exclusively by the runtime identity PID.");
  assert(isExpectedBackend(topology.backend), "The runtime identity PID is not a Resource Manager backend process.");
  assert(isExpectedNativeUi(topology.nativeUi), "The CDP-owning process tree is not rooted at ResourceManager.NativeUi.exe.");
  assert(String(topology.backend.processStartUtcTicks) === String(runtimeIdentity.processStartUtcTicks),
    "The runtime identity start ticks do not match the backend process snapshot.");
  const headCommit = git(["rev-parse", "HEAD"]);
  assert(String(runtimeIdentity.buildVersion).toLowerCase().includes(headCommit.toLowerCase()),
    "The backend build version is not anchored to the current HEAD commit.");
  const artifactClosureBefore = await captureRuntimeArtifactClosure();
  await writeJson("artifacts-before.json", artifactClosureBefore);
  await writeJson("environment.json", {
    schemaVersion: 1,
    capturedAt: new Date().toISOString(),
    commit: headCommit,
    dirtyState: git(["status", "--porcelain"]).split(/\r?\n/).filter(Boolean),
    node: process.version,
    browserVersion: browser.version(),
    platform: `${os.platform()} ${os.release()} ${os.arch()}`,
    cpuModel: os.cpus()[0]?.model ?? null,
    logicalCpuCount: os.cpus().length,
    totalMemoryBytes: os.totalmem(),
    freeMemoryBytesAtStart: os.freemem(),
    frontendBuild: startup.frontendBuild,
    runtimeIdentity,
    processTopology: topology
  });

  await resetRuntime(page);
  const routeMeasurements = [];
  for (let cycle = 0; cycle < profile.routeCycles; cycle += 1) {
    for (const pageId of ["components", "optimization", "details", "settings", "monitor"]) {
      routeMeasurements.push({ cycle: cycle + 1, ...(await navigateToPage(page, pageId)) });
    }
  }
  const routeRuntime = await readRuntime(page);

  await navigateToPage(page, "monitor");
  await activateResourceMonitor(page);
  await resetRuntime(page);
  await page.evaluate(() => {
    globalThis.__rmLiveFrontendFrames = { active: true, last: null, intervals: [] };
    const frame = (at) => {
      const state = globalThis.__rmLiveFrontendFrames;
      if (!state?.active) return;
      if (state.last !== null) state.intervals.push(at - state.last);
      state.last = at;
      requestAnimationFrame(frame);
    };
    requestAnimationFrame(frame);
  });
  const cdpBefore = await captureCdp(session);
  const sampler = collectProcessSamples(topology.subjects, profile.stableSeconds);
  const stableStartedAt = await page.evaluate(() => performance.now());
  await delay(profile.stableSeconds * 1000);
  const stableFinishedAt = await page.evaluate(() => performance.now());
  const frameIntervals = await page.evaluate(() => {
    const state = globalThis.__rmLiveFrontendFrames;
    if (!state) return [];
    state.active = false;
    return [...state.intervals];
  });
  const cdpAfter = await captureCdp(session);
  const stableRuntime = await readRuntime(page);
  const processSamples = await sampler;

  await resetRuntime(page);
  const memoryWarmupRoutes = [];
  const memoryWarmupCheckpoints = [];
  for (let cycle = 0; cycle < profile.memoryWarmupCycles; cycle += 1) {
    for (const pageId of ["components", "optimization", "details", "settings", "monitor"]) {
      memoryWarmupRoutes.push({ cycle: cycle + 1, ...(await navigateToPage(page, pageId)) });
    }
    memoryWarmupCheckpoints.push(await captureMemoryCheckpoint(session, cycle + 1));
  }
  const memoryBefore = await captureCollectedMemory(session);
  const memoryRoutes = [];
  const memoryCheckpoints = [];
  for (let cycle = 0; cycle < profile.memoryCycles; cycle += 1) {
    for (const pageId of ["components", "optimization", "details", "settings", "monitor"]) {
      memoryRoutes.push({ cycle: cycle + 1, ...(await navigateToPage(page, pageId)) });
    }
    memoryCheckpoints.push(await captureMemoryCheckpoint(session, cycle + 1));
  }
  const memoryAfter = await captureCollectedMemory(session);
  const memoryRuntime = await readRuntime(page);
  const finalIdentity = await readRuntimeIdentity(page);
  assert(sameRuntimeIdentity(runtimeIdentity, finalIdentity), "Backend identity changed during the live performance profile.");
  const finalTopology = readProcessTopology(
    finalIdentity.processId,
    Number(new URL(endpoint).port),
    Number(new URL(expectedFrontendUrl).port));
  assert(sameSubjectTopology(topology, finalTopology),
    "The backend, Native UI, WebView2, or listener topology changed during the live performance profile.");
  const artifactClosureAfter = await captureRuntimeArtifactClosure();
  await writeJson("artifacts-after.json", artifactClosureAfter);
  assert(JSON.stringify(artifactClosureBefore) === JSON.stringify(artifactClosureAfter),
    "The tested backend, Native UI, or frontend artifact closure changed during the profile.");

  const resourceDom = await readResourceDom(page);
  const runtimeEvidence = {
    schemaVersion: 1,
    startup: startup.runtime,
    route: routeRuntime,
    stable: stableRuntime,
    memory: memoryRuntime
  };
  const browserMetrics = {
    schemaVersion: 1,
    startup: startup.metrics,
    routeMeasurements,
    stable: {
      startedAt: stableStartedAt,
      finishedAt: stableFinishedAt,
      elapsedMs: stableFinishedAt - stableStartedAt,
      frameIntervals,
      cdpDelta: subtractMetrics(cdpAfter.performance, cdpBefore.performance),
      domBefore: cdpBefore.dom,
      domAfter: cdpAfter.dom,
      resourceDom
    },
    memory: {
      warmupCycles: profile.memoryWarmupCycles,
      warmupRouteMeasurements: memoryWarmupRoutes,
      warmupCheckpoints: memoryWarmupCheckpoints,
      before: memoryBefore,
      after: memoryAfter,
      routeMeasurements: memoryRoutes,
      checkpoints: memoryCheckpoints
    }
  };
  const processMetrics = {
    schemaVersion: 1,
    topologyBefore: topology,
    topologyAfter: finalTopology,
    samples: processSamples,
    summary: summarizeProcessSamples(topology.subjects, processSamples)
  };
  await writeJson("runtime-events.json", runtimeEvidence);
  await writeJson("browser-metrics.json", browserMetrics);
  await writeJson("process-metrics.json", processMetrics);
  await writeJson("network.json", { schemaVersion: 1, diagnostics });

  const assessment = assess({
    startup,
    routeMeasurements,
    routeRuntime,
    stableRuntime,
    memoryRuntime,
    stableElapsedMs: stableFinishedAt - stableStartedAt,
    frameIntervals,
    cdpDelta: browserMetrics.stable.cdpDelta,
    memoryBefore,
    memoryAfter,
    memoryCheckpoints,
    resourceDom,
    processSummary: processMetrics.summary
  });
  await writeJson("assessment.json", assessment);
  await writeFile(resolve(runRoot, "report.md"), renderReport(assessment), "utf8");
  await sealEvidence();
  process.stdout.write(`${JSON.stringify({ runId, runRoot, passed: assessment.passed, assessment }, null, 2)}\n`);
  process.exit(assessment.passed ? 0 : 1);
} catch (error) {
  await writeJson("failure.json", {
    schemaVersion: 1,
    failedAt: new Date().toISOString(),
    error: error instanceof Error
      ? { name: error.name, message: error.message, stack: error.stack ?? null }
      : { name: "Unknown", message: String(error), stack: null },
    diagnostics
  });
  await sealEvidence();
  process.stderr.write(`Live frontend performance attempt preserved at ${runRoot}\n`);
  process.exit(1);
}

function assess(input) {
  const routeSummary = summarize(input.routeMeasurements.map((item) => item.secondFrameMs));
  const events = input.stableRuntime.events ?? [];
  const longTasks = events.filter((event) => event.kind === "long-task");
  const resourceSettles = events.filter((event) =>
    event.kind === "source"
    && event.phase === "settled"
    && event.sourceKey.includes("resource-monitor"));
  const requestEvents = events.filter((event) => event.kind === "request");
  const frameSummary = summarize(input.frameIntervals);
  const heapGrowthBytes = input.memoryAfter.heap.usedSize - input.memoryBefore.heap.usedSize;
  const heapGrowthRatio = heapGrowthBytes / Math.max(1, input.memoryBefore.heap.usedSize);
  const domGrowthNodes = input.memoryAfter.dom.nodes - input.memoryBefore.dom.nodes;
  const domGrowthRatio = domGrowthNodes / Math.max(1, input.memoryBefore.dom.nodes);
  const heapSlopeBytesPerCycle = linearSlope(input.memoryCheckpoints.map((item) => item.heap.usedSize));
  const domSlopeNodesPerCycle = linearSlope(input.memoryCheckpoints.map((item) => item.dom.nodes));
  const projectedHeapGrowthBytes = Math.max(0, heapSlopeBytesPerCycle * Math.max(0, input.memoryCheckpoints.length - 1));
  const projectedDomGrowthNodes = Math.max(0, domSlopeNodesPerCycle * Math.max(0, input.memoryCheckpoints.length - 1));
  const heapGrowthLimit = Math.max(thresholds.maximumHeapGrowthBytes, input.memoryBefore.heap.usedSize * thresholds.maximumHeapGrowthRatio);
  const domGrowthLimit = Math.max(thresholds.maximumDomGrowthNodes, input.memoryBefore.dom.nodes * thresholds.maximumDomGrowthRatio);
  const sampleRatio = resourceSettles.length / Math.max(1, profile.stableSeconds);
  const dropped = [input.routeRuntime, input.stableRuntime, input.memoryRuntime]
    .reduce((sum, item) => sum + (item?.droppedEventCount ?? 0), 0);
  const unexpectedRequestFailures = diagnostics.requestFailures.filter((item) =>
    !String(item.failure).includes("ERR_ABORTED"));
  const gates = {
    coldStartupEvidence: gate(
      Number.isFinite(input.startup.metrics.appRenderedAt)
        && Number.isFinite(input.startup.metrics.initialRouteSecondFrameAt)
        && Number.isFinite(input.startup.metrics.initialMetricSourceSettledAt)
        && Number.isFinite(input.startup.metrics.initialResourceSourceSettledAt),
      input.startup.metrics,
      "current cold navigation contains app, initial route, metric-source, and visible resource-source milestones"),
    routeSecondFrameP95: gate(routeSummary.p95 <= thresholds.routeSecondFrameP95Ms, routeSummary.p95, `<= ${thresholds.routeSecondFrameP95Ms} ms`),
    routeSecondFrameMaximum: gate(routeSummary.max <= thresholds.routeSecondFrameMaximumMs, routeSummary.max, `<= ${thresholds.routeSecondFrameMaximumMs} ms`),
    monitorSampleContinuity: gate(sampleRatio >= thresholds.minimumStableSourceSampleRatio, { count: resourceSettles.length, ratio: sampleRatio }, `>= ${thresholds.minimumStableSourceSampleRatio}`),
    recurringMonitorLongTask: gate(maximumConsecutiveAffectedSamples(resourceSettles, longTasks) < 3, maximumConsecutiveAffectedSamples(resourceSettles, longTasks), "< 3 consecutive samples affected"),
    settledHeapGrowth: gate(heapGrowthBytes <= heapGrowthLimit, { heapGrowthBytes, heapGrowthRatio }, "<= max(8 MiB, 10%)"),
    settledHeapTrend: gate(projectedHeapGrowthBytes <= heapGrowthLimit, { heapSlopeBytesPerCycle, projectedHeapGrowthBytes }, "projected measured-window growth <= max(8 MiB, 10%)"),
    settledDomGrowth: gate(domGrowthNodes <= domGrowthLimit, { domGrowthNodes, domGrowthRatio }, "<= max(32 nodes, 5%)"),
    settledDomTrend: gate(projectedDomGrowthNodes <= domGrowthLimit, { domSlopeNodesPerCycle, projectedDomGrowthNodes }, "projected measured-window growth <= max(32 nodes, 5%)"),
    diagnosticsIntegrity: gate(diagnostics.consoleErrors.length === 0 && diagnostics.pageErrors.length === 0 && diagnostics.apiFailures.length === 0 && unexpectedRequestFailures.length === 0, { consoleErrors: diagnostics.consoleErrors.length, pageErrors: diagnostics.pageErrors.length, apiFailures: diagnostics.apiFailures.length, unexpectedRequestFailures: unexpectedRequestFailures.length }, "all zero"),
    runtimeBufferIntegrity: gate(dropped === 0, dropped, "0 dropped events"),
    processContinuity: gate(input.processSummary.missingSubjectSamples === 0, input.processSummary.missingSubjectSamples, "0 missing subject samples"),
    processIdentityContinuity: gate(input.processSummary.identityDriftSamples === 0, input.processSummary.identityDriftSamples, "0 PID/start identity drift samples")
  };
  const elapsedSeconds = input.stableElapsedMs / 1000;
  return {
    schemaVersion: 1,
    profileId: profile.id,
    runId,
    passed: Object.values(gates).every((item) => item.passed),
    claimBoundary: "Real Resource Manager NativeUi/WebView2 frontend main-thread and exact process behavior; not display-completion FPS, dropped frames, game performance, scheduler benefit, or photon latency.",
    startup: input.startup.metrics,
    routeSecondFrameMs: routeSummary,
    stableMonitor: {
      elapsedMs: input.stableElapsedMs,
      resourceSourceSamples: resourceSettles.length,
      longTaskCount: longTasks.length,
      longTaskDurationMs: summarize(longTasks.map((item) => item.durationMs)),
      frameIntervalsMs: frameSummary,
      frameIntervalsOver50Ms: input.frameIntervals.filter((value) => value > 50).length,
      frameIntervalsOver100Ms: input.frameIntervals.filter((value) => value > 100).length,
      requestDurationMs: summarize(requestEvents.map((item) => item.durationMs)),
      requestOutcomes: countBy(requestEvents, (item) => item.outcome),
      sourceDurationMs: summarize(resourceSettles.map((item) => item.durationMs).filter(Number.isFinite)),
      mainThreadTaskPercent: elapsedSeconds > 0 ? input.cdpDelta.TaskDuration / elapsedSeconds * 100 : null,
      scriptPercent: elapsedSeconds > 0 ? input.cdpDelta.ScriptDuration / elapsedSeconds * 100 : null,
      layoutPercent: elapsedSeconds > 0 ? input.cdpDelta.LayoutDuration / elapsedSeconds * 100 : null,
      cdpDelta: input.cdpDelta,
      resourceDom: input.resourceDom
    },
    memory: {
      before: input.memoryBefore,
      after: input.memoryAfter,
      checkpoints: input.memoryCheckpoints,
      heapGrowthBytes,
      heapGrowthRatio,
      heapSlopeBytesPerCycle,
      projectedHeapGrowthBytes,
      domGrowthNodes,
      domGrowthRatio,
      domSlopeNodesPerCycle,
      projectedDomGrowthNodes
    },
    processes: input.processSummary,
    gates
  };
}

function renderReport(assessment) {
  const rows = Object.entries(assessment.gates)
    .map(([name, value]) => `| ${name} | ${value.passed ? "PASS" : "FAIL"} | ${escapeCell(JSON.stringify(value.actual))} | ${escapeCell(value.expected)} |`)
    .join("\n");
  const roleRows = Object.entries(assessment.processes.roles)
    .map(([role, value]) => `| ${role} | ${formatNumber(value.cpuMeanPercent)}% | ${formatNumber(value.cpuP95Percent)}% | ${formatBytes(value.workingSetPeakBytes)} | ${formatBytes(value.privatePeakBytes)} |`)
    .join("\n");
  return `# 真实 Native UI 新前端性能报告\n\n- Run: \`${assessment.runId}\`\n- Profile: \`${assessment.profileId}\`\n- Result: **${assessment.passed ? "PASS" : "FAIL"}**\n- Scope: ${assessment.claimBoundary}\n\n## 前端\n\n- 页面选择至第二绘制帧：p50 ${formatNumber(assessment.routeSecondFrameMs.p50)} ms，p95 ${formatNumber(assessment.routeSecondFrameMs.p95)} ms，p99 ${formatNumber(assessment.routeSecondFrameMs.p99)} ms，max ${formatNumber(assessment.routeSecondFrameMs.max)} ms。\n- 稳定监控 ${formatNumber(assessment.stableMonitor.elapsedMs / 1000)} s：${assessment.stableMonitor.resourceSourceSamples} 个 source 样本，${assessment.stableMonitor.longTaskCount} 个 >=50 ms long task；主线程 TaskDuration ${formatNumber(assessment.stableMonitor.mainThreadTaskPercent)}%。\n- rAF cadence：p95 ${formatNumber(assessment.stableMonitor.frameIntervalsMs.p95)} ms，p99 ${formatNumber(assessment.stableMonitor.frameIntervalsMs.p99)} ms，max ${formatNumber(assessment.stableMonitor.frameIntervalsMs.max)} ms。\n- 回收后 heap：${formatBytes(assessment.memory.heapGrowthBytes)}（${formatNumber(assessment.memory.heapGrowthRatio * 100)}%）；DOM：${assessment.memory.domGrowthNodes} nodes。\n\n## 精确进程窗口\n\n| Role | CPU mean | CPU p95 | Working set peak | Private peak |\n| --- | ---: | ---: | ---: | ---: |\n${roleRows}\n\n## 独立门\n\n| Gate | Result | Actual | Expected |\n| --- | --- | --- | --- |\n${rows}\n\n## 结论边界\n\n这是应用主线程与真实 NativeUi/WebView2 进程证据，不是显示完成 FPS、显示掉帧、游戏性能、调度收益或光子延迟证据。\n`;
}

async function readStartup(page) {
  return page.evaluate(async () => {
    const frontendBuildResponse = await fetch("/frontend-build.json", { cache: "no-store" });
    if (!frontendBuildResponse.ok) throw new Error(`frontend-build.json returned ${frontendBuildResponse.status}`);
    const navigation = performance.getEntriesByType("navigation")[0];
    const paints = performance.getEntriesByType("paint");
    const runtime = globalThis.__resourceManagerFrontendPerformance.snapshot();
    return {
      frontendBuild: await frontendBuildResponse.json(),
      runtime,
      metrics: {
        timeOriginEpochMs: performance.timeOrigin,
        observedAt: performance.now(),
        navigation: navigation ? {
          responseEnd: navigation.responseEnd,
          domContentLoaded: navigation.domContentLoadedEventEnd,
          loadEventEnd: navigation.loadEventEnd
        } : null,
        firstContentfulPaint: paints.find((entry) => entry.name === "first-contentful-paint")?.startTime ?? null,
        appRenderedAt: runtime.events.find((event) => event.kind === "lifecycle" && event.phase === "app-render-returned")?.at ?? null,
        initialRouteSecondFrameAt: runtime.events.find((event) => event.kind === "route" && event.phase === "frame-2")?.at ?? null,
        initialMetricSourceSettledAt: runtime.events.find((event) => event.kind === "source"
          && event.phase === "settled" && event.sourceKey.includes("monitor.metric-snapshot"))?.finishedAt ?? null,
        initialResourceSourceSettledAt: runtime.events.find((event) => event.kind === "source"
          && event.phase === "settled" && event.sourceKey.includes("resource-monitor"))?.finishedAt ?? null
      }
    };
  });
}

async function readRuntimeIdentity(page) {
  return page.evaluate(async () => {
    const challenge = "C".repeat(64);
    const response = await fetch(`/api/runtime/identity?challenge=${challenge}`, { cache: "no-store" });
    if (!response.ok) throw new Error(`runtime identity returned ${response.status}`);
    const text = await response.text();
    const value = JSON.parse(text);
    const exact = (name) => text.match(new RegExp(`"${name}"\\s*:\\s*(\\d+)`))?.[1] ?? null;
    return { ...value, instanceId: exact("instanceId"), processStartUtcTicks: exact("processStartUtcTicks") };
  });
}

function readProcessTopology(backendPid, debugPort, frontendPort) {
  const script = `
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$processes = @(Get-CimInstance Win32_Process | ForEach-Object {
  $live = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
  $liveStart = $null
  $livePath = $null
  if ($null -ne $live) {
    try { $liveStart = $live.StartTime.ToUniversalTime() } catch { $liveStart = $null }
    try { $livePath = [string]$live.Path } catch { $livePath = $null }
  }
  [pscustomobject]@{
    processId = [int]$_.ProcessId
    parentProcessId = [int]$_.ParentProcessId
    name = [string]$_.Name
    executablePath = if ($null -ne $_.ExecutablePath) { [string]$_.ExecutablePath } elseif (-not [string]::IsNullOrWhiteSpace($livePath)) { $livePath } else { $null }
    creationDate = if ($null -eq $liveStart) { $null } else { $liveStart.ToString('O') }
    processStartUtcTicks = if ($null -eq $liveStart) { $null } else { [string]$liveStart.Ticks }
  }
})
$listeners = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort ${debugPort} -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique)
$backendListeners = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort ${frontendPort} -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique)
[pscustomobject]@{ processes = $processes; listenerPids = $listeners; backendListenerPids = $backendListeners } | ConvertTo-Json -Compress -Depth 5
`;
  const raw = execFileSync(
    "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
    ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodePowerShell(script)],
    { encoding: "utf8", windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
  const snapshot = JSON.parse(raw.trim());
  const processes = Array.isArray(snapshot.processes) ? snapshot.processes : [snapshot.processes];
  const backend = processes.find((item) => item.processId === Number(backendPid)) ?? null;
  const listenerPids = Array.isArray(snapshot.listenerPids) ? snapshot.listenerPids.map(Number) : [Number(snapshot.listenerPids)];
  const backendListenerPids = Array.isArray(snapshot.backendListenerPids)
    ? snapshot.backendListenerPids.map(Number)
    : [Number(snapshot.backendListenerPids)];
  const listenerOwners = processes.filter((item) => listenerPids.includes(item.processId));
  const listenerWebViews = listenerOwners.filter((item) => item.name.toLowerCase() === "msedgewebview2.exe");
  const nativeAncestorIds = [...new Set(listenerWebViews.flatMap((item) =>
    findAncestors(processes, item.processId)
      .filter((candidate) => candidate.name.toLowerCase() === "resourcemanager.nativeui.exe")
      .map((candidate) => candidate.processId)))];
  const nativeUi = nativeAncestorIds.length === 1
    ? processes.find((item) => item.processId === nativeAncestorIds[0]) ?? null
    : null;
  const descendants = nativeUi ? findDescendants(processes, nativeUi.processId) : [];
  const webView = descendants.filter((item) => item.name.toLowerCase() === "msedgewebview2.exe");
  const subjects = [
    ...(backend ? [{ ...backend, role: "backend" }] : []),
    ...(nativeUi ? [{ ...nativeUi, role: "native-ui" }] : []),
    ...webView.map((item) => ({ ...item, role: "webview" }))
  ];
  return {
    backend,
    nativeUi,
    webView,
    listenerOwners,
    listenerPids,
    backendListenerPids,
    listenerOwnedByWebView: listenerPids.length > 0
      && listenerPids.every((pid) => webView.some((item) => item.processId === pid)),
    backendListenerOwnedByRuntime: backendListenerPids.length === 1
      && backendListenerPids[0] === Number(backendPid),
    subjects
  };
}

function findAncestors(processes, processId) {
  const byId = new Map(processes.map((item) => [item.processId, item]));
  const result = [];
  const seen = new Set();
  let current = byId.get(processId) ?? null;
  while (current && !seen.has(current.processId)) {
    seen.add(current.processId);
    const parent = byId.get(current.parentProcessId) ?? null;
    if (!parent) break;
    result.push(parent);
    current = parent;
  }
  return result;
}

function findDescendants(processes, rootPid) {
  const result = [];
  const pending = [rootPid];
  while (pending.length > 0) {
    const parent = pending.shift();
    for (const process of processes.filter((item) => item.parentProcessId === parent)) {
      result.push(process);
      pending.push(process.processId);
    }
  }
  return result;
}

function collectProcessSamples(subjects, durationSeconds) {
  const ids = subjects.map((item) => item.processId).join(",");
  const script = `
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ids = @(${ids})
$clock = [Diagnostics.Stopwatch]::StartNew()
do {
  $rows = @($ids | ForEach-Object {
    $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
    if ($null -eq $process) {
      [pscustomobject]@{ processId = $_; present = $false }
    } else {
      $processStartUtcTicks = $null
      try { $processStartUtcTicks = [string]$process.StartTime.ToUniversalTime().Ticks } catch { $processStartUtcTicks = $null }
      [pscustomobject]@{
        processId = $_
        present = $true
        cpuTotalMs = $process.TotalProcessorTime.TotalMilliseconds
        workingSetBytes = $process.WorkingSet64
        privateBytes = $process.PrivateMemorySize64
        handles = $process.HandleCount
        threads = $process.Threads.Count
        processStartUtcTicks = $processStartUtcTicks
      }
    }
  })
  [pscustomobject]@{ atMs = $clock.Elapsed.TotalMilliseconds; rows = $rows } | ConvertTo-Json -Compress -Depth 4
  if ($clock.Elapsed.TotalSeconds -lt ${durationSeconds}) { Start-Sleep -Milliseconds 1000 }
} while ($clock.Elapsed.TotalSeconds -lt ${durationSeconds})
`;
  return new Promise((resolveSamples, rejectSamples) => {
    const child = spawn(
      "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
      ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodePowerShell(script)],
      { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", rejectSamples);
    child.on("exit", (code) => {
      if (code !== 0) {
        rejectSamples(new Error(`Process sampler exited ${code}: ${stderr}`));
        return;
      }
      try {
        resolveSamples(stdout.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)));
      } catch (error) {
        rejectSamples(new Error(`Process sampler returned invalid JSON: ${stderr}`, { cause: error }));
      }
    });
  });
}

function summarizeProcessSamples(subjects, samples) {
  const byPid = new Map(subjects.map((subject) => [subject.processId, subject]));
  const roleIntervals = new Map();
  let missingSubjectSamples = 0;
  let identityDriftSamples = 0;
  for (let index = 1; index < samples.length; index += 1) {
    const previous = new Map(toRows(samples[index - 1].rows).map((row) => [row.processId, row]));
    const current = new Map(toRows(samples[index].rows).map((row) => [row.processId, row]));
    const elapsedMs = samples[index].atMs - samples[index - 1].atMs;
    const roleCpu = new Map();
    for (const subject of subjects) {
      const before = previous.get(subject.processId);
      const after = current.get(subject.processId);
      if (!before?.present || !after?.present) {
        missingSubjectSamples += 1;
        continue;
      }
      if (String(before.processStartUtcTicks) !== String(subject.processStartUtcTicks)
        || String(after.processStartUtcTicks) !== String(subject.processStartUtcTicks)) {
        identityDriftSamples += 1;
        continue;
      }
      const cpu = Math.max(0, after.cpuTotalMs - before.cpuTotalMs)
        / Math.max(1, elapsedMs)
        / Math.max(1, os.cpus().length)
        * 100;
      roleCpu.set(subject.role, (roleCpu.get(subject.role) ?? 0) + cpu);
    }
    for (const [role, cpuPercent] of roleCpu) {
      const list = roleIntervals.get(role) ?? [];
      list.push(cpuPercent);
      roleIntervals.set(role, list);
    }
  }
  const roles = {};
  for (const role of ["backend", "native-ui", "webview"]) {
    const roleSubjects = subjects.filter((item) => item.role === role);
    const intervalCpu = roleIntervals.get(role) ?? [];
    const memoryRows = samples.map((sample) => toRows(sample.rows)
      .filter((row) => row.present && roleSubjects.some((subject) =>
        subject.processId === row.processId
        && String(subject.processStartUtcTicks) === String(row.processStartUtcTicks))));
    roles[role] = {
      processCount: roleSubjects.length,
      cpuMeanPercent: summarize(intervalCpu).mean,
      cpuP95Percent: summarize(intervalCpu).p95,
      cpuMaximumPercent: summarize(intervalCpu).max,
      workingSetPeakBytes: Math.max(0, ...memoryRows.map((rows) => rows.reduce((sum, row) => sum + row.workingSetBytes, 0))),
      privatePeakBytes: Math.max(0, ...memoryRows.map((rows) => rows.reduce((sum, row) => sum + row.privateBytes, 0))),
      handlePeak: Math.max(0, ...memoryRows.map((rows) => rows.reduce((sum, row) => sum + row.handles, 0))),
      threadPeak: Math.max(0, ...memoryRows.map((rows) => rows.reduce((sum, row) => sum + row.threads, 0)))
    };
  }
  return { sampleCount: samples.length, missingSubjectSamples, identityDriftSamples, roles, subjects: [...byPid.values()] };
}

function toRows(value) {
  return Array.isArray(value) ? value : value ? [value] : [];
}

function attachDiagnostics(page) {
  page.on("console", (message) => {
    if (message.type() === "error") diagnostics.consoleErrors.push(message.text());
  });
  page.on("pageerror", (error) => diagnostics.pageErrors.push(String(error)));
  page.on("requestfailed", (request) => diagnostics.requestFailures.push({
    method: request.method(),
    path: safePath(request.url()),
    failure: request.failure()?.errorText ?? "unknown"
  }));
  page.on("response", (response) => {
    const url = new URL(response.url());
    if (url.origin !== new URL(expectedFrontendUrl).origin || !url.pathname.startsWith("/api/")) return;
    diagnostics.apiResponses += 1;
    if (!response.ok()) diagnostics.apiFailures.push({ path: url.pathname, status: response.status() });
  });
}

async function navigateToPage(page, pageId) {
  if (await page.evaluate((expected) => document.body.dataset.page === expected, pageId)) {
    return { pageId, secondFrameMs: 0, routeId: null, noOp: true };
  }
  const before = await page.evaluate(() => Math.max(0, ...(globalThis.__resourceManagerFrontendPerformance.snapshot().events)
    .filter((event) => event.kind === "route").map((event) => event.routeId)));
  await page.getByRole("button", { name: pageLabel(pageId), exact: true }).click();
  await page.waitForFunction((expected) => document.body.dataset.page === expected, pageId, { timeout: 10_000 });
  await page.waitForFunction((routeId) => globalThis.__resourceManagerFrontendPerformance.snapshot().events
    .some((event) => event.kind === "route" && event.routeId > routeId && event.phase === "frame-2"), before, { timeout: 10_000 });
  return page.evaluate((routeId) => {
    const event = [...globalThis.__resourceManagerFrontendPerformance.snapshot().events]
      .reverse().find((candidate) => candidate.kind === "route" && candidate.routeId > routeId && candidate.phase === "frame-2");
    return { pageId: event.page, secondFrameMs: event.elapsedFromIntentMs, routeId: event.routeId, noOp: false };
  }, before);
}

async function activateResourceMonitor(page) {
  const bars = page.locator(".resource-breakdown-panel");
  await bars.scrollIntoViewIfNeeded();
  await bars.waitFor({ state: "visible", timeout: 15_000 });

  const list = page.locator(".resource-table-frame[role='table']");
  await list.scrollIntoViewIfNeeded();
  await list.waitFor({ state: "visible", timeout: 15_000 });
  await page.waitForFunction(() => document.querySelectorAll(
    ".resource-table-frame[role='table'] .resource-table-row:not(.summary-row)").length > 0,
  null, { timeout: 15_000 });
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

async function readResourceDom(page) {
  return page.evaluate(() => ({
    totalNodes: document.getElementsByTagName("*").length,
    paintPlanes: [...document.querySelectorAll("[data-resource-paint-plane]")].map((node) => Number(node.getAttribute("data-resource-segment-count"))),
    interactionProxies: document.querySelectorAll(".resource-segment-interaction-proxy").length,
    tooltips: document.querySelectorAll(".resource-tooltip").length,
    tableAriaRows: Number(document.querySelector(".resource-table-frame")?.getAttribute("aria-rowcount")),
    tableDomRows: document.querySelectorAll(".resource-table-row").length
  }));
}

async function captureCdp(session) {
  const [{ metrics }, dom] = await Promise.all([session.send("Performance.getMetrics"), session.send("Memory.getDOMCounters")]);
  return { performance: Object.fromEntries(metrics.map((item) => [item.name, item.value])), dom };
}

async function captureCollectedMemory(session) {
  const samples = [];
  for (let index = 0; index < 12; index += 1) {
    await session.send("HeapProfiler.collectGarbage");
    await delay(250);
    const [heap, dom] = await Promise.all([
      session.send("Runtime.getHeapUsage"),
      session.send("Memory.getDOMCounters")
    ]);
    samples.push({ sample: index + 1, heap, dom });
  }
  const heapFloor = samples.reduce((best, item) =>
    item.heap.usedSize < best.heap.usedSize ? item : best);
  const domFloor = samples.reduce((best, item) =>
    item.dom.nodes < best.dom.nodes ? item : best);
  return {
    selection: "independent minimum post-GC heap usedSize and DOM node floors across 12 fixed 250 ms samples after GC-driven route warmup",
    heap: heapFloor.heap,
    dom: domFloor.dom,
    heapFloorSample: heapFloor.sample,
    domFloorSample: domFloor.sample,
    samples
  };
}

async function captureMemoryCheckpoint(session, cycle) {
  await session.send("HeapProfiler.collectGarbage");
  await delay(250);
  await session.send("HeapProfiler.collectGarbage");
  await delay(250);
  const [heap, dom] = await Promise.all([
    session.send("Runtime.getHeapUsage"),
    session.send("Memory.getDOMCounters")
  ]);
  return { cycle, heap, dom };
}

function subtractMetrics(after, before) {
  return Object.fromEntries(["TaskDuration", "ScriptDuration", "LayoutDuration", "RecalcStyleDuration", "LayoutCount", "RecalcStyleCount", "JSHeapUsedSize", "Nodes", "Documents"]
    .map((name) => [name, (after[name] ?? 0) - (before[name] ?? 0)]));
}

async function readRuntime(page) {
  return page.evaluate(() => globalThis.__resourceManagerFrontendPerformance.snapshot());
}

async function resetRuntime(page) {
  await page.evaluate(() => globalThis.__resourceManagerFrontendPerformance.reset());
}

function sameRuntimeIdentity(left, right) {
  return left.instanceId === right.instanceId
    && Number(left.processId) === Number(right.processId)
    && left.processStartUtcTicks === right.processStartUtcTicks
    && left.buildVersion === right.buildVersion;
}

function sameSubjectTopology(left, right) {
  const project = (value) => value.subjects
    .map((item) => `${item.role}:${item.processId}:${item.processStartUtcTicks}`)
    .sort();
  return JSON.stringify(project(left)) === JSON.stringify(project(right))
    && JSON.stringify([...left.listenerPids].sort((a, b) => a - b))
      === JSON.stringify([...right.listenerPids].sort((a, b) => a - b))
    && JSON.stringify([...left.backendListenerPids].sort((a, b) => a - b))
      === JSON.stringify([...right.backendListenerPids].sort((a, b) => a - b));
}

function isExpectedBackend(process) {
  return process?.name?.toLowerCase() === "resourcemanager.exe"
    && (process.executablePath === null
      || isPathWithin(applicationRoot, process.executablePath));
}

function isExpectedNativeUi(process) {
  return process?.name?.toLowerCase() === "resourcemanager.nativeui.exe"
    && (process.executablePath === null
      || isPathWithin(resolve(applicationRoot, "NativeUi"), process.executablePath));
}

function isPathWithin(root, candidate) {
  if (typeof candidate !== "string" || candidate.length === 0) return false;
  const path = relative(resolve(root), resolve(candidate));
  return path.length > 0 && path !== ".." && !path.startsWith(`..\\`) && !isAbsolute(path);
}

function maximumConsecutiveAffectedSamples(samples, longTasks) {
  let current = 0;
  let maximum = 0;
  for (const sample of samples) {
    const affected = longTasks.some((task) => task.at <= sample.finishedAt + 250 && task.at + task.durationMs >= sample.finishedAt - 250);
    current = affected ? current + 1 : 0;
    maximum = Math.max(maximum, current);
  }
  return maximum;
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

function percentile(sorted, probability) {
  if (sorted.length === 1) return sorted[0];
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  return sorted[lower] * (1 - (position - lower)) + sorted[upper] * (position - lower);
}

function linearSlope(values) {
  const samples = values.filter(Number.isFinite);
  if (samples.length < 2) return 0;
  const meanX = (samples.length - 1) / 2;
  const meanY = samples.reduce((sum, value) => sum + value, 0) / samples.length;
  let numerator = 0;
  let denominator = 0;
  for (let index = 0; index < samples.length; index += 1) {
    const x = index - meanX;
    numerator += x * (samples[index] - meanY);
    denominator += x * x;
  }
  return denominator === 0 ? 0 : numerator / denominator;
}

function countBy(values, selectKey) {
  const counts = Object.create(null);
  for (const value of values) {
    const key = String(selectKey(value));
    counts[key] = (counts[key] ?? 0) + 1;
  }
  return counts;
}

function gate(passed, actual, expected) {
  return { passed: Boolean(passed), actual, expected };
}

async function loadPlaywright() {
  const candidates = [
    process.env.RM_PLAYWRIGHT_MODULE,
    resolve(clientRoot, "node_modules/playwright/index.mjs"),
  ].filter(Boolean);
  const modulePath = candidates.find((candidate) => existsSync(candidate));
  if (!modulePath) throw new Error("Playwright is unavailable.");
  return import(pathToFileURL(modulePath).href);
}

function git(args) {
  return execFileSync("git", args, { cwd: repositoryRoot, encoding: "utf8" }).trim();
}

function encodePowerShell(script) {
  return Buffer.from(script, "utf16le").toString("base64");
}

function safePath(value) {
  try { return new URL(value).pathname; } catch { return "invalid-url"; }
}

async function captureRuntimeArtifactClosure() {
  const frontendRoot = resolve(applicationRoot, "wwwroot");
  const files = [
    resolve(applicationRoot, "bin/Debug/net10.0-windows/ResourceManager.exe"),
    resolve(applicationRoot, "../../src/UI/Core/bin/Debug/net10.0-windows/ResourceManager.NativeUi.exe"),
    ...await listFiles(frontendRoot)
  ];
  const entries = [];
  for (const path of [...new Set(files)].sort()) {
    const content = await readFile(path);
    entries.push({
      path: relative(repositoryRoot, path).replaceAll("\\", "/"),
      bytes: content.length,
      sha256: createHash("sha256").update(content).digest("hex").toUpperCase()
    });
  }
  return Object.freeze({ schemaVersion: 1, entries });
}

function parseExpectedCapacity(value) {
  if (!/^\d+$/.test(value)) throw new Error("Expected performance capacity must be a decimal integer.");
  const capacity = Number(value);
  if (!Number.isSafeInteger(capacity) || capacity < 256 || capacity > 100_000) {
    throw new Error("Expected performance capacity must be from 256 through 100000.");
  }
  return capacity;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function writeJson(name, value) {
  await writeFile(resolve(runRoot, name), `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

async function sealEvidence() {
  const files = await listFiles(runRoot);
  const lines = [];
  for (const path of files.filter((item) => basename(item) !== "manifest.sha256").sort()) {
    const content = await readFile(path);
    lines.push(`${createHash("sha256").update(content).digest("hex").toUpperCase()}  ${relative(runRoot, path).replaceAll("\\", "/")}`);
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
  return Number.isFinite(value) ? `${(value / 1024 / 1024).toFixed(2)} MiB` : "n/a";
}

function delay(milliseconds) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}
