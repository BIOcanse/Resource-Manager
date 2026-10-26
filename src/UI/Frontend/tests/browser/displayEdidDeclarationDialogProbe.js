import { NOT_GHOST } from "./liveLocator.js";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";

// DisplayID Type V/VI、Adaptive-Sync 与动态时序范围：五份仓库夹具都由真实 parser／生产 DTO 生成
// （tests/Validation/FrontendWire/DisplayEdidUiWireFixtureTests.Declarations.cs）。先核对夹具里的原始事实，再核对真实弹窗。
const recordings = {
  typeVVi: ["displayEdidTypeVVi.json", "644E957618D4D9CB14F78BA4B1457406BAF2F1C354FE4205D0768819DBB479EC"],
  typeVViClock: ["displayEdidTypeVViClock.json", "7F06B45FDF4EE0D0EE2A2C6AB68926CF3A58DB4A92639CFB89B9C5CF760201CD"],
  adaptiveSync: ["displayEdidAdaptiveSync.json", "87E91403320AFB0C412BB5555564BFF2E5239B3227E61DD6052628019F567EA1"],
  adaptiveSyncZero: ["displayEdidAdaptiveSyncZero.json", "1A350FEA5B69822DE38E3994ACA4EBFC38059407FAF4F20FA71954420F2D015D"],
  dynamicRange: ["displayEdidDynamicRange.json", "21D9B9FBE827A5CCBD455DD9014734F9611D62274CC55CCE815903B666E50A53"]
};
const displays = Object.fromEntries(Object.entries(recordings).map(([variant, [file, sha]]) => {
  const display = JSON.parse(readFileSync(new URL(`../fixtures/${file}`, import.meta.url), "utf8")).snapshot.ports[0].display;
  if (createHash("sha256").update(Buffer.from(display.edidRawDeclaredBase64, "base64")).digest("hex").toUpperCase() !== sha)
    throw new Error(`${variant} browser input is not its bound independent EDID`);
  return [variant, display];
}));
const hex = bytes => bytes.map(value => value.toString(16).toUpperCase().padStart(2, "0")).join(" ");
for (const variant of ["typeVVi", "typeVViClock"]) {
  const c = displays[variant].modeCapabilityDeclared; const [typeV, vi14, vi17] = c.modes;
  const clockConflict = variant === "typeVViClock";
  if (c.modes.length !== 3 || c.ranges.length || c.dynamicTimingRanges.length || c.adaptiveSyncRanges.length
      || typeV.encoding !== "displayid-v" || typeV.timing !== null || typeV.cvtParameters !== null || typeV.containerRevision !== 0x12
      || JSON.stringify(typeV.displayIdTypeVOptions) !== JSON.stringify({ formulaCode: 1, stereoSupportCode: 2, fractionalRefreshSupported: true })
      || hex(typeV.rawDescriptor) !== "D1 A5 7F 07 37 04 3B"
      || vi14.encoding !== "displayid-vi" || vi14.byteOffset !== 8 || vi14.rawDescriptor.length !== 14 || "displayIdTypeViImageSize" in vi14
      || (vi14.timing === null) !== clockConflict || (vi14.displayIdTypeViTiming.pixelClockHz === null) !== clockConflict
      || vi14.interlaced !== clockConflict || vi14.displayIdTypeViTiming.horizontalBlankingPixels !== 280 || vi14.displayIdTypeViTiming.verticalSyncWidthLines !== 5
      || vi17.byteOffset !== 22 || vi17.rawDescriptor.length !== 17 || vi17.timing?.pixelClockHz !== 148500000
      || JSON.stringify(vi17.aspectRatioDeclared) !== JSON.stringify({ code: 152, horizontal: 456, vertical: 256 })
      || JSON.stringify(vi17.displayIdTypeViImageSize) !== JSON.stringify({ aspectMultiplier: 152, verticalImageBaseSizeMillimeters: 299, sizeMultiplier: 2, verticalImageSizeMillimeters: 598 }))
    throw new Error(`${variant} wire lost its Type V/VI declarations or generated a timing`);
}
for (const [variant, zero] of [["adaptiveSync", false], ["adaptiveSyncZero", true]]) {
  const c = displays[variant].modeCapabilityDeclared; const range = c.adaptiveSyncRanges[0];
  if (c.modes.length || c.dynamicTimingRanges.length || c.adaptiveSyncRanges.length !== 1 || range.containerRevision !== 0x20
      || range.frameDurationIncrease.zeroCodeMeansNoJitterThroughoutRange !== zero || range.frameDurationDecrease.zeroCodeMeansNoJitterThroughoutRange !== zero
      || (range.frameDurationIncrease.maximumSingleStepMicroseconds === null) !== zero || range.nativePanelRange !== zero)
    throw new Error(`${variant} wire lost its Adaptive-Sync declaration`);
}
{
  const c = displays.dynamicRange.modeCapabilityDeclared; const range = c.dynamicTimingRanges[0];
  if (c.modes.length || c.adaptiveSyncRanges.length || c.dynamicTimingRanges.length !== 1 || range.revision !== 1
      || range.minimumPixelClockHz !== 60000000 || range.maximumPixelClockHz !== 600000000 || range.maximumVerticalRateHz !== 360)
    throw new Error("dynamicRange wire lost its DisplayID 2.0 dynamic timing range");
}

function expectations(variant, app) {
  const c = app.deviceTopology.edid; const yes = app.shellBridge.yes; const no = app.shellBridge.no;
  const typeVVi = clockConflict => ({ section: c.modes, details: [
    { title: `1920 × 1080 · 60 Hz (${c.nominal})`, rows: [[c.source, "displayid-v · 1:8"], [c.resolution, "1920 × 1080"], [c.nominal, "60 Hz"],
      [c.frameRate, c.unavailable], [c.formula, "cvt"], [c.formulaCode, "1"], [c.stereo, "2"], [c.fractional, yes], [c.containerRevision, "18"],
      [c.descriptor, "D1 A5 7F 07 37 04 3B"]], absent: [c.pixelClock, c.horizontal, c.vertical, c.rbVersion, c.aspectMultiplier] },
    { title: clockConflict ? "1920 × 1080" : "1920 × 1080 · 60/1 Hz", rows: [[c.source, "displayid-vi · 2:8"],
      [c.pixelClock, clockConflict ? c.clockConflict : "148500000 Hz"], [c.frameRate, clockConflict ? c.clockConflict : "60/1 Hz"],
      [c.horizontal, "1920 / 280 / 88 / 44"], [c.vertical, "1080 / 45 / 4 / 5"], [c.horizontalPositive, yes], [c.verticalPositive, yes],
      [c.stereo, "1"], [c.scan, clockConflict ? c.interlaced : c.progressive],
      [c.descriptor, clockConflict ? "13 44 A2 7F 87 37 84 17 57 10 2B 2C 03 A4" : "13 44 82 7F 87 37 84 17 57 10 2B 2C 03 24"]],
      absent: [c.fieldRate, c.declaredAspect, c.declaredAspectCode, c.aspectMultiplier, c.verticalImageSize, c.formulaCode] },
    { title: "1920 × 1080 · 60/1 Hz", rows: [[c.source, "displayid-vi · 2:22"], [c.pixelClock, "148500000 Hz"], [c.declaredAspect, "456:256"],
      [c.aspectMultiplier, "152"], [c.verticalImageSize, "299 mm × 2 = 598 mm"],
      [c.descriptor, "13 44 C2 7F 87 37 84 17 57 10 2B 2C 03 24 98 2A 11"]], absent: [c.declaredAspectCode] }
  ] });
  const adaptive = zero => ({ section: c.adaptiveSync, details: [
    { title: `${c.declaredBase}: ${zero ? "48–144" : "1–1024"} Hz`, rows: [[c.source, "DisplayID 0x2B · 1:8"], [c.revision, "0"],
      [c.containerFlags, "0"], [c.containerRevision, "32"], [c.declaredBaseRange, `${zero ? "48–144" : "1–1024"} Hz`],
      [c.nativePanelRange, zero ? yes : no], [c.avtSupported, zero ? no : yes], [c.seamlessTransition, yes],
      [c.frameIncrease, zero ? c.noJitterLimit : "250 µs"], [c.frameIncreaseScope, zero ? no : yes],
      [c.frameDecrease, zero ? c.noJitterLimit : "63750 µs"], [c.frameDecreaseScope, zero ? no : yes],
      [c.descriptor, zero ? "01 00 30 8F 00 00" : "26 01 01 FF 03 FF"]], absent: [c.pixelClock, c.frameRate] }
  ] });
  return {
    typeVVi: typeVVi(false), typeVViClock: typeVVi(true), adaptiveSync: adaptive(false), adaptiveSyncZero: adaptive(true),
    dynamicRange: { section: c.dynamicRanges, details: [
      { title: `${c.verticalRange}: 48–360 Hz`, rows: [[c.source, "DisplayID 0x25 · 1:8"], [c.revision, "1"], [c.containerFlags, "1"],
        [c.containerRevision, "32"], [c.verticalRange, "48–360 Hz"], [c.pixelClockRange, "60000000–600000000 Hz"],
        [c.seamlessDynamicBlanking, yes], [c.descriptor, "5F EA 00 BF 27 09 30 68 81"]], absent: [c.pixelClock, c.frameRate] }
    ] }
  }[variant];
}

async function waitForRow(page, index, label, value) {
  await page.waitForFunction(({ index, label, value }) => {
    const declaration = document.querySelectorAll(".user-details-modal [data-display-edid] details")[index];
    return !!declaration && [...declaration.querySelectorAll(".user-details-row")].some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent === value);
  }, { index, label, value });
}

/** 已选物理接口的原详情弹窗显示 Type V/VI、Adaptive-Sync 或动态时序范围声明；不新增入口。 */
export async function checkDeclarationDisplayDialog(page, app, variant) {
  const spec = expectations(variant, app); const display = displays[variant];
  if (!spec || !display) throw new Error("Unknown declaration variant: " + variant);
  const c = app.deviceTopology.edid; const topology = page.locator(".device-topology-panel");
  const roots = topology.locator(".device-port-item:not(.nested)");
  if (await roots.count() !== 1) throw new Error(`${variant} requires its exact one physical root`);
  await roots.first().click();
  if (await roots.first().getAttribute("aria-pressed") !== "true") throw new Error(`${variant} physical root was not selected`);
  await topology.locator(".device-details-button").click();
  const dialog = page.locator(`.user-details-modal${NOT_GHOST}:visible`); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", { name: c.declared, exact: true }).waitFor();
  // 先等到本场景的真实载荷到达，避免读到上一个场景留下的同数量声明。
  await page.waitForFunction(expected => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === expected.count
    && document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent === expected.raw,
  { count: spec.details.length, raw: display.edidRawDeclaredBase64 });
  await declared.getByRole("heading", { name: spec.section, exact: true }).waitFor();
  for (const [index, detail] of spec.details.entries()) {
    const item = declared.locator("details").nth(index);
    if ((await item.locator("summary").textContent())?.trim() !== detail.title) throw new Error(`${variant} declaration ${index} title did not match ${detail.title}`);
    await item.locator("summary").click();
    for (const [label, value] of detail.rows) {
      const row = item.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).locator("dd");
      await row.first().scrollIntoViewIfNeeded(); await row.first().waitFor({ state: "visible" });
      await waitForRow(page, index, label, value);
    }
    for (const label of detail.absent)
      if (await item.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).count()) throw new Error(`${variant} displayed an unreported value: ${label}`);
    if (await item.locator("dd").filter({ hasText: /(^|\D)0 µs/ }).count()) throw new Error(`${variant} rendered a zero-code limit as 0 µs`);
  }
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  await page.waitForFunction(expected => document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent === expected, display.edidRawDeclaredBase64);
  await dialog.locator("[data-display-current]").getByText("60000/1001 Hz", { exact: true }).waitFor();
  const layout = await dialog.evaluate(element => ({ width: element.getBoundingClientRect().width, viewport: innerWidth,
    bodyWidth: element.querySelector(".user-details-body").clientWidth, bodyScroll: element.querySelector(".user-details-body").scrollWidth }));
  if (layout.width > layout.viewport || layout.bodyScroll > layout.bodyWidth + 1) throw new Error(`${variant} details/raw overflow: ${JSON.stringify(layout)}`);
  let openRefresh = false;
  if (variant === "adaptiveSync") {
    // 同一已选设备的模态内：订阅推来零码声明时，已展开的同一声明节点原位更新，不收拢、不重建。
    const opened = await declared.locator("details").first().elementHandle();
    try {
      for (const [scenario, value] of [["adaptiveSyncZero", c.noJitterLimit], ["adaptiveSync", "250 µs"]]) {
        const ok = await page.evaluate(async displayEdid => (await fetch("/__test/scenario", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid }) })).ok, scenario);
        if (!ok) throw new Error("Adaptive-Sync refresh scenario failed");
        await waitForRow(page, 0, c.frameIncrease, value);
        if (!opened || !await opened.evaluate(element => element.isConnected && element.open)) throw new Error("Adaptive-Sync refresh replaced or collapsed the expanded declaration");
      }
      openRefresh = true;
    } finally { await opened?.dispose(); }
  }
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
  if (!await topology.locator(".device-details-button").evaluate(element => element === document.activeElement)) throw new Error(`${variant} dialog did not return keyboard focus`);
  return { variant, declarations: spec.details.length, rawComplete: true, openRefresh, layout };
}
