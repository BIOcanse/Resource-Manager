export default async function deviceTopologySourceJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const subscriptionPath = "/api/device-topology/state/subscribe";
  const directPath = "/api/device-topology/state";
  const channelPath = "/api/subscriptions/stream";
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await setScenario(page, {
    capabilities: "ready",
    settings: "ready",
    deviceTopologyDelayMs: 0
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await resetTelemetry(page);

  const pageNavigation = page.getByRole("navigation", { name: "页面切换" });
  const topology = page.locator(".device-topology-panel");
  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await page.locator('body[data-page="details"]').waitFor({ state: "attached", timeout: 5_000 });
  await topology.waitFor({ state: "visible", timeout: 5_000 });
  const topologyHeader = topology.locator(".device-topology-header");
  await topologyHeader.locator("span").filter({ hasText: "Frontend Harness" })
    .waitFor({ state: "visible", timeout: 5_000 });
  const firstActivation = await waitForTelemetry(page, subscriptionPath, (value) =>
    value.logicalInFlight === 1 && value.publications >= 1);
  const firstChannel = await readTelemetry(page, channelPath);
  const firstDirect = await readTelemetry(page, directPath);
  assertSingleLogicalSubscription(firstActivation, "device topology");
  assertSinglePhysicalChannel(firstChannel);
  assert(firstDirect.started === 0,
    `Device topology used a direct GET instead of its callback value: ${JSON.stringify(firstDirect)}`);

  const dom = {
    heading: (await topology.getByRole("heading", { name: "设备拓扑", exact: true })
      .textContent())?.trim(),
    brand: (await topologyHeader.locator("span").textContent())?.trim(),
    manufacturer: (await topology.getByLabel("电脑俯视图").locator("strong")
      .textContent())?.trim(),
    model: (await topology.getByLabel("电脑俯视图").locator("small")
      .textContent())?.trim(),
    portText: (await topology.getByLabel("外部接口清单").textContent())?.trim(),
    detailText: (await topology.getByLabel("拓扑详情").textContent())?.trim(),
    loadingVisible: await topology.locator(".device-topology-loading").count() > 0,
    errorVisible: await topology.locator(".observation-state.error").count() > 0
  };
  assert(dom.heading === "设备拓扑"
    && dom.brand === "Frontend Harness"
    && dom.manufacturer === "Frontend Harness"
    && dom.model === "Fixture PC"
    && dom.portText?.includes("HDMI 接口")
    && dom.detailText?.includes("HDMI 接口")
    && !dom.loadingVisible
    && !dom.errorVisible,
  `Strict device topology data did not reach the DOM: ${JSON.stringify(dom)}`);

  const longTransport = "48 Gbps FRL，支持 7680 × 4320、可变刷新率与高动态范围传输";
  const hdmiItem = topology.locator(".device-port-item").filter({ hasText: "HDMI 接口" }).first();
  const longTextPresentation = await hdmiItem.evaluate((element, expectedText) => {
    const secondary = element.querySelector("small");
    const itemRect = element.getBoundingClientRect();
    const secondaryRect = secondary?.getBoundingClientRect();
    return {
      text: secondary?.textContent?.trim(),
      itemWidth: itemRect.width,
      itemBottom: itemRect.bottom,
      secondaryWidth: secondaryRect?.width ?? 0,
      secondaryBottom: secondaryRect?.bottom ?? 0,
      secondaryScrollWidth: secondary?.scrollWidth ?? 0,
      secondaryClientWidth: secondary?.clientWidth ?? 0,
      wraps: secondary
        ? secondary.getBoundingClientRect().height > parseFloat(getComputedStyle(secondary).lineHeight) + 1
        : false,
      containsExpectedText: secondary?.textContent?.includes(expectedText) ?? false
    };
  }, longTransport);
  assert(longTextPresentation.containsExpectedText
    && longTextPresentation.secondaryWidth <= longTextPresentation.itemWidth
    && longTextPresentation.secondaryBottom <= longTextPresentation.itemBottom + 1
    && longTextPresentation.secondaryScrollWidth <= longTextPresentation.secondaryClientWidth + 1
    && longTextPresentation.wraps,
  `Long device description is clipped or unrecoverable: ${JSON.stringify(longTextPresentation)}`);
  await hdmiItem.focus();
  await page.keyboard.press("Enter");
  const selectedDetails = (await topology.getByLabel("拓扑详情").textContent())?.trim();
  assert(selectedDetails?.includes(longTransport),
    `Keyboard selection did not expose the full device description: ${selectedDetails}`);

  const active = await waitForTelemetry(page, subscriptionPath, (value) => value.publications >= 2);
  assert(active.started === 1
    && active.logicalStarted === 1
    && active.logicalInFlight === 1
    && active.logicalMaxInFlight === 1,
  `Device topology created duplicate logical subscriptions: ${JSON.stringify(active)}`);

  await pageNavigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  await topology.waitFor({ state: "detached", timeout: 3_000 });
  await page.locator('body[data-page="monitor"]').waitFor({ state: "attached", timeout: 3_000 });
  const retired = await waitForTelemetry(page, subscriptionPath, (value) =>
    value.logicalInFlight === 0 && value.logicalRetired >= 1);
  const retiredPublicationCount = retired.publications;
  await page.waitForTimeout(1_200);
  const inactive = await readTelemetry(page, subscriptionPath);
  assert(inactive.publications === retiredPublicationCount && inactive.logicalInFlight === 0,
    `Device topology callbacks continued without demand: ${JSON.stringify({ retired, inactive })}`);

  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await topology.waitFor({ state: "visible", timeout: 3_000 });
  const reactivated = await waitForTelemetry(page, subscriptionPath, (value) =>
    value.logicalStarted >= 2 && value.logicalInFlight === 1 && value.publications > inactive.publications);
  assert(reactivated.logicalMaxInFlight === 1,
    `Device topology reactivation overlapped logical subscriptions: ${JSON.stringify(reactivated)}`);
  const reactivatedChannel = await readTelemetry(page, channelPath);
  assertSinglePhysicalChannel(reactivatedChannel);

  const finalDirect = await readTelemetry(page, directPath);
  assert(finalDirect.started === 0,
    `Device topology callback display was overwritten by a direct GET: ${JSON.stringify(finalDirect)}`);
  assert(pageErrors.length === 0,
    `Device topology browser failures: ${JSON.stringify(pageErrors)}`);

  return {
    dom,
    longTextPresentation,
    firstActivation,
    firstChannel,
    active,
    inactive,
    reactivated,
    reactivatedChannel,
    finalDirect
  };
}

async function setScenario(page, patch) {
  const result = await page.evaluate(async (value) => {
    const response = await fetch("/__test/scenario", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    return { ok: response.ok, status: response.status };
  }, patch);
  assert(result.ok, `Unable to set frontend scenario: ${JSON.stringify(result)}`);
}

async function resetTelemetry(page) {
  const result = await page.evaluate(async () => {
    const response = await fetch("/__test/requests/reset", { method: "POST" });
    return { ok: response.ok, status: response.status };
  });
  assert(result.ok, `Unable to reset request telemetry: ${JSON.stringify(result)}`);
}

async function waitForTelemetry(page, path, predicate) {
  const deadline = Date.now() + 6_500;
  let actual = await readTelemetry(page, path);
  while (Date.now() < deadline) {
    if (predicate(actual)) {
      return actual;
    }
    await page.waitForTimeout(50);
    actual = await readTelemetry(page, path);
  }
  throw new Error(`Timed out waiting for ${path}: ${JSON.stringify(actual)}`);
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
  `${label} did not retain exactly one logical subscription: ${JSON.stringify(telemetry)}`);
}

function assertSinglePhysicalChannel(telemetry) {
  assert(telemetry.physicalStarted >= 1
    && telemetry.physicalInFlight === 1
    && telemetry.physicalMaxInFlight === 1
    && telemetry.physicalCompleted === 0
    && telemetry.physicalStarted === telemetry.physicalAborted + 1,
  `Shared callback transport was not single-flight: ${JSON.stringify(telemetry)}`);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
