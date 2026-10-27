import { NOT_GHOST } from "./liveLocator.js";
import { formatBytes } from "../../src/presentation/byteUnits.ts";
import { applyLanguage, currentLanguage } from "../../src/text.ts";
import { waitForCondition } from "./journeyWaits.js";

// 卷 F: 的容量与可用空间，和 mock 的 fixtureStorageVolumePort / deviceTopologyVolumeFreeBytes 一致。
const capacityBytes = 1_000_185_827_328;
const fieldsPath = "/api/device-topology/fields/subscribe";
const statePath = "/api/device-topology/state/subscribe";

/*
 * 移动硬盘卷的已用/总量：可用空间是 Sampled 值，不在拓扑结构帧里，由存储详情自己持有设备信息字段订阅。
 * 检查同一份文字（usedOfTotal 加按当前语言格式化的字节数）、只改采样值时显示跟着变而结构订阅不出帧，
 * 以及订阅只在这一节显示期间存在。
 */
export async function checkVolumeFreeSpace(page, app, language, baseUrl) {
  const topology = page.locator(".device-topology-panel");
  const numberLanguage = currentLanguage();
  await applyLanguage(language);
  try {
    await patch(baseUrl, { deviceTopologyStorage: true, deviceTopologyVolumeFreeBytes: "250092956672" });
    await topology.getByRole("radio", { name: app.deviceTopology.externalScope, exact: true }).click();
    const node = topology.locator(".device-port-item.device-node").filter({ hasText: "Fixture Portable SSD" }).first();
    await node.click();
    const idle = await telemetry(baseUrl, fieldsPath);
    if (idle.logicalInFlight !== 0) throw new Error(`${language}: the storage free-space subscription is held without its section: ${JSON.stringify(idle)}`);
    await topology.locator(".device-details-button").click();
    const dialog = page.locator(`.user-details-modal${NOT_GHOST}:visible`);
    const volume = dialog.locator(".device-specialized-details.kind-external-storage .device-storage-volume");
    const first = await expectUsage(volume, app, 250_092_956_672, language);
    const held = await telemetry(baseUrl, fieldsPath);
    if (held.logicalInFlight !== 1) throw new Error(`${language}: the open storage section does not hold one fields subscription: ${JSON.stringify(held)}`);

    // 只有采样值变：显示跟着字段帧变，拓扑结构订阅不出帧。
    const structuralBefore = (await telemetry(baseUrl, statePath)).publications;
    await patch(baseUrl, { deviceTopologyVolumeFreeBytes: "200000000000" });
    const second = await expectUsage(volume, app, 200_000_000_000, language);
    const structuralAfter = (await telemetry(baseUrl, statePath)).publications;
    if (structuralAfter !== structuralBefore)
      throw new Error(`${language}: a sampled free-space change produced ${structuralAfter - structuralBefore} structural frames`);

    await page.keyboard.press("Escape");
    await dialog.waitFor({ state: "hidden" });
    await waitForCondition(page, async () => (await telemetry(baseUrl, fieldsPath)).logicalInFlight === 0,
      { description: `${language}: the closed storage section releases its fields subscription` });
    return { language, first, second, structuralFramesDuringSample: structuralAfter - structuralBefore };
  } finally {
    await applyLanguage(numberLanguage);
    const dialog = page.locator(`.user-details-modal${NOT_GHOST}:visible`);
    if (await dialog.count()) { await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" }); }
    await patch(baseUrl, { deviceTopologyStorage: false, deviceTopologyVolumeFreeBytes: "250092956672" });
    // 移除硬盘是一次结构变化：等它那一帧到达，后面的检查不会把它算成自己的推送。
    await topology.locator(".device-port-item.device-node").filter({ hasText: "Fixture Portable SSD" }).first()
      .waitFor({ state: "detached" });
  }
}

async function expectUsage(volume, app, freeBytes, language) {
  const text = app.deviceSpecialized.usedOfTotal(formatBytes(capacityBytes - freeBytes, "storage"), formatBytes(capacityBytes, "storage"));
  const label = app.deviceSpecialized.usedPercent((((capacityBytes - freeBytes) / capacityBytes) * 100).toFixed(1));
  await volume.locator("small").getByText(text, { exact: true }).waitFor();
  const bar = volume.locator(".device-storage-usage");
  const actualLabel = await bar.getAttribute("aria-label");
  if (actualLabel !== label) throw new Error(`${language}: the usage bar reads ${actualLabel}, expected ${label}`);
  return { text, label };
}

async function patch(baseUrl, value) {
  const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST",
    headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
  if (!response.ok) throw new Error(`storage scenario rejected: ${response.status} ${await response.text()}`);
}

async function telemetry(baseUrl, path) {
  const values = await (await fetch(`${baseUrl}/__test/requests`)).json();
  return values[path] ?? { logicalInFlight: 0, publications: 0 };
}
