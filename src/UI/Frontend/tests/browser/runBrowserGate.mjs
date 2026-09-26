import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import cpuCurrentValueSubscriptionJourney from "./cpuCurrentValueSubscriptionJourney.js";
import cpuManualPlacementSubscriptionJourney from "./cpuManualPlacementSubscriptionJourney.js";
import creditsJourney from "./creditsJourney.js";
import currentValueSubscriptionJourney from "./currentValueSubscriptionJourney.js";
import monitorCurrentValueRetentionJourney from "./monitorCurrentValueRetentionJourney.js";
import deviceTopologySourceJourney from "./deviceTopologySourceJourney.js";
import frontendUsabilityJourney from "./frontendUsabilityJourney.js";
import frontendVisibilityDemandJourney from "./frontendVisibilityDemandJourney.js";
import optimizationReportsJourney from "./optimizationReportsJourney.js";
import r2RemediationJourney from "./r2RemediationJourney.js";
import resourceBarColorModesJourney from "./resourceBarColorModesJourney.js";
import resourceBarProportionJourney from "./resourceBarProportionJourney.js";
import controlReadingsJourney from "./controlReadingsJourney.js";
import controlCardJourney from "./controlCardJourney.js";
import resourceLayoutTaskJourney from "./resourceLayoutTaskJourney.js";
import resourceTableContentStateJourney from "./resourceTableContentStateJourney.js";
import resourceTableScrollStabilityJourney from "./resourceTableScrollStabilityJourney.js";
import settingsSourceJourney from "./settingsSourceJourney.js";
import softwareIssueJourney from "./softwareIssueJourney.js";
import taskCenterJourney from "./taskCenterJourney.js";
import themeConsistencyJourney from "./themeConsistencyJourney.js";
import uiPrimitivesJourney from "./uiPrimitivesJourney.js";
import usabilityProbe from "./usabilityProbe.js";

const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const allJourneys = [
  ["credits", creditsJourney],
  ["frontend-usability", frontendUsabilityJourney],
  ["ui-primitives", uiPrimitivesJourney],
  ["settings-source", settingsSourceJourney],
  ["resource-bar-color-modes", resourceBarColorModesJourney],
  ["resource-bar-proportion", resourceBarProportionJourney],
  ["control-readings", controlReadingsJourney],
  ["control-card", controlCardJourney],
  ["theme-consistency", themeConsistencyJourney],
  ["software-issues", softwareIssueJourney],
  ["device-topology-source", deviceTopologySourceJourney],
  ["cpu-current-value-subscription", cpuCurrentValueSubscriptionJourney],
  ["cpu-manual-placement-subscription", cpuManualPlacementSubscriptionJourney],
  ["current-value-subscription", currentValueSubscriptionJourney],
  ["monitor-current-value-retention", monitorCurrentValueRetentionJourney],
  ["frontend-visibility-demand", frontendVisibilityDemandJourney],
  ["optimization-reports", optimizationReportsJourney],
  ["task-center", taskCenterJourney],
  ["resource-layout-task", resourceLayoutTaskJourney],
  ["resource-table-content-state", resourceTableContentStateJourney],
  ["resource-table-scroll-stability", resourceTableScrollStabilityJourney],
  ["r2-remediation", r2RemediationJourney]
];
const journeyFilter = process.env.RM_BROWSER_JOURNEY?.trim();
const journeys = journeyFilter
  ? allJourneys.filter(([name]) => name === journeyFilter)
  : allJourneys;
if (journeys.length === 0) {
  throw new Error(`Unknown browser journey: ${journeyFilter}`);
}

const port = await reservePort();
const baseUrl = `http://127.0.0.1:${port}`;
const server = spawn(process.execPath, [resolve(projectRoot, "tests/browser/mockFrontendServer.mjs")], {
  cwd: projectRoot,
  env: { ...process.env, RM_FRONTEND_HARNESS_PORT: String(port) },
  stdio: ["ignore", "pipe", "pipe"]
});
let serverOutput = "";
server.stdout.on("data", (chunk) => { serverOutput += chunk; });
server.stderr.on("data", (chunk) => { serverOutput += chunk; });

let browser;
try {
  await waitForServer(baseUrl, server);
  const { chromium } = await loadPlaywright();
  browser = await launchBrowser(chromium);
  const results = [];

  for (const [name, journey] of journeys) {
    await resetFixture(baseUrl);
    const context = await browser.newContext();
    const page = await context.newPage();
    try {
      const result = await journey(page, baseUrl);
      const usability = await usabilityProbe(page);
      const defaultPerformanceState = await readDefaultPerformanceState(page);
      if (defaultPerformanceState.bootstrapPresent
        || defaultPerformanceState.apiPresent
        || defaultPerformanceState.monitorResources.length > 0) {
        throw new Error(
          `Default frontend unexpectedly enabled performance instrumentation: ${JSON.stringify(defaultPerformanceState)}`);
      }
      results.push({ name, result, usability, defaultPerformanceState });
      process.stdout.write(`PASS ${name}\n`);
    } catch (error) {
      const [requests, pageState] = await Promise.all([
        fetch(`${baseUrl}/__test/requests`)
          .then(async (response) => response.ok ? response.json() : ({ status: response.status }))
          .catch((requestError) => ({ error: String(requestError) })),
        page.evaluate(() => ({
          href: location.href,
          title: document.title,
          text: document.body?.innerText?.slice(0, 4000) ?? "",
          readyState: document.readyState
        })).catch((pageError) => ({ error: String(pageError) }))
      ]);
      throw new Error(
        `${error instanceof Error ? error.message : String(error)}\n`
        + `Browser gate diagnostic: ${JSON.stringify({ name, requests, pageState, serverOutput })}`,
        { cause: error });
    } finally {
      await context.close();
    }
  }

  process.stdout.write(`${JSON.stringify({
    baseUrl,
    journeys: results.map(({ name, result, usability, defaultPerformanceState }) => ({
      name,
      result,
      defaultPerformanceState,
      pageId: usability.pageId,
      viewport: usability.viewport,
      unnamedControlCount: usability.visibleUnnamedInputs.length,
      documentOverflow: usability.document.scrollWidth > usability.document.clientWidth,
      shellOverflow: Boolean(usability.shell && usability.shell.scrollWidth > usability.shell.clientWidth)
    }))
  }, null, 2)}\n`);
} finally {
  await browser?.close();
  await stopChildProcess(server);
}

async function readDefaultPerformanceState(page) {
  return page.evaluate(() => ({
    bootstrapPresent: Object.hasOwn(
      globalThis,
      "__resourceManagerFrontendPerformanceBootstrap"),
    apiPresent: Object.hasOwn(
      globalThis,
      "__resourceManagerFrontendPerformance"),
    monitorResources: performance.getEntriesByType("resource")
      .map((entry) => entry.name)
      .filter((name) => name.includes("FrontendPerformanceMonitor-"))
  }));
}

async function loadPlaywright() {
  const candidates = [
    process.env.RM_PLAYWRIGHT_MODULE,
    resolve(projectRoot, "node_modules/playwright/index.mjs"),
  ].filter(Boolean);
  const modulePath = candidates.find((candidate) => existsSync(candidate));
  if (!modulePath) {
    throw new Error("Playwright is unavailable. Set RM_PLAYWRIGHT_MODULE or install Playwright in the frontend project.");
  }
  return import(pathToFileURL(modulePath).href);
}

async function launchBrowser(chromium) {
  try {
    return await chromium.launch({ headless: true });
  } catch (error) {
    if (!String(error).includes("Executable doesn't exist")) {
      throw error;
    }
  }

  const systemBrowser = [
    process.env.RM_PLAYWRIGHT_BROWSER,
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"
  ].filter(Boolean).find((candidate) => existsSync(candidate));
  if (!systemBrowser) {
    throw new Error("No Playwright browser or supported system Edge/Chrome executable is available.");
  }
  return chromium.launch({ headless: true, executablePath: systemBrowser });
}

async function reservePort() {
  const server = createServer();
  await new Promise((resolveListen, rejectListen) => {
    server.once("error", rejectListen);
    server.listen(0, "127.0.0.1", resolveListen);
  });
  const address = server.address();
  const selectedPort = typeof address === "object" && address ? address.port : 0;
  await new Promise((resolveClose, rejectClose) => server.close((error) => error ? rejectClose(error) : resolveClose()));
  if (!selectedPort) {
    throw new Error("Unable to reserve a browser-gate port.");
  }
  return selectedPort;
}

async function waitForServer(baseUrl, processHandle) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    if (processHandle.exitCode !== null) {
      throw new Error(`Frontend harness exited early (${processHandle.exitCode}).\n${serverOutput}`);
    }
    try {
      const response = await fetch(`${baseUrl}/__test/scenario`);
      if (response.ok) {
        return;
      }
    } catch {
      // The harness has not bound the reserved port yet.
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
  }
  throw new Error(`Frontend harness did not become ready.\n${serverOutput}`);
}

async function resetFixture(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario/reset`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`Unable to reset the browser fixture: ${response.status}`);
  }
}

async function stopChildProcess(processHandle) {
  if (processHandle.exitCode === null) {
    const exited = new Promise((resolveExit) => processHandle.once("exit", resolveExit));
    processHandle.kill();
    await Promise.race([exited, new Promise((resolveDelay) => setTimeout(resolveDelay, 3000))]);
    if (processHandle.exitCode === null) {
      processHandle.kill("SIGKILL");
      await Promise.race([exited, new Promise((resolveDelay) => setTimeout(resolveDelay, 1000))]);
    }
  }
  processHandle.stdout.destroy();
  processHandle.stderr.destroy();
}
