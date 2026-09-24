export default async function cpuManualPlacementSubscriptionJourney(page, baseUrl) {
  const path = "/api/cpu/topology/subscribe";
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await updateScenario(baseUrl, { gpuPlacementEnabled: true, cpuTopologyEmpty: true });
  await page.setViewportSize({ width: 1100, height: 760 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" })
    .getByRole("button", { name: /一般应用/ }).click();
  const card = page.locator(".software-item").filter({ hasText: "示例软件" });
  await card.getByRole("button", { name: "设置和迁移" }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.getByRole("tab", { name: "策略", exact: true }).click();
  const placement = dialog.locator(".cpu-manual-placement");
  await placement.waitFor({ state: "visible" });

  const empty = await waitForRequests(baseUrl,
    (requests) => requests[path]?.publications >= 1, 65_000);
  assert(empty[path]?.publications >= 1,
    "The empty topology callback was not actually published");
  assert(await placement.locator(".cpu-manual-core").count() === 0,
    "Empty topology displayed invented CPU cores");
  assert(await placement.locator(".software-detail-empty").innerText() === "-",
    "Empty topology did not display its placeholder");
  assert(empty[path].logicalInFlight === 1 && empty[path].logicalMaxInFlight === 1,
    "Manual CPU placement did not own exactly one logical subscription");
  assert((empty["/api/cpu/topology"]?.started ?? 0) === 0,
    "Manual CPU placement used a one-shot topology read");

  await updateScenario(baseUrl, { cpuTopologyEmpty: false });
  await placement.getByText("Frontend Harness CPU", { exact: true })
    .waitFor({ state: "visible", timeout: 65_000 });
  assert(await placement.locator(".cpu-manual-core").count() === 2,
    "The normal callback did not restore the real fixture core choices");
  const recovered = await readRequests(baseUrl);
  assert(recovered[path].logicalStarted === empty[path].logicalStarted
    && recovered[path].publications > empty[path].publications,
  "Manual CPU placement replaced its subscription to recover from null");
  assert((recovered["/api/cpu/topology"]?.started ?? 0) === 0,
    "Manual CPU placement polled topology during recovery");

  const firstCore = placement.locator(".cpu-manual-core").first();
  await firstCore.getByLabel("固定", { exact: true }).check();
  assert(await firstCore.getByLabel("固定", { exact: true }).isChecked(),
    "Recovered topology could not edit the local CPU placement draft");
  await dialog.locator('button[aria-label="关闭"]').click();
  await dialog.waitFor({ state: "hidden" });
  const closed = await waitForRequests(baseUrl,
    (requests) => requests[path]?.logicalInFlight === 0, 6_500);
  assert(closed[path].logicalInFlight === 0 && closed[path].logicalRetired === 1,
    "Closing the policy dialog did not release its topology subscription");
  assert(errors.length === 0, `CPU placement page errors: ${JSON.stringify(errors)}`);
  return { empty: empty[path], recovered: recovered[path], closed: closed[path], pageErrors: errors };
}

async function updateScenario(baseUrl, patch) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(patch)
  });
  assert(response.ok, `Unable to update CPU placement fixture: ${response.status}`);
}

async function readRequests(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/requests`);
  assert(response.ok, `Unable to read CPU placement telemetry: ${response.status}`);
  return response.json();
}

async function waitForRequests(baseUrl, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let requests;
  while (Date.now() < deadline) {
    requests = await readRequests(baseUrl);
    if (predicate(requests)) {
      return requests;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Request telemetry did not reach the expected state: ${JSON.stringify(requests)}`);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
