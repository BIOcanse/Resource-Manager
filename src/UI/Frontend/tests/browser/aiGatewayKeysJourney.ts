import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function aiGatewayKeysJourney(page: Page, baseUrl: string) {
  const labels = (await loadSettingsText("zh-CN")).systemIntegration;
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.route("**/api/runtime/capabilities", async route => {
    const response = await route.fetch();
    const value = await response.json() as { publicServiceCoordination?: boolean };
    value.publicServiceCoordination = true;
    await route.fulfill({ response, json: value });
  });
  await page.route("**/api/settings/app", async route => {
    const response = await route.fetch();
    const value = await response.json() as { settings: { publicService?: { enabled: boolean; aiModelCatalogEnabled: boolean; fileIndexEnabled: boolean; databaseServiceEnabled: boolean } } };
    value.settings.publicService = {
      enabled: true, aiModelCatalogEnabled: true,
      fileIndexEnabled: false, databaseServiceEnabled: false
    };
    await route.fulfill({ response, json: value });
  });
  const credential = {
    id: "gateway-fixture", displayName: "Gateway fixture", compatibilityProfile: "anthropic",
    createdAt: "2026-10-26T00:00:00Z", baseUrl: "http://127.0.0.1/fixture",
    authenticationHeader: "x-api-key"
  };
  let rows: (typeof credential)[] = [];
  let reads = 0;
  let failRead = false;
  let release!: () => void;
  const initialRead = new Promise<void>(resolve => { release = resolve; });
  const writes: { compatibilityProfile: string; displayName: string }[] = [];
  await page.route(url => url.pathname === "/api/ai-gateway/credentials", async route => {
    if (route.request().method() === "POST") {
      writes.push(route.request().postDataJSON() as { compatibilityProfile: string; displayName: string });
      rows = [credential];
      return route.fulfill({ json: { credential, apiKey: "fixture-one-time-key" } });
    }
    reads++;
    if (reads === 1) await initialRead;
    return failRead
      ? route.fulfill({ status: 503, json: { error: "fixture unavailable" } })
      : route.fulfill({ json: rows });
  });
  await page.route("**/api/ai-gateway/credentials/gateway-fixture", async route => {
    assert.equal(route.request().method(), "DELETE");
    rows = [];
    await route.fulfill({ json: { revoked: true } });
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: "设置", exact: true }).click();
  const open = () => page.getByRole("button", { name: "公共服务", exact: true }).click();
  const requested = page.waitForRequest(request => new URL(request.url()).pathname === "/api/ai-gateway/credentials");
  await open();
  const manager = page.locator(".ai-gateway-manager");
  await requested;
  assert.equal(await manager.locator(".ai-gateway-empty").count(), 0, "pending read is not an accepted empty list");
  release();
  await manager.getByText(labels.aiGatewayEmpty, { exact: true }).waitFor();
  await manager.getByRole("radio", { name: labels.aiGatewayAnthropicProfile, exact: true }).click();
  await manager.getByRole("textbox", { name: labels.aiGatewayNamePlaceholder, exact: true }).fill(credential.displayName);
  await manager.getByRole("button", { name: labels.aiGatewayGenerate, exact: true }).click();
  await manager.locator(".ai-gateway-credential-row").filter({ hasText: credential.displayName }).waitFor();
  await manager.getByText("fixture-one-time-key", { exact: true }).waitFor();
  assert.deepEqual(writes, [{ compatibilityProfile: "anthropic", displayName: credential.displayName }]);
  await manager.getByRole("button", { name: labels.aiGatewayRevoke, exact: true }).click();
  await manager.locator(".ai-gateway-credential-row").waitFor({ state: "detached" });
  await manager.locator(".ai-gateway-created").waitFor({ state: "detached" });
  await manager.getByText(labels.aiGatewayEmpty, { exact: true }).waitFor();
  assert.equal(reads, 3, "initial load, add and delete each read the shared list once");
  failRead = true;
  await page.getByRole("button", { name: "外观", exact: true }).click();
  await open();
  await manager.locator(".ai-gateway-error").waitFor();
  failRead = false;
  await page.getByRole("button", { name: "外观", exact: true }).click();
  const recovered = page.waitForResponse(response => response.ok()
    && new URL(response.url()).pathname === "/api/ai-gateway/credentials");
  await open();
  await recovered;
  await manager.locator(".ai-gateway-error").waitFor({ state: "detached" });
  assert.deepEqual(errors, []);
  return { initialPending: true, addAndDelete: true, readFailureRecovery: true, reads };
}
