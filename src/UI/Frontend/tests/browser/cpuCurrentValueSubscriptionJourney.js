export default async function cpuCurrentValueSubscriptionJourney(
  page,
  baseUrl = "http://127.0.0.1:4177"
) {
  const topologySubscriptionPath = "/api/cpu/topology/subscribe";
  const residencySubscriptionPath = "/api/cpu/residency/subscribe";
  const topologyDirectPath = "/api/cpu/topology";
  const residencyDirectPath = "/api/cpu/residency";
  const channelPath = "/api/subscriptions/stream";
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  await page.setViewportSize({ width: 1100, height: 760 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await resetTelemetry(page);

  const pageNavigation = page.getByRole("navigation", { name: "页面切换" });
  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  const tabs = page.getByRole("tablist", { name: "详细信息分页" });
  await tabs.getByRole("tab", { name: "CPU 拓扑", exact: true }).click();
  const cpuPanel = page.getByLabel("CPU 拓扑模型");
  assert(!/读取中|正在加载|线程活动暂时不可用/.test(await cpuPanel.innerText()),
    "CPU subscription values were replaced with a frontend sampling status");
  // Allow the first aligned callback of the existing 60-second topology subscription.
  await cpuPanel.getByText("Frontend Harness CPU", { exact: true })
    .waitFor({ state: "visible", timeout: 65_000 });
  await page.waitForFunction(() => {
    const header = document.querySelector(".cpu-topology-header span");
    return header?.textContent?.includes("2C / 4T") === true;
  }, null, { timeout: 6_500 });

  const initial = {
    topology: await waitForTelemetry(page, topologySubscriptionPath, (value) =>
      value.logicalInFlight === 1 && value.publications >= 1),
    residency: await waitForTelemetry(page, residencySubscriptionPath, (value) =>
      value.logicalInFlight === 1 && value.publications >= 1),
    channel: await readTelemetry(page, channelPath),
    topologyDirect: await readTelemetry(page, topologyDirectPath),
    residencyDirect: await readTelemetry(page, residencyDirectPath)
  };
  assertSingleLogicalSubscription(initial.topology, "CPU topology");
  assertSingleLogicalSubscription(initial.residency, "CPU residency");
  assertSinglePhysicalChannel(initial.channel);
  assert(initial.topologyDirect.started === 0 && initial.residencyDirect.started === 0,
    `CPU current values used direct GET: ${JSON.stringify(initial)}`);

  await cpuPanel.getByRole("button", {
    name: /^核心 0，使用率 .*，性能 .*，执行时长 900 ms$/
  }).waitFor({ state: "visible", timeout: 6_500 });
  await cpuPanel.getByRole("button", {
    name: "逻辑处理器 0，执行时长 700 ms",
    exact: true
  }).waitFor({ state: "visible", timeout: 6_500 });
  const coreZeroProcesses = cpuPanel.locator(".cpu-core").first()
    .locator(".cpu-process-score-list span");
  assert(await coreZeroProcesses.count() === 2,
    "The accepted residency value did not render both process durations");
  assert(await coreZeroProcesses.nth(0).locator("small").innerText() === "example.exe"
    && await coreZeroProcesses.nth(0).locator("strong").innerText() === "800 ms"
    && await coreZeroProcesses.nth(1).locator("small").innerText() === "slower-same-switches.exe"
    && await coreZeroProcesses.nth(1).locator("strong").innerText() === "100 ms",
  `Residency rows were not ordered and rendered by execution time: ${JSON.stringify(
    await coreZeroProcesses.allInnerTexts())}`);
  assert(!/frontend-harness|provider|Warming|Unavailable/.test(await cpuPanel.innerText()),
    "CPU residency provider diagnostics leaked into the normal page");

  const scrollBefore = await page.evaluate(() => ({ x: scrollX, y: scrollY }));
  const coreButton = cpuPanel.getByRole("button", { name: /^核心 0，使用率/ });
  await coreButton.click();
  const scrollAfter = await page.evaluate(() => ({ x: scrollX, y: scrollY }));
  assert(scrollAfter.x === scrollBefore.x && scrollAfter.y === scrollBefore.y,
    `CPU interaction changed document scroll: ${JSON.stringify({ scrollBefore, scrollAfter })}`);

  await setScenario(page, { cpuTopologyEmpty: true });
  await page.waitForFunction(() => {
    const header = document.querySelector(".cpu-topology-header span");
    return header?.textContent?.trim() === "-";
  }, null, { timeout: 65_000 });
  assert(await cpuPanel.getByRole("button", { name: /^核心 0，使用率/ }).count() === 0,
    "A null topology callback retained the old processor diagram");
  assert(!/读取中|正在加载|不可用|重试/.test(await cpuPanel.innerText()),
    "A null topology callback introduced sampling status UI");
  const empty = await readTelemetry(page, topologySubscriptionPath);
  assert(empty.publications > initial.topology.publications,
    "The empty topology was not delivered by the existing callback");

  await setScenario(page, { cpuTopologyEmpty: false });
  await cpuPanel.getByText("Frontend Harness CPU", { exact: true })
    .waitFor({ state: "visible", timeout: 65_000 });
  const recovered = await readTelemetry(page, topologySubscriptionPath);
  const recoveryChannel = await readTelemetry(page, channelPath);
  assert(recovered.logicalStarted === initial.topology.logicalStarted
    && recovered.publications > empty.publications,
  `Null recovery recreated the logical subscription: ${JSON.stringify({ initial, empty, recovered })}`);
  assert(recoveryChannel.physicalStarted === initial.channel.physicalStarted,
    `Null recovery reconnected the shared channel: ${JSON.stringify(recoveryChannel)}`);
  assert((await readTelemetry(page, topologyDirectPath)).started === 0,
    "Null recovery issued a direct topology read");

  await pageNavigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  const retired = {
    topology: await waitForTelemetry(page, topologySubscriptionPath, (value) =>
      value.logicalInFlight === 0 && value.logicalRetired >= 1),
    residency: await waitForTelemetry(page, residencySubscriptionPath, (value) =>
      value.logicalInFlight === 0 && value.logicalRetired >= 1)
  };
  const publicationCounts = {
    topology: retired.topology.publications,
    residency: retired.residency.publications
  };
  await page.waitForTimeout(1_200);
  const inactive = {
    topology: await readTelemetry(page, topologySubscriptionPath),
    residency: await readTelemetry(page, residencySubscriptionPath)
  };
  assert(inactive.topology.publications === publicationCounts.topology
    && inactive.residency.publications === publicationCounts.residency,
  `CPU callbacks continued without demand: ${JSON.stringify({ retired, inactive })}`);

  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await tabs.getByRole("tab", { name: "CPU 拓扑", exact: true }).click();
  await cpuPanel.getByText("Frontend Harness CPU", { exact: true })
    .waitFor({ state: "visible", timeout: 65_000 });
  const reactivated = {
    topology: await waitForTelemetry(page, topologySubscriptionPath, (value) =>
      value.logicalStarted >= 2 && value.logicalInFlight === 1
        && value.publications > publicationCounts.topology),
    residency: await waitForTelemetry(page, residencySubscriptionPath, (value) =>
      value.logicalStarted >= 2 && value.logicalInFlight === 1),
    channel: await readTelemetry(page, channelPath),
    topologyDirect: await readTelemetry(page, topologyDirectPath),
    residencyDirect: await readTelemetry(page, residencyDirectPath)
  };
  assert(reactivated.topology.logicalMaxInFlight === 1
    && reactivated.residency.logicalMaxInFlight === 1,
  `CPU logical subscriptions overlapped on reactivation: ${JSON.stringify(reactivated)}`);
  assertSinglePhysicalChannel(reactivated.channel);
  assert(reactivated.topologyDirect.started === 0 && reactivated.residencyDirect.started === 0,
    `CPU direct GET appeared after reactivation: ${JSON.stringify(reactivated)}`);
  assert(pageErrors.length === 0,
    `CPU current-value journey page errors: ${JSON.stringify(pageErrors)}`);

  return { initial, scrollBefore, scrollAfter, empty, recovered, recoveryChannel, retired, inactive, reactivated };
}

async function setScenario(page, patch) {
  const result = await page.evaluate(async (value) => {
    const response = await fetch("/__test/scenario", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(value)
    });
    return { ok: response.ok, status: response.status };
  }, patch);
  assert(result.ok, `Unable to set CPU callback fixture: ${JSON.stringify(result)}`);
}

async function resetTelemetry(page) {
  const response = await page.evaluate(async () => {
    const result = await fetch("/__test/requests/reset", { method: "POST" });
    return { ok: result.ok, status: result.status };
  });
  assert(response.ok, `Unable to reset request telemetry: ${JSON.stringify(response)}`);
}

async function waitForTelemetry(page, path, predicate) {
  const deadline = Date.now() + 6_500;
  let value = await readTelemetry(page, path);
  while (Date.now() < deadline) {
    if (predicate(value)) {
      return value;
    }
    await page.waitForTimeout(50);
    value = await readTelemetry(page, path);
  }
  throw new Error(`Timed out waiting for ${path}: ${JSON.stringify(value)}`);
}

async function readTelemetry(page, path) {
  return page.evaluate(async (requestPath) => {
    const response = await fetch("/__test/requests");
    const telemetry = await response.json();
    return telemetry[requestPath] ?? {
      physicalStarted: 0,
      physicalCompleted: 0,
      physicalAborted: 0,
      physicalInFlight: 0,
      physicalMaxInFlight: 0,
      logicalStarted: 0,
      logicalRetired: 0,
      logicalInFlight: 0,
      logicalMaxInFlight: 0,
      started: 0,
      completed: 0,
      aborted: 0,
      inFlight: 0,
      maxInFlight: 0,
      publications: 0,
      statuses: []
    };
  }, path);
}

function assertSingleLogicalSubscription(telemetry, label) {
  assert(telemetry.logicalStarted >= 1
    && telemetry.logicalInFlight === 1
    && telemetry.logicalMaxInFlight === 1,
  `${label} did not retain one logical subscription: ${JSON.stringify(telemetry)}`);
}

function assertSinglePhysicalChannel(telemetry) {
  assert(telemetry.physicalStarted >= 1
    && telemetry.physicalInFlight === 1
    && telemetry.physicalMaxInFlight === 1
    && telemetry.physicalCompleted === 0
    && telemetry.physicalStarted === telemetry.physicalAborted + 1,
  `Shared callback channel was not single-flight: ${JSON.stringify(telemetry)}`);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
