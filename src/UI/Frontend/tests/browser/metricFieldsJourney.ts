import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { checkCpuMetricFields, checkDeviceMetricFields, enableMetricFields } from "./metricFieldsProbe.ts";

/** 规范字段新显示：NUMA、有效频率、NPU 忙碌率与 AMD NPU 数值（简体中文）。九语见多语言旅程。 */
export default async function metricFieldsJourney(page: Page, baseUrl: string) {
  await enableMetricFields(baseUrl);
  const app = await loadAppCopy("zh-CN");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
  await checkCpuMetricFields(page, app, "zh-CN");
  return await checkDeviceMetricFields(page, app, "zh-CN");
}
