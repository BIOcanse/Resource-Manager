import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import usabilityProbe from "./usabilityProbe.js";
import { isExpectedNavigationCancellation } from "./liveNativeUiDiagnostics.mjs";

const clientRoot = fileURLToPath(new URL("../..", import.meta.url));
const repositoryRoot = resolve(clientRoot, "../../..");
const endpoint = process.env.RM_NATIVE_UI_CDP_ENDPOINT?.trim() || "http://127.0.0.1:9333";
const expectedFrontendUrl = process.env.RM_EXPECTED_FRONTEND_URL?.trim()
  || "http://127.0.0.1:9321/";
const observationMilliseconds = boundedInteger(
  process.env.RM_LIVE_OBSERVATION_MS,
  15_000,
  5_000,
  120_000);
const runId = new Date().toISOString().replaceAll(":", "-").replaceAll(".", "-");
const artifactRoot = resolve(
  process.env.RM_LIVE_ARTIFACT_DIR?.trim()
    || resolve(repositoryRoot, ".artifacts/tests/live-native-ui", runId));

mkdirSync(artifactRoot, { recursive: true });

const diagnostics = {
  consoleErrors: [],
  pageErrors: [],
  requestFailures: [],
  expectedNavigationCancellations: [],
  apiFailures: [],
  apiResponseCount: 0,
  apiResponseCounts: {}
};
let report = null;
let transitionTargetPageId = null;
let transitionDeadlineMilliseconds = 0;

try {
  const { chromium } = await loadPlaywright();
  const browser = await chromium.connectOverCDP(endpoint);
  const pages = browser.contexts().flatMap(context => context.pages());
  const candidates = pages.filter(page => page.url().startsWith(expectedFrontendUrl));
  assert(candidates.length === 1,
    `Expected one Resource Manager page at ${expectedFrontendUrl}; found ${candidates.length}.`);
  const page = candidates[0];

  page.on("console", message => {
    if (message.type() === "error") {
      diagnostics.consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", error => diagnostics.pageErrors.push(String(error)));
  page.on("requestfailed", request => {
    const record = {
      method: request.method(),
      url: request.url(),
      failure: request.failure()?.errorText ?? "unknown"
    };
    if (isExpectedNavigationCancellation(record, {
      expectedOrigin: new URL(expectedFrontendUrl).origin,
      transitionTargetPageId,
      transitionDeadlineMilliseconds,
      observedAtMilliseconds: Date.now()
    })) {
      diagnostics.expectedNavigationCancellations.push({
        ...record,
        transitionTargetPageId
      });
      return;
    }
    diagnostics.requestFailures.push(record);
  });
  page.on("response", response => {
    const url = new URL(response.url());
    if (url.origin !== new URL(expectedFrontendUrl).origin || !url.pathname.startsWith("/api/")) {
      return;
    }
    const record = { status: response.status(), path: url.pathname };
    const key = `${record.status} ${record.path}`;
    diagnostics.apiResponseCount += 1;
    diagnostics.apiResponseCounts[key] = (diagnostics.apiResponseCounts[key] ?? 0) + 1;
    if (response.status() >= 400) {
      diagnostics.apiFailures.push(record);
    }
  });

  await page.waitForFunction(() => document.readyState === "complete");
  const startup = await inspectStartup(page);
  assert(startup.url === expectedFrontendUrl,
    `Unexpected frontend URL: ${startup.url}`);
  assert(startup.capabilities.profileId === "full" && startup.capabilities.readOnly === false,
    `The live instance is not using the full startup profile: ${JSON.stringify(startup.capabilities)}`);

  const pageMatrix = [];
  const expectedPageIds = ["monitor", "components", "optimization", "details", "settings"];
  const navigation = page.locator(".shell-page");
  assert(await navigation.count() === expectedPageIds.length,
    `Expected ${expectedPageIds.length} primary pages.`);
  for (let index = 0; index < expectedPageIds.length; index += 1) {
    const expectedPageId = expectedPageIds[index];
    transitionTargetPageId = expectedPageId;
    transitionDeadlineMilliseconds = Date.now() + 2_000;
    await navigation.nth(index).click();
    await page.waitForFunction(
      pageId => document.body.dataset.page === pageId,
      expectedPageId);
    await page.waitForTimeout(250);
    const dom = await inspectPageDom(page);
    assert(dom.pageId === expectedPageId,
      `Navigation selected '${dom.pageId}' instead of '${expectedPageId}'.`);
    assert(dom.headingCount > 0, `Page '${expectedPageId}' has no visible heading.`);
    assert(dom.visibleUnnamedControls.length === 0,
      `Page '${expectedPageId}' has unnamed controls: ${JSON.stringify(dom.visibleUnnamedControls)}`);
    assert(!dom.documentOverflow && !dom.shellOverflow,
      `Page '${expectedPageId}' has horizontal overflow: ${JSON.stringify(dom)}`);
    await page.screenshot({ path: resolve(artifactRoot, `${index}-${expectedPageId}.png`) });
    pageMatrix.push(dom);
  }

  transitionTargetPageId = "monitor";
  transitionDeadlineMilliseconds = Date.now() + 2_000;
  await navigation.nth(0).click();
  await page.waitForFunction(() => document.body.dataset.page === "monitor");
  await page.evaluate(() => {
    const shell = document.querySelector(".app-shell");
    if (shell) shell.scrollTop = 0;
  });

  const metricContinuity = await observeCoreMetrics(page, observationMilliseconds);
  const resourceMonitor = await activateResourceMonitor(page, diagnostics);
  const monitorUsability = await usabilityProbe(page);
  await page.screenshot({ path: resolve(artifactRoot, "monitor-resource-list.png") });

  assert(diagnostics.consoleErrors.length === 0,
    `Console errors occurred: ${JSON.stringify(diagnostics.consoleErrors)}`);
  assert(diagnostics.pageErrors.length === 0,
    `Page errors occurred: ${JSON.stringify(diagnostics.pageErrors)}`);
  assert(diagnostics.requestFailures.length === 0,
    `Requests failed: ${JSON.stringify(diagnostics.requestFailures)}`);
  assert(diagnostics.expectedNavigationCancellations.length <= expectedPageIds.length * 4,
    `Navigation cancellation count is unbounded: ${JSON.stringify(diagnostics.expectedNavigationCancellations)}`);
  assert(diagnostics.apiFailures.length === 0,
    `API requests returned failures: ${JSON.stringify(diagnostics.apiFailures)}`);

  report = {
    contract: "resource-manager-live-native-ui-usability-v2",
    passed: true,
    endpoint,
    expectedFrontendUrl,
    artifactRoot,
    startup,
    pageMatrix,
    metricContinuity,
    resourceMonitor,
    monitorUsability,
    diagnostics
  };
  writeReport(report);
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
  process.exit(0);
} catch (error) {
  report = {
    contract: "resource-manager-live-native-ui-usability-v2",
    passed: false,
    endpoint,
    expectedFrontendUrl,
    artifactRoot,
    error: String(error?.stack || error),
    diagnostics
  };
  writeReport(report);
  process.stderr.write(`${JSON.stringify(report, null, 2)}\n`);
  process.exit(1);
}

async function inspectStartup(page) {
  return page.evaluate(async () => {
    const capabilitiesResponse = await fetch(
      "/api/runtime/capabilities",
      { cache: "no-store" });
    if (!capabilitiesResponse.ok) {
      throw new Error("Backend runtime capabilities are unavailable.");
    }
    return {
      url: location.href,
      title: document.title,
      readyState: document.readyState,
      language: document.documentElement.lang,
      capabilities: await capabilitiesResponse.json()
    };
  });
}

async function inspectPageDom(page) {
  return page.evaluate(() => {
    const isVisible = element => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0
        && style.visibility !== "hidden" && style.display !== "none";
    };
    const accessibleName = element => {
      const labelledBy = element.getAttribute("aria-labelledby");
      if (labelledBy) {
        return labelledBy.split(/\s+/)
          .map(id => document.getElementById(id)?.textContent?.trim() ?? "")
          .join(" ").trim();
      }
      const id = element.getAttribute("id");
      const explicitLabel = id
        ? document.querySelector(`label[for='${CSS.escape(id)}']`)?.textContent?.trim()
        : "";
      return element.getAttribute("aria-label")?.trim()
        || explicitLabel
        || element.closest("label")?.textContent?.trim()
        || element.getAttribute("title")?.trim()
        || element.textContent?.trim()
        || "";
    };
    const shell = document.querySelector(".app-shell");
    const unnamed = Array.from(document.querySelectorAll(
      "button, input, select, textarea, [role='button'], [role='radio'], [role='tab'], [role='listbox']"))
      .filter(element => isVisible(element) && !accessibleName(element))
      .map(element => element.outerHTML.slice(0, 240));
    return {
      pageId: document.body.dataset.page ?? "unknown",
      headingCount: Array.from(document.querySelectorAll("h1, h2"))
        .filter(isVisible).length,
      headings: Array.from(document.querySelectorAll("h1, h2"))
        .filter(isVisible).map(element => element.textContent?.trim() ?? ""),
      visibleUnnamedControls: unnamed,
      documentOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      shellOverflow: Boolean(shell && shell.scrollWidth > shell.clientWidth),
      viewport: { width: innerWidth, height: innerHeight },
      shell: shell && {
        clientWidth: shell.clientWidth,
        scrollWidth: shell.scrollWidth,
        clientHeight: shell.clientHeight,
        scrollHeight: shell.scrollHeight
      }
    };
  });
}

async function observeCoreMetrics(page, durationMilliseconds) {
  const samples = [];
  const deadline = Date.now() + durationMilliseconds;
  do {
    const sample = await page.evaluate(() => {
      const value = id => document.querySelector(
        `.metric-card[data-pointer-reorder-id='${id}'] .metric-main .metric-value`)?.textContent?.trim() ?? "";
      return { at: new Date().toISOString(), cpu: value("cpu"), memory: value("memory") };
    });
    assert(sample.cpu && sample.cpu !== "N/A" && sample.cpu !== "--",
      `CPU metric lost its published value: ${JSON.stringify(sample)}`);
    assert(sample.memory && sample.memory !== "N/A" && sample.memory !== "--",
      `Memory metric lost its published value: ${JSON.stringify(sample)}`);
    samples.push(sample);
    if (Date.now() < deadline) await page.waitForTimeout(1_000);
  } while (Date.now() < deadline);
  return { durationMilliseconds, sampleCount: samples.length, samples };
}

async function activateResourceMonitor(page, diagnosticsState) {
  const resourceResponseKey = "200 /api/resource-monitor/subscribe";
  const snapshotResponseKey = "200 /api/resource-monitor/snapshot";
  const responseStart = diagnosticsState.apiResponseCounts[resourceResponseKey] ?? 0;
  const snapshotResponseStart = diagnosticsState.apiResponseCounts[snapshotResponseKey] ?? 0;
  const bars = page.locator(".resource-breakdown-panel");
  await bars.scrollIntoViewIfNeeded();
  await bars.waitFor({ state: "visible", timeout: 10_000 });

  const list = page.locator(".resource-table-frame[role='table']");
  await list.scrollIntoViewIfNeeded();
  await list.waitFor({ state: "visible", timeout: 10_000 });
  await page.waitForFunction(() => document.querySelectorAll(
    ".resource-table-frame[role='table'] .resource-table-row:not(.summary-row)").length > 0);
  await page.waitForTimeout(1_000);

  const openedResourceSubscription = (
    diagnosticsState.apiResponseCounts[resourceResponseKey] ?? 0) > responseStart;
  assert(openedResourceSubscription,
    "Mounted resource work did not open a successful resource-monitor push stream.");
  assert((diagnosticsState.apiResponseCounts[snapshotResponseKey] ?? 0) === snapshotResponseStart,
    "Mounted resource work used the retired resource-monitor snapshot GET.");

  return page.evaluate(() => {
    const barsPanel = document.querySelector(".resource-breakdown-panel");
    const table = document.querySelector(".resource-table-frame[role='table']");
    const statusText = document.querySelector("#resourceTableStatus")?.textContent?.trim() ?? "";
    return {
      barsReady: Boolean(barsPanel),
      listReady: Boolean(table),
      statusText,
      visibleResourceRows: table?.querySelectorAll(
        ".resource-table-row:not(.summary-row)").length ?? 0,
      ariaRowCount: table?.getAttribute("aria-rowcount") ?? null,
      ariaColumnCount: table?.getAttribute("aria-colcount") ?? null
    };
  });
}

async function loadPlaywright() {
  const candidates = [
    process.env.RM_PLAYWRIGHT_MODULE,
    resolve(clientRoot, "node_modules/playwright/index.mjs"),
  ].filter(Boolean);
  const modulePath = candidates.find(candidate => existsSync(candidate));
  if (!modulePath) {
    throw new Error("Playwright is unavailable. Set RM_PLAYWRIGHT_MODULE.");
  }
  return import(pathToFileURL(modulePath).href);
}

function boundedInteger(value, fallback, minimum, maximum) {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isInteger(parsed) && parsed >= minimum && parsed <= maximum
    ? parsed
    : fallback;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function writeReport(value) {
  writeFileSync(resolve(artifactRoot, "report.json"), `${JSON.stringify(value, null, 2)}\n`, "utf8");
}
