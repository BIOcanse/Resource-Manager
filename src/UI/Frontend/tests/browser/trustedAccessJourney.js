import assert from "node:assert/strict";
import decisions from "../fixtures/trustedAccessDecisions.json" with { type: "json" };
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

const defaultScopes = [
  "read", "metrics.read", "processes.read", "session", "adapters.register.self",
  "desktop.publish", "gpu.resolve", "gpu.report", "keys.manage"
];
const allScopes = [...defaultScopes, "machine.write", "injection.configure"];
const iso = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();

export default async function trustedAccessJourney(page, baseUrl) {
  const errors = [];
  const calls = [];
  const defaultSecret = "native-default-secret-must-never-reach-web-content";
  const independentSecret = "independent-one-time-secret";
  const rotatedSecret = "rotated-independent-secret";
  let nextKey = 1;
  let nextDefault = 1;
  let defaultEnabled = true;
  let defaultKeyId = "default-1";
  let keys = [
    makeDefault(defaultKeyId),
    {
      id: "script-1", name: "Local report script", purpose: "control",
      scopes: ["read", "metrics.read"], createdAt: iso(-60_000), isDefault: false
    },
    {
      id: "revoke-1", name: "Temporary helper", purpose: "control",
      scopes: ["read"], createdAt: iso(-30_000), isDefault: false
    }
  ];
  let pendingRequests = [];
  const keySnapshot = () => ({
    keys,
    defaultEnabled,
    defaultKeyId,
    availableScopes: allScopes
  });
  const publishPendingRequests = async requests => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ accessRequests: requests })
    });
    assert.equal(response.ok, true, "the mock subscription publishes the pending access request snapshot");
  };

  const settings = await loadSettingsText("zh-CN");
  const copy = settings.systemIntegration.trustedAccess;
  page.on("pageerror", error => errors.push(String(error)));
  await page.addInitScript(() => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: async value => { globalThis.__trustedAccessClipboard = value; } }
    });
  });

  await page.route("**/api/access-keys**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const body = request.postDataJSON?.() ?? null;
    calls.push({ path: url.pathname, method, body });

    if (url.pathname === "/api/access-keys" && method === "GET") {
      return route.fulfill({ json: keySnapshot() });
    }
    if (url.pathname === "/api/access-keys" && method === "POST") {
      assert.equal(body.purpose, "control");
      const key = {
        id: `created-${nextKey++}`, name: body.name, purpose: "control",
        scopes: body.scopes, createdAt: iso(), isDefault: false
      };
      keys = [...keys, key];
      return route.fulfill({ json: { key, apiKey: independentSecret } });
    }
    if (url.pathname === "/api/access-keys/default" && method === "POST") {
      if (!body.enabled) {
        defaultEnabled = false;
        keys = keys.filter(key => !key.isDefault);
        defaultKeyId = null;
      } else if (!defaultEnabled || body.rotate) {
        defaultEnabled = true;
        defaultKeyId = `default-${++nextDefault}`;
        keys = [...keys.filter(key => !key.isDefault), makeDefault(defaultKeyId)];
      }
      // The native default secret never appears in the HTTP response body.
      return route.fulfill({ json: { defaultEnabled, defaultKeyId } });
    }
    const rotate = url.pathname.match(/^\/api\/access-keys\/([^/]+)\/rotate$/);
    if (rotate && method === "POST") {
      const oldKey = keys.find(key => key.id === decodeURIComponent(rotate[1]));
      assert.ok(oldKey && !oldKey.isDefault, "only an independent key can be rotated through this route");
      const key = { ...oldKey, id: `rotated-${nextKey++}`, createdAt: iso() };
      keys = [...keys.filter(candidate => candidate.id !== oldKey.id), key];
      return route.fulfill({ json: { key, apiKey: rotatedSecret } });
    }
    const revoke = url.pathname.match(/^\/api\/access-keys\/([^/]+)$/);
    if (revoke && method === "DELETE") {
      const id = decodeURIComponent(revoke[1]);
      assert.ok(keys.some(key => key.id === id && !key.isDefault));
      keys = keys.filter(key => key.id !== id);
      return route.fulfill({ status: 204 });
    }
    return route.continue();
  });

  await page.route("**/api/access-requests/*/approve", async route => {
    const request = route.request();
    const id = new URL(request.url()).pathname.split("/").at(-2);
    const body = request.postDataJSON();
    calls.push({ path: new URL(request.url()).pathname, method: request.method(), body });
    if (id === "req-expired") {
      pendingRequests = pendingRequests.filter(item => item.requestId !== id);
      await publishPendingRequests(pendingRequests);
      return route.fulfill({ status: 409, json: decisions.expired });
    }
    assert.equal(id, "req-narrow");
    assert.deepEqual(body.scopes, ["read"], "approval includes only the selected requested scope");
    pendingRequests = pendingRequests.filter(item => item.requestId !== id);
    await publishPendingRequests(pendingRequests);
    keys = [...keys, decisions.approved.key];
    return route.fulfill({ json: decisions.approved });
  });

  await page.route("**/api/access-requests/*/deny", async route => {
    const request = route.request();
    const id = new URL(request.url()).pathname.split("/").at(-2);
    const body = request.postDataJSON();
    calls.push({ path: new URL(request.url()).pathname, method: request.method(), body });
    assert.equal(id, "req-denied");
    assert.equal(body.reasonCode, "unsupported");
    pendingRequests = pendingRequests.filter(item => item.requestId !== id);
    await publishPendingRequests(pendingRequests);
    return route.fulfill({ json: decisions.denied });
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForResponse(response => {
    const body = response.request().postDataJSON?.();
    return response.url().includes("/api/subscriptions/stream")
      && body?.subscriptions?.some(item => item.path.startsWith("/api/access-requests/subscribe")) === true;
  });
  assert.equal(await page.locator("body").getAttribute("data-page"), "monitor",
    "the third-party request arrives while the user is outside settings");
  pendingRequests = [makeRequest("req-narrow", "Telemetry helper", ["read", "metrics.read"])];
  await publishPendingRequests(pendingRequests);
  const streamTelemetry = await page.evaluate(async () => (await fetch("/__test/requests")).json());
  const accessRequestSets = streamTelemetry["/api/subscriptions/stream"]?.subscriptionSets
    ?.filter(items => items.includes("/api/access-requests/subscribe")) ?? [];
  assert.ok(accessRequestSets.length > 0, "the pending banner is fed by the shared subscription stream");
  assert.ok(accessRequestSets.every(items => items.filter(path => path === "/api/access-requests/subscribe").length === 1),
    "the banner and manager share one source subscription");
  const banner = page.getByRole("region", { name: copy.requestsTitle, exact: true });
  await banner.getByText("Telemetry helper").waitFor();
  if (process.env.RM_TRUSTED_ACCESS_BANNER_SCREENSHOT) {
    await page.screenshot({ path: process.env.RM_TRUSTED_ACCESS_BANNER_SCREENSHOT, fullPage: true });
  }
  await banner.getByRole("button", { name: copy.reviewRequests, exact: true }).click();
  await page.waitForFunction(() => document.body.dataset.page === "settings");
  await page.locator(".trusted-access-notification").waitFor({ state: "detached" });
  const panel = page.getByRole("region", { name: copy.title, exact: true });
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await panel.getByText(copy.defaultKeyName, { exact: true }).waitFor();
  await panel.getByText(copy.defaultAccessLossWarning, { exact: true }).waitFor();
  await panel.getByText("UserData/Security/default-key", { exact: true }).waitFor();
  assert.doesNotMatch(await panel.innerText(), new RegExp(defaultSecret));
  assert.ok(await panel.getByText("Telemetry helper", { exact: true }).count() > 0);
  assert.ok(await panel.getByText("C:\\Tools\\telemetry.exe", { exact: true }).count() > 0);
  assert.ok(await panel.getByText("4321", { exact: true }).count() > 0);

  const firstRequest = panel.locator(".trusted-access-request").filter({ hasText: "Telemetry helper" });
  const requestScopeGroup = firstRequest.getByRole("group", { name: copy.requestScopesLabel });
  await requestScopeGroup.getByLabel(copy.scopeLabels["metrics.read"]).uncheck();
  await firstRequest.getByRole("button", { name: copy.approve, exact: true }).click();
  await panel.locator(".trusted-access-outcome.approved")
    .filter({ hasText: copy.requestApproved }).waitFor();
  const approvedKeyRow = panel.locator(".trusted-access-key-row").filter({ hasText: "Telemetry helper" });
  await approvedKeyRow.waitFor();
  assert.equal(await approvedKeyRow.getByText(copy.scopeLabels["read"], { exact: true }).count(), 1,
    "approval adds the granted key to the visible key list");

  pendingRequests = [makeRequest("req-expired", "Expired helper", ["read"])];
  await publishPendingRequests(pendingRequests);
  const expiredRequest = panel.locator(".trusted-access-request").filter({ hasText: "Expired helper" });
  await expiredRequest.waitFor();
  await expiredRequest.getByRole("button", { name: copy.approve, exact: true }).click();
  await panel.locator(".trusted-access-outcome.expired")
    .filter({ hasText: copy.requestExpired }).waitFor();

  pendingRequests = [makeRequest("req-denied", "Unsupported helper", ["processes.read"])];
  await publishPendingRequests(pendingRequests);
  const deniedRequest = panel.locator(".trusted-access-request").filter({ hasText: "Unsupported helper" });
  await deniedRequest.waitFor();
  await deniedRequest.getByLabel(copy.denyReasonLabel).selectOption("unsupported");
  await deniedRequest.getByRole("button", { name: copy.deny, exact: true }).click();
  const deniedOutcome = panel.locator(".trusted-access-outcome.denied");
  await deniedOutcome.filter({ hasText: copy.requestDenied }).waitFor();
  await deniedOutcome.filter({ hasText: copy.denialReasons.unsupported }).waitFor();
  await panel.getByText(copy.requestsEmpty, { exact: true }).waitFor();
  if (process.env.RM_TRUSTED_ACCESS_MANAGER_SCREENSHOT) {
    await page.screenshot({ path: process.env.RM_TRUSTED_ACCESS_MANAGER_SCREENSHOT, fullPage: true });
  }

  const createPanel = panel.locator(".trusted-access-create");
  await createPanel.getByRole("textbox", { name: copy.nameLabel }).fill("Read-only report");
  const createScopeGroup = createPanel.getByRole("group", { name: copy.createScopesLabel });
  for (const scope of allScopes) {
    const checkbox = createScopeGroup.getByLabel(copy.scopeLabels[scope] ?? scope);
    if (scope === "read") await checkbox.check();
    else await checkbox.uncheck();
  }
  await createPanel.getByRole("button", { name: copy.create, exact: true }).click();
  await panel.getByText(independentSecret, { exact: true }).waitFor();
  await panel.getByRole("button", { name: copy.copy, exact: true }).click();
  await page.waitForFunction(value => globalThis.__trustedAccessClipboard === value, independentSecret);
  const createCall = calls.find(call => call.path === "/api/access-keys" && call.method === "POST");
  assert.deepEqual(createCall?.body, { name: "Read-only report", purpose: "control", scopes: ["read"] });

  const rotateRow = panel.locator(".trusted-access-key-row").filter({ hasText: "Local report script" });
  await rotateRow.getByRole("button", { name: `${copy.rotate} · Local report script`, exact: true }).click();
  await panel.getByText(rotatedSecret, { exact: true }).waitFor();

  const revokeRow = panel.locator(".trusted-access-key-row").filter({ hasText: "Temporary helper" });
  await revokeRow.getByRole("button", { name: `${copy.revoke} · Temporary helper`, exact: true }).click();
  await revokeRow.waitFor({ state: "detached" });

  await panel.getByRole("button", { name: copy.disableDefault, exact: true }).click();
  await panel.getByText(copy.defaultDisabled, { exact: true }).waitFor();
  await panel.getByRole("button", { name: copy.enableDefault, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await panel.getByRole("button", { name: copy.regenerateDefault, exact: true }).click();
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  assert.doesNotMatch(await panel.innerText(), new RegExp(defaultSecret));
  assert.equal(await page.locator("#root").evaluate((node, secret) => node.innerHTML.includes(secret), defaultSecret), false,
    "the default file secret never appears in rendered frontend content");
  assert.equal(calls.some(call => call.path.includes("acquire-default")), false,
    "the frontend never requests the native-only default secret");
  assert.deepEqual(errors, []);
  return {
    requests: ["approved-subset", "expired", "denied"],
    keyActions: ["create-one-time", "rotate", "revoke", "disable-default", "enable-default", "regenerate-default"],
    approvalScopes: createCall?.body.scopes,
    defaultSecretRendered: false
  };
}

function makeDefault(id) {
  return {
    id, name: "Native default", purpose: "control", scopes: defaultScopes,
    createdAt: iso(-120_000), isDefault: true
  };
}

function makeRequest(requestId, name, requestedScopes) {
  return {
    requestId,
    name,
    requestedScopes,
    observedExecutablePath: "C:\\Tools\\telemetry.exe",
    observedProcessId: 4321,
    submittedAt: iso(),
    expiresAt: iso(300_000)
  };
}

