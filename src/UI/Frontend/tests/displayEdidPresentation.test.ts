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
    containerFlags: 1, containerRevision: 0x13, dscPassThrough: null, formula: "cvt", reducedBlanking: true,
    timing: null, cvtParameters: null, aspectRatioDeclared: { code: 4, horizontal: 16, vertical: 9 } },
  { ...type3Input.modes[0], blockIndex: 1, byteOffset: 11, encoding: "displayid-iii", revision: 1, rawDescriptor: [8, 0, 0],
    code: null, bitIndex: null, width: 8, height: null, horizontalPixelRepetition: null,
    interlaced: false, nominalVerticalRateHz: 1, native: null, preferred: false,
    yCbCr420Only: null, yCbCr420Encoding: null, yCbCr420Supported: null, yCbCr420CapabilityMap: null,
    containerFlags: 1, containerRevision: 0x13, dscPassThrough: null, formula: "cvt", reducedBlanking: false,
    timing: null, cvtParameters: null, aspectRatioDeclared: { code: 8, horizontal: null, vertical: null } }
];
type3Input.ranges = []; type3Input.selectionDeclarations = [];
const type3 = decodeDisplayEdidCapability(type3Input, "$.type3");
assert.deepEqual(type3, type3Input);
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
