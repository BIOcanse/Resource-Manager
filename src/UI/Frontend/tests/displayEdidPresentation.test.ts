import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import type { DeviceTopologyDisplayConnection } from "../src/types.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { buildDisplayEdidView, edidRate } from "../src/features/deviceTopology/displayEdidPresentation.ts";
import { decodeDisplayEdidCapability } from "../src/features/deviceTopology/displayEdidDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { deviceTopologyStateDecoder } from "../src/features/deviceTopology/deviceTopologyStateDecoder.ts";
const wire = JSON.parse(readFileSync(new URL("./fixtures/displayEdid.json", import.meta.url), "utf8"));
const fixture = wire.snapshot.ports[0].display as DeviceTopologyDisplayConnection;
const app = await loadAppCopy("en-US"); const c = app.deviceTopology.edid;
const capability = decodeDisplayEdidCapability(fixture.modeCapabilityDeclared, "$.capability");
assert.deepEqual(capability, fixture.modeCapabilityDeclared);
assert.notEqual(capability, fixture.modeCapabilityDeclared);
const view = buildDisplayEdidView({ ...fixture, modeCapabilityDeclared: capability }, app);
assert.equal(view.current[1].value, "60000/1001 Hz");
assert.equal(view.modes.length, 7); assert.equal(view.selections.length, 3);
assert.equal(view.modes[1].rows.find(row => row.label === c.frameRate)?.value, "1978/33 Hz");
assert.equal(view.modes[5].rows.find(row => row.label === c.nominal)?.value, c.unavailable);
assert.equal(view.modes[5].rows.find(row => row.label === c.frameRate)?.value, c.unavailable);
assert.equal(view.modes[4].rows.find(row => row.label === c.reduced)?.value, app.shellBridge.no);
assert.equal(view.modes[6].rows.find(row => row.label === c.stereo)?.value, "0");
assert.equal(view.modes[6].rows.find(row => row.label === c.deltaBlanking)?.value, "6");
assert.equal(view.selections[0].rows.filter(row => row.label === c.reference).length, 5);
assert.deepEqual(view.selections[0].rows.filter(row => row.label === c.reference).map(row => row.value.split(" → ")[0]), ["146", "129", "16", "146", "130"]);
assert.match(view.selections[1].rows.find(row => row.label === c.imageCodes)!.value, /65535 × 65534; 1000 µm/);
assert.equal(view.raw, fixture.edidRawDeclaredBase64);
const inactive = buildDisplayEdidView({ ...fixture, active: false }, app);
assert.ok(inactive.current.every(row => row.value === c.unavailable)); assert.equal(inactive.modes.length, 7);
const unknown = buildDisplayEdidView({ ...fixture, modeCapabilityDeclared: null }, app);
assert.equal(unknown.available, false); assert.equal(unknown.raw, view.raw);
const empty = buildDisplayEdidView({ ...fixture, modeCapabilityDeclared: { ...capability, modes: [], ranges: [], selectionDeclarations: [] }, edidRawDeclaredBase64: "" }, app);
assert.equal(empty.available, true); assert.equal(empty.modes.length, 0); assert.equal(empty.raw, c.emptyRaw);
assert.equal(buildDisplayEdidView({ ...fixture, edidRawDeclaredBase64: undefined, modeCapabilityDeclared: undefined }, app).raw, c.unavailable);
assert.equal(edidRate({ numerator: 1978, denominator: 33 }), "1978/33 Hz");
for (const mutate of [
  (value: typeof capability) => { value.modes[0].rawDescriptor[0] = 256; },
  (value: typeof capability) => { value.modes[0].timing!.frameRate.denominator = 0; },
  (value: typeof capability) => { value.modes[0].timing!.pixelClockHz = Number.MAX_SAFE_INTEGER + 1; },
  (value: typeof capability) => { value.selectionDeclarations![0].references[0].shortVideoReference = -1; }
]) {
  const value = structuredClone(capability); mutate(value);
  assert.throws(() => decodeDisplayEdidCapability(value, "$.capability"), ResponseDecodeError);
}
for (const setInvalid of [
  (value: typeof capability) => { value.ctaFlags = 256; },
  (value: typeof capability) => { value.modes[0].containerFlags = 256; },
  (value: typeof capability) => { value.modes[0].containerRevision = 256; },
  (value: typeof capability) => { value.modes[0].bitIndex = 0x80000000; },
  (value: typeof capability) => { value.modes[6].cvtParameters!.deltaHorizontalBlankingCode = 256; },
  (value: typeof capability) => { value.modes[6].cvtParameters!.additionalVerticalBlankingCode = 256; },
  (value: typeof capability) => { value.selectionDeclarations![0].flags = 256; }
]) {
  const value = structuredClone(capability); setInvalid(value);
  assert.throws(() => decodeDisplayEdidCapability(value, "$.capability"), ResponseDecodeError);
}
// Independent DTO-shape probes, separate from the frozen raw EDID parser fixture.
const rangeInput = structuredClone(capability);
rangeInput.ranges = [{ blockIndex: 0, byteOffset: 90, encoding: "edid-range", revision: 4, rawDescriptor: [0, 0, 0, 253], formula: "cvt", minimumVerticalRateHz: 48, maximumVerticalRateHz: 144,
  minimumHorizontalFrequencyHz: 30000, maximumHorizontalFrequencyHz: 200000, maximumPixelClockHz: 600000000,
  secondaryGtf: null,
  cvt: { version: 17, maximumActivePixelsPerLine: 4096, supportedAspectRatiosMask: 31, preferredAspectRatioCode: 2, standardBlanking: false, reducedBlanking: true, scalingMask: 15, preferredVerticalRateHz: 60 } }];
rangeInput.ranges.push({ ...rangeInput.ranges[0], byteOffset: 108, formula: "secondary-gtf", cvt: null,
  secondaryGtf: { startHorizontalFrequencyHz: 40000, cTimesTwo: 80, m: 600, k: 128, jTimesTwo: 40 } });
const rangeView = buildDisplayEdidView({ ...fixture, modeCapabilityDeclared: decodeDisplayEdidCapability(rangeInput, "$.ranges") }, app);
for (const label of [c.gtfStart, c.parameters, c.cvtVersion, c.maxActivePixels, c.aspectRatioMask, c.aspectRatioCode, c.standardBlanking, c.scalingMask, c.preferredRate]) {
  assert.ok(rangeView.ranges.flatMap(range => range.rows).some(row => row.label === label), label);
}
assert.equal(rangeView.ranges[0].rows.find(row => row.label === c.standardBlanking)?.value, app.shellBridge.no);
for (const key of ["version", "supportedAspectRatiosMask", "preferredAspectRatioCode", "scalingMask"] as const) {
  const invalid = structuredClone(rangeInput); invalid.ranges[0].cvt![key] = 256;
  assert.throws(() => decodeDisplayEdidCapability(invalid, "$.ranges"), ResponseDecodeError);
}
const polarity = structuredClone(capability); polarity.modes[0].timing!.flags = 0; polarity.modes[0].timing!.positiveHorizontalSync = false;
const polarityView = buildDisplayEdidView({ ...fixture, modeCapabilityDeclared: polarity }, app);
assert.equal(polarityView.modes[0].rows.find(row => row.label === c.signalFlags)?.value, "0");
assert.equal(polarityView.modes[0].rows.find(row => row.label === c.horizontalPositive)?.value, app.shellBridge.no);
// Set only in the future official gate after generating this file from the real C# DTO/parser.
const actualWirePath = process.env.RM_EDID_UI_STATE_JSON;
if (actualWirePath) {
  const actual = deviceTopologyStateDecoder.decode(JSON.parse(readFileSync(actualWirePath, "utf8")));
  assert.deepEqual(actual.snapshot!.ports[0].display, deviceTopologyStateDecoder.decode(wire).snapshot!.ports[0].display);
}


// Type III DTO-shape probes: independent of the frozen older real-wire fixture above.
const type3Input = structuredClone(capability);
type3Input.modes = [
  { ...type3Input.modes[0], blockIndex: 1, byteOffset: 8, encoding: "displayid-iii", revision: 1, rawDescriptor: [0x14, 0xEF, 0xBB],
    code: null, bitIndex: null, width: 1920, height: null, horizontalPixelRepetition: null,
    interlaced: true, nominalVerticalRateHz: 60, native: null, preferred: false,
    yCbCr420Only: null, yCbCr420Encoding: null, yCbCr420Supported: null, yCbCr420CapabilityMap: null,
    containerFlags: 1, containerRevision: 0x12, dscPassThrough: null, formula: "cvt", reducedBlanking: true,
    timing: null, cvtParameters: null, aspectRatioDeclared: { code: 4, horizontal: 16, vertical: 9 } },
  { ...type3Input.modes[0], blockIndex: 1, byteOffset: 11, encoding: "displayid-iii", revision: 1, rawDescriptor: [8, 0, 0],
    code: null, bitIndex: null, width: 8, height: null, horizontalPixelRepetition: null,
    interlaced: false, nominalVerticalRateHz: 1, native: null, preferred: false,
    yCbCr420Only: null, yCbCr420Encoding: null, yCbCr420Supported: null, yCbCr420CapabilityMap: null,
    containerFlags: 1, containerRevision: 0x12, dscPassThrough: null, formula: "cvt", reducedBlanking: false,
    timing: null, cvtParameters: null, aspectRatioDeclared: { code: 8, horizontal: null, vertical: null } }
];
type3Input.ranges = []; type3Input.selectionDeclarations = [];
const type3 = decodeDisplayEdidCapability(type3Input, "$.type3");
assert.deepEqual(type3, type3Input);
// The literal modes above must equal the TypeIII wire regenerated through the real parser (legacy structure byte 0x12).
const type3Wire = JSON.parse(readFileSync(new URL("./fixtures/displayEdidType3.json", import.meta.url), "utf8")).snapshot.ports[0].display;
const type3Recorded = decodeDisplayEdidCapability(type3Wire.modeCapabilityDeclared, "$.type3Wire");
assert.deepEqual(type3Recorded, type3Wire.modeCapabilityDeclared);
assert.deepEqual(type3Recorded.modes, type3.modes);
assert.deepEqual([type3Recorded.dynamicTimingRanges, type3Recorded.adaptiveSyncRanges], [[], []]);
for (const option of languageOptions) {
  if (option.id === "system") continue;
  const copy = await loadAppCopy(option.id); const text = copy.deviceTopology.edid;
  const localized = buildDisplayEdidView({ ...fixture, active: false, modeCapabilityDeclared: type3 }, copy);
  assert.ok(localized.current.every(row => row.value === text.unavailable), option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.resolution)?.value, `${text.declaredWidth}: 1920 px`, option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.declaredAspect)?.value, "16:9", option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.declaredAspectCode)?.value, "4", option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.nominalFieldRate)?.value, "60 Hz", option.id);
  assert.ok(localized.modes[0].title.includes(text.nominalFieldRate), option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.frameRate)?.value, text.unavailable, option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.scan)?.value, text.interlaced, option.id);
  assert.equal(localized.modes[1].rows.find(row => row.label === text.declaredAspect)?.value, text.undefinedAspect, option.id);
  assert.equal(localized.modes[1].rows.find(row => row.label === text.declaredAspectCode)?.value, "8", option.id);
  assert.equal(localized.modes[1].rows.find(row => row.label === text.nominalFrameRate)?.value, "1 Hz", option.id);
  assert.ok(!localized.modes[0].rows.some(row => row.value.includes("1080")), option.id);
  assert.equal(localized.modes[0].rows.find(row => row.label === text.descriptor)?.value, "14 EF BB", option.id);
  for (const key of ["declaredWidth", "declaredAspect", "declaredAspectCode", "undefinedAspect", "nominalFrameRate", "nominalFieldRate"] as const) {
    assert.ok(text[key].trim(), `${option.id}.${key}`);
    if (option.id !== "en-US") assert.notEqual(text[key], c[key], `${option.id}.${key}`);
  }
}
for (const mutate of [
  (value: typeof type3) => { value.modes[0].aspectRatioDeclared!.code = 256; },
  (value: typeof type3) => { value.modes[0].aspectRatioDeclared!.horizontal = 0x100000000; },
  (value: typeof type3) => { value.modes[0].aspectRatioDeclared!.vertical = -1; }
]) {
  const invalid = structuredClone(type3); mutate(invalid);
  assert.throws(() => decodeDisplayEdidCapability(invalid, "$.type3"), ResponseDecodeError);
}
const legacyType3 = structuredClone(type3); delete legacyType3.modes[0].aspectRatioDeclared;
const legacyDecoded = decodeDisplayEdidCapability(legacyType3, "$.legacyType3");
assert.equal(legacyDecoded.modes[0].aspectRatioDeclared, undefined);
assert.deepEqual(legacyDecoded.modes[0].rawDescriptor, [0x14, 0xEF, 0xBB]);
assert.equal(legacyDecoded.modes[0].height, null);
const nullType3 = structuredClone(type3); nullType3.modes[0].aspectRatioDeclared = null;
assert.equal(decodeDisplayEdidCapability(nullType3, "$.nullType3").modes[0].aspectRatioDeclared, null);

// DisplayID Type V/VI, Adaptive-Sync and dynamic timing ranges: each fixture is real parser + production DTO wire output
// (tests/Validation/FrontendWire/DisplayEdidUiWireFixtureTests.Declarations.cs); decoding must keep it byte-for-byte.
function recordedDeclaration(name: string) {
  const display = JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), "utf8")).snapshot.ports[0].display as DeviceTopologyDisplayConnection;
  const decoded = decodeDisplayEdidCapability(display.modeCapabilityDeclared, `$.${name}`);
  assert.deepEqual(decoded, display.modeCapabilityDeclared, name);
  return { display, decoded };
}
const typeVVi = recordedDeclaration("displayEdidTypeVVi.json");
const typeVViClock = recordedDeclaration("displayEdidTypeVViClock.json");
const adaptiveSync = recordedDeclaration("displayEdidAdaptiveSync.json");
const adaptiveSyncZero = recordedDeclaration("displayEdidAdaptiveSyncZero.json");
const dynamicRange = recordedDeclaration("displayEdidDynamicRange.json");
const rowOf = (entry: { rows: { label: string; value: string }[] }, label: string) => entry.rows.find(row => row.label === label)?.value;
const declarationKeys = ["dynamicRanges", "adaptiveSync", "formulaCode", "aspectMultiplier", "verticalImageSize", "clockConflict", "pixelClockRange",
  "seamlessDynamicBlanking", "declaredBase", "declaredBaseRange", "nativePanelRange", "avtSupported", "seamlessTransition",
  "frameIncrease", "frameDecrease", "frameIncreaseScope", "frameDecreaseScope", "noJitterLimit"] as const;
for (const option of languageOptions) {
  if (option.id === "system") continue;
  const copy = await loadAppCopy(option.id); const text = copy.deviceTopology.edid;
  const yes = copy.shellBridge.yes; const no = copy.shellBridge.no; const id = option.id;
  for (const key of declarationKeys) {
    assert.ok(text[key].trim(), `${id}.${key}`);
    if (id !== "en-US") assert.notEqual(text[key], c[key], `${id}.${key}`);
  }

  const vvi = buildDisplayEdidView({ ...typeVVi.display, modeCapabilityDeclared: typeVVi.decoded }, copy);
  assert.equal(vvi.modes.length, 3, id);
  const [typeV, vi14, vi17] = vvi.modes;
  // Type V: original option codes only; no generated timing.
  assert.equal(rowOf(typeV, text.source), "displayid-v · 1:8", id);
  assert.equal(rowOf(typeV, text.resolution), "1920 × 1080", id);
  assert.equal(rowOf(typeV, text.nominal), "60 Hz", id);
  assert.equal(rowOf(typeV, text.formula), "cvt", id);
  assert.equal(rowOf(typeV, text.formulaCode), "1", id);
  assert.equal(rowOf(typeV, text.stereo), "2", id);
  assert.equal(rowOf(typeV, text.fractional), yes, id);
  assert.equal(rowOf(typeV, text.reduced), yes, id);
  assert.equal(rowOf(typeV, text.preferred), yes, id);
  assert.equal(rowOf(typeV, text.frameRate), text.unavailable, id);
  assert.equal(rowOf(typeV, text.containerRevision), "18", id);
  for (const label of [text.pixelClock, text.horizontal, text.vertical, text.rbVersion, text.aspectMultiplier, text.declaredAspectCode])
    assert.equal(rowOf(typeV, label), undefined, `${id}: Type V invented ${label}`);
  assert.equal(rowOf(typeV, text.descriptor), "D1 A5 7F 07 37 04 3B", id);
  // Type VI 14-byte record with a known clock.
  assert.equal(rowOf(vi14, text.source), "displayid-vi · 2:8", id);
  assert.equal(rowOf(vi14, text.pixelClock), "148500000 Hz", id);
  assert.equal(rowOf(vi14, text.frameRate), "60/1 Hz", id);
  assert.equal(rowOf(vi14, text.horizontal), "1920 / 280 / 88 / 44", id);
  assert.equal(rowOf(vi14, text.vertical), "1080 / 45 / 4 / 5", id);
  assert.equal(rowOf(vi14, text.horizontalPositive), yes, id);
  assert.equal(rowOf(vi14, text.verticalPositive), yes, id);
  assert.equal(rowOf(vi14, text.stereo), "1", id);
  assert.equal(rowOf(vi14, text.scan), text.progressive, id);
  for (const label of [text.declaredAspect, text.declaredAspectCode, text.aspectMultiplier, text.verticalImageSize, text.formulaCode, text.fieldRate])
    assert.equal(rowOf(vi14, label), undefined, `${id}: 14-byte Type VI invented ${label}`);
  // Type VI 17-byte record: the multiplier is not an ordinary aspect-ratio code.
  assert.equal(rowOf(vi17, text.source), "displayid-vi · 2:22", id);
  assert.equal(rowOf(vi17, text.declaredAspect), "456:256", id);
  assert.equal(rowOf(vi17, text.aspectMultiplier), "152", id);
  assert.equal(rowOf(vi17, text.declaredAspectCode), undefined, id);
  assert.equal(rowOf(vi17, text.verticalImageSize), "299 mm × 2 = 598 mm", id);
  assert.equal(rowOf(vi17, text.descriptor), "13 44 C2 7F 87 37 84 17 57 10 2B 2C 03 24 98 2A 11", id);

  // Contradictory Type VI clock: only the clock and clock-derived rate are unavailable, with the reason.
  const clockView = buildDisplayEdidView({ ...typeVViClock.display, modeCapabilityDeclared: typeVViClock.decoded }, copy);
  const conflict = clockView.modes[1];
  assert.equal(rowOf(conflict, text.pixelClock), text.clockConflict, id);
  assert.equal(rowOf(conflict, text.frameRate), text.clockConflict, id);
  assert.equal(rowOf(conflict, text.fieldRate), undefined, id);
  assert.equal(rowOf(conflict, text.horizontal), "1920 / 280 / 88 / 44", id);
  assert.equal(rowOf(conflict, text.vertical), "1080 / 45 / 4 / 5", id);
  assert.equal(rowOf(conflict, text.horizontalPositive), yes, id);
  assert.equal(rowOf(conflict, text.verticalPositive), yes, id);
  assert.equal(rowOf(conflict, text.stereo), "1", id);
  assert.equal(rowOf(conflict, text.scan), text.interlaced, id);
  assert.equal(rowOf(conflict, text.descriptor), "13 44 A2 7F 87 37 84 17 57 10 2B 2C 03 A4", id);
  assert.equal(rowOf(clockView.modes[2], text.pixelClock), "148500000 Hz", `${id}: neighbouring Type VI clock stays available`);

  // Adaptive-Sync with positive quarter-millisecond codes; base values are declared, not actual.
  const positive = buildDisplayEdidView({ ...adaptiveSync.display, modeCapabilityDeclared: adaptiveSync.decoded }, copy);
  assert.equal(positive.adaptiveSyncReported, true, id); assert.equal(positive.dynamicRangesReported, true, id);
  assert.equal(positive.adaptiveSyncRanges.length, 1, id); assert.equal(positive.dynamicRanges.length, 0, id); assert.equal(positive.modes.length, 0, id);
  const range = positive.adaptiveSyncRanges[0];
  assert.equal(range.title, `${text.declaredBase}: 1–1024 Hz`, id);
  assert.deepEqual([rowOf(range, text.source), rowOf(range, text.revision), rowOf(range, text.containerFlags), rowOf(range, text.containerRevision)],
    ["DisplayID 0x2B · 1:8", "0", "0", "32"], id);
  assert.equal(rowOf(range, text.declaredBaseRange), "1–1024 Hz", id);
  assert.deepEqual([rowOf(range, text.nativePanelRange), rowOf(range, text.avtSupported), rowOf(range, text.seamlessTransition)], [no, yes, yes], id);
  assert.deepEqual([rowOf(range, text.frameIncrease), rowOf(range, text.frameIncreaseScope)], ["250 µs", yes], id);
  assert.deepEqual([rowOf(range, text.frameDecrease), rowOf(range, text.frameDecreaseScope)], ["63750 µs", yes], id);
  assert.equal(rowOf(range, text.descriptor), "26 01 01 FF 03 FF", id);
  // Zero codes: a dedicated no-jitter sentence, never 0 µs or unavailable; the independent scope flag stays visible.
  const zero = buildDisplayEdidView({ ...adaptiveSyncZero.display, modeCapabilityDeclared: adaptiveSyncZero.decoded }, copy).adaptiveSyncRanges[0];
  assert.equal(zero.title, `${text.declaredBase}: 48–144 Hz`, id);
  assert.deepEqual([rowOf(zero, text.nativePanelRange), rowOf(zero, text.avtSupported), rowOf(zero, text.seamlessTransition)], [yes, no, yes], id);
  for (const [limit, scope] of [[text.frameIncrease, text.frameIncreaseScope], [text.frameDecrease, text.frameDecreaseScope]]) {
    assert.equal(rowOf(zero, limit), text.noJitterLimit, id);
    assert.notEqual(text.noJitterLimit, text.unavailable, id);
    assert.equal(rowOf(zero, scope), no, id);
  }
  assert.ok(!zero.rows.some(row => /(^|\D)0 µs/.test(row.value)), id);
  assert.equal(rowOf(zero, text.descriptor), "01 00 30 8F 00 00", id);

  // Dynamic timing range (DisplayID 2.0 tag 0x25, revision 1 ten-bit maximum).
  const dynamic = buildDisplayEdidView({ ...dynamicRange.display, modeCapabilityDeclared: dynamicRange.decoded }, copy);
  assert.equal(dynamic.dynamicRanges.length, 1, id); assert.equal(dynamic.adaptiveSyncRanges.length, 0, id);
  const timingRange = dynamic.dynamicRanges[0];
  assert.equal(timingRange.title, `${text.verticalRange}: 48–360 Hz`, id);
  assert.deepEqual([rowOf(timingRange, text.source), rowOf(timingRange, text.revision), rowOf(timingRange, text.containerFlags), rowOf(timingRange, text.containerRevision)],
    ["DisplayID 0x25 · 1:8", "1", "1", "32"], id);
  assert.equal(rowOf(timingRange, text.pixelClockRange), "60000000–600000000 Hz", id);
  assert.equal(rowOf(timingRange, text.verticalRange), "48–360 Hz", id);
  assert.equal(rowOf(timingRange, text.seamlessDynamicBlanking), yes, id);
  assert.equal(rowOf(timingRange, text.descriptor), "5F EA 00 BF 27 09 30 68 81", id);
}
// Legacy payloads without the new collections keep the legacy dialog: no new sections, nothing coerced to empty.
const legacyCollections = structuredClone(adaptiveSync.decoded); delete legacyCollections.adaptiveSyncRanges; delete legacyCollections.dynamicTimingRanges;
const legacyCollectionsDecoded = decodeDisplayEdidCapability(legacyCollections, "$.legacyCollections");
assert.ok(!("adaptiveSyncRanges" in legacyCollectionsDecoded) && !("dynamicTimingRanges" in legacyCollectionsDecoded));
const legacyCollectionsView = buildDisplayEdidView({ ...adaptiveSync.display, modeCapabilityDeclared: legacyCollectionsDecoded }, app);
assert.equal(legacyCollectionsView.adaptiveSyncReported, false); assert.equal(legacyCollectionsView.dynamicRangesReported, false);
const legacyTypeVi = structuredClone(typeVVi.decoded);
for (const mode of legacyTypeVi.modes) { delete mode.displayIdTypeVOptions; delete mode.displayIdTypeViTiming; delete mode.displayIdTypeViImageSize; }
const legacyTypeViDecoded = decodeDisplayEdidCapability(legacyTypeVi, "$.legacyTypeVi");
assert.ok(legacyTypeViDecoded.modes.every(mode => !("displayIdTypeVOptions" in mode) && !("displayIdTypeViTiming" in mode) && !("displayIdTypeViImageSize" in mode)));
for (const mutate of [
  (value: typeof typeVVi.decoded) => { value.modes[0].displayIdTypeVOptions!.formulaCode = 256; },
  (value: typeof typeVVi.decoded) => { (value.modes[0].displayIdTypeVOptions as unknown as Record<string, unknown>).fractionalRefreshSupported = 1; },
  (value: typeof typeVVi.decoded) => { value.modes[1].displayIdTypeViTiming!.pixelClockHz = Number.MAX_SAFE_INTEGER + 1; },
  (value: typeof typeVVi.decoded) => { value.modes[1].displayIdTypeViTiming!.horizontalBlankingPixels = -1; },
  (value: typeof typeVVi.decoded) => { value.modes[2].displayIdTypeViImageSize!.sizeMultiplier = 256; },
  (value: typeof typeVVi.decoded) => { value.modes[2].displayIdTypeViImageSize!.verticalImageSizeMillimeters = 0.5; }
]) {
  const invalid = structuredClone(typeVVi.decoded); mutate(invalid);
  assert.throws(() => decodeDisplayEdidCapability(invalid, "$.typeVVi"), ResponseDecodeError);
}
for (const mutate of [
  (value: typeof adaptiveSync.decoded) => { value.adaptiveSyncRanges![0].frameDurationIncrease.zeroCodeMeansNoJitterThroughoutRange = true; },
  (value: typeof adaptiveSync.decoded) => { value.adaptiveSyncRanges![0].frameDurationDecrease.maximumSingleStepMicroseconds = null; },
  (value: typeof adaptiveSync.decoded) => { value.adaptiveSyncRanges![0].containerRevision = 256; },
  (value: typeof adaptiveSync.decoded) => { (value as unknown as Record<string, unknown>).adaptiveSyncRanges = null; },
  (value: typeof adaptiveSync.decoded) => { (value as unknown as Record<string, unknown>).dynamicTimingRanges = {}; }
]) {
  const invalid = structuredClone(adaptiveSync.decoded); mutate(invalid);
  assert.throws(() => decodeDisplayEdidCapability(invalid, "$.adaptiveSync"), ResponseDecodeError);
}
const invalidDynamic = structuredClone(dynamicRange.decoded); invalidDynamic.dynamicTimingRanges![0].minimumPixelClockHz = -1;
assert.throws(() => decodeDisplayEdidCapability(invalidDynamic, "$.dynamicRange"), ResponseDecodeError);
// Valid false and 0 survive decoding and presentation.
const zeroBase = structuredClone(adaptiveSyncZero.decoded); zeroBase.adaptiveSyncRanges![0].baseMinimumRefreshRateHz = 0; zeroBase.adaptiveSyncRanges![0].seamlessTransitionSupported = false;
const zeroBaseView = buildDisplayEdidView({ ...adaptiveSyncZero.display, modeCapabilityDeclared: decodeDisplayEdidCapability(zeroBase, "$.zeroBase") }, app);
assert.equal(rowOf(zeroBaseView.adaptiveSyncRanges[0], c.declaredBaseRange), "0–144 Hz");
assert.equal(rowOf(zeroBaseView.adaptiveSyncRanges[0], c.seamlessTransition), app.shellBridge.no);
