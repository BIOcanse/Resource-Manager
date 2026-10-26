import { metricFieldDevices } from "./metricFieldsProbe.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { checkTypeXDisplayDialog, checkType3DisplayDialog, checkRecordedDisplayDialog, checkDisplayVariant } from "./displayEdidDialogProbe.js";
import { checkDeclarationDisplayDialog } from "./displayEdidDeclarationDialogProbe.js";
import { observeSubscriptionChannel } from "./subscriptionChannelProbe.js";
import { checkDeviceTopologyWireFacts } from "./deviceTopologyWireProbe.js";
import { checkMachineIdentity } from "./deviceMachineIdentityProbe.js";
import { checkNetworkConnectionStates } from "./deviceTopologyConnectionProbe.js";
import { checkBadgesNotTruncated } from "./deviceBadgeProbe.js";
export default async function deviceTopologySourceJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const subscriptionPath = "/api/device-topology/state/subscribe";
  const directPath = "/api/device-topology/state";
  const channelPath = "/api/subscriptions/stream";
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  const channelProbe = await observeSubscriptionChannel(page);

  await page.setViewportSize({ width: 800, height: 500 });
  // Configure the isolated server before the only document/owner is created.
  // Reloading an already mounted app mixes two owners' stream retirement into telemetry.
  const initialScenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ capabilities: "ready", settings: "ready", deviceTopologyDelayMs: 0, metricFieldDevices })
  });
  assert(initialScenario.ok, `Unable to configure initial topology scenario: ${initialScenario.status}`);
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const pageNavigation = page.getByRole("navigation", { name: "页面切换" });
  const topology = page.locator(".device-topology-panel");
  await pageNavigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await page.locator('body[data-page="details"]').waitFor({ state: "attached", timeout: 5_000 });
  await topology.waitFor({ state: "visible", timeout: 5_000 });
  const topologyHeader = topology.locator(".device-topology-header");
  await topologyHeader.locator(".device-topology-identity").filter({ hasText: "Frontend Harness" })
    .waitFor({ state: "visible", timeout: 5_000 });
  const firstActivation = await waitForTelemetry(page, subscriptionPath, (value) =>
    value.logicalInFlight === 1 && value.publications >= 1);
  const firstChannel = await readTelemetry(page, channelPath);
  const firstDirect = await readTelemetry(page, directPath);
  assertSingleLogicalSubscription(firstActivation, "device topology");
  assertSinglePhysicalChannel(firstChannel);
  await channelProbe.assertSinglePhysicalChannel();
  assert(firstDirect.started === 0,
    `Device topology used a direct GET instead of its callback value: ${JSON.stringify(firstDirect)}`);

  const dom = {
    heading: (await topology.getByRole("heading", { name: "设备拓扑", exact: true })
      .textContent())?.trim(),
    brand: (await topologyHeader.locator(".device-topology-identity").textContent())?.trim(),
    manufacturer: (await topology.getByLabel("电脑俯视图").locator(".device-machine-manufacturer")
      .textContent())?.trim(),
    model: (await topology.getByLabel("电脑俯视图").locator(".device-machine-model")
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
  // 所在分页已经叫“设备拓扑”：区域里没有第二个看得见的同名标题，读屏仍从隐藏标题得到区域名，顶部是整机识别名。
  const panelHeading = await page.evaluate(() => {
    const panel = document.querySelector(".device-topology-panel");
    const shown = (element) => {
      const rect = element.getBoundingClientRect();
      return rect.width > 2 && rect.height > 2 && getComputedStyle(element).visibility !== "hidden";
    };
    const headings = [...document.querySelectorAll("h1, h2, h3, h4, h5, h6, [role='heading']")]
      .filter((element) => element.textContent?.trim() === "设备拓扑");
    const labelledBy = panel?.getAttribute("aria-labelledby");
    const identity = panel?.querySelector(".device-topology-identity");
    return {
      visibleOnPage: headings.filter(shown).length,
      visibleInPanel: headings.filter((element) => panel?.contains(element) && shown(element)).length,
      regionName: labelledBy ? document.getElementById(labelledBy)?.textContent?.trim() : null,
      identity: identity?.textContent?.trim(),
      identityShown: identity ? shown(identity) : false
    };
  });
  assert(panelHeading.visibleOnPage <= 1 && panelHeading.visibleInPanel === 0
    && panelHeading.regionName === "设备拓扑" && panelHeading.identity === "Frontend Harness" && panelHeading.identityShown,
  `The topology area repeats the visible page heading or lost its region name: ${JSON.stringify(panelHeading)}`);
  const machineIdentity = await checkMachineIdentity(page, topology, async (patch) => {
    const count = (await readTelemetry(page, subscriptionPath)).publications;
    await setScenario(page, patch);
    await waitForTelemetry(page, subscriptionPath, (value) => value.publications > count);
  });

  // 清单副标题只占一行；被截断的完整内容在条目的悬停提示里（协调者 10-26：列表项只放简短的一行）。
  const hdmiItem = topology.locator(".device-port-item").filter({ hasText: "HDMI 接口" }).first();
  const longTextPresentation = await topology.locator(".device-port-list").evaluate((list) => {
    const previousWidth = list.style.width;
    // 类别标签不再单占一列（放不下时换到标题下），副标题更宽：挤到 110 px 才会出现被截断的副标题。
    list.style.width = "110px";
    const items = [...list.querySelectorAll(".device-port-item")].map((item) => {
      const secondary = item.querySelector("small");
      const text = secondary?.textContent?.trim() ?? "";
      return {
        text,
        oneLine: !secondary || secondary.getBoundingClientRect().height <= parseFloat(getComputedStyle(secondary).lineHeight) + 1,
        truncated: Boolean(secondary && secondary.scrollWidth > secondary.clientWidth + 1),
        tooltipHasText: (item.getAttribute("title") ?? "").includes(text)
      };
    });
    list.style.width = previousWidth;
    return items;
  });
  assert(longTextPresentation.length > 0
    && longTextPresentation.every((item) => item.oneLine && item.tooltipHasText)
    && longTextPresentation.some((item) => item.truncated),
  `List subtitles are not single lines recoverable from the tooltip: ${JSON.stringify(longTextPresentation)}`);
  // 清单比面板高时，换行的副标题仍在条目边框内（auto 行曾只按条目的 min-height 分配高度，副标题溢出卡片）。
  const constrainedList = await topology.locator(".device-port-list").evaluate((list) => {
    const previousHeight = list.style.height;
    list.style.height = "90px";
    const items = [...list.querySelectorAll(".device-port-item")].map((item) => {
      const secondary = item.querySelector("small");
      return {
        text: item.textContent?.trim().slice(0, 60),
        overflow: item.scrollHeight - item.clientHeight,
        secondaryBottom: secondary?.getBoundingClientRect().bottom ?? 0,
        itemBottom: item.getBoundingClientRect().bottom
      };
    });
    const overflowing = list.scrollHeight > list.clientHeight;
    list.style.height = previousHeight;
    return { overflowing, items };
  });
  assert(constrainedList.overflowing
    && constrainedList.items.length > 0
    && constrainedList.items.every((item) => item.overflow <= 1 && item.secondaryBottom <= item.itemBottom + 1),
  `Device list items overflow their cards when the list scrolls: ${JSON.stringify(constrainedList)}`);
  await hdmiItem.focus();
  await page.keyboard.press("Enter");
  // 详情头只写一句概括（类别和明确的连接状态），不重复清单副标题和速率；完整内容在下面的格子里（协调者 10-26 走查）。
  const detailHead = await topology.getByLabel("拓扑详情").evaluate((pane) => ({
    title: pane.querySelector(".device-port-detail-title > div > strong")?.textContent?.trim(),
    headline: pane.querySelector(".device-port-detail-title > div > span")?.textContent?.trim(),
    cells: [...pane.querySelectorAll(".device-detail-summary-item strong")].map((cell) => cell.textContent?.trim())
  }));
  const hdmiSubtitle = (await hdmiItem.locator("small").textContent())?.trim();
  assert(detailHead.title === "HDMI 接口" && detailHead.headline === "外部接口"
    && detailHead.headline !== hdmiSubtitle && !detailHead.cells.includes(detailHead.headline),
    `Keyboard selection did not show a short detail headline: ${JSON.stringify({ detailHead, hdmiSubtitle })}`);

  const app = await loadAppCopy("zh-CN");
  const wireFacts = await checkDeviceTopologyWireFacts(page, app, "zh-CN", baseUrl);
  await checkNetworkConnectionStates(page, app, "zh-CN", baseUrl);
  const beforeEdid = (await readTelemetry(page, subscriptionPath)).publications;
  await setScenario(page, { displayEdid: "ready" });
  await waitForTelemetry(page, subscriptionPath, value => value.publications > beforeEdid);
  const edidDetails = await checkRecordedDisplayDialog(page, app);
  for (const variant of ["inactive", "inactiveConnected", "unknown", "empty", "emptyRaw", "legacy"]) {
    const count = (await readTelemetry(page, subscriptionPath)).publications;
    await setScenario(page, { displayEdid: variant });
    await waitForTelemetry(page, subscriptionPath, value => value.publications > count);
    await checkDisplayVariant(page, app, variant);
  }
  // 端口上什么都没接：弹窗不列显示节，也不渲染当前模式、EDID 声明和原始 EDID。
  const beforeDetached = (await readTelemetry(page, subscriptionPath)).publications;
  await setScenario(page, { displayEdid: "detached" });
  await waitForTelemetry(page, subscriptionPath, value => value.publications > beforeDetached);
  const detachedDetails = await checkDetachedDisplayDialog(page, app);
  await setScenario(page, { displayEdid: "identity" });
  const interfaceRoots = topology.locator(".device-port-item:not(.nested)");
  await page.waitForFunction(() => document.querySelectorAll(".device-topology-panel .device-port-item:not(.nested)").length === 2);
  const selectRootAndVerifyIdentity = async (root, deviceId) => {
    await root.click();
    assert(await root.getAttribute("aria-pressed") === "true", "The intended interface root was not selected");
    // Existing roots have no identity row; their attached-device details expose the same port's DeviceId.
    const attached = root.locator("xpath=following-sibling::button[1]");
    assert((await attached.getAttribute("class"))?.split(" ").includes("nested")
      && (await attached.getAttribute("class"))?.split(" ").includes("device-node"),
    "The selected interface does not have its expected attached display");
    await attached.click();
    await topology.locator(".device-details-button").click();
    const identityDialog = page.locator(".user-details-modal:visible");
    await identityDialog.getByText(deviceId, { exact: true }).waitFor();
    await page.keyboard.press("Escape"); await identityDialog.waitFor({ state: "hidden" });
    await root.click();
    assert(await root.getAttribute("aria-pressed") === "true", "Identity verification did not return to its interface root");
  };
  await selectRootAndVerifyIdentity(interfaceRoots.nth(1), String.raw`DISPLAY\FIXTURE_HDMI_2`);
  await checkDisplayVariant(page, app, "unknown");
  await selectRootAndVerifyIdentity(interfaceRoots.first(), String.raw`DISPLAY\FIXTURE_HDMI_1`);
  await checkRecordedDisplayDialog(page, app);
  const beforeRecovery = (await readTelemetry(page, subscriptionPath)).publications;
  await setScenario(page, { displayEdid: "ready" });
  await waitForTelemetry(page, subscriptionPath, value => value.publications > beforeRecovery);
  await checkRecordedDisplayDialog(page, app);
  const type3Details = [];
  for (const variant of ["type3", "type3Inactive", "type3Unknown", "type3"]) {
    const count = (await readTelemetry(page, subscriptionPath)).publications;
    await setScenario(page, { displayEdid: variant });
    await waitForTelemetry(page, subscriptionPath, value => value.publications > count);
    type3Details.push(await checkType3DisplayDialog(page, app, variant));
  }
  const beforeTypeX = (await readTelemetry(page, subscriptionPath)).publications;
  await setScenario(page, { displayEdid: "typex" });
  await waitForTelemetry(page, subscriptionPath, value => value.publications > beforeTypeX);
  const typeXDetails = await checkTypeXDisplayDialog(page, app);
  const declarationDetails = [];
  for (const variant of ["typeVVi", "typeVViClock", "adaptiveSync", "adaptiveSyncZero", "dynamicRange"]) {
    const count = (await readTelemetry(page, subscriptionPath)).publications;
    await setScenario(page, { displayEdid: variant });
    await waitForTelemetry(page, subscriptionPath, value => value.publications > count);
    declarationDetails.push(await checkDeclarationDisplayDialog(page, app, variant));
  }
  const beforeOriginal = (await readTelemetry(page, subscriptionPath)).publications;
  await setScenario(page, { displayEdid: "ready" });
  await waitForTelemetry(page, subscriptionPath, value => value.publications > beforeOriginal);
  await checkRecordedDisplayDialog(page, app);
  const active = await waitForTelemetry(page, subscriptionPath, (value) => value.publications >= 2);
  assert(active.started === 1
    && active.logicalStarted === 1
    && active.logicalInFlight === 1
    && active.logicalMaxInFlight === 1,
  `Device topology created duplicate logical subscriptions: ${JSON.stringify(active)}`);

  await topology.locator(".device-port-item.nested.device-node").first().click();
  await topology.locator(".device-details-button").click();
  const metricDialog = page.locator(".user-details-modal:visible");
  await metricDialog.getByText("137.5%", { exact: true }).waitFor();
  await setScenario(page, { invalidMetricFields: true });
  await metricDialog.getByText("137.5%", { exact: true }).waitFor({ state: "detached" });
  assert(await topologyHeader.locator(".device-topology-identity").textContent() === "Frontend Harness", "Metric failure affects only its own data");
  await setScenario(page, { invalidMetricFields: false });
  await metricDialog.getByText("137.5%", { exact: true }).waitFor();
  await page.keyboard.press("Escape");
  await metricDialog.waitFor({ state: "hidden" });
  await setScenario(page, { deviceTopologyNullProtocol: true });
  await page.waitForFunction(() => document.querySelectorAll(".device-topology-panel .device-port-item:not(.nested)").length === 2);
  assert(await topology.locator(".observation-state.error").count() === 0, "One nullable protocol must not reject the snapshot");
  await topology.locator(".device-port-item.nested.device-node").nth(1).click();
  await topology.locator(".device-details-button").click();
  const protocolDialog = page.locator(".user-details-modal:visible");
  await protocolDialog.waitFor();
  const protocolRow = protocolDialog.locator(".user-details-row").filter({ has: page.getByText(app.deviceTopology.label.protocol, { exact: true }) });
  assert((await protocolRow.locator("dd").textContent()) === app.deviceTopology.deviceCategory.unknown, "Only the nullable port protocol shows localized unknown");
  await page.keyboard.press("Escape");
  await protocolDialog.waitFor({ state: "hidden" });
  const scrolledSelection = await checkScrolledSelectionSurvivesPublications(page, topology, subscriptionPath);
  await setScenario(page, { deviceTopologyInvalid: true });
  await topology.locator(".observation-state.error").waitFor();
  assert(await topology.locator(".device-port-item").count() === 0, "Decode failure clears the current topology");
  await setScenario(page, { deviceTopologyInvalid: false, deviceTopologyNullProtocol: false });
  await topologyHeader.locator(".device-topology-identity").filter({ hasText: "Frontend Harness" }).waitFor();
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await waitForTelemetry(page, subscriptionPath, value => value.logicalInFlight === 0);
  // 需求撤销只停止采样，不清除已接受的值（用户 2026-10-24：「应该保持在最晚接受到的数据的状态」）。
  const hiddenItems = await topology.locator(".device-port-item").count();
  assert(hiddenItems > 0 && await topology.locator(".observation-state.error").count() === 0,
    `Hidden topology must keep its last accepted value: ${hiddenItems}`);
  await setScenario(page, { deviceTopologyInvalid: true });
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await topology.locator(".observation-state.error").waitFor();
  assert(await topology.locator(".device-port-item").count() === 0, "Resuming cannot replay last-good topology");
  await setScenario(page, { deviceTopologyInvalid: false });
  await topologyHeader.locator(".device-topology-identity").filter({ hasText: "Frontend Harness" }).waitFor();

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
  await channelProbe.assertSinglePhysicalChannel();

  const finalDirect = await readTelemetry(page, directPath);
  assert(finalDirect.started === 0,
    `Device topology callback display was overwritten by a direct GET: ${JSON.stringify(finalDirect)}`);
  // 最后切换语言（要重新加载页面，放在订阅检查之后）：清单的类别标签在英语、德语，1100 和 1440 下都完整可读。
  const badges = await checkBadgesNotTruncated(page);
  assert(pageErrors.length === 0,
    `Device topology browser failures: ${JSON.stringify(pageErrors)}`);

  return {
    dom,
    panelHeading,
    machineIdentity,
    wireFacts,
    badges,
    nullableProtocolAccepted: true,
    metricFailureClearsOnlyItsOwnData: true,
    failureClearsHiddenRetains: true,
    edidDetails,
    type3Details,
    typeXDetails,
    declarationDetails,
    detachedDetails,
    longTextPresentation,
    constrainedList,
    scrolledSelection,
    firstActivation,
    firstChannel,
    active,
    inactive,
    reactivated,
    reactivatedChannel,
    finalDirect
  };
}

async function checkDetachedDisplayDialog(page, app) {
  const topology = page.locator(".device-topology-panel");
  const root = topology.locator(".device-port-item:not(.nested)").filter({ hasText: "HDMI" }).first();
  await root.click();
  await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible");
  await dialog.waitFor();
  await dialog.getByText(app.deviceTopology.label.interfaceType, { exact: true }).first().waitFor();
  const details = {
    headings: await dialog.locator("h3").allInnerTexts(),
    edidSections: await dialog.locator("[data-display-current], [data-display-edid], [data-display-edid-raw]").count(),
    nested: await topology.locator(".device-port-item.nested").count()
  };
  assert(details.edidSections === 0
    && !details.headings.includes(app.deviceTopology.section.display)
    && !details.headings.includes(app.deviceTopology.edid.current)
    && details.headings.includes(app.deviceTopology.section.connection)
    && details.nested === 0,
  `An empty display port still lists display or EDID sections: ${JSON.stringify(details)}`);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden" });
  return details;
}

// 清单滚到下面点选一项后，后续推送不能让清单跳回顶部，也不能把条目和详情行整块重建
// （每次推送节点对象都是新的，按对象做键时整张清单被删掉重建，带焦点的按钮一删清单就回到顶部）。
async function checkScrolledSelectionSurvivesPublications(page, topology, subscriptionPath) {
  const portCount = 24;
  const rootCount = await topology.locator(".device-port-item:not(.nested)").count();
  await setScenario(page, { deviceTopologyPortCount: portCount, displayEdid: "off" });
  await page.waitForFunction((count) =>
    document.querySelectorAll(".device-topology-panel .device-port-item").length === count, portCount);
  const list = topology.locator(".device-port-list");
  await list.evaluate((element) => { element.scrollTop = element.scrollHeight; });
  const target = topology.locator(".device-port-item").nth(portCount - 2);
  await target.click();
  const before = await list.evaluate((element, index) => {
    const button = element.querySelectorAll(".device-port-item")[index];
    button.dataset.scrollProbe = "selected";
    return { scrollTop: element.scrollTop, maxScroll: element.scrollHeight - element.clientHeight };
  }, portCount - 2);
  assert(before.maxScroll > 200 && before.scrollTop > 200,
    `The fixture list does not scroll far enough to observe a jump: ${JSON.stringify(before)}`);
  const published = (await readTelemetry(page, subscriptionPath)).publications;
  await waitForTelemetry(page, subscriptionPath, value => value.publications >= published + 2);
  await page.waitForTimeout(100);
  const after = await list.evaluate((element) => {
    const probe = element.querySelector('[data-scroll-probe="selected"]');
    return {
      scrollTop: element.scrollTop,
      probeConnected: probe !== null,
      probeFocused: probe !== null && document.activeElement === probe,
      probePressed: probe?.getAttribute("aria-pressed") ?? null
    };
  });
  assert(Math.abs(after.scrollTop - before.scrollTop) <= 1
    && after.probeConnected && after.probeFocused && after.probePressed === "true",
  `Selecting a scrolled device lost its place after a publication: ${JSON.stringify({ before, after })}`);

  await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible");
  await dialog.waitFor();
  const rowCount = await dialog.evaluate((element) => {
    const rows = element.querySelectorAll(".user-details-row");
    rows.forEach((row) => { row.dataset.publicationProbe = "kept"; });
    return rows.length;
  });
  const dialogPublished = (await readTelemetry(page, subscriptionPath)).publications;
  await waitForTelemetry(page, subscriptionPath, value => value.publications >= dialogPublished + 2);
  await page.waitForTimeout(100);
  const keptRows = await dialog.evaluate((element) =>
    element.querySelectorAll('.user-details-row[data-publication-probe="kept"]').length);
  assert(rowCount > 0 && keptRows === rowCount,
    `Device details rows were rebuilt by a publication: ${JSON.stringify({ rowCount, keptRows })}`);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden" });

  await setScenario(page, { deviceTopologyPortCount: null, displayEdid: "ready" });
  await page.waitForFunction((count) =>
    document.querySelectorAll(".device-topology-panel .device-port-item:not(.nested)").length === count, rootCount);
  return { before, after, rowCount, keptRows };
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

// The mock's close of an aborted stream can reach it after the next POST on another connection, so
// overlap is measured at the browser (channelProbe); the server counts still prove one live stream.
function assertSinglePhysicalChannel(telemetry) {
  assert(telemetry.physicalStarted >= 1
    && telemetry.physicalInFlight === 1
    && telemetry.physicalCompleted === 0
    && telemetry.physicalStarted === telemetry.physicalAborted + 1,
  `Shared callback transport was not single-flight: ${JSON.stringify(telemetry)}`);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}
