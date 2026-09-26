import {
  createEffect,
  createMemo,
  createSignal,
  For,
  onCleanup,
  Show
} from "solid-js";
import {
  resetCpuCorePerformanceOverrides,
  saveCpuCorePerformanceOverrides
} from "../api";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { sourceCanRender, type SourceSnapshot } from "../frontendRuntime/source/SourceSnapshot";
import { useSource } from "../frontendRuntime/source/useSource";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import type { JSX } from "solid-js";
import type {
  CpuCcdModel,
  CpuCoreCacheLevelModel,
  CpuExclusiveBinding,
  CpuCoreResidencySnapshot,
  CpuPhysicalCoreModel,
  CpuTopologySnapshot
} from "../types";
import { UserDetailsDialog } from "./UserDetailsDialog";
import { uiText } from "../text.ts";
import { formatBytes } from "../presentation/byteUnits.ts";

type CpuSelectionKind = "ccd" | "core" | "logical";

interface CpuSelection {
  kind: CpuSelectionKind;
  id: string;
}

interface CpuRuntimeDuration {
  executionTimeMilliseconds: number;
  topProcesses: CpuProcessRuntimeDuration[];
}

interface CpuProcessRuntimeDuration {
  key: string;
  processId: number;
  processName: string;
  executionTimeMilliseconds: number;
}

interface CpuResidencyDurations {
  hasData: boolean;
  cores: Map<string, CpuRuntimeDuration>;
  logicalProcessors: Map<number, CpuRuntimeDuration>;
}

const emptyRuntimeDuration: CpuRuntimeDuration = {
  executionTimeMilliseconds: 0,
  topProcesses: []
};

export function CpuTopologyDiagram(props: {
  runtimeEffectsEnabled: boolean;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
}) {
  const demandId = "details.cpu-model";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsCpuModel]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const [topology, setTopology] = createSignal<CpuTopologySnapshot | null>(null);
  const [residency, setResidency] =
    createSignal<CpuCoreResidencySnapshot | null>(null);
  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsCpuModel)) {
      return;
    }
    const stopTopology = frontendRuntime.sources.cpuTopology.subscribe(
      60_000,
      setTopology);
    const stopResidency = frontendRuntime.sources.cpuResidency.subscribe(
      1_000,
      setResidency);
    onCleanup(() => {
      stopResidency();
      stopTopology();
    });
  });
  const [commandError, setCommandError] = createSignal<string | null>(null);
  const [selected, setSelected] = createSignal<CpuSelection[]>([]);
  const [editing, setEditing] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [detailsOpen, setDetailsOpen] = createSignal(false);
  const [detailsCoreId, setDetailsCoreId] = createSignal<string | null>(null);
  const exclusiveBindingsSource = useSource(
    frontendRuntime.sources.cpuExclusiveBindings,
    () => ({
      active: frontendWork.isNeeded(frontendWorkIds.detailsCpuModel) && detailsOpen(),
      refreshIntervalMs: null
    }));
  const [scoreDraft, setScoreDraft] = createSignal<Record<number, string>>({});
  const snapshot = () => topology() ?? undefined;
  const residencySnapshot = () => residency() ?? undefined;
  const exclusiveBindingsSnapshot = () =>
    renderableSourceData(exclusiveBindingsSource.snapshot());
  const residencyDurations = createMemo(() => buildCpuResidencyDurations(residencySnapshot()));
  const residencyHasData = () => residencyDurations().hasData;
  const topologySummary = createMemo(() => {
    const model = snapshot();
    return model
      ? `${model.physicalCoreCount}C / ${model.logicalProcessorCount}T · ${model.ccdCount} CCD`
      : null;
  });
  const selectedLogicalIds = createMemo(() => resolveSelectedLogicalIds(snapshot(), selected()));

  createEffect(() => {
    const current = snapshot();
    if (!current) {
      return;
    }

    setSelected((items) => items.filter((item) => selectionStillExists(current, item)));
  });

  createEffect(() => {
    const current = snapshot();
    if (!current || editing()) {
      return;
    }

    setScoreDraft(createScoreDraft(current));
  });

  createEffect(() => {
    if (editing() && (!props.runtimeEffectsEnabled || !snapshot())) {
      setEditing(false);
    }
  });

  return (
    <section
      {...frontendVisibilitySurface("visible.details.cpu-model.surface", [demandId])}
      class="cpu-topology-panel"
      aria-label={uiText.cpuTopology.modelLabel}
    >
      <div class="panel-header cpu-topology-header">
        <div class="optimization-heading">
          <h2>{uiText.cpuTopology.panel}</h2>
          <span>
            <Show when={topologySummary()} fallback="-">
              {(summary) => summary()}
            </Show>
          </span>
        </div>
        <Show when={snapshot()}>
          {(model) => (
            <div class="cpu-topology-actions">
              <Show when={editing()} fallback={
                <>
                  <button class="secondary" type="button" onClick={() => openDetails(model())}>
                    {uiText.cpuTopology.details}
                  </button>
                  <Show when={props.runtimeEffectsEnabled}>
                    <button class="secondary" type="button" onClick={() => startEditing(model())}>
                      {uiText.cpuTopology.edit}
                    </button>
                  </Show>
                </>
              }>
                <button class="secondary" type="button" disabled={saving()} onClick={() => cancelEditing(model())}>
                  {uiText.cpuTopology.cancel}
                </button>
                <button class="secondary" type="button" disabled={saving()} onClick={() => resetScores(model())}>
                  {uiText.cpuTopology.reset}
                </button>
                <button type="button" disabled={saving()} onClick={() => saveScores(model())}>
                  {uiText.cpuTopology.save}
                </button>
              </Show>
            </div>
          )}
        </Show>
      </div>

      <Show when={commandError()}>
        {(message) => <p role="alert">{message()}</p>}
      </Show>
        <Show when={snapshot()} fallback={<div class="optimization-empty">-</div>}>
          {(model) => (
            <>
            <div class="cpu-topology-meta">
              <strong>{model().cpuName}</strong>
            </div>
            <div class="cpu-topology-diagram" classList={{ "ring-layout": model().visualLayoutKind === "RingBus" }}>
              <Show when={model().visualLayoutKind === "RingBus"} fallback={
                <For each={model().ccds}>
                  {(ccd) => (
                    <article class="cpu-ccd" classList={{ selected: isSelected(selected(), "ccd", ccd.id) }}>
                      <button
                        class="cpu-ccd-header"
                        type="button"
                        aria-pressed={isSelected(selected(), "ccd", ccd.id)}
                        onClick={() => toggleSelection(setSelected, "ccd", ccd.id)}
                      >
                        <span>{ccd.label}</span>
                        <small>{formatUsage(ccd.usagePercent)}</small>
                      </button>
                      <CpuCacheHierarchy
                        cores={coresForCcd(model(), ccd)}
                        renderCore={(core) => {
                            const runtime = () => coreRuntimeDuration(residencyDurations(), core.id);
                            return (
                              <article class="cpu-core" classList={{ selected: isSelected(selected(), "core", core.id) }}>
                                <CpuCoreSelectionButton
                                  core={core}
                                  executionTimeMilliseconds={runtime().executionTimeMilliseconds}
                                  residencyAvailable={residencyHasData()}
                                  selected={isSelected(selected(), "core", core.id)}
                                  variant="grid"
                                  onToggle={() => toggleSelection(setSelected, "core", core.id)}
                                />
                                <Show when={editing()}>
                                  <input
                                    class="cpu-score-input"
                                    type="number"
                                    min="0"
                                    step="any"
                                    aria-label={uiText.cpuTopology.setCoreScore(core.label)}
                                    value={scoreDraft()[core.index] ?? formatScore(core.performanceScore)}
                                    onInput={(event) => updateScoreDraft(core.index, event.currentTarget.value)}
                                  />
                                </Show>
                                <div class="cpu-logical-list">
                                  <For each={logicalForCore(model(), core)}>
                                    {(logical) => {
                                      const logicalRuntime = () => logicalRuntimeDuration(residencyDurations(), logical.id);
                                      return (
                                        <button
                                          class="cpu-logical-chip"
                                          classList={{ selected: isSelected(selected(), "logical", String(logical.id)), unavailable: !logical.affinitySelectable }}
                                          type="button"
                                          disabled={!logical.affinitySelectable}
                                          aria-pressed={isSelected(selected(), "logical", String(logical.id))}
                                          aria-label={uiText.cpuTopology.logicalProcessorLabel(logical.id, formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData()))}
                                          title={uiText.cpuTopology.logicalProcessorTitle(logical.processorGroup, logical.groupRelativeIndex, formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData()))}
                                          onClick={() => toggleSelection(setSelected, "logical", String(logical.id))}
                                        >
                                          <span>{uiText.cpuTopology.logicalProcessor(logical.id)}</span>
                                          <small>{formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData())}</small>
                                        </button>
                                      );
                                    }}
                                  </For>
                                </div>
                                <CpuProcessDurationList processes={runtime().topProcesses} />
                              </article>
                            );
                          }}
                      />
                    </article>
                  )}
                </For>
              }>
                <article class="cpu-ring-view">
                  <div class="cpu-ring-header">
                    <button
                      class="cpu-ring-summary"
                      type="button"
                      disabled={!model().ccds[0]}
                      aria-pressed={Boolean(model().ccds[0] && isSelected(selected(), "ccd", model().ccds[0].id))}
                      onClick={() => toggleSelection(setSelected, "ccd", model().ccds[0]?.id ?? "")}
                    >
                      <span>{uiText.cpuTopology.ringBus}</span>
                      <small>{uiText.cpuTopology.coreCount(model().physicalCoreCount)}</small>
                    </button>
                  </div>
                  <div class="cpu-ring-stage">
                    <div class="cpu-ring-bus" aria-hidden="true" />
                    <CpuRingCacheLayer model={model()} />
                    <For each={model().physicalCores}>
                      {(core, index) => {
                        const runtime = () => coreRuntimeDuration(residencyDurations(), core.id);
                        return (
                          <article
                            class="cpu-ring-core"
                            classList={{
                              selected: isSelected(selected(), "core", core.id),
                              high: core.performanceScore >= 90,
                              medium: core.performanceScore < 90 && core.performanceScore >= 60,
                              low: core.performanceScore < 60
                            }}
                            style={ringCorePosition(index(), model().physicalCores.length)}
                          >
                            <CpuRingPrivateCacheShell core={core}>
                              <CpuCoreSelectionButton
                                core={core}
                                executionTimeMilliseconds={runtime().executionTimeMilliseconds}
                                residencyAvailable={residencyHasData()}
                                selected={isSelected(selected(), "core", core.id)}
                                variant="ring"
                                onToggle={() => toggleSelection(setSelected, "core", core.id)}
                              />
                              <Show when={editing()}>
                                <input
                                  class="cpu-score-input"
                                  type="number"
                                  min="0"
                                  step="any"
                                  aria-label={uiText.cpuTopology.setCoreScore(core.label)}
                                  value={scoreDraft()[core.index] ?? formatScore(core.performanceScore)}
                                  onInput={(event) => updateScoreDraft(core.index, event.currentTarget.value)}
                                />
                              </Show>
                              <div class="cpu-ring-logical-list">
                                <For each={logicalForCore(model(), core)}>
                                  {(logical) => {
                                    const logicalRuntime = () => logicalRuntimeDuration(residencyDurations(), logical.id);
                                    return (
                                      <button
                                        class="cpu-ring-logical-chip"
                                        classList={{ selected: isSelected(selected(), "logical", String(logical.id)), unavailable: !logical.affinitySelectable }}
                                        type="button"
                                        disabled={!logical.affinitySelectable}
                                        aria-pressed={isSelected(selected(), "logical", String(logical.id))}
                                        aria-label={uiText.cpuTopology.logicalProcessorLabel(logical.id, formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData()))}
                                        title={uiText.cpuTopology.logicalProcessorTitle(logical.processorGroup, logical.groupRelativeIndex, formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData()))}
                                        onClick={() => toggleSelection(setSelected, "logical", String(logical.id))}
                                      >
                                        <span>{logical.id}</span>
                                        <small>{formatRuntimeDuration(logicalRuntime().executionTimeMilliseconds, residencyHasData())}</small>
                                      </button>
                                    );
                                  }}
                                </For>
                              </div>
                              <CpuProcessDurationList processes={runtime().topProcesses} compact />
                            </CpuRingPrivateCacheShell>
                          </article>
                        );
                      }}
                    </For>
                  </div>
                </article>
              </Show>
            </div>

            <Show when={selected().length > 0}>
              <div class="cpu-selection-summary">
                <span>{uiText.cpuTopology.selectedCount(selected().length)}</span>
                <span>{uiText.cpuTopology.logicalProcessors}：{selectedLogicalIds().length > 0 ? selectedLogicalIds().join(", ") : "--"}</span>
                <button class="secondary" type="button" onClick={() => setSelected([])}>
                  {uiText.cpuTopology.clear}
                </button>
              </div>
            </Show>
            </>
          )}
        </Show>

        <Show when={snapshot()}>
          {(model) => (
            <CpuTopologyDetailsDialog
              open={detailsOpen()}
              model={model()}
              residency={residencyDurations()}
              residencyAvailable={residencyHasData()}
              bindings={exclusiveBindingsSnapshot()?.bindings ?? []}
              bindingsLoading={!sourceCanRender(exclusiveBindingsSource.snapshot())}
              selectedCoreId={detailsCoreId()}
              onSelectCore={setDetailsCoreId}
              onOpenSoftwareSettings={props.onOpenSoftwareSettings}
              onClose={() => setDetailsOpen(false)}
            />
          )}
        </Show>
    </section>
  );

  function openDetails(model: CpuTopologySnapshot) {
    setDetailsCoreId(resolveDetailCoreId(model, selected()) ?? model.physicalCores[0]?.id ?? null);
    setDetailsOpen(true);
  }

  function startEditing(model: CpuTopologySnapshot) {
    if (!props.runtimeEffectsEnabled) {
      return;
    }
    setCommandError(null);
    setScoreDraft(createScoreDraft(model));
    setEditing(true);
  }

  function cancelEditing(model: CpuTopologySnapshot) {
    setScoreDraft(createScoreDraft(model));
    setEditing(false);
  }

  function updateScoreDraft(
    coreIndex: number,
    value: string) {
    setScoreDraft((current) => ({ ...current, [coreIndex]: value }));
  }

  async function saveScores(model: CpuTopologySnapshot) {
    if (!props.runtimeEffectsEnabled || saving()) {
      return;
    }
    setSaving(true);
    setCommandError(null);
    try {
      await saveCpuCorePerformanceOverrides({
        cpuName: model.cpuName,
        scores: model.physicalCores.map((core) => ({
          coreIndex: core.index,
          performanceScore: normalizeScore(scoreDraft()[core.index], core.performanceScore)
        }))
      });
      setEditing(false);
    } catch (error) {
      setCommandError(userFacingErrorMessage(error, uiText.apiError.saveCpuCoreScoreFailed));
    } finally {
      setSaving(false);
    }
  }

  async function resetScores(model: CpuTopologySnapshot) {
    if (!props.runtimeEffectsEnabled || saving()) {
      return;
    }
    setSaving(true);
    setCommandError(null);
    try {
      await resetCpuCorePerformanceOverrides(model.cpuName);
      setEditing(false);
    } catch (error) {
      setCommandError(userFacingErrorMessage(error, uiText.apiError.resetCpuCoreScoreFailed));
    } finally {
      setSaving(false);
    }
  }

}

function CpuCoreSelectionButton(props: {
  core: CpuPhysicalCoreModel;
  executionTimeMilliseconds: number;
  residencyAvailable: boolean;
  selected: boolean;
  variant: "grid" | "ring";
  onToggle: () => void;
}) {
  const usage = () => formatUsage(props.core.usagePercent);
  const performance = () => formatScore(props.core.performanceScore);
  const executionTime = () => formatRuntimeDuration(
    props.executionTimeMilliseconds,
    props.residencyAvailable);
  return (
    <button
      class={props.variant === "ring" ? "cpu-ring-core-main" : "cpu-core-main"}
      type="button"
      aria-pressed={props.selected}
      aria-label={uiText.cpuTopology.coreAriaLabel(props.core.label, usage(), performance(), executionTime())}
      onClick={props.onToggle}
    >
      <span>{props.core.label}</span>
      <strong>{usage()}</strong>
      <small>{uiText.cpuTopology.corePerformance(performance())}</small>
      <small>{uiText.cpuTopology.coreExecutionTime(executionTime())}</small>
      <Show when={props.variant === "grid"}>
        <span class="cpu-core-meter" aria-hidden="true">
          <span style={{ width: `${clampPercent(props.core.usagePercent ?? 0)}%` }} />
        </span>
      </Show>
    </button>
  );
}

function renderableSourceData<T>(source: SourceSnapshot<T>): T | undefined {
  return sourceCanRender(source) ? source.data ?? undefined : undefined;
}

function CpuTopologyDetailsDialog(props: {
  open: boolean;
  model: CpuTopologySnapshot;
  residency: CpuResidencyDurations;
  residencyAvailable: boolean;
  bindings: CpuExclusiveBinding[];
  bindingsLoading: boolean;
  selectedCoreId: string | null;
  onSelectCore: (coreId: string) => void;
  onOpenSoftwareSettings: (softwareId: string, softwareName: string) => void;
  onClose: () => void;
}) {
  const core = () =>
    props.model.physicalCores.find((item) => item.id === props.selectedCoreId)
    ?? props.model.physicalCores[0];
  const ccd = () => props.model.ccds.find((item) => item.id === core()?.ccdId);
  const logical = () => core()
    ? logicalForCore(props.model, core()!)
    : [];
  const runtime = () => core()
    ? coreRuntimeDuration(props.residency, core()!.id)
    : emptyRuntimeDuration;
  const bindings = () => core()
    ? props.bindings.filter((binding) =>
        binding.expandedExclusivePhysicalCoreIds.includes(core()!.id)
        || binding.expandedLockedPhysicalCoreIds.includes(core()!.id))
    : [];

  return (
    <UserDetailsDialog
      open={props.open}
      title={uiText.cpuTopology.coreDetailTitle}
      summary={uiText.cpuTopology.coreDetailSummary}
      className="cpu-core-details-modal"
      onClose={props.onClose}
    >
      <div class="cpu-core-details-layout">
        <nav class="cpu-core-details-list" aria-label={uiText.cpuTopology.physicalCoreList}>
          <For each={props.model.physicalCores}>
            {(item) => (
              <button
                type="button"
                class="cpu-core-details-option"
                classList={{ active: item.id === core()?.id }}
                aria-pressed={item.id === core()?.id}
                onClick={() => props.onSelectCore(item.id)}
              >
                <span>{item.label}</span>
                <small>{formatUsage(item.usagePercent)}</small>
              </button>
            )}
          </For>
        </nav>
        <div class="cpu-core-details-content">
          <Show when={core()}>
            {(current) => (
              <>
                <section class="user-details-section">
                  <h3>{current().label}</h3>
                  <dl>
                    <div class="user-details-row"><dt>CCD</dt><dd>{ccd()?.label ?? current().ccdId}</dd></div>
                    <div class="user-details-row"><dt>{uiText.cpuTopology.usage}</dt><dd>{formatUsage(current().usagePercent)}</dd></div>
                    <div class="user-details-row"><dt>{uiText.cpuTopology.performanceScore}</dt><dd>{formatScore(current().performanceScore)}</dd></div>
                    <div class="user-details-row"><dt>{uiText.cpuTopology.logicalProcessors}</dt><dd>{logical().map((item) => item.id).join(", ") || "--"}</dd></div>
                    <div class="user-details-row"><dt>{uiText.cpuTopology.cache}</dt><dd>{formatCoreCacheLevels(current().cacheLevels)}</dd></div>
                    <div class="user-details-row">
                      <dt>{uiText.cpuTopology.executionTime}</dt>
                      <dd>{formatRuntimeDuration(runtime().executionTimeMilliseconds, props.residencyAvailable)}</dd>
                    </div>
                  </dl>
                  <CpuProcessDurationList processes={runtime().topProcesses} />
                </section>
                <section class="user-details-section">
                  <h3>{uiText.cpuTopology.exclusiveBindings}</h3>
                  <Show
                    when={!props.bindingsLoading}
                    fallback={<p class="user-details-summary">{uiText.cpuTopology.loadingSoftwarePolicy}</p>}
                  >
                    <Show
                      when={bindings().length > 0}
                      fallback={<p class="user-details-summary">{uiText.cpuTopology.noBindings}</p>}
                    >
                      <div class="cpu-exclusive-binding-list">
                        <For each={bindings()}>
                          {(binding) => (
                            <article class="cpu-exclusive-binding-item">
                              <div>
                                <strong>{binding.softwareName}</strong>
                                <span>{uiText.cpuTopology.exclusiveClaim(formatRequestedCpuPositions(props.model, binding.requestedExclusivePositionIds))}</span>
                                <span>{uiText.cpuTopology.exclusiveCores(formatPhysicalCoreIds(props.model, binding.expandedExclusivePhysicalCoreIds))}</span>
                                <Show when={binding.requestedLockedPositionIds.length > 0}>
                                  <span>{uiText.cpuTopology.lockedClaim(formatRequestedCpuPositions(props.model, binding.requestedLockedPositionIds))}</span>
                                  <span>{uiText.cpuTopology.lockedCores(formatPhysicalCoreIds(props.model, binding.expandedLockedPhysicalCoreIds))}</span>
                                </Show>
                              </div>
                              <button
                                type="button"
                                class="secondary"
                                onClick={() => props.onOpenSoftwareSettings(binding.softwareId, binding.softwareName)}
                              >
                                {uiText.cpuTopology.openSoftwareSettings}
                              </button>
                            </article>
                          )}
                        </For>
                      </div>
                    </Show>
                  </Show>
                </section>
              </>
            )}
          </Show>
        </div>
      </div>
    </UserDetailsDialog>
  );
}

function resolveDetailCoreId(
  model: CpuTopologySnapshot,
  selection: CpuSelection[]
): string | null {
  for (const item of [...selection].reverse()) {
    if (item.kind === "core" && model.physicalCores.some((core) => core.id === item.id)) {
      return item.id;
    }
    if (item.kind === "logical") {
      const logical = model.logicalProcessors.find((processor) => String(processor.id) === item.id);
      if (logical) {
        return logical.physicalCoreId;
      }
    }
    if (item.kind === "ccd") {
      const ccd = model.ccds.find((candidate) => candidate.id === item.id);
      const core = ccd && model.physicalCores.find((candidate) => ccd.physicalCoreIndexes.includes(candidate.index));
      if (core) {
        return core.id;
      }
    }
  }
  return null;
}

function formatCoreCacheLevels(levels: CpuCoreCacheLevelModel[]) {
  return levels.length > 0
    ? levels
        .map((level) => `L${level.level} ${level.sizeKb === null ? uiText.cpuTopology.cacheUnknownSize : formatCacheSize(level.sizeKb)}`)
        .join(" · ")
    : "--";
}

function formatRequestedCpuPositions(model: CpuTopologySnapshot, positionIds: string[]) {
  if (positionIds.length === 0) {
    return "--";
  }
  return positionIds
    .map((id) =>
      model.ccds.find((ccd) => ccd.id.toLowerCase() === id.toLowerCase())?.label
      ?? model.physicalCores.find((core) => core.id.toLowerCase() === id.toLowerCase())?.label
      ?? id)
    .join(", ");
}

function formatPhysicalCoreIds(model: CpuTopologySnapshot, coreIds: string[]) {
  if (coreIds.length === 0) {
    return "--";
  }
  return coreIds
    .map((id) => model.physicalCores.find((core) => core.id === id)?.label ?? id)
    .join(", ");
}

interface CpuCacheGroup {
  key: string;
  level: number;
  sizeKb: number | null;
  logicalProcessorIds: number[];
  coreIds: string[];
  coreIndexes: number[];
  scope: string;
}

interface CpuCachePartition {
  group?: CpuCacheGroup;
  cores: CpuPhysicalCoreModel[];
}

function CpuCacheHierarchy(props: { cores: CpuPhysicalCoreModel[]; renderCore: (core: CpuPhysicalCoreModel) => JSX.Element }) {
  return (
    <div class="cpu-cache-hierarchy">
      <For each={partitionByCacheLevel(props.cores, 3)}>
        {(l3Partition) => (
          <CpuCacheBox group={l3Partition.group} level={3}>
            <For each={partitionByCacheLevel(l3Partition.cores, 2)}>
              {(l2Partition) => (
                <CpuCacheBox group={l2Partition.group} level={2}>
                  <For each={partitionByCacheLevel(l2Partition.cores, 1)}>
                    {(l1Partition) => (
                      <CpuCacheBox group={l1Partition.group} level={1}>
                        <For each={l1Partition.cores}>
                          {(core) => props.renderCore(core)}
                        </For>
                      </CpuCacheBox>
                    )}
                  </For>
                </CpuCacheBox>
              )}
            </For>
          </CpuCacheBox>
        )}
      </For>
    </div>
  );
}

function CpuCacheBox(props: { group?: CpuCacheGroup; level: number; variant?: "ring-private"; children: JSX.Element }) {
  if (!props.group) {
    return <div class="cpu-cache-pass">{props.children}</div>;
  }

  return (
    <section
      class={`cpu-cache-box level-${props.level}`}
      classList={{
        unknown: props.group.sizeKb === null,
        "ring-private": props.variant === "ring-private"
      }}
      title={formatCacheGroupTitle(props.group)}
    >
      <span class="cpu-cache-label">{formatCacheGroupLabel(props.group)}</span>
      {props.children}
    </section>
  );
}

function CpuRingPrivateCacheShell(props: { core: CpuPhysicalCoreModel; children: JSX.Element }) {
  const l1Group = () => cacheGroupForSingleCore(props.core, 1, true);
  const l2Group = () => cacheGroupForSingleCore(props.core, 2, false);
  return (
    <CpuCacheBox group={l2Group()} level={2} variant="ring-private">
      <CpuCacheBox group={l1Group()} level={1} variant="ring-private">
        {props.children}
      </CpuCacheBox>
    </CpuCacheBox>
  );
}

function CpuRingCacheLayer(props: { model: CpuTopologySnapshot }) {
  const groups = () => ringSharedCacheGroups(props.model);
  return (
    <div class="cpu-ring-cache-layer" aria-hidden="true">
      <For each={groups()}>
        {(group) => (
          <div
            class={`cpu-ring-cache-region level-${group.level}`}
            style={ringCacheRegionStyle(group, props.model.physicalCores.length)}
            title={formatCacheGroupTitle(group)}
          >
            <span>{formatCacheGroupLabel(group)}</span>
          </div>
        )}
      </For>
    </div>
  );
}

function CpuProcessDurationList(props: { processes: CpuProcessRuntimeDuration[]; compact?: boolean }) {
  return (
    <Show when={props.processes.length > 0}>
      <div class="cpu-process-score-list" classList={{ compact: props.compact === true }}>
        <For each={props.processes.slice(0, props.compact ? 2 : 4)}>
          {(process) => (
            <span title={`${process.processName} · PID ${process.processId}`}>
              <small>{process.processName}</small>
              <strong>{formatDuration(process.executionTimeMilliseconds)}</strong>
            </span>
          )}
        </For>
      </div>
    </Show>
  );
}

function buildCpuResidencyDurations(snapshot: CpuCoreResidencySnapshot | undefined): CpuResidencyDurations {
  const cores = new Map<string, MutableCpuRuntimeDuration>();
  const logicalProcessors = new Map<number, MutableCpuRuntimeDuration>();
  for (const process of snapshot?.processes ?? []) {
    for (const core of process.physicalCores ?? []) {
      const executionTimeMilliseconds = sanitizeDuration(core.executionTimeMilliseconds);
      const coreDuration = mutableCoreDuration(cores, core.physicalCoreId);
      coreDuration.executionTimeMilliseconds += executionTimeMilliseconds;
      coreDuration.topProcesses.push({
        key: `${process.processInstanceId}:${core.physicalCoreId}`,
        processId: process.processId,
        processName: process.processName,
        executionTimeMilliseconds
      });
    }

    for (const logical of process.logicalProcessors ?? []) {
      mutableLogicalDuration(logicalProcessors, logical.logicalProcessorId)
        .executionTimeMilliseconds += sanitizeDuration(logical.executionTimeMilliseconds);
    }
  }

  return {
    hasData: snapshot !== undefined,
    cores: finalizeRuntimeDurationMap(cores),
    logicalProcessors: finalizeRuntimeDurationMap(logicalProcessors)
  };
}

interface MutableCpuRuntimeDuration {
  executionTimeMilliseconds: number;
  topProcesses: CpuProcessRuntimeDuration[];
}

function mutableCoreDuration(
  scores: Map<string, MutableCpuRuntimeDuration>,
  coreId: string) {
  let score = scores.get(coreId);
  if (!score) {
    score = createMutableRuntimeDuration();
    scores.set(coreId, score);
  }

  return score;
}

function mutableLogicalDuration(
  scores: Map<number, MutableCpuRuntimeDuration>,
  logicalProcessorId: number) {
  let score = scores.get(logicalProcessorId);
  if (!score) {
    score = createMutableRuntimeDuration();
    scores.set(logicalProcessorId, score);
  }

  return score;
}

function createMutableRuntimeDuration(): MutableCpuRuntimeDuration {
  return {
    executionTimeMilliseconds: 0,
    topProcesses: []
  };
}

function finalizeRuntimeDurationMap<TKey>(scores: Map<TKey, MutableCpuRuntimeDuration>) {
  return new Map([...scores.entries()].map(([key, value]) => [key, {
    executionTimeMilliseconds: value.executionTimeMilliseconds,
    topProcesses: value.topProcesses
      .sort((left, right) => right.executionTimeMilliseconds - left.executionTimeMilliseconds || left.processName.localeCompare(right.processName) || left.processId - right.processId)
      .slice(0, 6)
  } satisfies CpuRuntimeDuration]));
}

function coreRuntimeDuration(
  scores: CpuResidencyDurations,
  coreId: string) {
  return scores.cores.get(coreId) ?? emptyRuntimeDuration;
}

function logicalRuntimeDuration(
  scores: CpuResidencyDurations,
  logicalProcessorId: number) {
  return scores.logicalProcessors.get(logicalProcessorId) ?? emptyRuntimeDuration;
}

function coresForCcd(
  model: CpuTopologySnapshot,
  ccd: CpuCcdModel) {
  const coreIds = new Set(ccd.physicalCoreIndexes);
  return model.physicalCores.filter((core) => coreIds.has(core.index));
}

function logicalForCore(
  model: CpuTopologySnapshot,
  core: CpuPhysicalCoreModel) {
  const logicalIds = new Set(core.logicalProcessorIds);
  return model.logicalProcessors.filter((logical) => logicalIds.has(logical.id));
}

function toggleSelection(
  setSelected: (update: (items: CpuSelection[]) => CpuSelection[]) => void,
  kind: CpuSelectionKind,
  id: string) {
  setSelected((items) => {
    const exists = items.some((item) => item.kind === kind && item.id === id);
    return exists
      ? items.filter((item) => item.kind !== kind || item.id !== id)
      : [...items, { kind, id }];
  });
}

function isSelected(
  selected: CpuSelection[],
  kind: CpuSelectionKind,
  id: string) {
  return selected.some((item) => item.kind === kind && item.id === id);
}

function selectionStillExists(
  model: CpuTopologySnapshot,
  item: CpuSelection) {
  if (item.kind === "ccd") {
    return model.ccds.some((ccd) => ccd.id === item.id);
  }

  if (item.kind === "core") {
    return model.physicalCores.some((core) => core.id === item.id);
  }

  return model.logicalProcessors.some((logical) => String(logical.id) === item.id);
}

function resolveSelectedLogicalIds(
  model: CpuTopologySnapshot | undefined,
  selected: CpuSelection[]) {
  if (!model) {
    return [];
  }

  const result = new Set<number>();
  for (const item of selected) {
    if (item.kind === "logical") {
      const logical = model.logicalProcessors.find((candidate) => String(candidate.id) === item.id);
      if (logical?.affinitySelectable) {
        result.add(logical.id);
      }
      continue;
    }

    if (item.kind === "core") {
      const core = model.physicalCores.find((candidate) => candidate.id === item.id);
      core?.logicalProcessorIds.forEach((id) => {
        const logical = model.logicalProcessors.find((candidate) => candidate.id === id);
        if (logical?.affinitySelectable) {
          result.add(id);
        }
      });
      continue;
    }

    const ccd = model.ccds.find((candidate) => candidate.id === item.id);
    ccd?.logicalProcessorIds.forEach((id) => {
      const logical = model.logicalProcessors.find((candidate) => candidate.id === id);
      if (logical?.affinitySelectable) {
        result.add(id);
      }
    });
  }

  return [...result].sort((left, right) => left - right);
}

function formatUsage(value?: number | null) {
  return typeof value === "number" ? `${value.toFixed(1)}%` : "--";
}

function formatScore(value: number) {
  return Number.isFinite(value) ? String(value) : "0";
}

function ringCorePosition(
  index: number,
  total: number) {
  const point = ringCorePoint(index, total);
  return {
    left: `${point.x}%`,
    top: `${point.y}%`
  };
}

function ringCorePoint(
  index: number,
  total: number) {
  const count = Math.max(1, total);
  const angle = -Math.PI / 2 + (Math.PI * 2 * index) / count;
  const x = 50 + Math.cos(angle) * 42;
  const y = 50 + Math.sin(angle) * 36;
  return {
    x,
    y
  };
}

function clampPercent(value: number) {
  return Math.max(0, Math.min(100, Number.isFinite(value) ? value : 0));
}

function createScoreDraft(model: CpuTopologySnapshot) {
  return Object.fromEntries(model.physicalCores.map((core) => [core.index, formatScore(core.performanceScore)]));
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

function formatCacheSize(sizeKb?: number | null) {
  if (typeof sizeKb !== "number" || !Number.isFinite(sizeKb) || sizeKb <= 0) {
    return "";
  }

  // 系统报的缓存大小以 KiB 计，先还原成字节再交给唯一所有者按内存类换算。
  return formatBytes(sizeKb * 1024, "memory");
}

function partitionByCacheLevel(
  cores: CpuPhysicalCoreModel[],
  level: number): CpuCachePartition[] {
  const groups = buildCacheGroupsForCores(cores, level)
    .filter((group) => shouldRenderCacheGroup(group, level));
  const assignedCoreIds = new Set<string>();
  const partitions: CpuCachePartition[] = [];

  for (const group of groups) {
    const groupedCores = cores.filter((core) => group.coreIds.includes(core.id) && !assignedCoreIds.has(core.id));
    if (groupedCores.length === 0) {
      continue;
    }

    groupedCores.forEach((core) => assignedCoreIds.add(core.id));
    partitions.push({ group, cores: groupedCores });
  }

  const remaining = cores.filter((core) => !assignedCoreIds.has(core.id));
  if (level === 1) {
    for (const core of remaining) {
      partitions.push({
        group: cacheGroupForSingleCore(core, 1, true),
        cores: [core]
      });
    }
  } else if (remaining.length > 0) {
    partitions.push({ cores: remaining });
  }

  return partitions.sort((left, right) => firstCoreIndex(left) - firstCoreIndex(right));
}

function buildCacheGroupsForCores(
  cores: CpuPhysicalCoreModel[],
  level: number) {
  const groups = new Map<string, CpuCacheGroup>();
  for (const core of cores) {
    const cache = cacheForLevel(core, level);
    if (!isKnownCache(cache)) {
      continue;
    }

    const logicalProcessorIds = normalizeLogicalProcessorIds(cache.logicalProcessorIds.length > 0
      ? cache.logicalProcessorIds
      : core.logicalProcessorIds);
    const sizeKb = normalizeCacheSize(cache.sizeKb);
    const key = cacheGroupKey(level, sizeKb, logicalProcessorIds);
    let group = groups.get(key);
    if (!group) {
      group = {
        key,
        level,
        sizeKb,
        logicalProcessorIds,
        coreIds: [],
        coreIndexes: [],
        scope: normalizeCacheScope(cache.scope)
      };
      groups.set(key, group);
    }

    if (!group.coreIds.includes(core.id)) {
      group.coreIds.push(core.id);
      group.coreIndexes.push(core.index);
    }
  }

  return [...groups.values()]
    .map((group) => ({
      ...group,
      coreIndexes: [...group.coreIndexes].sort((left, right) => left - right)
    }))
    .sort(compareCacheGroups);
}

function shouldRenderCacheGroup(
  group: CpuCacheGroup,
  level: number) {
  if (level === 1) {
    return true;
  }

  return group.sizeKb !== null || group.logicalProcessorIds.length > 0 || group.coreIds.length > 1;
}

function cacheGroupForSingleCore(
  core: CpuPhysicalCoreModel,
  level: number,
  allowUnknownFallback: boolean): CpuCacheGroup | undefined {
  const cache = cacheForLevel(core, level);
  if (!isKnownCache(cache)) {
    return allowUnknownFallback ? fallbackCoreCacheGroup(core, level) : undefined;
  }

  const logicalProcessorIds = normalizeLogicalProcessorIds(cache.logicalProcessorIds.length > 0
    ? cache.logicalProcessorIds
    : core.logicalProcessorIds);
  const isSharedWithOtherCores = logicalProcessorIds.some((logicalId) => !core.logicalProcessorIds.includes(logicalId))
    || normalizeCacheScope(cache.scope) === "shared";
  if (!allowUnknownFallback && isSharedWithOtherCores) {
    return undefined;
  }

  const sizeKb = normalizeCacheSize(cache.sizeKb);
  return {
    key: cacheGroupKey(level, sizeKb, logicalProcessorIds),
    level,
    sizeKb,
    logicalProcessorIds,
    coreIds: [core.id],
    coreIndexes: [core.index],
    scope: normalizeCacheScope(cache.scope)
  };
}

function fallbackCoreCacheGroup(
  core: CpuPhysicalCoreModel,
  level: number): CpuCacheGroup {
  return {
    key: `fallback:${level}:${core.id}`,
    level,
    sizeKb: null,
    logicalProcessorIds: normalizeLogicalProcessorIds(core.logicalProcessorIds),
    coreIds: [core.id],
    coreIndexes: [core.index],
    scope: "unknown"
  };
}

function ringSharedCacheGroups(model: CpuTopologySnapshot) {
  return [3, 2]
    .flatMap((level) => buildCacheGroupsForCores(model.physicalCores, level))
    .filter((group) => group.coreIds.length > 1)
    .sort((left, right) => right.level - left.level || compareCacheGroups(left, right));
}

function ringCacheRegionStyle(
  group: CpuCacheGroup,
  totalCoreCount: number) {
  const count = Math.max(1, totalCoreCount);
  if (group.coreIndexes.length >= count && count > 2) {
    return {
      left: "4%",
      top: "5%",
      width: "92%",
      height: "90%"
    };
  }

  const points = group.coreIndexes.map((index) => ringCorePoint(index, count));
  const padding = group.level === 3 ? 14 : 10;
  const minWidth = group.level === 3 ? 24 : 18;
  const minHeight = group.level === 3 ? 20 : 16;
  const minX = Math.min(...points.map((point) => point.x));
  const maxX = Math.max(...points.map((point) => point.x));
  const minY = Math.min(...points.map((point) => point.y));
  const maxY = Math.max(...points.map((point) => point.y));
  return rectanglePercentStyle(minX, maxX, minY, maxY, padding, minWidth, minHeight);
}

function rectanglePercentStyle(
  minX: number,
  maxX: number,
  minY: number,
  maxY: number,
  padding: number,
  minWidth: number,
  minHeight: number) {
  const centerX = (minX + maxX) / 2;
  const centerY = (minY + maxY) / 2;
  const width = Math.max(minWidth, maxX - minX + padding * 2);
  const height = Math.max(minHeight, maxY - minY + padding * 2);
  const left = clampRange(centerX - width / 2, 2, 98 - width);
  const top = clampRange(centerY - height / 2, 2, 98 - height);
  return {
    left: `${left}%`,
    top: `${top}%`,
    width: `${Math.min(width, 96)}%`,
    height: `${Math.min(height, 96)}%`
  };
}

function cacheForLevel(
  core: CpuPhysicalCoreModel,
  level: number): CpuCoreCacheLevelModel | undefined {
  return core.cacheLevels?.find((cache) => cache.level === level);
}

function isKnownCache(cache?: CpuCoreCacheLevelModel): cache is CpuCoreCacheLevelModel {
  return cache !== undefined
    && (normalizeCacheSize(cache.sizeKb) !== null
      || (cache.logicalProcessorIds?.length ?? 0) > 0
      || cache.scope === "core"
      || cache.scope === "shared");
}

function normalizeCacheSize(sizeKb?: number | null) {
  return typeof sizeKb === "number" && Number.isFinite(sizeKb) && sizeKb > 0
    ? sizeKb
    : null;
}

function normalizeLogicalProcessorIds(logicalProcessorIds: number[]) {
  return [...new Set(logicalProcessorIds.filter((id) => Number.isFinite(id)))]
    .sort((left, right) => left - right);
}

function normalizeCacheScope(scope: string | undefined) {
  return scope === "core" || scope === "shared" ? scope : "unknown";
}

function cacheGroupKey(
  level: number,
  sizeKb: number | null,
  logicalProcessorIds: number[]) {
  return `${level}:${sizeKb ?? "unknown"}:${logicalProcessorIds.join(",")}`;
}

function compareCacheGroups(
  left: CpuCacheGroup,
  right: CpuCacheGroup) {
  return Math.min(...left.coreIndexes) - Math.min(...right.coreIndexes)
    || right.coreIndexes.length - left.coreIndexes.length
    || left.level - right.level
    || (left.sizeKb ?? 0) - (right.sizeKb ?? 0);
}

function firstCoreIndex(partition: CpuCachePartition) {
  return Math.min(...partition.cores.map((core) => core.index));
}

function formatCacheGroupLabel(group: CpuCacheGroup) {
  const size = formatCacheSize(group.sizeKb);
  return size ? uiText.cpuTopology.cacheLevel(String(group.level), size) : uiText.cpuTopology.cacheLevelShort(String(group.level));
}

function formatCacheGroupTitle(group: CpuCacheGroup) {
  const size = formatCacheSize(group.sizeKb) || uiText.cpuTopology.cacheCapacityUnknown;
  const scope = group.coreIds.length > 1 ? uiText.cpuTopology.cacheSharedBy(group.coreIds.length) : uiText.cpuTopology.cacheSingleCore;
  const logicalIds = group.logicalProcessorIds.length > 0 ? group.logicalProcessorIds.join(", ") : "--";
  return uiText.cpuTopology.cacheDetail(String(group.level), size, scope, logicalIds);
}

function clampRange(
  value: number,
  min: number,
  max: number) {
  if (max < min) {
    return min;
  }

  return Math.max(min, Math.min(max, value));
}

function formatRuntimeDuration(
  value: number,
  hasData: boolean) {
  return hasData ? formatDuration(value) : "--";
}

function formatDuration(value: number) {
  const milliseconds = sanitizeDuration(value);
  if (milliseconds >= 1000) {
    return `${(milliseconds / 1000).toFixed(milliseconds >= 10_000 ? 1 : 2)} s`;
  }

  if (milliseconds >= 100) {
    return `${Math.round(milliseconds)} ms`;
  }

  if (milliseconds >= 10) {
    return `${milliseconds.toFixed(1)} ms`;
  }

  return `${milliseconds.toFixed(2)} ms`;
}

function sanitizeDuration(value?: number | null) {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? value
    : 0;
}
