import type { DeviceTopologyDisplayConnection, DisplayEdidAdaptiveSyncTolerance, DisplayEdidDisplayIdSource, DisplayEdidMode, DisplayEdidRate, DisplayEdidReference } from "../../types.ts";
import type { AppCopy } from "../../text.ts";
import { uiText } from "../../text.ts";
import type { UserDetailItem } from "../../presentation/userDetails.ts";

export function edidRate(value: DisplayEdidRate): string {
  return `${value.numerator}/${value.denominator} Hz`;
}
function position(value: DisplayEdidReference | { blockIndex: number; byteOffset: number }): string {
  return `${value.blockIndex}:${value.byteOffset}`;
}
function bytes(value: readonly number[]): string {
  return value.map(item => item.toString(16).toUpperCase().padStart(2, "0")).join(" ");
}
function geometry(width: number | null | undefined, height: number | null | undefined): string | null {
  return width != null && height != null ? `${width} × ${height}` : null;
}
function declaredGeometry(mode: DisplayEdidMode, c: AppCopy["deviceTopology"]["edid"]): string | null {
  return geometry(mode.width, mode.height) ?? (mode.width == null ? null : `${c.declaredWidth}: ${mode.width} px`);
}
function nominalLabel(mode: DisplayEdidMode, c: AppCopy["deviceTopology"]["edid"]): string {
  return mode.interlaced == null ? c.nominal : mode.interlaced ? c.nominalFieldRate : c.nominalFrameRate;
}
function modeTitle(mode: DisplayEdidMode, c: AppCopy["deviceTopology"]["edid"]): string {
  return [declaredGeometry(mode, c), mode.timing ? edidRate(mode.timing.frameRate) : null,
    !mode.timing && mode.nominalVerticalRateHz != null ? `${mode.nominalVerticalRateHz} Hz (${mode.encoding === "displayid-iii" ? nominalLabel(mode, c) : c.nominal})` : null,
    mode.width == null && mode.code != null ? `${c.code} #${mode.code}` : null].filter(value => value !== null).join(" · ") || c.unavailable;
}
export function buildDisplayEdidView(display: DeviceTopologyDisplayConnection, app: AppCopy = uiText) {
  const c = app.deviceTopology.edid;
  const yesNo = (value: boolean) => value ? app.shellBridge.yes : app.shellBridge.no;
  function rows(entries: Array<[string, string | number | boolean | null | undefined]>): UserDetailItem[] {
    return entries.filter((entry): entry is [string, string | number | boolean] => entry[1] != null)
      .map(([label, value]) => ({ label, value: typeof value === "boolean" ? yesNo(value) : String(value) }));
  }
  const active = display.active && display.targetAvailable;
  const width = active && display.currentWidth != null && display.currentWidth > 0 ? display.currentWidth : null;
  const height = active && display.currentHeight != null && display.currentHeight > 0 ? display.currentHeight : null;
  const rate = active && display.currentRefreshRateNumerator != null && display.currentRefreshRateNumerator > 0
    && display.currentRefreshRateDenominator != null && display.currentRefreshRateDenominator > 0
    ? edidRate({ numerator: display.currentRefreshRateNumerator, denominator: display.currentRefreshRateDenominator }) : null;
  const current = rows([[c.resolution, geometry(width, height) ?? c.unavailable], [c.refresh, rate ?? c.unavailable],
    [c.bitDepth, active && display.currentBitsPerColorChannel != null && display.currentBitsPerColorChannel > 0 ? display.currentBitsPerColorChannel : c.unavailable]]);
  const capability = display.modeCapabilityDeclared;
  const modes = capability?.modes.map(mode => {
    const t = mode.timing; const cvt = mode.cvtParameters; const aspect = mode.aspectRatioDeclared;
    const typeV = mode.displayIdTypeVOptions; const vi = mode.displayIdTypeViTiming; const viSize = mode.displayIdTypeViImageSize;
    // Type VI 时钟高位与规范上限矛盾：只有时钟和依赖时钟的速率不可用，消隐、前沿、同步、极性与立体码照常显示。
    const viClockConflict = vi != null && vi.pixelClockHz == null;
    return { title: modeTitle(mode, c), rows: rows([
      [c.source, `${mode.encoding} · ${position(mode)}`], [c.resolution, declaredGeometry(mode, c)],
      [c.declaredAspect, aspect ? aspect.horizontal == null || aspect.vertical == null ? c.undefinedAspect : `${aspect.horizontal}:${aspect.vertical}` : null],
      // Type VI 17 字节记录的 code 是宽高比倍率（比值 = 倍率×3:256），不是普通宽高比代码。
      [c.declaredAspectCode, viSize ? null : aspect?.code], [c.aspectMultiplier, viSize?.aspectMultiplier],
      [c.verticalImageSize, viSize ? `${viSize.verticalImageBaseSizeMillimeters} mm × ${viSize.sizeMultiplier} = ${viSize.verticalImageSizeMillimeters} mm` : null],
      [c.revision, mode.revision], [c.containerFlags, mode.containerFlags], [c.containerRevision, mode.containerRevision],
      [c.bitIndex, mode.bitIndex], [c.pixelRepetition, mode.horizontalPixelRepetition],
      [c.code, mode.code], [mode.encoding === "displayid-iii" ? nominalLabel(mode, c) : c.nominal, mode.nominalVerticalRateHz == null ? c.unavailable : `${mode.nominalVerticalRateHz} Hz`],
      [c.frameRate, t ? edidRate(t.frameRate) : viClockConflict ? c.clockConflict : c.unavailable], [c.fieldRate, t?.fieldRate ? edidRate(t.fieldRate) : null],
      [c.pixelClock, t ? `${t.pixelClockHz} Hz` : viClockConflict ? c.clockConflict : null], [c.scan, mode.interlaced == null ? null : mode.interlaced ? c.interlaced : c.progressive],
      [c.horizontal, t ? `${t.horizontalActive} / ${t.horizontalBlanking} / ${t.horizontalSyncOffset} / ${t.horizontalSyncWidth}`
        : vi && mode.width != null ? `${mode.width} / ${vi.horizontalBlankingPixels} / ${vi.horizontalFrontPorchPixels} / ${vi.horizontalSyncWidthPixels}` : null],
      [c.vertical, t ? `${t.verticalActive} / ${t.verticalBlanking} / ${t.verticalSyncOffset} / ${t.verticalSyncWidth}`
        : vi && mode.height != null ? `${mode.height} / ${vi.verticalBlankingLines} / ${vi.verticalFrontPorchLines} / ${vi.verticalSyncWidthLines}` : null],
      [c.signalFlags, t?.flags], [c.horizontalPositive, t?.positiveHorizontalSync ?? vi?.positiveHorizontalSync], [c.verticalPositive, t?.positiveVerticalSync ?? vi?.positiveVerticalSync],
      [c.formula, mode.formula], [c.formulaCode, typeV?.formulaCode], [c.reduced, mode.reducedBlanking], [c.native, mode.native], [c.preferred, mode.preferred],
      [`${c.support420} · ${c.source}`, mode.yCbCr420CapabilityMap ? `${position(mode.yCbCr420CapabilityMap)} · ${bytes(mode.yCbCr420CapabilityMap.rawDescriptor)}` : null],
      [c.only420, mode.yCbCr420Only], [c.encoding420, mode.yCbCr420Encoding], [c.support420, mode.yCbCr420Supported], [c.dsc, mode.dscPassThrough],
      [c.rbVersion, cvt?.reducedBlankingVersion], [c.stereo, cvt?.stereoCode ?? typeV?.stereoSupportCode ?? vi?.stereoSupportCode],
      [c.fractional, cvt?.alternativeRefresh1000Over1001Supported ?? typeV?.fractionalRefreshSupported],
      [c.base160, cvt?.horizontalBlankingBase160Pixels], [c.earlyVsync, cvt?.earlyVsyncRequired], [c.deltaBlanking, cvt?.deltaHorizontalBlankingCode],
      [c.addBlanking, cvt?.additionalVerticalBlankingCode], [c.altMinimum, cvt?.alternativeMinimumVerticalBlanking],
      [c.descriptor, bytes(mode.rawDescriptor)]
    ]) };
  }) ?? [];
  const ranges = capability?.ranges.map(range => ({ title: `${c.formula}: ${range.formula.toUpperCase()}`, rows: rows([
    [c.source, `${range.encoding} · ${position(range)}`],
    [c.revision, range.revision],
    [c.verticalRange, `${range.minimumVerticalRateHz}–${range.maximumVerticalRateHz} Hz`],
    [c.horizontalRange, `${range.minimumHorizontalFrequencyHz}–${range.maximumHorizontalFrequencyHz} Hz`],
    [c.maximumClock, range.maximumPixelClockHz == null ? null : `${range.maximumPixelClockHz} Hz`],
    [c.gtfStart, range.secondaryGtf ? `${range.secondaryGtf.startHorizontalFrequencyHz} Hz` : null],
    [c.parameters, range.secondaryGtf ? `C×2=${range.secondaryGtf.cTimesTwo}; M=${range.secondaryGtf.m}; K=${range.secondaryGtf.k}; J×2=${range.secondaryGtf.jTimesTwo}` : null],
    [c.cvtVersion, range.cvt?.version], [c.maxActivePixels, range.cvt?.maximumActivePixelsPerLine],
    [c.aspectRatioMask, range.cvt?.supportedAspectRatiosMask], [c.aspectRatioCode, range.cvt?.preferredAspectRatioCode],
    [c.standardBlanking, range.cvt?.standardBlanking], [c.reduced, range.cvt?.reducedBlanking],
    [c.scalingMask, range.cvt?.scalingMask], [c.preferredRate, range.cvt?.preferredVerticalRateHz == null ? null : `${range.cvt.preferredVerticalRateHz} Hz`],
    [c.descriptor, bytes(range.rawDescriptor)]
  ]) })) ?? [];
  const selections = capability?.selectionDeclarations?.map(selection => ({ title: selection.encoding === "cta-vfpdb" ? c.preferenceOrder : c.nativeSelection, rows: rows([
    [c.source, `${selection.encoding} · ${position(selection)}`], [c.revision, selection.revision], [c.signalFlags, selection.flags],
    ...selection.references.map(reference => [c.reference, `${reference.shortVideoReference} → ${reference.targetEncoding}${reference.code == null ? "" : ` #${reference.code}`}${reference.target ? ` · ${position(reference.target)}` : ""}`] as [string, string]),
    [c.imageCodes, selection.imageSize ? `${selection.imageSize.widthCode} × ${selection.imageSize.heightCode}; ${selection.imageSize.unitMicrometers} µm` : null],
    [c.descriptor, bytes(selection.rawDescriptor)]
  ]) })) ?? [];
  const container = (value: DisplayEdidDisplayIdSource, tag: string) => [[c.source, `DisplayID ${tag} · ${position(value)}`],
    [c.revision, value.revision], [c.containerFlags, value.containerFlags], [c.containerRevision, value.containerRevision]] as Array<[string, string | number]>;
  const dynamicRanges = capability?.dynamicTimingRanges?.map(range => ({ title: `${c.verticalRange}: ${range.minimumVerticalRateHz}–${range.maximumVerticalRateHz} Hz`, rows: rows([
    ...container(range, "0x25"),
    [c.verticalRange, `${range.minimumVerticalRateHz}–${range.maximumVerticalRateHz} Hz`],
    [c.pixelClockRange, `${range.minimumPixelClockHz}–${range.maximumPixelClockHz} Hz`],
    [c.seamlessDynamicBlanking, range.seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking],
    [c.descriptor, bytes(range.rawDescriptor)]
  ]) })) ?? [];
  // 零码是"整个范围内任意变化都满足无抖动要求"的独立语义，不写成 0 µs，也不当作缺失。
  const tolerance = (value: DisplayEdidAdaptiveSyncTolerance) => value.zeroCodeMeansNoJitterThroughoutRange ? c.noJitterLimit : `${value.maximumSingleStepMicroseconds} µs`;
  const adaptiveSyncRanges = capability?.adaptiveSyncRanges?.map(range => ({ title: `${c.declaredBase}: ${range.baseMinimumRefreshRateHz}–${range.baseMaximumRefreshRateHz} Hz`, rows: rows([
    ...container(range, "0x2B"),
    [c.declaredBaseRange, `${range.baseMinimumRefreshRateHz}–${range.baseMaximumRefreshRateHz} Hz`],
    [c.nativePanelRange, range.nativePanelRange], [c.avtSupported, range.adaptiveVTotalSupported], [c.seamlessTransition, range.seamlessTransitionSupported],
    [c.frameIncrease, tolerance(range.frameDurationIncrease)], [c.frameIncreaseScope, range.frameDurationIncrease.flickerRestrictedToSingleStep],
    [c.frameDecrease, tolerance(range.frameDurationDecrease)], [c.frameDecreaseScope, range.frameDurationDecrease.flickerRestrictedToSingleStep],
    [c.descriptor, bytes(range.rawDescriptor)]
  ]) })) ?? [];
  return { current, modes, ranges, selections, dynamicRanges, adaptiveSyncRanges, declaration: rows([[c.revision, capability ? `1.${capability.edidRevision}` : null],
    [c.baseFeatures, capability?.baseFeatures], [c.ctaFlags, capability?.ctaFlags]]),
    available: capability != null,
    selectionsReported: capability?.selectionDeclarations != null,
    // 旧载荷没有这两个集合时不显示对应分区，保持旧载荷的原有显示。
    dynamicRangesReported: capability?.dynamicTimingRanges != null,
    adaptiveSyncReported: capability?.adaptiveSyncRanges != null,
    raw: display.edidRawDeclaredBase64 == null ? c.unavailable : display.edidRawDeclaredBase64 === "" ? c.emptyRaw : display.edidRawDeclaredBase64 };
}
