import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import {
  resetGpuPerformanceScoreOverrides,
  saveGpuPerformanceScoreOverrides
} from "../api";
import {
  buildGpuSpecializedCounterIds,
  type GpuSchedulingModelSnapshot
} from "../data/gpu/gpuSchedulingModelSource";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import type { SourceLease } from
  "../frontendRuntime/source/SourceDescriptor";
import { SourceLeaseBinding } from
  "../frontendRuntime/source/SourceLeaseBinding";
import {
  sourceCanRender,
  type SourceSnapshot
} from "../frontendRuntime/source/SourceSnapshot";
import { resolveGpuDeviceName } from "../features/gpuScheduling/gpuDeviceName";
import { metricDisplayValue } from "../presentation/metricLabels";
import {
  failedObservation,
  loadingObservation,
  observationCanRender,
  readyObservation,
  type ObservationState
} from "../observation/observationState";
import type {
  GpuPerformanceScoreItem,
  GpuSpecializedTelemetrySnapshot,
  MetricDefinition,
  MetricSnapshot,
  ResourceBreakdownSnapshot,
  ResourceSoftwareSegment
} from "../types";
import { ObservationStateBoundary } from "./ObservationStateNotice";
import { uiText } from "../text.ts";

interface GpuSchedulingPosition {
  id: string;
  index: number;
  label: string;
  name: string;
  rasterPerformanceScore: number;
  generationBonusScore: number;
  useCaseBonusScore: number;
  gpuPerformanceUseCases: string[];
  defaultPerformanceScore: number;
  performanceScore: number;
  hasPerformanceOverride: boolean;
  usagePercent?: number | null;
  vramPercent?: number | null;
  vramDisplay: string;
  graphicsClockDisplay: string;
  memoryClockDisplay: string;
  scoreSource: string;
  matchedPreset?: string | null;
  full: boolean;
  fullReason: string;
  hasDedicatedMemoryMetrics: boolean;
  processKeys: string[];
  softwareIds: string[];
  softwareScoreTotal: number;
  specializedUsages: GpuSpecializedUsage[];
}

interface GpuProcessLoad {
  processKeys: string[];
  softwareIds: string[];
  softwareScoreTotal: number;
}

interface GpuSpecializedUsage {
  counterId: string;
  label: string;
  value: number | null;
  unit: string;
  providerId: string;
  isIntrusive: boolean;
  engineName?: string | null;
}

const gpuUsageFullPressurePercent = 95;
const gpuVramFullPressurePercent = 90;
const gpuSamplingIntervalMs = 1_000;

interface GpuSchedulingModelProps {
  runtimeEffectsEnabled: boolean;
}

export function GpuSchedulingModel(props: GpuSchedulingModelProps) {
  const demandId = "details.gpu-model";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsGpuModel]);
  const frontendWork = useFrontendWork();
  const runtime = useFrontendRuntime();
  const catalogLease = runtime.sources.metricCatalog.acquire({
    active: true,
    refreshIntervalMs: null
  });
  const [catalogSource, setCatalogSource] =
    createSignal<SourceSnapshot<MetricDefinition[]>>(catalogLease.snapshot);
  const [modelSource, setModelSource] =
    createSignal<SourceSnapshot<GpuSchedulingModelSnapshot> | null>(null);
  const modelBinding = new SourceLeaseBinding<GpuSchedulingModelSnapshot>(
    setModelSource);
  let modelLease: SourceLease<GpuSchedulingModelSnapshot> | null = null;
  const [metricSnapshot, setMetricSnapshot] =
    createSignal<MetricSnapshot | null>(null);
  const [resourceBreakdown, setResourceBreakdown] =
    createSignal<ResourceBreakdownSnapshot | null>(null);
  const [specializedTelemetry, setSpecializedTelemetry] =
    createSignal<GpuSpecializedTelemetrySnapshot | null>(null);
  const unsubscribeCatalog = catalogLease.subscribe(setCatalogSource);
  const catalog = createMemo(() => {
    const source = catalogSource();
    return sourceCanRender(source) ? source.data ?? [] : [];
  });
  const gpuIndexKey = createMemo(() => resolveGpuIndexes(catalog()).join(","));
  const gpuIndexes = createMemo(() => gpuIndexKey()
    .split(",")
    .filter(Boolean)
    .map((value) => Number(value)));

  createEffect(() => {
    const indexes = gpuIndexes();
    const active = frontendWork.isNeeded(frontendWorkIds.detailsGpuModel);
    modelLease = modelBinding.switch(
      JSON.stringify([active, indexes]),
      () => runtime.sources.gpuSchedulingModel.acquire(
        { gpuIndexes: indexes },
        { active, refreshIntervalMs: null }));
  });

  createEffect(() => {
    const indexes = gpuIndexes();
    const active = frontendWork.isNeeded(frontendWorkIds.detailsGpuModel);
    setMetricSnapshot(null);
    setResourceBreakdown(null);
    setSpecializedTelemetry(null);
    if (!active || indexes.length === 0) {
      return;
    }

    const metricIds = indexes.flatMap((index) => [
      `gpu.${index}.usage`,
      `gpu.${index}.vram`,
      `gpu.${index}.vramPercent`,
      `gpu.${index}.graphicsClock`,
      `gpu.${index}.memoryClock`
    ]);
    const stopMetric = runtime.sources.metricSnapshot.subscribe(
      { ids: metricIds },
      gpuSamplingIntervalMs,
      setMetricSnapshot);
    const stopResource = runtime.sources.resourceMonitor.subscribe(
      {
        scope: "bars",
        bars: indexes.flatMap((index) => [
          { metricId: `gpu.${index}.usage`, scaleMode: "active" as const },
          { metricId: `gpu.${index}.vram`, scaleMode: "capacity" as const }
        ]),
        sampleMetricIds: [],
        visibleColumnIds: [],
        sortColumnId: "impact",
        sortDirection: "desc",
        tableMode: "software",
        processDetailSoftwareIds: []
      },
      gpuSamplingIntervalMs,
      (value) => setResourceBreakdown(value.breakdown));
    const stopSpecialized = runtime.sources.gpuSpecializedTelemetry.subscribe(
      { counterIds: buildGpuSpecializedCounterIds(indexes) },
      gpuSamplingIntervalMs,
      setSpecializedTelemetry);
    onCleanup(() => {
      stopMetric();
      stopResource();
      stopSpecialized();
    });
  });

  onCleanup(() => {
    unsubscribeCatalog();
    catalogLease.release();
    modelBinding.release();
    modelLease = null;
  });

  const model = createMemo(() => {
    const catalogSnapshot = catalogSource();
    const modelSnapshot = modelSource();
    if (!modelSnapshot
        || !sourceCanRender(catalogSnapshot)
        || !sourceCanRender(modelSnapshot)) {
      return null;
    }
    return modelSnapshot.data;
  });
  const observation = createMemo<ObservationState>(() => {
    const data = model();
    if (data && metricSnapshot() !== null && resourceBreakdown() !== null) {
      return readyObservation(data.capturedAt ?? undefined);
    }
    const failed = [catalogSource(), modelSource()]
      .find((source) => source?.status === "error"
        || source?.status === "unavailable");
    return failed
      ? failedObservation(
          loadingObservation(),
          failed.error instanceof Error
            ? failed.error.message
            : uiText.gpuScheduling.dataReadFailed)
      : loadingObservation();
  });
  const processLoads = createMemo(() =>
    buildGpuProcessLoads(resourceBreakdown() ?? undefined));
  const specializedUsages = createMemo(() =>
    buildGpuSpecializedUsages(specializedTelemetry() ?? undefined));
  const specializedAdapterNames = createMemo(() =>
    buildGpuSpecializedAdapterNames(specializedTelemetry() ?? undefined));
  const positions = createMemo(() => buildGpuPositions(
    metricSnapshot() ?? undefined,
    gpuIndexes(),
    model()?.scoreSnapshot.gpus ?? [],
    model()?.scoreOverrides.scoresByGpuId ?? {},
    processLoads(),
    specializedUsages(),
    specializedAdapterNames()));
  const sortedPositions = createMemo(() => [...positions()].sort(compareGpuPositions));
  const telemetryStatus = createMemo(() => {
    switch (observation().status) {
      case "ready":
        return uiText.gpuScheduling.telemetryLive;
      case "stale":
        return uiText.gpuScheduling.telemetryStale;
      case "error":
        return uiText.gpuScheduling.telemetryUnavailable;
      default:
        return uiText.gpuScheduling.telemetryLoading;
    }
  });
  const [editing, setEditing] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [scoreDraft, setScoreDraft] = createSignal<Record<string, string>>({});

  createEffect(() => {
    if (editing()) {
      return;
    }

    setScoreDraft(createScoreDraft(positions()));
  });

  createEffect(() => {
    if (editing() && (!props.runtimeEffectsEnabled || observation().status !== "ready")) {
      setEditing(false);
    }
  });

  return (
    <section
      class="gpu-scheduling-panel"
      aria-label={uiText.gpuScheduling.modelLabel}
      {...frontendVisibilitySurface(
        "visible.details.gpu-model.surface",
        [demandId])}
    >
      <div class="panel-header gpu-scheduling-header">
        <div class="optimization-heading">
          <h2>{uiText.gpuScheduling.panel}</h2>
          <span>{uiText.gpuScheduling.summary(observationCanRender(observation()) ? String(positions().length) : "--", gpuUsageFullPressurePercent, gpuVramFullPressurePercent, telemetryStatus())}</span>
        </div>
        <div class="gpu-scheduling-actions">
          <Show when={editing()} fallback={
            <Show when={props.runtimeEffectsEnabled && observation().status === "ready"}>
              <button class="secondary" type="button" disabled={!model()} onClick={startEditing}>
                {uiText.gpuScheduling.edit}
              </button>
            </Show>
          }>
            <button class="secondary" type="button" disabled={saving()} onClick={cancelEditing}>
              {uiText.gpuScheduling.cancel}
            </button>
            <button class="secondary" type="button" disabled={saving()} onClick={resetScores}>
              {uiText.gpuScheduling.reset}
            </button>
            <button type="button" disabled={saving()} onClick={saveScores}>
              {uiText.gpuScheduling.save}
            </button>
          </Show>
        </div>
      </div>

      <ObservationStateBoundary
        state={observation()}
        label={uiText.gpuScheduling.observationLabel}
        onRetry={refreshAll}
      >
        <Show when={positions().length > 0} fallback={<div class="optimization-empty">{uiText.gpuScheduling.noPositions}</div>}>
          <div class="gpu-position-grid">
            <For each={sortedPositions()}>
              {(position) => (
                <article
                  class="gpu-position"
                  classList={{
                    full: position.full
                  }}
                >
                <div class="gpu-position-main">
                  <div>
                    <strong>{position.name}</strong>
                    <span>{position.label}</span>
                  </div>
                  <em>{position.full ? position.fullReason : uiText.gpuScheduling.schedulable}</em>
                </div>

                <div class="gpu-position-meta">
                  <Show when={editing()} fallback={
                    <span>
                      {uiText.gpuScheduling.performanceScore(formatScore(position.performanceScore))}
                      <Show when={position.hasPerformanceOverride}>
                        {" "}· {uiText.gpuScheduling.defaultScore(formatScore(position.defaultPerformanceScore))}
                      </Show>
                    </span>
                  }>
                    <label class="gpu-score-editor">
                      <span>{uiText.gpuScheduling.performanceScoreLabel}</span>
                      <input
                        class="gpu-score-input"
                        type="number"
                        min="0"
                        step="any"
                        value={scoreDraft()[position.id] ?? formatScore(position.performanceScore)}
                        onInput={(event) => updateScoreDraft(position.id, event.currentTarget.value)}
                      />
                      <small>
                        {uiText.gpuScheduling.defaultScore(formatScore(position.defaultPerformanceScore))}
                        {" "}· {uiText.gpuScheduling.rasterScore(formatScore(position.rasterPerformanceScore))}
                        <Show when={position.generationBonusScore > 0}>
                          {" "}+ {uiText.gpuScheduling.generationBonus(formatScore(position.generationBonusScore))}
                        </Show>
                        <Show when={position.useCaseBonusScore > 0}>
                          {" "}+ {uiText.gpuScheduling.useCaseBonus(formatScore(position.useCaseBonusScore))}
                        </Show>
                      </small>
                    </label>
                  </Show>
                  <span>{uiText.gpuScheduling.softwareScoreTotal(formatScore(position.softwareScoreTotal))}</span>
                  <span>{formatProcessCount(position)}</span>
                  <span>{position.graphicsClockDisplay}</span>
                  <span>{position.memoryClockDisplay}</span>
                  <span>{position.vramDisplay}</span>
                </div>

                <div class="gpu-pressure-grid">
                  <PressureMeter label="GPU" value={position.usagePercent} />
                  <PressureMeter
                    label={uiText.gpuScheduling.vram}
                    value={position.hasDedicatedMemoryMetrics ? position.vramPercent : null}
                    emptyLabel={position.hasDedicatedMemoryMetrics ? uiText.gpuScheduling.noData : uiText.gpuScheduling.shared}
                  />
                </div>

                  <SpecializedUsageStrip usages={position.specializedUsages} />
                </article>
              )}
            </For>
          </div>
        </Show>
      </ObservationStateBoundary>
    </section>
  );

  function startEditing() {
    if (!props.runtimeEffectsEnabled || observation().status !== "ready") {
      return;
    }
    setScoreDraft(createScoreDraft(positions()));
    setEditing(true);
  }

  function cancelEditing() {
    setScoreDraft(createScoreDraft(positions()));
    setEditing(false);
  }

  function updateScoreDraft(
    gpuId: string,
    value: string) {
    setScoreDraft((current) => ({ ...current, [gpuId]: value }));
  }

  async function saveScores() {
    if (!props.runtimeEffectsEnabled || saving()) {
      return;
    }
    setSaving(true);
    try {
      await saveGpuPerformanceScoreOverrides({
        scores: positions().map((position) => ({
          gpuId: position.id,
          performanceScore: normalizeScore(scoreDraft()[position.id], position.performanceScore)
        }))
      });
      setEditing(false);
      await refreshAll();
    } finally {
      setSaving(false);
    }
  }

  async function resetScores() {
    if (!props.runtimeEffectsEnabled || saving()) {
      return;
    }
    setSaving(true);
    try {
      await resetGpuPerformanceScoreOverrides();
      setEditing(false);
      await refreshAll();
    } finally {
      setSaving(false);
    }
  }

  async function refreshAll() {
    await Promise.all([
      catalogLease.refresh(),
      modelLease?.refresh() ?? Promise.resolve(null)
    ]);
  }
}

function PressureMeter(props: { label: string; value?: number | null; emptyLabel?: string }) {
  const value = () => sanitizePercent(props.value);
  return (
    <div class="gpu-pressure-meter">
      <div>
        <span>{props.label}</span>
        <strong>{value() === null ? props.emptyLabel ?? uiText.gpuScheduling.noData : formatPercent(value())}</strong>
      </div>
      <span class="gpu-pressure-track" aria-hidden="true">
        <span style={{ width: `${value() ?? 0}%` }} />
      </span>
    </div>
  );
}

function SpecializedUsageStrip(props: { usages: GpuSpecializedUsage[] }) {
  return (
    <div class="gpu-specialized-strip" aria-label={uiText.gpuScheduling.specializedStrip}>
      <For each={props.usages} fallback={
        <SpecializedUsageMeter usage={{
          counterId: "gpu.specialized.none",
          label: uiText.gpuScheduling.specializedUsage,
          value: null,
          unit: "%",
          providerId: "",
          isIntrusive: false
        }} />
      }>
        {(usage) => <SpecializedUsageMeter usage={usage} />}
      </For>
    </div>
  );
}

function SpecializedUsageMeter(props: { usage: GpuSpecializedUsage }) {
  const value = () => sanitizePercent(props.usage.value);
  const title = () => [
    props.usage.label,
    props.usage.providerId,
    props.usage.engineName ?? ""
  ].filter(Boolean).join(" · ");
  return (
    <div class="gpu-specialized-meter" title={title()}>
      <div>
        <span>{props.usage.label}</span>
        <strong>{value() === null ? uiText.gpuScheduling.noData : formatPercent(value())}</strong>
      </div>
      <span class="gpu-specialized-track" aria-hidden="true">
        <span style={{ width: `${value() ?? 0}%` }} />
      </span>
    </div>
  );
}

function resolveGpuIndexes(catalog: MetricDefinition[]) {
  return catalog
    .map((definition) => parseGpuIndex(definition.id))
    .filter((index): index is number => index !== null)
    .filter((index, itemIndex, values) => values.indexOf(index) === itemIndex)
    .sort((left, right) => left - right);
}

function buildGpuPositions(
  snapshot: MetricSnapshot | undefined,
  indexes: number[],
  scoreItems: GpuPerformanceScoreItem[],
  overrideScoresByGpuId: Record<string, number>,
  processLoads: Map<number, GpuProcessLoad>,
  specializedUsages: Map<number, GpuSpecializedUsage[]>,
  specializedAdapterNames: Map<number, string>): GpuSchedulingPosition[] {
  const items = snapshot?.items ?? {};
  const scoreById = new Map(scoreItems.map((item) => [item.gpuId, item]));
  return indexes.map((index) => {
    const usage = items[`gpu.${index}.usage`];
    const vram = items[`gpu.${index}.vram`];
    const vramPercent = items[`gpu.${index}.vramPercent`];
    const graphicsClock = items[`gpu.${index}.graphicsClock`];
    const memoryClock = items[`gpu.${index}.memoryClock`];
    const label = `GPU${index}`;
    const id = `gpu:${index}`;
    const scoreItem = scoreById.get(id);
    const name = resolveGpuDeviceName(index, scoreItem?.name, specializedAdapterNames.get(index));
    const fallbackOverrideScore = sanitizeScore(overrideScoresByGpuId[id]);
    const fallbackScore = fallbackOverrideScore ?? 1;
    const defaultPerformanceScore = scoreItem?.defaultPerformanceScore ?? fallbackScore;
    const performanceScore = scoreItem?.performanceScore ?? fallbackScore;
    const usagePercent = sanitizePercent(usage?.percent ?? usage?.numericValue);
    const dedicatedMemory = vram !== undefined || vramPercent !== undefined;
    const usedVramPercent = dedicatedMemory
      ? sanitizePercent(vramPercent?.percent ?? vramPercent?.numericValue ?? vram?.percent)
      : null;
    const fullReason = fullReasonFor(usagePercent, usedVramPercent);
    const processLoad = processLoads.get(index);

    return {
      id,
      index,
      label,
      name,
      rasterPerformanceScore: scoreItem?.rasterPerformanceScore ?? defaultPerformanceScore,
      generationBonusScore: scoreItem?.generationBonusScore ?? 0,
      useCaseBonusScore: scoreItem?.useCaseBonusScore ?? 0,
      gpuPerformanceUseCases: scoreItem?.gpuPerformanceUseCases ?? ["general"],
      defaultPerformanceScore,
      performanceScore,
      hasPerformanceOverride: scoreItem?.hasPerformanceOverride ?? fallbackOverrideScore !== null,
      usagePercent,
      vramPercent: usedVramPercent,
      vramDisplay: metricDisplayValue(
        vram,
        dedicatedMemory ? uiText.gpuScheduling.vramNoData : uiText.gpuScheduling.sharedMemory),
      graphicsClockDisplay: graphicsClock?.displayValue ? uiText.gpuScheduling.clock(graphicsClock.displayValue) : uiText.gpuScheduling.clockNoData,
      memoryClockDisplay: memoryClock?.displayValue ? uiText.gpuScheduling.memoryClock(memoryClock.displayValue) : uiText.gpuScheduling.memoryClockNoData,
      scoreSource: scoreItem?.source ?? "unknown",
      matchedPreset: scoreItem?.matchedPreset,
      full: fullReason !== "",
      fullReason: fullReason || uiText.gpuScheduling.schedulable,
      hasDedicatedMemoryMetrics: dedicatedMemory,
      processKeys: processLoad?.processKeys ?? [],
      softwareIds: processLoad?.softwareIds ?? [],
      softwareScoreTotal: processLoad?.softwareScoreTotal ?? 0,
      specializedUsages: specializedUsages.get(index) ?? []
    };
  });
}

function buildGpuSpecializedUsages(snapshot: GpuSpecializedTelemetrySnapshot | undefined) {
  const usagesByIndex = new Map<number, GpuSpecializedUsage[]>();
  for (const adapter of snapshot?.adapters ?? []) {
    const usages = adapter.counters
      .map((counter) => ({
        counterId: counter.counterId,
        label: labelForSpecializedCounter(counter.counterId, counter.displayName),
        value: sanitizePercent(counter.value),
        unit: counter.unit || "%",
        providerId: counter.providerId,
        isIntrusive: counter.isIntrusive,
        engineName: counter.engineName
      } satisfies GpuSpecializedUsage))
      .sort((left, right) => specializedCounterOrder(left.counterId) - specializedCounterOrder(right.counterId));
    usagesByIndex.set(adapter.adapterIndex, usages);
  }

  return usagesByIndex;
}

function buildGpuSpecializedAdapterNames(snapshot: GpuSpecializedTelemetrySnapshot | undefined) {
  return new Map((snapshot?.adapters ?? [])
    .map((adapter) => [adapter.adapterIndex, adapter.adapterName?.trim()] as const)
    .filter((entry): entry is readonly [number, string] => Boolean(entry[1])));
}

function specializedCounterOrder(counterId: string) {
  const normalized = counterId.toLowerCase();
  if (normalized.includes(".nvidia.rt.")) {
    return 10;
  }
  if (normalized.includes(".nvidia.cuda.")) {
    return 20;
  }
  if (normalized.includes(".nvidia.tensor.")) {
    return 30;
  }
  if (normalized.includes(".amd.gcn.")) {
    return 40;
  }
  if (normalized.includes(".amd.rdna.")) {
    return 50;
  }
  if (normalized.includes(".amd.cdna.")) {
    return 60;
  }

  return 100;
}

function labelForSpecializedCounter(counterId: string, fallback?: string) {
  const normalized = counterId.toLowerCase();
  if (normalized.includes(".nvidia.rt.")) {
    return uiText.gpuScheduling.rtUsage;
  }
  if (normalized.includes(".nvidia.cuda.")) {
    return uiText.gpuScheduling.cudaUsage;
  }
  if (normalized.includes(".nvidia.tensor.")) {
    return uiText.gpuScheduling.tensorUsage;
  }
  if (normalized.includes(".amd.gcn.")) {
    return uiText.gpuScheduling.gcnUsage;
  }
  if (normalized.includes(".amd.rdna.")) {
    return uiText.gpuScheduling.rdnaUsage;
  }
  if (normalized.includes(".amd.cdna.")) {
    return uiText.gpuScheduling.cdnaUsage;
  }

  return fallback || uiText.gpuScheduling.specializedUsage;
}

function compareGpuPositions(
  left: GpuSchedulingPosition,
  right: GpuSchedulingPosition) {
  if (right.performanceScore !== left.performanceScore) {
    return right.performanceScore - left.performanceScore;
  }

  return left.index - right.index;
}

function buildGpuProcessLoads(snapshot: ResourceBreakdownSnapshot | undefined) {
  const byGpu = new Map<number, MutableGpuProcessLoad>();
  for (const bar of snapshot?.bars ?? []) {
    const parsed = parseGpuBreakdownMetric(bar.metricId);
    if (!parsed) {
      continue;
    }

    const load = getMutableGpuProcessLoad(byGpu, parsed.index);
    const softwareSegments = (bar.software ?? []).filter(isRealProcessSoftwareSegment);
    for (const segment of softwareSegments) {
      load.softwareIds.add(segment.softwareId);
      const currentScore = load.softwareScores.get(segment.softwareId) ?? 0;
      load.softwareScores.set(segment.softwareId, Math.max(currentScore, resolveSegmentBaseScore(segment)));
      for (const process of segment.processes ?? []) {
        load.processKeys.add(`${process.processId}|${process.name}`);
      }
    }

  }

  return new Map([...byGpu.entries()].map(([index, load]) => [index, {
    processKeys: [...load.processKeys],
    softwareIds: [...load.softwareIds],
    softwareScoreTotal: [...load.softwareScores.values()].reduce((total, value) => total + value, 0)
  } satisfies GpuProcessLoad]));
}

interface MutableGpuProcessLoad {
  processKeys: Set<string>;
  softwareIds: Set<string>;
  softwareScores: Map<string, number>;
}

function getMutableGpuProcessLoad(
  loads: Map<number, MutableGpuProcessLoad>,
  index: number) {
  let load = loads.get(index);
  if (!load) {
    load = {
      processKeys: new Set<string>(),
      softwareIds: new Set<string>(),
      softwareScores: new Map<string, number>()
    };
    loads.set(index, load);
  }

  return load;
}

function parseGpuBreakdownMetric(metricId?: string | null) {
  const match = metricId?.match(/^gpu\.(\d+)\.(usage|vram)$/i);
  return match
    ? { index: Number(match[1]), metric: match[2].toLowerCase() as "usage" | "vram" }
    : null;
}

function isRealProcessSoftwareSegment(segment: ResourceSoftwareSegment) {
  return Boolean(segment.softwareId)
    && !segment.softwareId.startsWith("resource-residual:");
}

function resolveSegmentBaseScore(segment: ResourceSoftwareSegment) {
  return sanitizeScore(segment.baseScore) ?? defaultBaseScoreForKind(segment.kind);
}

function defaultBaseScoreForKind(kind?: string | null) {
  switch ((kind ?? "").toLowerCase()) {
    case "game":
      return 95;
    case "highperformance":
      return 85;
    case "windowssystem":
    case "windowscomponent":
    case "windowsservice":
      return 90;
    case "adapted":
      return 80;
    case "dependencysupport":
      return 65;
    case "runtimeproduct":
    case "runtimepackage":
    case "runtimeroot":
      return 55;
    case "controlled":
      return 45;
    case "managed":
      return 42;
    case "unattributed":
      return 30;
    default:
      return 35;
  }
}

function fullReasonFor(usagePercent: number | null, vramPercent: number | null) {
  const reasons = [
    usagePercent !== null && usagePercent >= gpuUsageFullPressurePercent ? uiText.gpuScheduling.gpuFull : "",
    vramPercent !== null && vramPercent >= gpuVramFullPressurePercent ? uiText.gpuScheduling.vramFull : ""
  ].filter(Boolean);
  return reasons.join(" / ");
}

function parseGpuIndex(metricId?: string | null) {
  const match = metricId?.match(/^gpu\.(\d+)\./i);
  return match ? Number(match[1]) : null;
}

function sanitizePercent(value?: number | null) {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.max(0, Math.min(100, value))
    : null;
}

function formatPercent(value: number | null) {
  return value === null ? uiText.gpuScheduling.noData : `${value.toFixed(1)}%`;
}

function formatProcessCount(position: GpuSchedulingPosition) {
  const processCount = position.processKeys.length;
  const softwareCount = position.softwareIds.length;
  if (processCount === 0 && softwareCount === 0) {
    return uiText.gpuScheduling.noProcessAttribution;
  }

  return uiText.gpuScheduling.positionSummary(softwareCount, processCount);
}

function formatScore(value: number) {
  return Number.isFinite(value) ? String(value) : "0";
}

function createScoreDraft(positions: GpuSchedulingPosition[]) {
  return Object.fromEntries(positions.map((position) => [position.id, formatScore(position.performanceScore)]));
}

function sanitizeScore(value?: number | null) {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.max(0, value)
    : null;
}

function normalizeScore(
  value: string | undefined,
  fallback: number) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.max(0, parsed);
}
