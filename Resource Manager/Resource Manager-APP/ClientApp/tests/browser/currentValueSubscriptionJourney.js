export default async function currentValueSubscriptionJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
  const localStreamPath = "/api/local-system/status/subscribe";
  const localDirectPath = "/api/local-system/status";
  const schedulingStreamPath = "/api/adapters/resource-manager/scheduling/subscribe";
  const schedulingDirectPath = "/api/adapters/resource-manager/scheduling";
  const channelPath = "/api/subscriptions/stream";
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  const scenarioResponse = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ localSystemIntervalMs: 1_000, optimizationRuntime: true })
  });
  assert(scenarioResponse.ok,
    `Unable to configure current-value fixture: ${scenarioResponse.status}`);
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await Promise.all([
    waitForStarted(page, localStreamPath),
    waitForStarted(page, schedulingStreamPath)
  ]);
  await page.waitForFunction(() => {
    const value = document.querySelector("#captureTime")?.textContent?.trim();
    return Boolean(value && value !== "--");
  }, null, { timeout: 6_500 });

  const initial = {
    local: await readTelemetry(page, localStreamPath),
    scheduling: await readTelemetry(page, schedulingStreamPath),
    channel: await readTelemetry(page, channelPath),
    localDirect: await readTelemetry(page, localDirectPath),
    schedulingDirect: await readTelemetry(page, schedulingDirectPath),
    uptime: await page.locator("#captureTime").textContent()
  };
  assertSingleActiveSubscription(initial.local, "local system");
  assertSingleActiveSubscription(initial.scheduling, "self scheduling");
  assertSinglePhysicalChannel(initial.channel, "physical callback channel");
  assert(initial.localDirect.started === 0,
    `Local-system value used periodic/direct GET: ${JSON.stringify(initial.localDirect)}`);
  assert(initial.schedulingDirect.started === 0,
    `Self-scheduling value used periodic/direct GET: ${JSON.stringify(initial.schedulingDirect)}`);

  await resetTelemetry(page);
  await page.waitForTimeout(1_200);
  const settled = {
    local: await readTelemetry(page, localStreamPath),
    scheduling: await readTelemetry(page, schedulingStreamPath),
    channel: await readTelemetry(page, channelPath),
    localDirect: await readTelemetry(page, localDirectPath),
    schedulingDirect: await readTelemetry(page, schedulingDirectPath)
  };
  assertStableStream(settled.local, "settled local system");
  assertStableStream(settled.scheduling, "settled self scheduling");
  assertStablePhysicalChannel(settled.channel, "settled physical callback channel");
  assert(settled.localDirect.started === 0 && settled.schedulingDirect.started === 0,
    `Direct GET reappeared after subscription: ${JSON.stringify(settled)}`);

  await resetTelemetry(page);
  await page.getByRole("button", { name: "组件与软件" }).click();
  await Promise.all([
    waitForStarted(page, "/api/components"),
    waitForStarted(page, "/api/software")
  ]);
  const pageEntryReads = {
    components: await readTelemetry(page, "/api/components"),
    software: await readTelemetry(page, "/api/software")
  };
  await page.waitForTimeout(2_200);
  const afterIdle = {
    components: await readTelemetry(page, "/api/components"),
    software: await readTelemetry(page, "/api/software")
  };
  assert(afterIdle.components.started === pageEntryReads.components.started,
    `Component inventory resumed periodic GET: ${JSON.stringify({ pageEntryReads, afterIdle })}`);
  assert(afterIdle.software.started === pageEntryReads.software.started,
    `Software inventory resumed periodic GET: ${JSON.stringify({ pageEntryReads, afterIdle })}`);

  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "详细信息", exact: true }).click();
  await page.getByRole("tablist", { name: "详细信息分页" })
    .getByRole("tab", { name: "报告", exact: true }).click();
  const smartPanel = page.getByLabel("智能调度报告", { exact: true });
  await smartPanel.locator(".smart-details-summary").waitFor({ state: "visible" });
  const smartPath = "/api/optimization/smart/state/subscribe";
  const smartStarted = await readTelemetry(page, smartPath);
  await page.waitForTimeout(3_200);
  const smartContinued = await readTelemetry(page, smartPath);
  const smartDirect = await readTelemetry(page, "/api/optimization/smart/state");
  assert(smartContinued.publications > smartStarted.publications,
    `Smart report stopped receiving callbacks: ${JSON.stringify({ smartStarted, smartContinued })}`);
  assert(smartContinued.logicalStarted === smartStarted.logicalStarted
    && smartContinued.logicalMaxInFlight === 1 && smartDirect.started === 0,
  `Smart report reintroduced requests beside its subscription: ${JSON.stringify({ smartStarted, smartContinued, smartDirect })}`);
  assertSinglePhysicalChannel(await readTelemetry(page, channelPath), "smart report callback channel");
  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "监视控制台", exact: true }).click();
  await page.waitForFunction(async (path) => {
    const telemetry = await (await fetch("/__test/requests")).json();
    return telemetry[path]?.logicalInFlight === 0;
  }, smartPath);
  const smartRetired = await readTelemetry(page, smartPath);
  await page.waitForTimeout(3_200);
  const smartInactive = await readTelemetry(page, smartPath);
  assert(smartInactive.publications === smartRetired.publications,
    `Smart report kept receiving callbacks while inactive: ${JSON.stringify({ smartRetired, smartInactive })}`);
  assert(pageErrors.length === 0,
    `Current-value subscription journey page errors: ${JSON.stringify(pageErrors)}`);

  return { initial, settled, pageEntryReads, afterIdle,
    smartStarted, smartContinued, smartDirect, smartRetired, smartInactive };
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

function assertStableStream(telemetry, label) {
  assert(telemetry.started === 1
    && telemetry.inFlight === 1
    && telemetry.maxInFlight === 1
    && telemetry.aborted === 0,
  `${label} did not retain one current-value stream: ${JSON.stringify(telemetry)}`);
}

function assertStablePhysicalChannel(telemetry, label) {
  assert(telemetry.physicalStarted === 1
    && telemetry.physicalInFlight === 1
    && telemetry.physicalMaxInFlight === 1
    && telemetry.logicalInFlight === 0
    && telemetry.physicalAborted === 0,
  `${label} did not retain one physical connection: ${JSON.stringify(telemetry)}`);
}

async function resetTelemetry(page) {
  await page.evaluate(async () => {
    const response = await fetch("/__test/requests/reset", { method: "POST" });
    if (!response.ok) {
      throw new Error(`Unable to reset telemetry: ${response.status}`);
    }
  });
}

async function waitForStarted(page, path) {
  const deadline = Date.now() + 6_500;
  let telemetry = await readTelemetry(page, path);
  while (Date.now() < deadline) {
    if (telemetry.started >= 1) {
      return telemetry;
    }
    await page.waitForTimeout(50);
    telemetry = await readTelemetry(page, path);
  }
  throw new Error(`Timed out waiting for ${path}: ${JSON.stringify(telemetry)}`);
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
      statuses: [],
      publications: 0
    };
  }, path);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
