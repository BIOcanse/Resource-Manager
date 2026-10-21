import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { isAbsolute } from "node:path";

export async function checkEmbeddedColourDisplayDialog(page, app, baseline, baselineDetails) {
  const variant = process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT;
  const spec = {
    "cta-colour20": [0x20, "FDC32DA72FCDAA0F93DAFA5550C519F2B4B5CCBAB414092AAACE829B0717E307"],
    "cta-colour21": [0x21, "57D6AE976C78C7DE4C8EDD865131006E1274DE456F07AF4852320DA245D16E68"]
  }[variant];
  const input = process.env.RM_EDID_UI_TYPEX_STATE_JSON;
  if (!spec || !input || !isAbsolute(input)) throw new Error("Embedded colour requires its explicit variant and absolute real wire input");
  const state = JSON.parse(readFileSync(input, "utf8"));
  const display = state.snapshot.ports[0].display; const capability = display.modeCapabilityDeclared;
  const bytes = Buffer.from(display.edidRawDeclaredBase64, "base64");
  if (bytes.length !== 256 || bytes[129] !== spec[0] || createHash("sha256").update(bytes).digest("hex").toUpperCase() !== spec[1])
    throw new Error("Embedded colour wire is not the selected independent literal");
  const modes = capability.modes; const codes = [16, 65, 193, 97], widths = [1920, 1280, 5120, 3840], heights = [1080, 720, 2160, 2160], rates = [60, 24, 120, 60], rawBytes = [0x90, 0x41, 0xC1, 97], offsets = [18, 19, 20, 23], supported = [true, false, true, true];
  if (modes.length !== 5 || capability.ranges.length || capability.selectionDeclarations.length || capability.ctaFlags !== null
      || modes[0].encoding !== "displayid-x" || modes[0].blockIndex !== 1 || modes[0].byteOffset !== 8
      || modes[0].width !== 1920 || modes[0].height !== 1080 || modes[0].nominalVerticalRateHz !== 60
      || modes[0].revision !== 0 || modes[0].containerFlags !== 0 || modes[0].containerRevision !== spec[0]
      || modes[0].timing !== null || modes[0].native !== null || modes[0].cvtParameters?.reducedBlankingVersion !== 2
      || modes[0].cvtParameters?.alternativeRefresh1000Over1001Supported !== true
      || JSON.stringify(modes[0].rawDescriptor) !== JSON.stringify([0x12, 0x7F, 7, 0x37, 4, 0x3B]))
    throw new Error("Embedded colour wire lost its complete declarations or invented a CTA header");
  for (let i = 0; i < 4; i++) {
    const mode = modes[i + 1];
    if (mode.encoding !== (i === 3 ? "displayid-cta-y420-vic" : "displayid-cta-svd") || mode.blockIndex !== 1 || mode.byteOffset !== offsets[i] || mode.revision !== 0
        || mode.containerFlags !== 0 || mode.containerRevision !== spec[0] || mode.code !== codes[i]
        || mode.width !== widths[i] || mode.height !== heights[i] || mode.nominalVerticalRateHz !== rates[i]
        || mode.native !== (i === 3 ? null : i === 0) || mode.interlaced !== false || mode.yCbCr420Only !== (i === 3)
        || mode.rawDescriptor.length !== 1 || mode.rawDescriptor[0] !== rawBytes[i]
        || [mode.timing, mode.cvtParameters, mode.preferred, mode.dscPassThrough, mode.yCbCr420Encoding].some(value => value !== null))
      throw new Error("Embedded colour wire changed an original code, native bit, position or unreported field");
    if (mode.yCbCr420Supported !== supported[i]) throw new Error("Embedded colour lost supported=false or only420=true");
    const map = mode.yCbCr420CapabilityMap;
    if (i < 3) {
      if (!map || map.blockIndex !== 1 || map.byteOffset !== 24 || map.bitIndex !== 16 + i || JSON.stringify(map.rawDescriptor) !== JSON.stringify([0xE2, 0x0F, 5]))
        throw new Error("Embedded colour lost the real bitmap source or logical ordinal");
    } else if (map !== null) throw new Error("Only420 acquired a synthetic bitmap ordinal");
  }
  const c = app.deviceTopology.edid; const topology = page.locator(".device-topology-panel");
  const roots = topology.locator(".device-port-item:not(.nested)");
  if (await roots.count() !== 1) throw new Error("Embedded colour requires its exact physical root");
  await roots.first().click();
  if (await roots.first().getAttribute("aria-pressed") !== "true") throw new Error("Embedded colour root was not selected");
  const attached = roots.first().locator("xpath=following-sibling::button[1]"); await attached.waitFor({ state: "visible" });
  if (!(await attached.getAttribute("class"))?.split(" ").includes("nested")) throw new Error("Embedded colour root lost its monitor");
  await attached.click(); await topology.locator(".device-details-button").click();
  const identity = page.locator(".user-details-modal:visible");
  await identity.getByText(String.raw`DISPLAY\FIXTURE_HDMI_1`, { exact: true }).waitFor();
  await page.keyboard.press("Escape"); await identity.waitFor({ state: "hidden" });
  await roots.first().click(); await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible"); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", { name: c.declared, exact: true }).waitFor();
  await page.waitForFunction(() => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === 5);
  for (let i = 0; i < 5; i++) {
    const detail = declared.locator("details").nth(i); await detail.locator("summary").click();
    const mode = modes[i];
    const expected = [[c.source, `${mode.encoding} · 1:${mode.byteOffset}`], [c.resolution, `${mode.width} × ${mode.height}`],
      [c.nominal, `${mode.nominalVerticalRateHz} Hz`], [c.frameRate, c.unavailable], [c.scan, c.progressive], [c.revision, "0"],
      [c.containerFlags, "0"], [c.containerRevision, String(spec[0])], [c.descriptor, mode.rawDescriptor.map(b => b.toString(16).toUpperCase().padStart(2, "0")).join(" ")]];
    if (i === 0) expected.push([c.formula, "cvt"], [c.reduced, app.shellBridge.yes], [c.rbVersion, "2"], [c.stereo, "0"], [c.fractional, app.shellBridge.yes], [c.encoding420, app.shellBridge.no]);
    else {
      expected.push([c.code, String(codes[i - 1])], [c.only420, i === 4 ? app.shellBridge.yes : app.shellBridge.no],
        [c.support420, supported[i - 1] ? app.shellBridge.yes : app.shellBridge.no]);
      if (i < 4) expected.push([c.native, i === 1 ? app.shellBridge.yes : app.shellBridge.no], [`${c.support420} · ${c.source}`, "1:24 · E2 0F 05"]);
    }
    for (const [label, value] of expected) {
      const row = detail.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).locator("dd");
      await row.scrollIntoViewIfNeeded(); await row.waitFor({ state: "visible" });
      await page.waitForFunction(({ i, label, value }) => [...document.querySelectorAll(".user-details-modal [data-display-edid] details")[i].querySelectorAll(".user-details-row")]
        .some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent === value), { i, label, value });
    }
    const absent = [c.pixelClock, c.preferred, c.dsc, c.bitIndex, c.declaredAspect, c.declaredAspectCode,
      c.base160, c.earlyVsync, c.deltaBlanking, c.addBlanking, c.altMinimum];
    if (i === 0) absent.push(c.code, c.native, c.only420, c.support420, `${c.support420} · ${c.source}`);
    else absent.push(c.formula, c.reduced, c.rbVersion, c.stereo, c.fractional, c.encoding420);
    if (i === 4) absent.push(c.native, `${c.support420} · ${c.source}`);
    for (const label of absent) if (await detail.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).count())
      throw new Error("Embedded colour displayed an unreported value: " + label);
  }
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  await page.waitForFunction(expected => document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent === expected, display.edidRawDeclaredBase64);
  await dialog.locator("[data-display-current]").getByText("60000/1001 Hz", { exact: true }).waitFor();
  const layout = await dialog.evaluate(element => ({ width: element.getBoundingClientRect().width, viewport: innerWidth,
    bodyWidth: element.querySelector(".user-details-body").clientWidth, bodyScroll: element.querySelector(".user-details-body").scrollWidth }));
  if (layout.width > layout.viewport || layout.bodyScroll > layout.bodyWidth + 1) throw new Error("Embedded colour details/raw overflow");
  const modalNode = await dialog.elementHandle();
  try {
    for (const [scenario, count, encoding, rawValue] of [["ready", baselineDetails, "edid-detailed", baseline.snapshot.ports[0].display.edidRawDeclaredBase64],
      ["typex", 5, "displayid-cta-svd", display.edidRawDeclaredBase64]]) {
      const ok = await page.evaluate(async displayEdid => (await fetch("/__test/scenario", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid }) })).ok, scenario);
      if (!ok) throw new Error("Embedded colour refresh scenario failed");
      await page.waitForFunction(({ count, encoding, rawValue, label }) => {
        const modal = document.querySelector(".user-details-modal"); const edid = modal?.querySelector("[data-display-edid]");
        return edid?.querySelectorAll("details").length === count && [...(edid?.querySelectorAll("details .user-details-row") ?? [])]
          .some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent?.startsWith(encoding + " · "))
          && modal?.querySelector("[data-display-edid-raw] code")?.textContent === rawValue;
      }, { count, encoding, rawValue, label: c.source });
      if (!modalNode || !await modalNode.evaluate(element => element.isConnected && element === document.querySelector(".user-details-modal")))
        throw new Error("Embedded colour refresh replaced the original open dialog");
    }
  } finally { await modalNode?.dispose(); }
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
  if (!await topology.locator(".device-details-button").evaluate(element => element === document.activeElement)) throw new Error("Embedded colour lost keyboard focus");
  return { modes: 5, source: "embedded-colour-real-wire-independent256B", containerVersion: spec[0], fullVic193: true,
    nativeBit: true, only420NativeNull: true, supportedFalseRetained: true, exactBitmapSource: true, rawComplete: true, openModalChangedAndRestored: true, layout };
}
