import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { isAbsolute } from "node:path";

export async function checkEmbeddedSvdDisplayDialog(page, app, baseline, baselineDetails) {
  const variant = process.env.RM_EDID_UI_TYPEX_INPUT_VARIANT;
  const spec = {
    "cta-svd20": [0x20, "DD066D47BEF3DA4F7BCC1AE47B65A2589E67F4E048CF92509013F389FA443C42"],
    "cta-svd21": [0x21, "5C3048E6D11E41BB54E15443FC24A50873D0675ECF6ED62295E3E61F86DF7AD8"]
  }[variant];
  const input = process.env.RM_EDID_UI_TYPEX_STATE_JSON;
  if (!spec || !input || !isAbsolute(input)) throw new Error("Embedded SVD requires its explicit variant and absolute real wire input");
  const state = JSON.parse(readFileSync(input, "utf8"));
  const display = state.snapshot.ports[0].display; const capability = display.modeCapabilityDeclared;
  const bytes = Buffer.from(display.edidRawDeclaredBase64, "base64");
  if (bytes.length !== 256 || bytes[129] !== spec[0] || createHash("sha256").update(bytes).digest("hex").toUpperCase() !== spec[1])
    throw new Error("Embedded SVD wire is not the selected independent literal");
  const modes = capability.modes; const codes = [16, 65, 193], widths = [1920, 1280, 5120], heights = [1080, 720, 2160], rates = [60, 24, 120], rawBytes = [0x90, 0x41, 0xC1];
  if (modes.length !== 4 || capability.ranges.length || capability.selectionDeclarations.length || capability.ctaFlags !== null
      || modes[0].encoding !== "displayid-x" || modes[0].blockIndex !== 1 || modes[0].byteOffset !== 8
      || modes[0].width !== 1920 || modes[0].height !== 1080 || modes[0].nominalVerticalRateHz !== 60
      || modes[0].revision !== 0 || modes[0].containerFlags !== 0 || modes[0].containerRevision !== spec[0]
      || modes[0].timing !== null || modes[0].native !== null || modes[0].cvtParameters?.reducedBlankingVersion !== 2
      || modes[0].cvtParameters?.alternativeRefresh1000Over1001Supported !== true
      || JSON.stringify(modes[0].rawDescriptor) !== JSON.stringify([0x12, 0x7F, 7, 0x37, 4, 0x3B]))
    throw new Error("Embedded SVD wire lost its complete declarations or invented a CTA header");
  for (let i = 0; i < 3; i++) {
    const mode = modes[i + 1];
    if (mode.encoding !== "displayid-cta-svd" || mode.blockIndex !== 1 || mode.byteOffset !== 18 + i || mode.revision !== 0
        || mode.containerFlags !== 0 || mode.containerRevision !== spec[0] || mode.code !== codes[i]
        || mode.width !== widths[i] || mode.height !== heights[i] || mode.nominalVerticalRateHz !== rates[i]
        || mode.native !== (i === 0) || mode.interlaced !== false || mode.yCbCr420Only !== false
        || mode.rawDescriptor.length !== 1 || mode.rawDescriptor[0] !== rawBytes[i]
        || [mode.timing, mode.cvtParameters, mode.preferred, mode.dscPassThrough, mode.yCbCr420Supported, mode.yCbCr420CapabilityMap].some(value => value !== null))
      throw new Error("Embedded SVD wire changed an original code, native bit, position or unreported field");
  }
  const c = app.deviceTopology.edid; const topology = page.locator(".device-topology-panel");
  const roots = topology.locator(".device-port-item:not(.nested)");
  if (await roots.count() !== 1) throw new Error("Embedded SVD requires its exact physical root");
  await roots.first().click();
  if (await roots.first().getAttribute("aria-pressed") !== "true") throw new Error("Embedded SVD root was not selected");
  const attached = roots.first().locator("xpath=following-sibling::button[1]"); await attached.waitFor({ state: "visible" });
  if (!(await attached.getAttribute("class"))?.split(" ").includes("nested")) throw new Error("Embedded SVD root lost its monitor");
  await attached.click(); await topology.locator(".device-details-button").click();
  const identity = page.locator(".user-details-modal:visible");
  await identity.getByText(String.raw`DISPLAY\FIXTURE_HDMI_1`, { exact: true }).waitFor();
  await page.keyboard.press("Escape"); await identity.waitFor({ state: "hidden" });
  await roots.first().click(); await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible"); const declared = dialog.locator("[data-display-edid]");
  await declared.getByRole("heading", { name: c.declared, exact: true }).waitFor();
  await page.waitForFunction(() => document.querySelectorAll(".user-details-modal [data-display-edid] details").length === 4);
  for (let i = 0; i < 4; i++) {
    const detail = declared.locator("details").nth(i); await detail.locator("summary").click();
    const mode = modes[i];
    const expected = [[c.source, `${mode.encoding} · 1:${mode.byteOffset}`], [c.resolution, `${mode.width} × ${mode.height}`],
      [c.nominal, `${mode.nominalVerticalRateHz} Hz`], [c.frameRate, c.unavailable], [c.scan, c.progressive], [c.revision, "0"],
      [c.containerFlags, "0"], [c.containerRevision, String(spec[0])], [c.descriptor, mode.rawDescriptor.map(b => b.toString(16).toUpperCase().padStart(2, "0")).join(" ")]];
    if (i === 0) expected.push([c.formula, "cvt"], [c.reduced, app.shellBridge.yes], [c.rbVersion, "2"], [c.stereo, "0"], [c.fractional, app.shellBridge.yes], [c.encoding420, app.shellBridge.no]);
    else expected.push([c.code, String(codes[i - 1])], [c.native, i === 1 ? app.shellBridge.yes : app.shellBridge.no], [c.only420, app.shellBridge.no]);
    for (const [label, value] of expected) {
      const row = detail.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).locator("dd");
      await row.scrollIntoViewIfNeeded(); await row.waitFor({ state: "visible" });
      await page.waitForFunction(({ i, label, value }) => [...document.querySelectorAll(".user-details-modal [data-display-edid] details")[i].querySelectorAll(".user-details-row")]
        .some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent === value), { i, label, value });
    }
    const absent = [c.pixelClock, c.preferred, c.dsc, c.support420, c.bitIndex, c.declaredAspect, c.declaredAspectCode,
      c.base160, c.earlyVsync, c.deltaBlanking, c.addBlanking, c.altMinimum];
    if (i === 0) absent.push(c.code, c.native, c.only420);
    else absent.push(c.formula, c.reduced, c.rbVersion, c.stereo, c.fractional, c.encoding420);
    for (const label of absent) if (await detail.locator(".user-details-row").filter({ has: page.getByText(label, { exact: true }) }).count())
      throw new Error("Embedded SVD displayed an unreported value: " + label);
  }
  const raw = dialog.locator("[data-display-edid-raw]"); await raw.locator("summary").click();
  await page.waitForFunction(expected => document.querySelector(".user-details-modal [data-display-edid-raw] code")?.textContent === expected, display.edidRawDeclaredBase64);
  await dialog.locator("[data-display-current]").getByText("60000/1001 Hz", { exact: true }).waitFor();
  const layout = await dialog.evaluate(element => ({ width: element.getBoundingClientRect().width, viewport: innerWidth,
    bodyWidth: element.querySelector(".user-details-body").clientWidth, bodyScroll: element.querySelector(".user-details-body").scrollWidth }));
  if (layout.width > layout.viewport || layout.bodyScroll > layout.bodyWidth + 1) throw new Error("Embedded SVD details/raw overflow");
  const modalNode = await dialog.elementHandle();
  try {
    for (const [scenario, count, encoding, rawValue] of [["ready", baselineDetails, "edid-detailed", baseline.snapshot.ports[0].display.edidRawDeclaredBase64],
      ["typex", 4, "displayid-cta-svd", display.edidRawDeclaredBase64]]) {
      const ok = await page.evaluate(async displayEdid => (await fetch("/__test/scenario", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid }) })).ok, scenario);
      if (!ok) throw new Error("Embedded SVD refresh scenario failed");
      await page.waitForFunction(({ count, encoding, rawValue, label }) => {
        const modal = document.querySelector(".user-details-modal"); const edid = modal?.querySelector("[data-display-edid]");
        return edid?.querySelectorAll("details").length === count && [...(edid?.querySelectorAll("details .user-details-row") ?? [])]
          .some(row => row.querySelector("dt")?.textContent === label && row.querySelector("dd")?.textContent?.startsWith(encoding + " · "))
          && modal?.querySelector("[data-display-edid-raw] code")?.textContent === rawValue;
      }, { count, encoding, rawValue, label: c.source });
      if (!modalNode || !await modalNode.evaluate(element => element.isConnected && element === document.querySelector(".user-details-modal")))
        throw new Error("Embedded SVD refresh replaced the original open dialog");
    }
  } finally { await modalNode?.dispose(); }
  await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" });
  if (!await topology.locator(".device-details-button").evaluate(element => element === document.activeElement)) throw new Error("Embedded SVD lost keyboard focus");
  return { modes: 4, source: "embedded-SVD-real-wire-independent256B", containerVersion: spec[0], fullVic193: true,
    nativeBit: true, rawComplete: true, openModalChangedAndRestored: true, layout };
}
