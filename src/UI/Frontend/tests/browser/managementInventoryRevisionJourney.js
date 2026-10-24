import assert from "node:assert/strict";

const revisionPath = "/api/management/inventory-revisions/subscribe";
const componentsPath = "/api/components";
const softwarePath = "/api/software";
const browserRuntimesPath = "/api/browser-runtimes";

export default async function managementInventoryRevisionJourney(page, baseUrl) {
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await waitForReadCompletion(baseUrl, componentsPath, 1);
  await waitForReadCompletion(baseUrl, softwarePath, 1);
  await waitForLogicalSubscription(baseUrl, 1);

  await resetTelemetry(baseUrl);
  await publishRevisions(baseUrl, { softwareRevision: "1", componentRevision: "1" });
  await waitForReadCompletion(baseUrl, componentsPath, 1);
  await waitForReadCompletion(baseUrl, softwarePath, 1);
  const visibleInventory = await readTelemetry(baseUrl);
  assert.equal(visibleInventory[componentsPath].started, 1,
    `Visible components should reread once for a changed fact: ${JSON.stringify(visibleInventory)}`);
  assert.equal(visibleInventory[softwarePath].started, 1,
    `Visible software should reread once for a changed fact: ${JSON.stringify(visibleInventory)}`);
  assert.equal(visibleInventory[browserRuntimesPath]?.started ?? 0, 0,
    `Hidden browser runtime list should remain unread: ${JSON.stringify(visibleInventory)}`);

  await resetTelemetry(baseUrl);
  await resetTelemetry(baseUrl);
  const visibleHeldRead = await holdNextComponentRead(page);
  await publishRevisions(baseUrl, { componentRevision: "2" });
  await visibleHeldRead.captured;
  await publishRevisions(baseUrl, { componentRevision: "3" });
  await delay(100);
  visibleHeldRead.release();
  await waitForReadCompletion(baseUrl, componentsPath, 2);
  const visibleInFlight = await readTelemetry(baseUrl);
  assert.equal(visibleInFlight[componentsPath].started, 2,
    `A component change arriving during a visible read should drain one additional read: ${JSON.stringify(visibleInFlight)}`);
  await page.unroute("**/api/components");

  await resetTelemetry(baseUrl);
  const hiddenHeldRead = await holdNextComponentRead(page);
  await publishRevisions(baseUrl, { componentRevision: "4" });
  await hiddenHeldRead.captured;
  await publishRevisions(baseUrl, { componentRevision: "5" });
  await delay(100);
  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "监视控制台", exact: true }).click();
  await waitForLogicalSubscription(baseUrl, 0);
  hiddenHeldRead.release();
  await waitForReadCompletion(baseUrl, componentsPath, 1);
  await delay(200);
  const hiddenQueuedRead = await readTelemetry(baseUrl);
  assert.equal(hiddenQueuedRead[componentsPath].started, 1,
    `A queued inventory reread should be dropped after the list becomes hidden: ${JSON.stringify(hiddenQueuedRead)}`);
  await page.unroute("**/api/components");

  await resetTelemetry(baseUrl);
  await publishRevisions(baseUrl, { softwareRevision: "2", componentRevision: "6" });
  await delay(200);
  const hiddenInventory = await readTelemetry(baseUrl);
  assert.equal(hiddenInventory[componentsPath]?.started ?? 0, 0,
    `Hidden components should not reread after a publication: ${JSON.stringify(hiddenInventory)}`);
  assert.equal(hiddenInventory[softwarePath]?.started ?? 0, 0,
    `Hidden software should not reread after a publication: ${JSON.stringify(hiddenInventory)}`);

  await page.getByRole("navigation", { name: "页面切换" })
    .getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.locator("#managementTab-BrowserRuntime").click();
  await waitForReadCompletion(baseUrl, browserRuntimesPath, 1);
  await waitForLogicalSubscription(baseUrl, 1);
  await resetTelemetry(baseUrl);
  await publishRevisions(baseUrl, {
    softwareRevision: "3",
    componentRevision: "7",
    browserRuntimeRevision: "1"
  });
  await waitForReadCompletion(baseUrl, browserRuntimesPath, 1);
  await waitForReadCompletion(baseUrl, componentsPath, 1);
  await delay(200);
  const visibleBrowserRuntime = await readTelemetry(baseUrl);
  assert.equal(visibleBrowserRuntime[browserRuntimesPath].started, 1,
    `Visible browser runtimes should reread once for a changed fact: ${JSON.stringify(visibleBrowserRuntime)}`);
  // The runtime page shows its install component, so a component change rereads the components once.
  assert.equal(visibleBrowserRuntime[componentsPath]?.started ?? 0, 1,
    `The runtime page's install component should reread once for a changed fact: ${JSON.stringify(visibleBrowserRuntime)}`);
  assert.equal(visibleBrowserRuntime[softwarePath]?.started ?? 0, 0,
    `Hidden software should remain unread on the browser runtime tab: ${JSON.stringify(visibleBrowserRuntime)}`);
  assert.deepEqual(pageErrors, []);

  return { visibleInventory, visibleInFlight, hiddenQueuedRead, hiddenInventory, visibleBrowserRuntime };
}

async function publishRevisions(baseUrl, revisions) {
  const response = await fetch(`${baseUrl}/__test/management-inventory-revisions/publish`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(revisions)
  });
  assert(response.ok, `Revision fixture rejected publication: ${response.status}`);
}

async function resetTelemetry(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/requests/reset`, { method: "POST" });
  assert(response.ok, `Unable to reset request telemetry: ${response.status}`);
}

async function waitForReadCompletion(baseUrl, path, completedCount) {
  await waitForTelemetry(baseUrl, telemetry =>
    (telemetry[path]?.completed ?? 0) >= completedCount
      && (telemetry[path]?.inFlight ?? 0) === 0,
  `read ${path} completion ${completedCount}`);
}

async function waitForLogicalSubscription(baseUrl, inFlightCount) {
  await waitForTelemetry(baseUrl, telemetry =>
    (telemetry[revisionPath]?.logicalInFlight ?? 0) === inFlightCount,
  `logical inventory subscription count ${inFlightCount}`);
}

async function waitForTelemetry(baseUrl, predicate, description) {
  const deadline = Date.now() + 8_000;
  let telemetry;
  while (Date.now() < deadline) {
    telemetry = await readTelemetry(baseUrl);
    if (predicate(telemetry)) return telemetry;
    await delay(50);
  }
  throw new Error(`Timed out waiting for ${description}: ${JSON.stringify(telemetry)}`);
}

async function readTelemetry(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/requests`);
  assert(response.ok, `Unable to read request telemetry: ${response.status}`);
  return response.json();
}

async function holdNextComponentRead(page) {
  let markCaptured;
  let releaseRequest;
  let didHold = false;
  const captured = new Promise(resolve => { markCaptured = resolve; });
  const held = new Promise(resolve => { releaseRequest = resolve; });
  await page.route("**/api/components", async route => {
    if (!didHold) {
      didHold = true;
      markCaptured();
      await held;
    }
    await route.continue();
  });
  return { captured, release: releaseRequest };
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
