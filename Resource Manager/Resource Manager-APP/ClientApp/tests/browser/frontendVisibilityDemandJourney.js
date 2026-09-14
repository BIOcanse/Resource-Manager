export default async function frontendVisibilityDemandJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
  const metricSubscriptionPath = "/api/metrics/subscribe";
  const metricSnapshotPath = "/api/metrics/snapshot";
  const resourceSubscriptionPath = "/api/resource-monitor/subscribe";
  const resourceSnapshotPath = "/api/resource-monitor/snapshot";
  const retiredVisibleRegionPath = "/api/adapters/resource-manager/visible-regions";
  const channelPath = "/api/subscriptions/stream";
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const dashboard = page.locator('.monitor-work-region[aria-label="实时指标"]');
  const resourceTable = page.locator('.monitor-work-region[aria-label="资源列表"]');
  await dashboard.waitFor({ state: "visible", timeout: 5_000 });
  await resourceTable.waitFor({ state: "attached", timeout: 5_000 });

  const attributes = await page.evaluate(() => ({
    monitorRegions: document.querySelectorAll(".monitor-work-region").length,
    monitorVisibilityOwners: document.querySelectorAll(
      ".monitor-work-region[data-frontend-visibility-surface], .monitor-work-region [data-frontend-visibility-surface]").length,
    retiredMarks: document.querySelectorAll("[data-frontend-mark], [data-frontend-partitions]").length
  }));
  assert(attributes.monitorRegions === 3
    && attributes.monitorVisibilityOwners === 0
    && attributes.retiredMarks === 0,
  `Monitor page retained section-level visibility ownership: ${JSON.stringify(attributes)}`);

  await waitForStarted(page, metricSubscriptionPath, 1);
  await waitForStarted(page, resourceSubscriptionPath, 2);
  await page.waitForTimeout(1_250);
  const initial = await readTelemetry(page, metricSubscriptionPath);
  const initialResource = await readTelemetry(page, resourceSubscriptionPath);
  const initialChannel = await readTelemetry(page, channelPath);
  assertSingleActiveSubscription(initial, "metric push stream");
  assert(initial.publications >= 1,
    `Monitor page did not receive a metric callback: ${JSON.stringify(initial)}`);
  assertSinglePhysicalChannel(initialChannel, "physical callback channel");
  assert((await readTelemetry(page, metricSnapshotPath)).started === 0,
    "Monitor page used the retired metric snapshot request.");
  assert(initialResource.started >= 2
    && initialResource.inFlight === 2
    && initialResource.aborted === initialResource.started - 2
    && initialResource.maxInFlight === 2
    && activeQueryCount(initialResource) === 2
    && maximumQueryConcurrency(initialResource) === 1
    && initialResource.publications >= 2,
  `Initial resource subscriptions did not settle after the single detail-query handoff: ${JSON.stringify(initialResource)}`);
  assert((await readTelemetry(page, resourceSnapshotPath)).started === 0,
    "Monitor page used the retired resource snapshot request.");

  await resourceTable.scrollIntoViewIfNeeded();
  await page.waitForFunction(() => {
    const shell = document.querySelector(".app-shell");
    const dashboardSurface = document.querySelector(
      '.monitor-work-region[aria-label="实时指标"]');
    const tableSurface = document.querySelector(
      '.monitor-work-region[aria-label="资源列表"]');
    if (!(shell instanceof HTMLElement)
      || !(dashboardSurface instanceof HTMLElement)
      || !(tableSurface instanceof HTMLElement)) {
      return false;
    }
    const root = shell.getBoundingClientRect();
    const dashboardRect = dashboardSurface.getBoundingClientRect();
    const tableRect = tableSurface.getBoundingClientRect();
    const intersects = (rect) => rect.bottom > root.top
      && rect.top < root.bottom
      && rect.right > root.left
      && rect.left < root.right;
    return !intersects(dashboardRect) && intersects(tableRect);
  }, null, { timeout: 5_000 });
  await page.waitForTimeout(150);
  await resetTelemetry(page);
  await page.waitForTimeout(1_250);
  const whileDashboardOffscreen = await readTelemetry(page, metricSubscriptionPath);
  const resourceWhileDashboardOffscreen = await readTelemetry(page, resourceSubscriptionPath);
  assertStableMetricStream(whileDashboardOffscreen, "dashboard offscreen");
  assertStableResourceStreams(resourceWhileDashboardOffscreen, "dashboard offscreen");

  await resetTelemetry(page);
  await dashboard.scrollIntoViewIfNeeded();
  await page.waitForTimeout(1_250);
  const afterScrollBack = await readTelemetry(page, metricSubscriptionPath);
  const resourceAfterScrollBack = await readTelemetry(page, resourceSubscriptionPath);
  assertStableMetricStream(afterScrollBack, "after scrolling back");
  assertStableResourceStreams(resourceAfterScrollBack, "after scrolling back");
  assert((await readTelemetry(page, metricSnapshotPath)).started === 0,
    "Scrolling used the retired metric snapshot request.");

  const retiredTransport = await readTelemetry(page, retiredVisibleRegionPath);
  assert(retiredTransport.started === 0,
    `Frontend visibility leaked into the retired backend Mark route: ${JSON.stringify(retiredTransport)}`);
  assert(pageErrors.length === 0, `Visibility journey page errors: ${JSON.stringify(pageErrors)}`);

  return {
    attributes,
    initial,
    initialResource,
    initialChannel,
    whileDashboardOffscreen,
    resourceWhileDashboardOffscreen,
    afterScrollBack,
    resourceAfterScrollBack,
    retiredTransport
  };
}

function assertSingleActiveSubscription(telemetry, label) {
  assert(telemetry.started >= 1
    && telemetry.inFlight === 1
    && telemetry.maxInFlight === 1
    && telemetry.completed === 0
    && telemetry.started === telemetry.aborted + 1,
  `${label} did not retain one active subscription: ${JSON.stringify(telemetry)}`);
}

function assertSinglePhysicalChannel(telemetry, label) {
  assert(telemetry.physicalStarted >= 1
    && telemetry.physicalInFlight === 1
    && telemetry.physicalMaxInFlight === 1
    && telemetry.logicalInFlight === 0
    && telemetry.physicalCompleted === 0
    && telemetry.physicalStarted === telemetry.physicalAborted + 1,
  `${label} did not retain one physical connection: ${JSON.stringify(telemetry)}`);
}

function activeQueryCount(telemetry) {
  return Object.values(telemetry.inFlightByQuery ?? {})
    .filter((count) => count > 0)
    .length;
}

function maximumQueryConcurrency(telemetry) {
  return Math.max(0, ...Object.values(telemetry.maxInFlightByQuery ?? {}));
}

function assertStableMetricStream(telemetry, stage) {
  assert(telemetry.started === 1
    && telemetry.inFlight === 1
    && telemetry.maxInFlight === 1
    && telemetry.aborted === 0
    && telemetry.publications >= 1,
  `Metric push stream changed ownership while ${stage}: ${JSON.stringify(telemetry)}`);
}

function assertStableResourceStreams(telemetry, stage) {
  assert(telemetry.started === 2
    && telemetry.inFlight === 2
    && telemetry.maxInFlight === 2
    && telemetry.aborted === 0
    && new Set(telemetry.queries).size === 2
    && telemetry.publications >= 2,
  `Resource push streams changed ownership during ${stage}: ${JSON.stringify(telemetry)}`);
}

async function resetTelemetry(page) {
  const result = await page.evaluate(async () => {
    const response = await fetch("/__test/requests/reset", { method: "POST" });
    return { ok: response.ok, status: response.status };
  });
  assert(result.ok, `Unable to reset request telemetry: ${JSON.stringify(result)}`);
}

async function waitForStarted(page, path, minimumStarted) {
  const deadline = Date.now() + 5_000;
  let telemetry = await readTelemetry(page, path);
  while (Date.now() < deadline) {
    if (telemetry.started >= minimumStarted) {
      return telemetry;
    }
    await page.waitForTimeout(50);
    telemetry = await readTelemetry(page, path);
  }
  throw new Error(
    `Timed out waiting for ${path} request ${minimumStarted}: ${JSON.stringify(telemetry)}`);
}

async function readTelemetry(page, path) {
  return page.evaluate(async (requestPath) => {
    const response = await fetch("/__test/requests");
    const telemetry = await response.json();
    return telemetry[requestPath] ?? {
      started: 0,
      completed: 0,
      aborted: 0,
      inFlight: 0,
      maxInFlight: 0,
      startTimes: [],
      queries: [],
      statuses: []
    };
  }, path);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
