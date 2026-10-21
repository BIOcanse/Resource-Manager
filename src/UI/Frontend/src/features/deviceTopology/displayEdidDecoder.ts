import { requireArray, requireBoolean, requireNonNegativeSafeInteger, requireRecord, requireString, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { DisplayEdidCapability, DisplayEdidMode, DisplayEdidRange, DisplayEdidReference, DisplayEdidSelection, DisplayEdidTiming } from "../../types.ts";

type RecordValue = Record<string, unknown>;
const number = requireNonNegativeSafeInteger;
function integer(value: unknown, path: string, maximum = 0xFFFFFFFF): number {
  const result = number(value, path);
  if (result > maximum) throw new ResponseDecodeError(path, `integer <= ${maximum}`);
  return result;
}
const byte = (value: unknown, path: string) => integer(value, path, 255);
export const decodeEdidUInt32 = (value: unknown, path: string) => integer(value, path);
export function optionalEdidValue<T>(value: unknown, path: string, decode: (value: unknown, path: string) => T): T | null {
  return value === null || value === undefined ? null : decode(value, path);
}
const n = (r: RecordValue, key: string, p: string) => optionalEdidValue(r[key], `${p}.${key}`, integer);
const nb = (r: RecordValue, key: string, p: string) => optionalEdidValue(r[key], `${p}.${key}`, byte);
const b = (r: RecordValue, key: string, p: string) => optionalEdidValue(r[key], `${p}.${key}`, requireBoolean);
const raw = (value: unknown, path: string) => requireArray(value, path).map((item, index) => byte(item, `${path}[${index}]`));
function source(r: RecordValue, p: string) {
  return { blockIndex: integer(r.blockIndex, `${p}.blockIndex`), byteOffset: integer(r.byteOffset, `${p}.byteOffset`), rawDescriptor: raw(r.rawDescriptor, `${p}.rawDescriptor`) };
}
function reference(value: unknown, path: string): DisplayEdidReference {
  const r = requireRecord(value, path); return { ...source(r, path), bitIndex: optionalEdidValue(r.bitIndex, `${path}.bitIndex`, (value, p) => integer(value, p, 0x7FFFFFFF)) };
}
function rate(value: unknown, path: string) {
  const r = requireRecord(value, path); const denominator = number(r.denominator, `${path}.denominator`);
  if (denominator === 0) throw new ResponseDecodeError(`${path}.denominator`, "positive safe integer");
  return { numerator: number(r.numerator, `${path}.numerator`), denominator };
}
function timing(value: unknown, path: string): DisplayEdidTiming {
  const r = requireRecord(value, path);
  return { pixelClockHz: number(r.pixelClockHz, `${path}.pixelClockHz`),
    horizontalActive: integer(r.horizontalActive, `${path}.horizontalActive`), horizontalBlanking: integer(r.horizontalBlanking, `${path}.horizontalBlanking`),
    horizontalSyncOffset: integer(r.horizontalSyncOffset, `${path}.horizontalSyncOffset`), horizontalSyncWidth: integer(r.horizontalSyncWidth, `${path}.horizontalSyncWidth`),
    verticalActive: integer(r.verticalActive, `${path}.verticalActive`), verticalBlanking: integer(r.verticalBlanking, `${path}.verticalBlanking`),
    verticalSyncOffset: integer(r.verticalSyncOffset, `${path}.verticalSyncOffset`), verticalSyncWidth: integer(r.verticalSyncWidth, `${path}.verticalSyncWidth`),
    flags: byte(r.flags, `${path}.flags`), positiveHorizontalSync: b(r, "positiveHorizontalSync", path), positiveVerticalSync: b(r, "positiveVerticalSync", path),
    frameRate: rate(r.frameRate, `${path}.frameRate`), fieldRate: optionalEdidValue(r.fieldRate, `${path}.fieldRate`, rate) };
}
function cvtParameters(value: unknown, path: string) {
  const r = requireRecord(value, path);
  return { reducedBlankingVersion: byte(r.reducedBlankingVersion, `${path}.reducedBlankingVersion`), stereoCode: byte(r.stereoCode, `${path}.stereoCode`),
    alternativeRefresh1000Over1001Supported: b(r, "alternativeRefresh1000Over1001Supported", path), horizontalBlankingBase160Pixels: b(r, "horizontalBlankingBase160Pixels", path),
    earlyVsyncRequired: b(r, "earlyVsyncRequired", path), deltaHorizontalBlankingCode: nb(r, "deltaHorizontalBlankingCode", path),
    additionalVerticalBlankingCode: nb(r, "additionalVerticalBlankingCode", path), alternativeMinimumVerticalBlanking: b(r, "alternativeMinimumVerticalBlanking", path) };
}
function aspectRatio(value: unknown, path: string) {
  const r = requireRecord(value, path);
  return { code: byte(r.code, `${path}.code`), horizontal: n(r, "horizontal", path), vertical: n(r, "vertical", path) };
}
function mode(value: unknown, path: string): DisplayEdidMode {
  const r = requireRecord(value, path);
  return { ...reference(r, path), encoding: requireString(r.encoding, `${path}.encoding`), revision: byte(r.revision, `${path}.revision`),
    ...(r.aspectRatioDeclared === undefined ? {} : { aspectRatioDeclared: optionalEdidValue(r.aspectRatioDeclared, `${path}.aspectRatioDeclared`, aspectRatio) }),
    code: n(r, "code", path), width: n(r, "width", path), height: n(r, "height", path), horizontalPixelRepetition: n(r, "horizontalPixelRepetition", path),
    interlaced: b(r, "interlaced", path), nominalVerticalRateHz: n(r, "nominalVerticalRateHz", path), native: b(r, "native", path), preferred: b(r, "preferred", path),
    yCbCr420Only: b(r, "yCbCr420Only", path), yCbCr420Encoding: b(r, "yCbCr420Encoding", path), yCbCr420Supported: b(r, "yCbCr420Supported", path),
    yCbCr420CapabilityMap: optionalEdidValue(r.yCbCr420CapabilityMap, `${path}.yCbCr420CapabilityMap`, reference), containerFlags: nb(r, "containerFlags", path),
    containerRevision: nb(r, "containerRevision", path), dscPassThrough: b(r, "dscPassThrough", path), formula: optionalEdidValue(r.formula, `${path}.formula`, requireString),
    reducedBlanking: b(r, "reducedBlanking", path), timing: optionalEdidValue(r.timing, `${path}.timing`, timing), cvtParameters: optionalEdidValue(r.cvtParameters, `${path}.cvtParameters`, cvtParameters) };
}
function range(value: unknown, path: string): DisplayEdidRange {
  const r = requireRecord(value, path);
  return { ...source(r, path), encoding: requireString(r.encoding, `${path}.encoding`), revision: byte(r.revision, `${path}.revision`), formula: requireString(r.formula, `${path}.formula`),
    minimumVerticalRateHz: integer(r.minimumVerticalRateHz, `${path}.minimumVerticalRateHz`), maximumVerticalRateHz: integer(r.maximumVerticalRateHz, `${path}.maximumVerticalRateHz`),
    minimumHorizontalFrequencyHz: integer(r.minimumHorizontalFrequencyHz, `${path}.minimumHorizontalFrequencyHz`), maximumHorizontalFrequencyHz: integer(r.maximumHorizontalFrequencyHz, `${path}.maximumHorizontalFrequencyHz`),
    maximumPixelClockHz: optionalEdidValue(r.maximumPixelClockHz, `${path}.maximumPixelClockHz`, number),
    secondaryGtf: optionalEdidValue(r.secondaryGtf, `${path}.secondaryGtf`, (value, p) => {
      const s = requireRecord(value, p); return { startHorizontalFrequencyHz: integer(s.startHorizontalFrequencyHz, `${p}.startHorizontalFrequencyHz`), cTimesTwo: integer(s.cTimesTwo, `${p}.cTimesTwo`),
        m: integer(s.m, `${p}.m`), k: integer(s.k, `${p}.k`), jTimesTwo: integer(s.jTimesTwo, `${p}.jTimesTwo`) };
    }), cvt: optionalEdidValue(r.cvt, `${path}.cvt`, (value, p) => {
      const c = requireRecord(value, p); return { version: byte(c.version, `${p}.version`), maximumActivePixelsPerLine: n(c, "maximumActivePixelsPerLine", p),
        supportedAspectRatiosMask: byte(c.supportedAspectRatiosMask, `${p}.supportedAspectRatiosMask`), preferredAspectRatioCode: byte(c.preferredAspectRatioCode, `${p}.preferredAspectRatioCode`),
        standardBlanking: requireBoolean(c.standardBlanking, `${p}.standardBlanking`), reducedBlanking: requireBoolean(c.reducedBlanking, `${p}.reducedBlanking`), scalingMask: byte(c.scalingMask, `${p}.scalingMask`), preferredVerticalRateHz: n(c, "preferredVerticalRateHz", p) };
    }) };
}
function selection(value: unknown, path: string): DisplayEdidSelection {
  const r = requireRecord(value, path);
  return { ...source(r, path), encoding: requireString(r.encoding, `${path}.encoding`), revision: byte(r.revision, `${path}.revision`), flags: nb(r, "flags", path),
    references: requireArray(r.references, `${path}.references`).map((item, index) => {
      const p = `${path}.references[${index}]`; const s = requireRecord(item, p);
      return { byteOffset: integer(s.byteOffset, `${p}.byteOffset`), shortVideoReference: byte(s.shortVideoReference, `${p}.shortVideoReference`), targetEncoding: requireString(s.targetEncoding, `${p}.targetEncoding`),
        code: n(s, "code", p), target: optionalEdidValue(s.target, `${p}.target`, reference) };
    }), imageSize: optionalEdidValue(r.imageSize, `${path}.imageSize`, (value, p) => {
      const s = requireRecord(value, p); return { widthCode: integer(s.widthCode, `${p}.widthCode`, 65535), heightCode: integer(s.heightCode, `${p}.heightCode`, 65535), unitMicrometers: integer(s.unitMicrometers, `${p}.unitMicrometers`) };
    }) };
}
export function decodeDisplayEdidCapability(value: unknown, path: string): DisplayEdidCapability {
  const r = requireRecord(value, path);
  return { modes: requireArray(r.modes, `${path}.modes`).map((item, i) => mode(item, `${path}.modes[${i}]`)),
    ranges: requireArray(r.ranges, `${path}.ranges`).map((item, i) => range(item, `${path}.ranges[${i}]`)),
    edidRevision: byte(r.edidRevision, `${path}.edidRevision`), baseFeatures: byte(r.baseFeatures, `${path}.baseFeatures`), ctaFlags: nb(r, "ctaFlags", path),
    selectionDeclarations: optionalEdidValue(r.selectionDeclarations, `${path}.selectionDeclarations`, (value, p) => requireArray(value, p).map((item, i) => selection(item, `${p}[${i}]`))) };
}
