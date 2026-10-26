import { createHash } from "node:crypto";
import { isDeepStrictEqual } from "node:util";
import { readFileSync } from "node:fs";
import { isAbsolute } from "node:path";
import { checkEmbeddedSvdDisplayDialog } from "./displayEdidEmbeddedSvdDialogProbe.js";
import { checkEmbeddedColourDisplayDialog } from "./displayEdidEmbeddedColourDialogProbe.js";
import { checkEmbeddedHdmiDisplayDialog } from "./displayEdidEmbeddedHdmiDialogProbe.js";
const original = JSON.parse(readFileSync(new URL("../fixtures/displayEdid.json", import.meta.url), "utf8"));
const recorded = JSON.parse(readFileSync(process.env.RM_EDID_UI_STATE_JSON || new URL("../fixtures/displayEdid.json", import.meta.url), "utf8"));
const recordedModes = recorded.snapshot.ports[0].display.modeCapabilityDeclared.modes;
const hasBitmap = recordedModes.length === 17;
const hasVicDomains = recordedModes.length === 13 || hasBitmap;
const hasCodeTiming = recordedModes.length === 9 || hasVicDomains;
const legacy = hasCodeTiming && recordedModes[7].encoding === "displayid-iv-dmt-code";
const codeTimingEncoding = legacy ? "displayid-iv-dmt-code" : "displayid-dmt-code";
if (hasCodeTiming && (recordedModes[7].encoding !== codeTimingEncoding || recordedModes[8].encoding !== codeTimingEncoding))
  throw new Error("Recorded additional declarations must be the independently bound VIII or IV pair");
const expectedDetails = hasBitmap ? 20 : hasVicDomains ? 16 : hasCodeTiming ? 12 : 10;
if (![7, 9, 13, 17].includes(recordedModes.length) || recorded.snapshot.ports[0].display.modeCapabilityDeclared.selectionDeclarations.length !== 3)
  throw new Error("Recorded EDID fixture must retain the original7 modes/3 selections plus only the2 VIII declarations");
if (hasCodeTiming && (!isDeepStrictEqual(recordedModes.slice(0, 7), original.snapshot.ports[0].display.modeCapabilityDeclared.modes)
    || !isDeepStrictEqual(recorded.snapshot.ports[0].display.modeCapabilityDeclared.selectionDeclarations, original.snapshot.ports[0].display.modeCapabilityDeclared.selectionDeclarations)))
  throw new Error("VIII input changed the original7 modes or ordered selection declarations");

if (hasBitmap && (!legacy || recordedModes[13].containerRevision !== 0x12
    || recordedModes.slice(7).some(mode => mode.containerRevision !== recordedModes[13].containerRevision)))
  throw new Error("Bitmap17 input must explicitly retain legacy structure 0x12 (DisplayID 1.2/1.3) across all DisplayID declarations");

export async function checkRecordedDisplayDialog(page, app) {
  const topology = page.locator(".device-topology-panel");
  await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible");
  const current = dialog.locator("[data-display-current]"); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", { name: app.deviceTopology.edid.declared, exact: true }).waitFor();
  await current.getByRole("heading", { name: app.deviceTopology.edid.current, exact: true }).waitFor();
  await current.getByText("60000/1001 Hz", { exact: true }).waitFor();
  await declared.getByText(app.deviceTopology.edid.hint, { exact: true }).waitFor();
  const declarations = declared.locator("details");
  await page.waitForFunction(expected => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === expected, expectedDetails);
  if (await declarations.count() !== expectedDetails) throw new Error("EDID modes or ordered selection declarations were omitted");
  await declarations.nth(1).locator("summary").click();
  await declarations.nth(1).getByText("1978/33 Hz", { exact: true }).waitFor();
  await declarations.nth(4).locator("summary").click();
  await declarations.nth(4).getByText(app.deviceTopology.edid.support420, { exact: true }).waitFor();
  await declarations.nth(6).locator("summary").click();
  await declarations.nth(6).getByText(app.deviceTopology.edid.deltaBlanking, { exact: true }).waitFor();
  if (hasCodeTiming) {
    for (const [index, code, source, geometry, rawBytes, nominal] of [[7, "4", "4:8", "640 × 480", legacy ? "04" : "04 00", "60 Hz"], [8, "88", legacy ? "4:9" : "4:10", "4096 × 2160", legacy ? "58" : "58 00", app.deviceTopology.edid.unavailable]]) {
      const mode = declarations.nth(index); await mode.locator("summary").click();
      const expected = [[app.deviceTopology.edid.source, `${codeTimingEncoding} · ${source}`], [app.deviceTopology.edid.resolution, geometry],
        [app.deviceTopology.edid.code, code], [app.deviceTopology.edid.nominal, nominal], [app.deviceTopology.edid.frameRate, app.deviceTopology.edid.unavailable], [app.deviceTopology.edid.descriptor, rawBytes]];
      for (const [label, value] of expected) {
        const row = mode.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) });
        const actual = row.locator("dd"); await actual.scrollIntoViewIfNeeded(); await actual.waitFor({ state: "visible" });
        if ((await actual.textContent())?.trim() !== value) throw new Error(`VIII visible ${label} did not match ${value}`);
      }
    }
  }
  if (hasVicDomains) {
    const offsets = legacy ? [13, 14, 18, 19] : [15, 17, 22, 24];
    for (const [i, code, geometry, nominal] of [[0,16,"1920 × 1080","60 Hz"],[1,193,"5120 × 2160","120 Hz"],[2,1,"3840 × 2160","30 Hz"],[3,4,"4096 × 2160","24 Hz"]]) {
      const mode = declarations.nth(i + 9); await mode.locator("summary").click();
      const encoding = (legacy ? "displayid-iv-" : "displayid-") + (i < 2 ? "cta-vic" : "hdmi-vic");
      const expected = [[app.deviceTopology.edid.source, `${encoding} · 4:${offsets[i]}`], [app.deviceTopology.edid.code, String(code)],
        [app.deviceTopology.edid.resolution, geometry], [app.deviceTopology.edid.nominal, nominal], [app.deviceTopology.edid.frameRate, app.deviceTopology.edid.unavailable],
        [app.deviceTopology.edid.descriptor, code.toString(16).toUpperCase().padStart(2,"0") + (legacy ? "" : " 00")]];
      for (const [label,value] of expected) {
        const actual = mode.locator(".user-details-row").filter({ has: page.getByText(label,{exact:true}) }).locator("dd");
        await actual.scrollIntoViewIfNeeded(); await actual.waitFor({state:"visible"});
        if ((await actual.textContent())?.trim() !== value) throw new Error(`DisplayID original domain visible ${label} did not match ${value}`);
      }
    }
  }
  if (hasBitmap) {
    // Independent recorded facts: DMT8 has no reported nominal rate in this verified contract.
    for (const [i, encoding, source, code, bitIndex, geometry, rawBytes, nominal] of [
      [0, "displayid-dmt-bitmap", "4:23", 4, 3, "640 × 480", "88", "60 Hz"],
      [1, "displayid-dmt-bitmap", "4:23", 8, 7, "800 × 600", "88", app.deviceTopology.edid.unavailable],
      [2, "displayid-cta-bitmap", "4:27", 1, 0, "640 × 480", "81", "60 Hz"],
      [3, "displayid-cta-bitmap", "4:27", 8, 7, "720 × 240", "81", "60 Hz"]
    ]) {
      const declaredMode = recordedModes[i + 13];
      if (declaredMode.encoding !== encoding || declaredMode.blockIndex !== 4 || declaredMode.byteOffset !== (i < 2 ? 23 : 27)
          || declaredMode.code !== code || declaredMode.bitIndex !== bitIndex
          || declaredMode.revision !== 0 || declaredMode.containerFlags !== 0
          || !isDeepStrictEqual(declaredMode.rawDescriptor, [i < 2 ? 0x88 : 0x81])
          || declaredMode.interlaced !== false || declaredMode.reducedBlanking !== (i < 2 ? false : null)
          || declaredMode.nominalVerticalRateHz !== (i === 1 ? null : 60))
        throw new Error("Bitmap17 recorded input lost independent original code/bit/source/raw facts");
      const mode = declarations.nth(i + 13); await mode.locator("summary").click();
      const expected = [[app.deviceTopology.edid.source, `${encoding} · ${source}`],
        [app.deviceTopology.edid.resolution, geometry], [app.deviceTopology.edid.code, String(code)],
        [app.deviceTopology.edid.bitIndex, String(bitIndex)], [app.deviceTopology.edid.descriptor, rawBytes],
        [app.deviceTopology.edid.nominal, nominal], [app.deviceTopology.edid.frameRate, app.deviceTopology.edid.unavailable],
        [app.deviceTopology.edid.scan, app.deviceTopology.edid.progressive], [app.deviceTopology.edid.revision, "0"],
        [app.deviceTopology.edid.containerFlags, "0"], [app.deviceTopology.edid.containerRevision, String(declaredMode.containerRevision)]];
      if (i < 2) expected.push([app.deviceTopology.edid.reduced, app.shellBridge.no]);
      for (const [label, value] of expected) {
        const actual = mode.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).locator("dd");
        await actual.scrollIntoViewIfNeeded(); await actual.waitFor({ state: "visible" });
        if ((await actual.textContent())?.trim() !== value) throw new Error(`Bitmap visible ${label} did not match ${value}`);
      }
      const absent = [["native", app.deviceTopology.edid.native], ["preferred", app.deviceTopology.edid.preferred],
        ["dscPassThrough", app.deviceTopology.edid.dsc], ["yCbCr420Encoding", app.deviceTopology.edid.encoding420],
        ["yCbCr420Only", app.deviceTopology.edid.only420], ["yCbCr420Supported", app.deviceTopology.edid.support420],
        ["formula", app.deviceTopology.edid.formula]];
      if (i >= 2) absent.push(["reducedBlanking", app.deviceTopology.edid.reduced]);
      for (const [property, label] of absent) {
        if (declaredMode[property] !== null || await mode.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).count())
          throw new Error(`Bitmap inherited unreported ${property}`);
      }
      if (declaredMode.timing !== null || declaredMode.cvtParameters !== null || declaredMode.yCbCr420CapabilityMap !== null
          || await mode.locator(".user-details-row").filter({ has: page.getByText(app.deviceTopology.edid.pixelClock, { exact: true }) }).count())
        throw new Error("Bitmap generated detailed timing/CVT/CTA reference metadata");
    }
  }
  const refreshInteraction = await checkDisplayRefreshInteraction(page, current, declarations.nth(hasBitmap ? 16 : hasVicDomains ? 12 : hasCodeTiming ? 8 : 6), app);
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  if (await raw.locator("code").textContent() !== recorded.snapshot.ports[0].display.edidRawDeclaredBase64) throw new Error("Raw EDID was truncated or replaced");
  const layout = await dialog.evaluate(element => ({ width: element.getBoundingClientRect().width, bodyWidth: element.querySelector(".user-details-body").clientWidth,
    bodyScroll: element.querySelector(".user-details-body").scrollWidth, viewport: innerWidth }));
  if (layout.width > layout.viewport || layout.bodyScroll > layout.bodyWidth + 1) throw new Error(`EDID detail/raw overflow: ${JSON.stringify(layout)}`);
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
  if (!await topology.locator(".device-details-button").evaluate(element => element === document.activeElement)) throw new Error("Original dialog did not restore keyboard focus");
  return { refreshInteraction, exactRate: true, originalRaw: true, modes: recordedModes.length, selectionBlocks: 3, displayIdOriginalVicDomainsVisible: hasVicDomains, displayIdBitmapVisible: hasBitmap, displayIdVIIINewValuesVisible: hasCodeTiming && !legacy, displayIdIVNewValuesVisible: hasCodeTiming && legacy, layout };
}

async function checkDisplayRefreshInteraction(page, current, declaration, app) {
  const opened = await declaration.elementHandle();
  const rate = current.locator(".user-details-row").filter({ has: page.getByText(app.deviceTopology.edid.refresh, { exact: true }) }).locator("dd");
  const rateNode = await rate.elementHandle();
  if (!opened || !rateNode || !await opened.evaluate(element => element.open)) throw new Error("Refresh witness requires the actual expanded declaration");
  const initial = await page.evaluate(async () => ({
    scenario: (await (await fetch("/__test/scenario")).json()).displayEdid,
    publications: (await (await fetch("/__test/requests")).json())["/api/device-topology/state/subscribe"].publications
  }));
  async function updateScenario(displayEdid) {
    const ok = await page.evaluate(async value => (await fetch("/__test/scenario", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: value })
    })).ok, displayEdid);
    if (!ok) throw new Error("Unable to configure the isolated refresh fixture");
  }
  async function checkSameNodes(expectedRate) {
    await rate.getByText(expectedRate, { exact: true }).waitFor({ state: "visible" });
    if (!await opened.evaluate(element => element.isConnected && element.open)) throw new Error("A subscription refresh replaced or collapsed the expanded declaration");
    if (!await rateNode.evaluate((element, expected) => element.isConnected && element.textContent?.trim() === expected, expectedRate)) throw new Error("The original row node did not receive the refreshed value");
  }
  try {
    await updateScenario("inactive");
    await checkSameNodes(app.deviceTopology.edid.unavailable);
    await updateScenario(initial.scenario);
    await checkSameNodes("60000/1001 Hz");
    const publications = await page.evaluate(async () => (await (await fetch("/__test/requests")).json())["/api/device-topology/state/subscribe"].publications);
    if (publications < initial.publications + 2) throw new Error("Refresh interaction did not consume both real subscription frames");
    return { sameDeclarationNode: true, remainedOpen: true, sameCurrentRowNode: true, currentValueChangedAndRestored: true, publications: publications - initial.publications };
  } finally {
    await opened.dispose(); await rateNode.dispose();
  }
}

export async function checkDisplayVariant(page, app, variant) {
  const topology = page.locator(".device-topology-panel"); await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible"); const declared = dialog.locator("[data-display-edid]");
  const current = dialog.locator("[data-display-current]");
  if (variant === "inactive" || variant === "inactiveConnected") {
    const connected = variant === "inactiveConnected";
    const monitor = topology.locator(".device-port-item.nested.device-node");
    if (await monitor.count() !== (connected ? 1 : 0))
      throw new Error(connected ? "Connected inactive display was removed from the topology" : "A cached declaration was shown as an attached display");
    const root = topology.locator(".device-port-item:not(.nested)").first();
    const state = connected ? app.deviceTopology.connectionState.connected : app.deviceTopology.connectionState.disconnected;
    if (!(await root.textContent())?.includes(state))
      throw new Error(`${variant} display interface was not marked ${state}`);
    await current.getByText(app.deviceTopology.edid.unavailable, { exact: true }).first().waitFor();
    if (await declared.locator("details").count() !== expectedDetails) throw new Error("Inactive static EDID was hidden");
  } else if (variant === "empty") {
    await declared.getByText(app.deviceTopology.edid.empty, { exact: true }).first().waitFor();
    if (await declared.locator("details").count() !== 0) throw new Error("Empty declarations inherited previous modes");
  } else {
    await declared.getByText(app.deviceTopology.edid.unavailable, { exact: true }).first().waitFor();
    if (await declared.locator("details").count() !== 0) throw new Error("Unavailable declaration retained previous modes");
  }
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  if (variant === "emptyRaw") await raw.getByText(app.deviceTopology.edid.emptyRaw, { exact: true }).waitFor();
  if (variant === "legacy") await raw.getByText(app.deviceTopology.edid.unavailable, { exact: true }).waitFor();
  if (variant === "unknown" && !(await raw.locator("code").textContent())) throw new Error("Unknown modes erased original raw declaration");
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
}


// Independent TypeIII two-mode scenario; old7/9/13/17-mode probe above is unchanged.
const type3Recording = JSON.parse(readFileSync(process.env.RM_EDID_UI_TYPE3_STATE_JSON || new URL("../fixtures/displayEdidType3.json", import.meta.url), "utf8"));
const type3Display = type3Recording.snapshot.ports[0].display;
const type3Raw = Buffer.from(type3Display.edidRawDeclaredBase64, "base64");
if (createHash("sha256").update(type3Raw).digest("hex").toUpperCase() !== "CF0102DCF20A2A973593B1DBE33E4212EC73E4A892EEB2B5185E7AB7C84C293C")
  throw new Error("TypeIII browser input is not the bound independent256B EDID");
const type3Modes = type3Display.modeCapabilityDeclared.modes;
if (type3Modes.length !== 2 || type3Modes[0].width !== 1920 || type3Modes[1].width !== 8
    || type3Modes[0].interlaced !== true || type3Modes[1].interlaced !== false
    || type3Modes[0].nominalVerticalRateHz !== 60 || type3Modes[1].nominalVerticalRateHz !== 1
    || !isDeepStrictEqual(type3Modes[0].aspectRatioDeclared, { code: 4, horizontal: 16, vertical: 9 })
    || !isDeepStrictEqual(type3Modes[1].aspectRatioDeclared, { code: 8, horizontal: null, vertical: null })
    || type3Modes.some((mode, index) => mode.encoding !== "displayid-iii" || mode.blockIndex !== 1 || mode.byteOffset !== 8 + index * 3
      || mode.height !== null || mode.timing !== null || mode.revision !== 1 || mode.containerFlags !== 1 || mode.containerRevision !== 18
      || !isDeepStrictEqual(mode.rawDescriptor, index === 0 ? [20, 239, 187] : [8, 0, 0])))
  throw new Error("TypeIII browser wire lost its independent declaration/source/nominal/aspect facts");

export async function checkType3DisplayDialog(page, app, variant = "type3") {
  const c = app.deviceTopology.edid; const topology = page.locator(".device-topology-panel");
  const roots = topology.locator(".device-port-item:not(.nested)");
  if (await roots.count() !== 1) throw new Error("TypeIII scenario requires its one exact physical interface root");
  await roots.first().click();
  if (await roots.first().getAttribute("aria-pressed") !== "true") throw new Error("TypeIII physical interface was not selected");
  // Use the original attached-device dialog to witness the actual selected parent identity.
  const attached = roots.first().locator("xpath=following-sibling::button[1]");
  if (variant === "type3Inactive") {
    // The existing tree deliberately has no attached-device child on an inactive connector.
    await attached.waitFor({ state: "detached" });
  } else {
    await attached.waitFor({ state: "visible" });
    if (!(await attached.getAttribute("class"))?.split(" ").includes("nested")) throw new Error("TypeIII root lost its attached monitor");
    await attached.click(); await topology.locator(".device-details-button").click();
    const identityDialog = page.locator(".user-details-modal:visible");
    await identityDialog.getByText(String.raw`DISPLAY\FIXTURE_HDMI_1`, { exact: true }).waitFor();
    await page.keyboard.press("Escape"); await identityDialog.waitFor({ state: "hidden" });
  }
  await roots.first().click(); await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible"); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", { name: c.declared, exact: true }).waitFor();
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  const expectedRaw = Buffer.from(type3Raw);
  if (variant === "type3Unknown") {
    expectedRaw[139] = 9; expectedRaw[142] = 0;
    expectedRaw[142] = (-expectedRaw.subarray(129, 142).reduce((sum, value) => sum + value, 0)) & 255;
    expectedRaw[255] = 0; expectedRaw[255] = (-expectedRaw.subarray(128, 255).reduce((sum, value) => sum + value, 0)) & 255;
    await page.waitForFunction(() => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === 0);
    await declared.getByText(c.unavailable, { exact: true }).waitFor();
    if (await declared.locator("details").count() !== 0) throw new Error("TypeIII unknown tail published a prefix");
  } else {
    await page.waitForFunction(() => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === 2);
    for (const i of [0, 1]) {
      const mode = declared.locator("details").nth(i); await mode.locator("summary").click();
      const expected = [[c.source, `displayid-iii · 1:${8 + i * 3}`], [c.resolution, `${c.declaredWidth}: ${i === 0 ? 1920 : 8} px`],
        [c.declaredAspect, i === 0 ? "16:9" : c.undefinedAspect], [c.declaredAspectCode, i === 0 ? "4" : "8"],
        [i === 0 ? c.nominalFieldRate : c.nominalFrameRate, i === 0 ? "60 Hz" : "1 Hz"], [c.frameRate, c.unavailable],
        [c.scan, i === 0 ? c.interlaced : c.progressive], [c.formula, "cvt"], [c.reduced, i === 0 ? app.shellBridge.yes : app.shellBridge.no],
        [c.preferred, app.shellBridge.no], [c.revision, "1"], [c.containerFlags, "1"], [c.containerRevision, "18"],
        [c.descriptor, i === 0 ? "14 EF BB" : "08 00 00"]];
      for (const [label, value] of expected) {
        const actual = mode.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).locator("dd");
        await actual.scrollIntoViewIfNeeded(); await actual.waitFor({ state: "visible" });
        await page.waitForFunction(({ index, label, value }) => {
          const declaration = document.querySelectorAll(".user-details-modal [data-display-edid] details")[index];
          return [...declaration.querySelectorAll(".user-details-row")].some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent === value);
        }, { index: i, label, value });
      }
      for (const label of [c.pixelClock, c.native, c.dsc, c.only420, c.encoding420, c.support420, c.rbVersion])
        if (await mode.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).count()) throw new Error("TypeIII invented " + label);
      if ((await mode.locator("summary").textContent())?.includes("1080")) throw new Error("TypeIII declaration inherited current pixel height");
    }
  }
  await page.waitForFunction(expected => document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent === expected, expectedRaw.toString("base64"));
  const current = dialog.locator("[data-display-current]");
  const refresh = current.locator(".user-details-row").filter({ has: page.getByText(c.refresh, { exact: true }) }).locator("dd");
  await refresh.getByText(variant === "type3Inactive" ? c.unavailable : "60000/1001 Hz", { exact: true }).waitFor();
  const layout = await dialog.evaluate(element => ({ width: element.getBoundingClientRect().width,
    bodyWidth: element.querySelector(".user-details-body").clientWidth, bodyScroll: element.querySelector(".user-details-body").scrollWidth, viewport: innerWidth }));
  if (layout.width > layout.viewport || layout.bodyScroll > layout.bodyWidth + 1) throw new Error("TypeIII detail/raw overflow");
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
  if (!await topology.locator(".device-details-button").evaluate(element => element === document.activeElement)) throw new Error("TypeIII dialog did not return keyboard focus");
  return { variant, exactIdentity: true, modes: variant === "type3Unknown" ? 0 : 2, source: "TypeIII-independent256B", rawComplete: true, widthOnly: true, layout };
}

// TypeX is accepted only from the real FrontendWire producer output supplied by the execution controller.
export async function checkTypeXDisplayDialog(page, app) {
  if (process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT === "cta-hdmi20")
    return checkEmbeddedHdmiDisplayDialog(page, app, recorded, expectedDetails);
  if (process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT === "cta-colour20")
    return checkEmbeddedColourDisplayDialog(page, app, recorded, expectedDetails);
  if (process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT === "cta-svd20")
    return checkEmbeddedSvdDisplayDialog(page, app, recorded, expectedDetails);
  const input = process.env.RM_EDID_UI_TYPEX_STATE_JSON;
  if (!input || !isAbsolute(input)) throw new Error("TypeX requires an absolute real RM_EDID_UI_TYPEX_STATE_JSON; no fallback");
  const state = JSON.parse(readFileSync(input, "utf8")); const display = state.snapshot.ports[0].display;
  const variant = process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT ?? "original";
  if (variant !== "original" && variant !== "cta-metadata") throw new Error("Unknown TypeX input variant");
  const expectedSha = variant === "cta-metadata" ? "1F1DA54864C86542EC8D63AD079BAD9079A0F2D50006BD50647FA3948B12D412"
    : "1422673347739154D366BD23059F60B2C3B4ECF49BEFB23A50DA22CCDEFE8DD6";
  if (createHash("sha256").update(Buffer.from(display.edidRawDeclaredBase64, "base64")).digest("hex").toUpperCase() !== expectedSha)
    throw new Error(`TypeX is not the selected independent256B source: ${variant}`);
  const modes = display.modeCapabilityDeclared.modes;
  const bodies = [[18,127,7,55,4,59],[2,127,7,55,4,43,1],[155,127,7,55,4,59,88,1]];
  if (modes.length !== 3 || display.modeCapabilityDeclared.ranges.length !== 0 || display.modeCapabilityDeclared.selectionDeclarations.length !== 0
      || modes.some((mode,i) => mode.encoding !== "displayid-x" || mode.blockIndex !== 1 || mode.byteOffset !== [8,17,27][i]
        || mode.width !== 1920 || mode.height !== 1080 || mode.revision !== 0 || mode.containerFlags !== i*16 || mode.containerRevision !== 32
        || mode.nominalVerticalRateHz !== (i === 1 ? 300 : 60) || mode.interlaced !== false || mode.formula !== "cvt" || mode.reducedBlanking !== true
        || mode.timing !== null || mode.native !== null || mode.preferred !== null || mode.code !== null || mode.dscPassThrough !== null
        || mode.bitIndex !== null || mode.aspectRatioDeclared != null || !isDeepStrictEqual(mode.rawDescriptor,bodies[i])))
    throw new Error("TypeX wire lost full source/body/container/nominal facts or generated unreported metadata");
  for (const [i, mode] of modes.entries()) {
    const expectedCvt = { reducedBlankingVersion:i===2?3:2, stereoCode:0, alternativeRefresh1000Over1001Supported:i===2?null:i===0,
      horizontalBlankingBase160Pixels:i===2?true:null, earlyVsyncRequired:i===2?true:null, deltaHorizontalBlankingCode:i===2?6:null,
      additionalVerticalBlankingCode:i===2?2:null, alternativeMinimumVerticalBlanking:i===2?true:null };
    if (!isDeepStrictEqual(mode.cvtParameters,expectedCvt) || mode.yCbCr420Encoding !== (i===2) || mode.yCbCr420Supported !== (i===2?true:null)
        || mode.yCbCr420Only !== null || mode.yCbCr420CapabilityMap !== null) throw new Error("TypeX wire lost exact CVT/420 declaration");
  }
  const c = app.deviceTopology.edid; const topology = page.locator(".device-topology-panel");
  const roots = topology.locator(".device-port-item:not(.nested)");
  if (await roots.count() !== 1) throw new Error("TypeX requires its exact one physical root");
  await roots.first().click();
  if(await roots.first().getAttribute("aria-pressed")!=="true") throw new Error("TypeX physical root was not selected");
  const attached=roots.first().locator("xpath=following-sibling::button[1]"); await attached.waitFor({state:"visible"});
  if(!(await attached.getAttribute("class"))?.split(" ").includes("nested")) throw new Error("TypeX selected root lost its monitor");
  await attached.click(); await topology.locator(".device-details-button").click();
  const identity=page.locator(".user-details-modal:visible");
  await identity.getByText(String.raw`DISPLAY\FIXTURE_HDMI_1`,{exact:true}).waitFor();
  await page.keyboard.press("Escape"); await identity.waitFor({state:"hidden"});
  await roots.first().click(); await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible"); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", {name:c.declared,exact:true}).waitFor();
  await page.waitForFunction(() => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === 3);
  for (const i of [0,1,2]) {
    const mode = declared.locator("details").nth(i); await mode.locator("summary").click();
    const expected = [[c.source,`displayid-x · 1:${[8,17,27][i]}`],[c.resolution,"1920 × 1080"],[c.nominal,`${i===1?300:60} Hz`],
      [c.frameRate,c.unavailable],[c.scan,c.progressive],[c.formula,"cvt"],[c.reduced,app.shellBridge.yes],[c.revision,"0"],
      [c.containerFlags,String(i*16)],[c.containerRevision,"32"],[c.rbVersion,i===2?"3":"2"],[c.stereo,"0"],
      [c.encoding420,i===2?app.shellBridge.yes:app.shellBridge.no], [c.descriptor,bodies[i].map(b=>b.toString(16).toUpperCase().padStart(2,"0")).join(" ")]];
    if (i < 2) expected.push([c.fractional,i===0?app.shellBridge.yes:app.shellBridge.no]);
    else expected.push([c.base160,app.shellBridge.yes],[c.earlyVsync,app.shellBridge.yes],[c.deltaBlanking,"6"],
      [c.addBlanking,"2"],[c.altMinimum,app.shellBridge.yes],[c.support420,app.shellBridge.yes]);
    for (const [label,value] of expected) {
      const actual = mode.locator(".user-details-row").filter({has:page.getByText(label,{exact:true})}).locator("dd");
      await actual.scrollIntoViewIfNeeded(); await actual.waitFor({state:"visible"});
      await page.waitForFunction(({i,label,value}) => [...document.querySelectorAll(".user-details-modal [data-display-edid] details")[i].querySelectorAll(".user-details-row")]
        .some(row=>row.querySelector("dt")?.textContent===label && row.querySelector("dd")?.textContent===value),{i,label,value});
    }
    const absent=[c.pixelClock,c.native,c.preferred,c.dsc,c.only420,c.code,c.bitIndex,c.declaredAspect,c.declaredAspectCode];
    if(i<2) absent.push(c.base160,c.earlyVsync,c.deltaBlanking,c.addBlanking,c.altMinimum,c.support420); else absent.push(c.fractional);
    for(const label of absent) if(await mode.locator(".user-details-row").filter({has:page.getByText(label,{exact:true})}).count())
      throw new Error("TypeX displayed an unreported value: "+label);
  }
  const raw=dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  await page.waitForFunction(expected=>document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent===expected,display.edidRawDeclaredBase64);
  const current=dialog.locator("[data-display-current]"); await current.getByText("60000/1001 Hz",{exact:true}).waitFor();
  const layout=await dialog.evaluate(element=>({width:element.getBoundingClientRect().width,viewport:innerWidth,
    bodyWidth:element.querySelector(".user-details-body").clientWidth,bodyScroll:element.querySelector(".user-details-body").scrollWidth}));
  if(layout.width>layout.viewport || layout.bodyScroll>layout.bodyWidth+1) throw new Error("TypeX details/raw overflow");
  // A changed payload must reach the already-open modal, not just increment mock telemetry.
  const modalNode=await dialog.elementHandle();
  try {
    for(const [scenario,count,encoding,rawValue] of [["ready",expectedDetails,"edid-detailed",recorded.snapshot.ports[0].display.edidRawDeclaredBase64],
      ["typex",3,"displayid-x",display.edidRawDeclaredBase64]]) {
      const ok=await page.evaluate(async displayEdid=>(await fetch("/__test/scenario",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({displayEdid})})).ok,scenario);
      if(!ok) throw new Error("TypeX open-modal refresh scenario failed");
      await page.waitForFunction(({count,encoding,rawValue,label})=>{
        const modal=document.querySelector(".user-details-modal"); const edid=modal?.querySelector("[data-display-edid]");
        const rows=edid?.querySelectorAll("details .user-details-row")??[];
        return edid?.querySelectorAll("details").length===count
          && [...rows].some(row=>row.querySelector("dt")?.textContent===label && row.querySelector("dd")?.textContent?.startsWith(encoding+" · "))
          && modal?.querySelector("[data-display-edid-raw] code")?.textContent===rawValue;
      },{count,encoding,rawValue,label:c.source});
      if(!modalNode || !await modalNode.evaluate(element=>element.isConnected && element===document.querySelector(".user-details-modal")))
        throw new Error("TypeX refresh replaced the original open dialog");
    }
  } finally { await modalNode?.dispose(); }
  await page.keyboard.press("Escape"); await dialog.waitFor({state:"hidden"});
  if(!await topology.locator(".device-details-button").evaluate(element=>element===document.activeElement)) throw new Error("TypeX did not restore keyboard focus");
  return {modes:3,source:"TypeX-real-wire-independent256B",cvt6And7And8Bytes:true,rawComplete:true,openModalChangedAndRestored:true,layout};
}
