import assert from "node:assert/strict";
import decisions from "../fixtures/trustedAccessDecisions.json" with { type: "json" };
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

const defaultScopes = [
  "read", "metrics.read", "processes.read", "session", "adapters.register.self",
  "desktop.publish", "gpu.resolve", "gpu.report", "keys.manage"
];
const allScopes = [...defaultScopes, "machine.write", "injection.configure"];
const iso = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();

export default async function trustedAccessJourney(page, baseUrl) {
  // 保持设置分包未就绪，越过旧实现的两帧窗口后才放行。
  const coldRouteFrameCount = 3;
  let releaseSettingsChunk;
  const settingsChunkReady = new Promise(resolve => { releaseSettingsChunk = resolve; });
  let settingsChunkIntercepted = false;
  await page.route("**/assets/SettingsWorkspace-*.js", async route => {
    settingsChunkIntercepted = true;
    await settingsChunkReady;
    await route.continue();
  });
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
  const appCopy = await loadAppCopy("zh-CN");
  const feedback = appCopy.feedback;
  const activeElement = () => page.evaluate(() => ({
    id: document.activeElement?.id ?? "",
    text: document.activeElement?.textContent?.trim() ?? "",
    label: document.activeElement?.getAttribute("aria-label") ?? "",
    keyAction: document.activeElement?.getAttribute("data-key-action") ?? ""
  }));
  // 破坏性操作先弹出确认；confirm=false 时取消，确认框关闭且不发请求。
  const answerConfirmation = async (title, confirmLabel, confirm = true) => {
    const dialog = page.getByRole("dialog", { name: title, exact: true });
    await dialog.waitFor();
    await dialog.getByRole("button", { name: confirm ? confirmLabel : feedback.cancel, exact: true }).click();
    await dialog.waitFor({ state: "detached" });
  };
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
  let coldRoute;
  try {
    await banner.getByRole("button", { name: copy.reviewRequests, exact: true }).click();
    coldRoute = await page.evaluate(async frameCount => {
      for (let frame = 0; frame < frameCount; frame++) {
        await new Promise(resolve => requestAnimationFrame(resolve));
      }
      return {
        titlePresent: Boolean(document.getElementById("trustedAccessManager")),
        activeId: document.activeElement?.id,
        at: performance.now()
      };
    }, coldRouteFrameCount);
    assert.equal(coldRoute.titlePresent, false, "the destination stays unmounted beyond the old focus window");
  } finally {
    releaseSettingsChunk();
  }
  await page.waitForFunction(() => document.body.dataset.page === "settings");
  await page.locator(".trusted-access-notification").waitFor({ state: "detached" });
  try {
    await page.waitForFunction(() => document.activeElement?.id === "trustedAccessManager");
  } catch (error) {
    const focusState = await page.evaluate(() => ({
      activeId: document.activeElement?.id,
      titlePresent: Boolean(document.getElementById("trustedAccessManager")),
      titleVisible: Boolean(document.getElementById("trustedAccessManager")?.getClientRects().length)
    }));
    throw new Error(`Review navigation did not focus the manager: ${JSON.stringify({ coldRoute, settingsChunkIntercepted, focusState })}`, { cause: error });
  }
  assert.equal(settingsChunkIntercepted, true, "the journey exercises an actual cold settings chunk");
  const coldFocus = await activeElement();
  // 已预热分包的再次导航也必须承接焦点，普通进入设置不应请求深层目标。
  await page.locator(".shell-page").filter({ hasText: appCopy.page.monitor }).click();
  await page.waitForFunction(() => document.body.dataset.page === "monitor"
    && document.activeElement?.id === "activePageTitle");
  await page.getByRole("button", { name: appCopy.page.settings, exact: true }).click();
  await page.locator("#trustedAccessManager").waitFor({ state: "visible" });
  assert.equal((await activeElement()).id, "activePageTitle", "ordinary settings navigation consumes no old review focus request");
  await page.getByRole("button", { name: appCopy.page.monitor, exact: true }).click();
  await page.waitForFunction(() => document.body.dataset.page === "monitor"
    && document.activeElement?.id === "activePageTitle");
  await banner.getByRole("button", { name: copy.reviewRequests, exact: true }).click();
  await page.waitForFunction(() => document.activeElement?.id === "trustedAccessManager");
  const warmFocus = await activeElement();
  const panel = page.getByRole("region", { name: copy.title, exact: true });
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await panel.getByText(copy.defaultKeyName, { exact: true }).waitFor();
  await panel.getByText(copy.defaultAccessLossWarning, { exact: true }).waitFor();
  await panel.getByText(copy.defaultFileLocation, { exact: true }).waitFor();
  assert.doesNotMatch(await panel.innerText(), /UserData\/Security/, "the file location uses Windows separators");
  assert.doesNotMatch(await panel.innerText(), new RegExp(defaultSecret));
  assert.ok(await panel.getByText("Telemetry helper", { exact: true }).count() > 0);
  assert.ok(await panel.getByText("C:\\Tools\\telemetry.exe", { exact: true }).count() > 0);
  assert.ok(await panel.getByText("4321", { exact: true }).count() > 0);

  const firstRequest = panel.locator(".trusted-access-request").filter({ hasText: "Telemetry helper" });
  const requestScopeGroup = firstRequest.getByRole("group", { name: copy.requestScopesLabel });
  await requestScopeGroup.getByLabel(copy.scopeLabels["metrics.read"]).uncheck();
  await firstRequest.getByRole("button", { name: copy.approve, exact: true }).click();
  await panel.locator(".trusted-access-outcome.approved")
    .filter({ hasText: copy.requestOutcome("Telemetry helper", copy.requestApproved) }).waitFor();
  assert.equal((await activeElement()).id, "trustedAccessRequestsTitle", "focus moves to the request section after a decision");
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
  assert.equal(await deniedRequest.locator("select").count(), 0, "the denial reason uses the shared select");
  await deniedRequest.getByRole("combobox", { name: copy.denyReasonLabel, exact: true }).click();
  await page.getByRole("option", { name: copy.denialReasons.unsupported, exact: true }).click();
  await deniedRequest.getByRole("button", { name: copy.deny, exact: true }).click();
  const deniedOutcome = panel.locator(".trusted-access-outcome.denied");
  await deniedOutcome.filter({
    hasText: copy.requestOutcome("Unsupported helper", copy.requestDeniedWithReason(copy.denialReasons.unsupported))
  }).waitFor();
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
  assert.equal((await activeElement()).text, copy.copy, "focus moves to the copy button of the one-time key");
  assert.equal(await page.locator('[role="status"], [aria-live]').filter({ hasText: independentSecret }).count(), 0,
    "the one-time key is never inside a live region");
  assert.equal(await panel.locator('[role="status"]').filter({ hasText: copy.oneTimeAnnouncement }).count(), 1,
    "screen readers hear only that a key was generated");
  await panel.getByRole("button", { name: copy.copy, exact: true }).click();
  await page.waitForFunction(value => globalThis.__trustedAccessClipboard === value, independentSecret);
  const createCall = calls.find(call => call.path === "/api/access-keys" && call.method === "POST");
  assert.deepEqual(createCall?.body, { name: "Read-only report", purpose: "control", scopes: ["read"] });

  // 按钮按层级渲染：破坏性和次要操作都是 secondary，不再使用未定义的 secondary-button。
  assert.equal(await panel.locator(".secondary-button").count(), 0);
  for (const name of [copy.disableDefault, copy.regenerateDefault, copy.rotateKeyLabel("Local report script"), copy.copied]) {
    assert.equal(await panel.getByRole("button", { name, exact: true })
      .evaluate(node => node.classList.contains("secondary")), true, name);
  }

  const rotateRow = panel.locator(".trusted-access-key-row").filter({ hasText: "Local report script" });
  await rotateRow.getByRole("button", { name: copy.rotateKeyLabel("Local report script"), exact: true }).click();
  await answerConfirmation(copy.confirmRotateTitle("Local report script"), copy.rotate);
  await panel.getByText(rotatedSecret, { exact: true }).waitFor();

  const revokeRow = panel.locator(".trusted-access-key-row").filter({ hasText: "Temporary helper" });
  const revokeButton = revokeRow.getByRole("button", { name: copy.revokeKeyLabel("Temporary helper"), exact: true });
  await revokeButton.click();
  await answerConfirmation(copy.confirmRevokeTitle("Temporary helper"), copy.revoke, false);
  assert.equal(calls.some(call => call.method === "DELETE"), false, "a canceled revoke sends nothing");
  await revokeButton.click();
  await answerConfirmation(copy.confirmRevokeTitle("Temporary helper"), copy.revoke);
  await revokeRow.waitFor({ state: "detached" });
  const afterRevoke = await activeElement();
  assert.ok(afterRevoke.keyAction === "rotate" || afterRevoke.id === "trustedAccessKeysTitle",
    `focus stays in the key list after a revoke (${JSON.stringify(afterRevoke)})`);

  // 关闭默认密钥以及之后的恢复在 trusted-access-recovery 里按后端拒绝请求的行为验证；这里验证重新生成。
  const defaultCallsBefore = calls.filter(call => call.path === "/api/access-keys/default").length;
  await panel.getByRole("button", { name: copy.regenerateDefault, exact: true }).click();
  await answerConfirmation(copy.confirmRegenerateTitle, copy.regenerateDefault);
  await panel.getByText(copy.defaultEnabled, { exact: true }).waitFor();
  await page.waitForFunction(() => !document.querySelector(".trusted-access-default button[disabled]"));
  assert.deepEqual(calls.filter(call => call.path === "/api/access-keys/default").map(call => call.body).slice(defaultCallsBefore),
    [{ enabled: true, rotate: true }]);
  assert.doesNotMatch(await panel.innerText(), new RegExp(defaultSecret));
  assert.equal(await page.locator("#root").evaluate((node, secret) => node.innerHTML.includes(secret), defaultSecret), false,
    "the default file secret never appears in rendered frontend content");
  assert.equal(calls.some(call => call.path.includes("acquire-default")), false,
    "the frontend never requests the native-only default secret");
  assert.deepEqual(errors, []);
  return {
    coldRoute, coldFocus, warmFocus,
    requests: ["approved-subset", "expired", "denied"],
    keyActions: ["create-one-time", "rotate-confirmed", "revoke-canceled", "revoke-confirmed", "regenerate-default-confirmed"],
    focus: ["banner-to-title", "decision-to-requests", "create-to-copy", "revoke-to-list"],
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

