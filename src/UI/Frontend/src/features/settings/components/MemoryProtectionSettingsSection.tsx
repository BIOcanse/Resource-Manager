import { createSignal, For, Index, Show } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { usePushSource } from "../../../frontendRuntime/push/usePushSource.ts";
import type { ConfirmDialogRequest } from "../../../ui/patterns/ConfirmDialog.tsx";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import {
  createNormalRunningPoint, deleteNormalRunningPoint, rebuildMemoryProtectionReserve, selectNormalRunningPoint
} from "../../../data/memoryProtection/memoryProtectionApi.ts";
import type {
  HostMemoryProtectionActionMode,
  HostMemoryProtectionLiveStatus,
  HostMemoryProtectionSettings,
  HostMemoryReserveSettings,
  NormalRunningPointSummary
} from "../../../data/memoryProtection/memoryProtectionTypes.ts";
import { formatBytePair, formatBytes } from "../../../presentation/byteUnits.ts";
import { formatInterfaceNumber, formatTrimmedDigits } from "../../../presentation/interfaceNumbers.ts";
import { userFacingDateTime, userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { ContentState } from "../../../ui/patterns/ContentState.tsx";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";
import { StableLabel } from "../../../ui/primitives/StableLabel.tsx";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { uiText } from "../../../text.ts";
import {
  bytesAsSent,
  exactByteCount,
  memoryProtectionDecisionText,
  memoryProtectionExclusionText,
  memoryProtectionReasonText
} from "../memoryProtectionPresentation.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { MemoryProtectionActionList, MemoryProtectionFact as Fact } from "../../memoryProtection/MemoryProtectionRecordList.tsx";

interface MemoryProtectionSettingsSectionProps {
  /** Edit memory-protection values through the shared settings draft. */
  settings: HostMemoryProtectionSettings | null | undefined;
  /** Compare against saved settings to detect draft changes. */
  committed: HostMemoryProtectionSettings | null | undefined;
  runtimeFailureCode: string | null;
  onChange: (settings: HostMemoryProtectionSettings) => void;
  /** Confirm irreversible point deletion, which can disable protection when the active point is removed. */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}

type ActionMessage = { kind: "status" | "alert"; text: () => string };
/** Serialize point creation, reserve rebuilding, activation, and deletion. */
type Busy = "point" | "rebuild" | `select:${string}` | `delete:${string}`;

// Editable fields use the shared settings draft.
// A single subscription owner supplies status, records, and saved points.
// Display its current value without rereading or merging frames.
// The backend owns thresholds, totals, point lists, and active selection.
export function MemoryProtectionSettingsSection(props: MemoryProtectionSettingsSectionProps) {
  const copy = () => uiText.memoryProtection;
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  // Visibility owns subscription lifetime; replay supplies the current value on return.
  const workId = frontendWorkIds.settingsMemoryProtection;
  useFrontendVisibilityDemand(workId, [workId]);
  const protection = usePushSource(runtime.sources.memoryProtection, () => ({ active: work.isNeeded(workId) }));
  const view = () => protection.snapshot().data;
  const statusData = () => view()?.status ?? null;
  const unavailable = () => protection.snapshot().status === "error" || protection.snapshot().status === "unavailable";
  const pointList = () => view()?.normalRunningPoints ?? null;
  const [busy, setBusy] = createSignal<Busy | null>(null);
  const [pointMessage, setPointMessage] = createSignal<ActionMessage | null>(null);
  const [savedPointMessage, setSavedPointMessage] = createSignal<ActionMessage | null>(null);
  const [rebuildMessage, setRebuildMessage] = createSignal<ActionMessage | null>(null);

  const unsaved = () => JSON.stringify(props.settings ?? null) !== JSON.stringify(props.committed ?? null);
  // Show known protection rejections or rejections of this section's unsaved changes.
  // Global type rejections carry a path and belong in the page save status.
  const rejection = () => {
    const code = props.runtimeFailureCode;
    if (!code || code === "invalid-settings-value-type") return null;
    return memoryProtectionReasonText(code) !== code || unsaved() ? code : null;
  };
  const update = (patch: Partial<HostMemoryProtectionSettings>) => {
    if (props.settings) props.onChange({ ...props.settings, ...patch });
  };
  const reserves = (): HostMemoryReserveSettings[] => props.settings?.reserves ?? [];
  const updateReserve = (index: number, patch: Partial<HostMemoryReserveSettings>) =>
    update({ reserves: reserves().map((entry, position) => position === index ? { ...entry, ...patch } : entry) });
  const protectedProcesses = (): string[] => props.settings?.protectedProcesses ?? [];

  const createPoint = async () => {
    if (busy()) return;
    setBusy("point");
    setPointMessage(null);
    try {
      const result = await createNormalRunningPoint(runtime.requestClient);
      setPointMessage(result.status === "done"
        ? { kind: "status", text: () => copy().pointCreated }
        : { kind: "alert", text: () => copy().failedBecause(copy().createPointFailed, memoryProtectionReasonText(result.reason)) });
    } catch (error) {
      setPointMessage({ kind: "alert", text: () => userFacingErrorMessage(error, copy().createPointFailed) });
    } finally {
      setBusy(null);
    }
  };
  // Wait for backend publication after activation or deletion; do not mutate the list locally.
  const changeSavedPoint = async (
    next: Busy,
    change: () => Promise<{ status: "done" } | { status: "refused"; reason: string }>,
    done: () => string,
    failed: () => string
  ) => {
    if (busy()) return;
    setBusy(next);
    setSavedPointMessage(null);
    try {
      const result = await change();
      setSavedPointMessage(result.status === "done"
        ? { kind: "status", text: done }
        : { kind: "alert", text: () => copy().failedBecause(failed(), memoryProtectionReasonText(result.reason)) });
    } catch (error) {
      setSavedPointMessage({ kind: "alert", text: () => userFacingErrorMessage(error, failed()) });
    } finally {
      setBusy(null);
    }
  };
  const selectPoint = (point: NormalRunningPointSummary) => changeSavedPoint(`select:${point.id}`,
    () => selectNormalRunningPoint(runtime.requestClient, point.id), () => copy().pointSelected, () => copy().selectPointFailed);
  const deletePoint = async (point: NormalRunningPointSummary, inEffect: boolean) => {
    if (busy()) return;
    const confirmed = await props.confirmDialog({
      title: copy().confirmDeletePoint,
      message: copy().pointItemLabel(userFacingDateTime(point.at)),
      details: [inEffect ? copy().deleteActivePointDetail : copy().deletePointDetail],
      tone: "warning",
      confirmLabel: copy().deletePoint
    });
    if (!confirmed) return;
    await changeSavedPoint(`delete:${point.id}`,
      () => deleteNormalRunningPoint(runtime.requestClient, point.id), () => copy().pointDeleted, () => copy().deletePointFailed);
  };
  const rebuild = async () => {
    if (busy()) return;
    setBusy("rebuild");
    setRebuildMessage(null);
    try {
      const result = await rebuildMemoryProtectionReserve(runtime.requestClient);
      const reason = result.value.reserve.reason;
      setRebuildMessage(result.status === 200
        ? { kind: "status", text: () => copy().rebuildBuilt }
        : { kind: "alert", text: () => reason === null ? copy().reserveNotBuilt : copy().rebuildNotBuilt(memoryProtectionReasonText(reason)) });
    } catch (error) {
      setRebuildMessage({ kind: "alert", text: () => userFacingErrorMessage(error, copy().rebuildFailed) });
    } finally {
      setBusy(null);
    }
  };

  const notProvided = () => copy().valueNotProvided;
  // Overlay the missing-point or unavailable message on the reserved three-value layout.
  const pointSlotCovered = () => Boolean(view()) || unavailable();

  return (
    <div class="settings-section-panel memory-protection-settings" {...frontendVisibilitySurface(workId, [workId])}>
      <p>{copy().intro}</p>
      <Show when={props.settings} fallback={<ContentState kind="unavailable" title={notProvided()} />}>
        {(settings) => (
          <>
            <Show when={unsaved()}><p role="status">{copy().unsavedNote}</p></Show>
            <Show when={rejection()}>
              {(code) => <p role="alert" data-full-content={code()}>{copy().rejected(memoryProtectionReasonText(code()))}</p>}
            </Show>
            {/* Keep populated status geometry while loading and announce progress without adding space. */}
            <Show when={!view()}>
              <Show
                when={unavailable()}
                fallback={<p class="sr-only" role="status">{copy().statusLoading}</p>}
              >
                <ContentState kind="unavailable" title={copy().statusUnavailable} detail={copy().statusUnavailableDetail} />
              </Show>
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().enabledTitle}</strong>
                  <HelpHint label={copy().enabledTitle}>{copy().enabledDescription}</HelpHint>
                </div>
              </div>
              <SwitchControl ariaLabel={copy().enabledTitle} checked={settings().enabled} onChange={(enabled) => update({ enabled })} />
            </div>
            {/* Show the failure explanation without a loading placeholder. */}
            <Show when={statusData() || !unavailable()}>
              <ProtectionState status={statusData()} />
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().thresholdTitle}</strong>
                  <HelpHint label={copy().thresholdTitle}>{copy().thresholdDescription}</HelpHint>
                </div>
                <Show
                  when={statusData()}
                  fallback={
                    <Show when={!unavailable()}>
                      <dl class="memory-protection-facts">
                        <Fact label={copy().triggerBytes}>--</Fact>
                        <Fact label={copy().remainingBytes}>--</Fact>
                        <Fact label={copy().currentCommit}>--</Fact>
                      </dl>
                    </Show>
                  }
                >
                  {(current) => (
                    <dl class="memory-protection-facts">
                      <Fact label={copy().triggerBytes}>
                        {current().triggerBytes === null ? notProvided() : formatBytes(current().triggerBytes, "memory")}
                      </Fact>
                      <Fact label={copy().remainingBytes}>
                        {current().remainingBytes === null ? notProvided() : bytesAsSent(current().remainingBytes!)}
                      </Fact>
                      <Fact label={copy().currentCommit}>
                        {current().currentCommit === null ? notProvided() : copy().commitPair(
                          formatBytePair(current().currentCommit!.currentBytes, current().currentCommit!.limitBytes, "memory"),
                          formatTrimmedDigits(current().currentCommit!.percent, 1))}
                      </Fact>
                    </dl>
                  )}
                </Show>
              </div>
              <NumberField label={copy().thresholdLabel} ariaLabel={copy().thresholdTitle} value={settings().thresholdPercent}
                min={0.1} max={100} step={0.1} onChange={(thresholdPercent) => update({ thresholdPercent })} />
            </div>

            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().checkIntervalTitle}</strong>
                  <HelpHint label={copy().checkIntervalTitle}>{copy().checkIntervalDescription}</HelpHint>
                </div>
              </div>
              <NumberField label={copy().checkIntervalLabel} ariaLabel={copy().checkIntervalTitle} value={settings().checkIntervalSeconds}
                min={1} max={86400} step={0.1} onChange={(checkIntervalSeconds) => update({ checkIntervalSeconds })} />
            </div>

            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().actionModeTitle}</strong>
                  <HelpHint label={copy().actionModeTitle}>{copy().actionModeDescription}</HelpHint>
                </div>
              </div>
              <SegmentedControl<HostMemoryProtectionActionMode>
                value={settings().actionMode}
                options={(["dumpThenTerminate", "terminateOnly"] as const).map((id) => ({ id, ...copy().actionModes[id] }))}
                ariaLabel={copy().actionModeTitle}
                onChange={(actionMode) => update({ actionMode })}
              />
            </div>

            {/* Send byte inputs unchanged and show a readable equivalent without rounding. */}
            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().minimumGrowthTitle}</strong>
                  <HelpHint label={copy().minimumGrowthTitle}>{copy().minimumGrowthDescription}</HelpHint>
                </div>
              </div>
              <div class="memory-protection-byte-control">
                <NumberField label={copy().minimumGrowthLabel} ariaLabel={copy().minimumGrowthTitle}
                  value={settings().minimumProcessGrowthBytes} min={0} max={Number.MAX_SAFE_INTEGER} step={1}
                  onChange={(minimumProcessGrowthBytes) => update({ minimumProcessGrowthBytes })} />
                <span class="memory-protection-size-readout">{formatBytes(settings().minimumProcessGrowthBytes, "memory")}</span>
              </div>
            </div>

            {/* Preserve whitespace and duplicate entries for backend validation. */}
            <div class="settings-row settings-row-block memory-protection-protected">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().protectedTitle}</strong>
                  <HelpHint label={copy().protectedTitle}>{copy().protectedDescription}</HelpHint>
                </div>
              </div>
              <Show when={protectedProcesses().length > 0} fallback={<p class="memory-protection-empty">{copy().protectedEmpty}</p>}>
                <ol class="memory-protection-reserve-editor">
                  <Index each={protectedProcesses()}>
                    {(entry, index) => {
                      const entryLabel = () => copy().protectedEntryLabel(index + 1);
                      return (
                        <li class="memory-protection-protected-row">
                          <label class="settings-number-field">
                            <span>{copy().protectedEntry}</span>
                            <input
                              type="text"
                              aria-label={`${entryLabel()} · ${copy().protectedEntry}`}
                              value={entry()}
                              onChange={(event) => update({ protectedProcesses: protectedProcesses()
                                .map((value, position) => position === index ? event.currentTarget.value : value) })}
                            />
                          </label>
                          <button type="button" class="secondary" aria-label={`${entryLabel()} · ${copy().removeProtected}`}
                            onClick={() => update({ protectedProcesses: protectedProcesses().filter((_, position) => position !== index) })}>
                            {copy().removeProtected}
                          </button>
                        </li>
                      );
                    }}
                  </Index>
                </ol>
              </Show>
              <div class="memory-protection-actions">
                <button type="button" class="secondary" onClick={() => update({ protectedProcesses: [...protectedProcesses(), ""] })}>
                  {copy().addProtected}
                </button>
              </div>
            </div>

            <div class="settings-row">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().pointTitle}</strong>
                  <HelpHint label={copy().pointTitle}>{copy().pointDescription}</HelpHint>
                </div>
                {/* Retain the three-value geometry for loading, missing-point, and unavailable states. */}
                <div class="memory-protection-point-slot">
                  <Show
                    when={view()?.normalRunningPoint}
                    fallback={
                      <>
                        <dl class="memory-protection-facts" classList={{ "slot-hidden": pointSlotCovered() }}
                          aria-hidden={pointSlotCovered() ? "true" : undefined}>
                          <Fact label={copy().pointAt}>--</Fact>
                          <Fact label={copy().pointProcessCount}>--</Fact>
                          <Fact label={copy().pointCommit}>--</Fact>
                        </dl>
                        <Show when={view()} fallback={<Show when={unavailable()}><span role="status">{copy().statusUnavailable}</span></Show>}>
                          <span class="memory-protection-point-none">{copy().pointNone}</span>
                        </Show>
                      </>
                    }
                  >
                    {(current) => (
                      <dl class="memory-protection-facts memory-protection-point">
                        <Fact label={copy().pointAt}>{userFacingDateTime(current().at)}</Fact>
                        <Fact label={copy().pointProcessCount}>{formatInterfaceNumber(current().processCount)}</Fact>
                        <Fact label={copy().pointCommit}>
                          {formatBytePair(current().commit.currentBytes, current().commit.limitBytes, "memory")}
                        </Fact>
                      </dl>
                    )}
                  </Show>
                </div>
                <Show when={pointMessage()}>
                  {(message) => <span role={message().kind}>{message().text()}</span>}
                </Show>
              </div>
              <button type="button" class="secondary" disabled={busy() !== null} onClick={() => void createPoint()}>
                <StableLabel labels={{ idle: copy().createPoint, busy: copy().creatingPoint }} current={busy() === "point" ? "busy" : "idle"} />
              </button>
            </div>

            {/* Preserve backend oldest-first point order and show loading or failure separately. */}
            <div class="settings-row settings-row-block memory-protection-saved-points">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().savedPointsTitle}</strong>
                  <HelpHint label={copy().savedPointsTitle}>{copy().savedPointsDescription}</HelpHint>
                </div>
                <dl class="memory-protection-facts">
                  <Fact label={copy().savedPointsCount}>
                    {pointList() ? `${formatInterfaceNumber(pointList()!.points.length)} / ${formatInterfaceNumber(pointList()!.limit)}` : "--"}
                  </Fact>
                </dl>
              </div>
              <Show
                when={pointList()}
                fallback={
                  <Show when={unavailable()} fallback={<p class="memory-protection-empty" aria-hidden="true">--</p>}>
                    <p class="memory-protection-empty" role="status">{copy().savedPointsUnavailable}</p>
                  </Show>
                }
              >
                {(list) => (
                  <Show when={list().points.length > 0} fallback={<p class="memory-protection-empty">{copy().savedPointsEmpty}</p>}>
                    <ol class="memory-protection-list">
                      <For each={list().points}>
                        {(point) => {
                          const inEffect = () => point.id === list().activeId;
                          const label = () => copy().pointItemLabel(userFacingDateTime(point.at));
                          return (
                            <li class="memory-protection-item memory-protection-saved-point" classList={{ "in-effect": inEffect() }}
                              aria-current={inEffect() ? "true" : undefined}>
                              <dl class="memory-protection-facts">
                                <Fact label={copy().pointAt}>{userFacingDateTime(point.at)}</Fact>
                                <Fact label={copy().pointProcessCount}>{formatInterfaceNumber(point.processCount)}</Fact>
                                <Fact label={copy().pointCommit}>
                                  {formatBytePair(point.commit.currentBytes, point.commit.limitBytes, "memory")}
                                </Fact>
                              </dl>
                              <div class="memory-protection-actions">
                                {/* Reserve the longest activation-label width for active and inactive buttons. */}
                                <button type="button" class="secondary" disabled={inEffect() || busy() !== null}
                                  aria-label={`${label()} · ${inEffect() ? copy().pointInEffect : copy().selectPoint}`}
                                  onClick={() => void selectPoint(point)}>
                                  <StableLabel labels={{ idle: copy().selectPoint, busy: copy().selectingPoint, inEffect: copy().pointInEffect }}
                                    current={inEffect() ? "inEffect" : busy() === `select:${point.id}` ? "busy" : "idle"} />
                                </button>
                                <button type="button" class="secondary" disabled={busy() !== null}
                                  aria-label={`${label()} · ${copy().deletePoint}`}
                                  onClick={() => void deletePoint(point, inEffect())}>
                                  <StableLabel labels={{ idle: copy().deletePoint, busy: copy().deletingPoint }}
                                    current={busy() === `delete:${point.id}` ? "busy" : "idle"} />
                                </button>
                              </div>
                            </li>
                          );
                        }}
                      </For>
                    </ol>
                  </Show>
                )}
              </Show>
              <Show when={savedPointMessage()}>
                {(message) => <span class="memory-protection-message" role={message().kind}>{message().text()}</span>}
              </Show>
            </div>

            <div class="settings-row settings-row-block">
              <div class="settings-row-copy">
                <div class="settings-row-title">
                  <strong>{copy().reserveTitle}</strong>
                  <HelpHint label={copy().reserveTitle}>{copy().reserveDescription}</HelpHint>
                </div>
              </div>
              <Show when={reserves().length > 0} fallback={<p class="memory-protection-empty">{copy().reserveNotConfigured}</p>}>
                <ol class="memory-protection-reserve-editor">
                  <Index each={reserves()}>
                    {(entry, index) => {
                      const entryLabel = () => copy().reserveEntryLabel(index + 1);
                      return (
                        <li class="memory-protection-reserve-row">
                          <label class="settings-number-field memory-protection-location">
                            <span>{copy().reserveLocation}</span>
                            <input
                              type="text"
                              aria-label={`${entryLabel()} · ${copy().reserveLocation}`}
                              value={entry().location}
                              onChange={(event) => updateReserve(index, { location: event.currentTarget.value })}
                            />
                          </label>
                          <NumberField label={copy().reserveSize} ariaLabel={`${entryLabel()} · ${copy().reserveSize}`}
                            value={entry().sizeBytes} min={0} max={Number.MAX_SAFE_INTEGER} step={1}
                            onChange={(sizeBytes) => updateReserve(index, { sizeBytes })} />
                          <span class="memory-protection-size-readout">{formatBytes(entry().sizeBytes, "storage")}</span>
                          <button type="button" class="secondary" aria-label={`${entryLabel()} · ${copy().removeReserve}`}
                            onClick={() => update({ reserves: reserves().filter((_, position) => position !== index) })}>
                            {copy().removeReserve}
                          </button>
                        </li>
                      );
                    }}
                  </Index>
                </ol>
              </Show>
              <div class="memory-protection-actions">
                <button type="button" class="secondary" onClick={() => update({ reserves: [...reserves(), { location: "", sizeBytes: 0 }] })}>
                  {copy().addReserve}
                </button>
              </div>
            </div>

            <Show when={view()}>
              {(currentView) => {
                const current = () => currentView().status;
                return (
                <>
                  <div class="settings-row settings-row-block memory-protection-reserve-status">
                    <div class="settings-row-copy">
                      <strong>{copy().reserveStatusTitle}</strong>
                    </div>
                    <Show when={current().reserve.entries.length > 0}>
                      <ol class="memory-protection-list">
                        <For each={current().reserve.entries}>
                          {(entry) => (
                            <li class="memory-protection-item">
                              <code class="memory-protection-path">{entry.location}</code>
                              <dl class="memory-protection-facts">
                                <Fact label={copy().reserveCluster}>{exactByteCount(entry.clusterBytes)}</Fact>
                                <Fact label={copy().reserveConfigured}>{formatBytes(entry.configuredSizeBytes, "storage")}</Fact>
                                <Fact label={copy().reserveRemaining}>{formatBytes(entry.remainingBytes, "storage")}</Fact>
                                <Fact label={copy().reserveBuildState}>
                                  <BuiltState built={entry.built} reason={entry.reason} />
                                </Fact>
                              </dl>
                            </li>
                          )}
                        </For>
                      </ol>
                    </Show>
                    <dl class="memory-protection-facts memory-protection-totals" aria-label={copy().reserveTotals}>
                      <Fact label={copy().reserveTotals}>{formatBytes(current().reserve.configuredSizeBytes, "storage")}</Fact>
                      <Fact label={copy().reserveRemaining}>{formatBytes(current().reserve.remainingBytes, "storage")}</Fact>
                      <Fact label={copy().reserveBuildState}>
                        <BuiltState built={current().reserve.built} reason={current().reserve.reason} />
                      </Fact>
                    </dl>
                    <div class="memory-protection-actions">
                      <button type="button" class="secondary" disabled={busy() !== null} onClick={() => void rebuild()}>
                        <StableLabel labels={{ idle: copy().rebuild, busy: copy().rebuilding }} current={busy() === "rebuild" ? "busy" : "idle"} />
                      </button>
                      <Show when={rebuildMessage()}>
                        {(message) => <span class="memory-protection-message" role={message().kind}>{message().text()}</span>}
                      </Show>
                    </div>
                  </div>

                  <div class="settings-row settings-row-block memory-protection-records">
                    <div class="settings-row-copy">
                      <div class="settings-row-title">
                        <strong>{copy().recordsTitle}</strong>
                        <HelpHint label={copy().recordsTitle}>{copy().recordsDescription(currentView().recordCapacity)}</HelpHint>
                      </div>
                      <Show when={currentView().unrecordedRecordCount > 0}>
                        <span role="status">{copy().recordsUnrecorded(currentView().unrecordedRecordCount)}</span>
                      </Show>
                    </div>
                    <Show when={currentView().records.length > 0} fallback={<p class="memory-protection-empty">{copy().recordsEmpty}</p>}>
                      <MemoryProtectionActionList records={currentView().records} />
                    </Show>
                  </div>

                  <div class="settings-row settings-row-block memory-protection-exclusions">
                    <div class="settings-row-copy">
                      <div class="settings-row-title">
                        <strong>{copy().excludedTitle}</strong>
                        <HelpHint label={copy().excludedTitle}>{copy().excludedDescription}</HelpHint>
                      </div>
                    </div>
                    <Show when={current().excludedCandidates.length > 0} fallback={<p class="memory-protection-empty">{copy().excludedEmpty}</p>}>
                      <ul class="memory-protection-list">
                        <For each={current().excludedCandidates}>
                          {(candidate) => (
                            <li class="memory-protection-item memory-protection-exclusion" data-reason={candidate.reason}>
                              <strong>{copy().recordProcess(candidate.name, candidate.processId)}</strong>
                              <span>{memoryProtectionExclusionText(candidate.reason)}</span>
                            </li>
                          )}
                        </For>
                      </ul>
                    </Show>
                  </div>
                </>
                );
              }}
            </Show>
          </>
        )}
      </Show>
    </div>
  );
}

// Protection may remain active after reserve construction fails.
// Display both backend states independently.
// Keep the same heading and four-value layout before status arrives.
function ProtectionState(props: { status: HostMemoryProtectionLiveStatus | null }) {
  const copy = () => uiText.memoryProtection;
  return (
    <div class="settings-row settings-row-block memory-protection-state">
      <div class="settings-row-copy" role="status">
        <div class="memory-protection-title">
          <strong>{copy().effectiveTitle}</strong>
          <HelpHint label={copy().effectiveHintLabel}>{copy().effectiveHint}</HelpHint>
        </div>
        <Show
          when={props.status}
          fallback={
            <>
              <span>--</span>
              <dl class="memory-protection-facts">
                <Fact label={copy().reserveTitle}>--</Fact>
                <Fact label={copy().thresholdState}>--</Fact>
                <Fact label={copy().currentAction}>--</Fact>
                <Fact label={copy().decisionTitle} wide>--</Fact>
              </dl>
            </>
          }
        >
          {(status) => <ProtectionStateValues status={status()} />}
        </Show>
      </div>
    </div>
  );
}

function ProtectionStateValues(props: { status: HostMemoryProtectionLiveStatus }) {
  const copy = () => uiText.memoryProtection;
  const threshold = () => props.status.atOrAboveThreshold === null
    ? copy().thresholdStateUnknown
    : props.status.atOrAboveThreshold ? copy().thresholdStateReached : copy().thresholdStateBelow;
  return (
    <>
      <span class="memory-protection-effective" data-active={String(props.status.active)}
        data-full-content={props.status.inactiveReason ?? undefined}>
        {props.status.active ? copy().effective : copy().inactive(memoryProtectionReasonText(props.status.inactiveReason!))}
      </span>
      <dl class="memory-protection-facts">
        <Fact label={copy().reserveTitle}>
          <span class="memory-protection-built memory-protection-state-reserve" classList={{ failed: !props.status.reserve.built }}
            data-full-content={props.status.reserve.reason ?? undefined}>
            {builtStateText(props.status.reserve.built, props.status.reserve.reason)}
          </span>
        </Fact>
        <Fact label={copy().thresholdState}>
          <span class="memory-protection-threshold-state" data-state={String(props.status.atOrAboveThreshold)}>{threshold()}</span>
        </Fact>
        {/* Reserve empty action and decision cells without adding absence text. */}
        <Fact label={copy().currentAction} empty={props.status.currentActionId === null}>
          <Show when={props.status.currentActionId}>{(id) => <code class="memory-protection-path">{id()}</code>}</Show>
        </Fact>
        <Fact label={copy().decisionTitle} wide empty={props.status.decisionReason === null}>
          <span class="memory-protection-decision" data-reason={props.status.decisionReason ?? ""}>
            {props.status.decisionReason === null ? "" : memoryProtectionDecisionText(props.status.decisionReason)}
          </span>
        </Fact>
      </dl>
    </>
  );
}

function builtStateText(built: boolean, reason: string | null): string {
  return built
    ? uiText.memoryProtection.reserveBuilt
    : reason === null
      ? uiText.memoryProtection.reserveNotBuilt
      : `${uiText.memoryProtection.reserveNotBuilt} · ${memoryProtectionReasonText(reason)}`;
}

function BuiltState(props: { built: boolean; reason: string | null }) {
  return (
    <span class="memory-protection-built" classList={{ failed: !props.built }} data-full-content={props.reason ?? undefined}>
      {builtStateText(props.built, props.reason)}
    </span>
  );
}
