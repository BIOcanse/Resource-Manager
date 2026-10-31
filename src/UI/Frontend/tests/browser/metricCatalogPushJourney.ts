import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import {
  addCardSlot,
  enableMetricFields,
  metricFieldDevices,
  seedFieldCards
} from "./metricFieldsProbe.ts";

/**
 * 目录订阅信封（{catalogRevision, fields, metrics}）在没有普通指标帧时也直接生效（简体中文）：
 * 先等卡片上的设备字段有读数，再暂停 /api/metrics/subscribe（值和字段值都不再推）。
 * 1. 目录换修订号：设备从目录里消失，卡片随订阅帧写「设备不在」；
 * 2. 只有组件安装信息变：它也是目录内容，目录换修订号；选择器里 cpu.frequency 从「缺组件」变成「无有效读数」（后端码 3/3）。
 * 两步都不发 GET /api/metrics/catalog，也没有任何指标帧。
 */
export default async function metricCatalogPushJourney(page: Page, baseUrl: string) {
  const app = await loadAppCopy("zh-CN");
  await enableMetricFields(baseUrl);
  await patchScenario(baseUrl, { metricCatalogComponent: "missing" });
  await seedFieldCards(page);
  const catalogGets: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/metrics/catalog") catalogGets.push(request.url());
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  const card = page.locator(".metric-card").filter({ hasText: app.fieldLabel.npu_all_engine_busy_percent.label }).first();
  const main = card.locator(".metric-main-content");
  await main.locator(".metric-value").filter({ hasText: "137.5%" }).waitFor({ timeout: 15_000 });

  // 从这里起不再有任何指标帧（按 id 的和按字段的都走 /api/metrics/subscribe）。
  await patchScenario(baseUrl, { pausedSubscriptionPaths: ["/api/metrics/subscribe"] });
  const valuePublications = await metricValuePublications(baseUrl);
  const catalogGetsBefore = catalogGets.length;
  const firstRevision = await catalogRevision(baseUrl);

  // 1. 设备消失：目录换修订号，信封带新的字段目录，卡片立即写「设备不在」。
  await patchScenario(baseUrl, {
    metricFieldDevices: metricFieldDevices.filter((device) => device.deviceIdentity !== "npu|fixture")
  });
  await main.locator(".metric-value").filter({ hasText: app.dashboard.deviceAbsent }).waitFor({ timeout: 10_000 });
  const secondRevision = await catalogRevision(baseUrl);
  if (secondRevision === firstRevision) throw new Error(`removing a device kept the catalog revision ${firstRevision}`);
  await expectNoValueFrameOrCatalogGet(baseUrl, valuePublications, catalogGets, catalogGetsBefore, "device removal");

  // 2. 组件安装信息变化：信封带新修订号和新的 metrics，选择器跟着变。
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  await addCardSlot(page, app, 1);
  const picker = page.locator("#metricModal");
  const option = picker.locator('[data-metric-id="cpu.frequency"]');
  await option.waitFor({ timeout: 10_000 });
  const noValidReading = app.backendMessage.metric.noValidReading;
  const unavailableText = async () => (await option.locator(".metric-unavailable").allTextContents()).map((text) => text.trim());
  const missingState = { disabled: await option.isDisabled(), unavailable: await unavailableText(),
    dependency: await option.locator(".metric-option-dependency").count() };
  if (missingState.unavailable.includes(noValidReading)) {
    throw new Error(`cpu.frequency already reads as installed before the change: ${JSON.stringify(missingState)}`);
  }
  await patchScenario(baseUrl, { metricCatalogComponent: "installed" });
  await option.locator(".metric-unavailable").filter({ hasText: noValidReading }).waitFor({ timeout: 10_000 });
  if (!await option.isDisabled()) throw new Error("an installed metric without a valid reading stays selectable");
  // 后端的原因原样留在提示里（域/码/参数），格子里写的是那句话，不是代码。
  const reasonTitle = await option.getAttribute("data-full-content");
  if (!reasonTitle?.split("\n").includes(app.backendMessage.reasonCode("3/3"))
    || (await unavailableText()).some((text) => text.includes("3/3"))) {
    throw new Error(`the disabled metric does not keep the backend reason code on hover only: ${JSON.stringify({ reasonTitle, aside: await unavailableText() })}`);
  }
  if (await option.locator(".metric-option-dependency").count() !== 0) {
    throw new Error("the installed component is still offered for installation");
  }
  const thirdRevision = await catalogRevision(baseUrl);
  if (thirdRevision === secondRevision) {
    throw new Error(`component install information kept the catalog revision ${secondRevision}`);
  }
  await expectNoValueFrameOrCatalogGet(baseUrl, valuePublications, catalogGets, catalogGetsBefore, "component install");

  await picker.getByRole("button", { name: app.metricPicker.cancel, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.cancelEditLabel, exact: true }).click();
  await patchScenario(baseUrl, { pausedSubscriptionPaths: [] });
  return {
    revisions: [firstRevision, secondRevision, thirdRevision],
    missingState,
    catalogGets: catalogGets.length,
    valuePublicationsWhilePaused: (await metricValuePublications(baseUrl)) - valuePublications
  };
}

async function expectNoValueFrameOrCatalogGet(baseUrl: string, valuePublications: number, catalogGets: string[], catalogGetsBefore: number, phase: string) {
  const published = await metricValuePublications(baseUrl);
  if (published !== valuePublications) {
    throw new Error(`${phase}: ${published - valuePublications} metric frames were published while paused`);
  }
  if (catalogGets.length !== catalogGetsBefore) {
    throw new Error(`${phase}: the catalog was re-read over GET: ${catalogGets.slice(catalogGetsBefore).join(" ")}`);
  }
}

async function metricValuePublications(baseUrl: string): Promise<number> {
  const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json() as Record<string, { publications: number }>;
  return telemetry["/api/metrics/subscribe"]?.publications ?? 0;
}

// 后端在单次快照上带同一个目录修订号；从测试进程读，不经过页面。
async function catalogRevision(baseUrl: string): Promise<string> {
  const response = await fetch(`${baseUrl}/api/metrics/snapshot?ids=cpu.usage`);
  if (!response.ok) throw new Error(`metric snapshot failed: ${response.status}`);
  return (await response.json() as { catalogRevision: string }).catalogRevision;
}

async function patchScenario(baseUrl: string, patch: Record<string, unknown>) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(`scenario patch failed: ${response.status} ${await response.text()}`);
}
