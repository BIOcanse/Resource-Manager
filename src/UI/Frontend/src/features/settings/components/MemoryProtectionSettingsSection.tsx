import { createEffect, createSignal, For, Index, on, Show, type JSX } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendReadIntervals } from "../../../app/FrontendReadIntervalsContext.tsx";
import { memoryProtectionDemand } from "../../../data/memoryProtection/memoryProtectionDemand.ts";
import { createNormalRunningPoint, rebuildMemoryProtectionReserve } from "../../../data/memoryProtection/memoryProtectionApi.ts";
import type {
  HostMemoryProtectionActionMode,
  HostMemoryProtectionSettings,
  HostMemoryReserveSettings
} from "../../../data/memoryProtection/memoryProtectionTypes.ts";
import type { SourceDemand } from "../../../frontendRuntime/source/SourceDescriptor.ts";
import { formatBytePair, formatBytes } from "../../../presentation/byteUnits.ts";
import { formatInterfaceNumber, formatTrimmedDigits } from "../../../presentation/interfaceNumbers.ts";
import { userFacingDateTime, userFacingErrorMessage } from "../../../presentation/userFacingText.ts";
import { ContentState } from "../../../ui/patterns/ContentState.tsx";
import { StableLabel } from "../../../ui/primitives/StableLabel.tsx";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { uiText } from "../../../text.ts";
import {
  bytesAsSent,
  exactByteCount,
  memoryProtectionExclusionText,
  memoryProtectionOutcomeText,
  memoryProtectionReasonText
} from "../memoryProtectionPresentation.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";

interface MemoryProtectionSettingsSectionProps {
  /** 草稿里的内存保护设置：开关、阈值、间隔、处理方式和保留区列表在这里编辑，随设置页一起保存。 */
  settings: HostMemoryProtectionSettings | null | undefined;
  /** 已保存的内存保护设置：它变了说明一次保存已提交，状态要按新设置重新读。 */
  committed: HostMemoryProtectionSettings | null | undefined;
  runtimeFailureCode: string | null;
  onChange: (settings: HostMemoryProtectionSettings) => void;
}

type ActionMessage = { kind: "status" | "alert"; text: () => string };

// 主机内存保护分区（docs/host_memory_protection_2026-10-26.md）。可编辑的设置走设置页草稿；
// 状态、正常运行点由后端给出，照原样显示，触发点、剩余量、合计都不在前端计算。
export function MemoryProtectionSettingsSection(props: MemoryProtectionSettingsSectionProps) {
  const copy = () => uiText.memoryProtection;
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  const intervals = useFrontendReadIntervals();
  const demand = (): SourceDemand => {
    const current = intervals.current();
    return current
      ? memoryProtectionDemand(work.frontendVisible(), current)
      : { active: work.frontendVisible(), refreshIntervalMs: null };
  };
  const status = useSource(runtime.sources.memoryProtection, demand);
  const point = useSource(runtime.sources.normalRunningPoint, demand);
  const statusData = () => status.snapshot().data;
  const [busy, setBusy] = createSignal<"point" | "rebuild" | null>(null);
  const [pointMessage, setPointMessage] = createSignal<ActionMessage | null>(null);
  const [rebuildMessage, setRebuildMessage] = createSignal<ActionMessage | null>(null);

  // 保存提交后后端按新设置重新建立保留区、重新判断是否生效：读一次新的状态。
  createEffect(on(() => JSON.stringify(props.committed ?? null), () => {
    void status.refresh({ afterCurrent: true });
  }, { defer: true }));

  const unsaved = () => JSON.stringify(props.settings ?? null) !== JSON.stringify(props.committed ?? null);
  // 设置被拒时后端给出原因码：认得的内存保护原因，或本分区有未保存的更改（拒绝的就是这次更改）时显示。
  const rejection = () => {
    const code = props.runtimeFailureCode;
    if (!code) return null;
    return memoryProtectionReasonText(code) !== code || unsaved() ? code : null;
  };
  const update = (patch: Partial<HostMemoryProtectionSettings>) => {
    if (props.settings) props.onChange({ ...props.settings, ...patch });
  };
  const reserves = (): HostMemoryReserveSettings[] => props.settings?.reserves ?? [];
  const updateReserve = (index: number, patch: Partial<HostMemoryReserveSettings>) =>
    update({ reserves: reserves().map((entry, position) => position === index ? { ...entry, ...patch } : entry) });

  const pointSnapshot = () => point.snapshot();
  const pointAccepted = () => pointSnapshot().acceptedAttempt !== null || pointSnapshot().data !== null;

  const createPoint = async () => {
    if (busy()) return;
    setBusy("point");
    setPointMessage(null);
    try {
      await createNormalRunningPoint(runtime.requestClient);
      setPointMessage({ kind: "status", text: () => copy().pointCreated });
    } catch (error) {
      setPointMessage({ kind: "alert", text: () => userFacingErrorMessage(error, copy().createPointFailed) });
    } finally {
      await Promise.all([point.refresh({ afterCurrent: true }), status.refresh({ afterCurrent: true })]);
      setBusy(null);
    }
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
      await status.refresh({ afterCurrent: true });
      setBusy(null);
    }
  };

  const notProvided = () => copy().valueNotProvided;

  return (
    <div class="settings-section-panel memory-protection-settings">
      <p>{copy().intro}</p>
      <Show when={props.settings} fallback={<ContentState kind="unavailable" title={notProvided()} />}>
        {(settings) => (
          <>
            <Show when={unsaved()}><p role="status">{copy().unsavedNote}</p></Show>
            <Show when={rejection()}>
              {(code) => <p role="alert" title={code()}>{copy().rejected(memoryProtectionReasonText(code()))}</p>}
            </Show>
            <Show when={!statusData()}>
              <Show
                when={status.snapshot().status === "error" || status.snapshot().status === "unavailable"}
                fallback={<ContentState kind="waiting" title={copy().statusLoading} />}
              >
                <ContentState
                  kind="unavailable"
                  title={copy().statusUnavailable}
                  detail={copy().statusUnavailableDetail}
                  actions={<button type="button" class="secondary" onClick={() => void status.refresh({ afterCurrent: true })}>{copy().retry}</button>}
                />
              </Show>
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().enabledTitle}</strong>
                <span>{copy().enabledDescription}</span>
              </div>
              <SwitchControl ariaLabel={copy().enabledTitle} checked={settings().enabled} onChange={(enabled) => update({ enabled })} />
            </div>
            <Show when={statusData()}>
              {(current) => (
                <div class="settings-row settings-row-block">
                  <div class="settings-row-copy" role="status">
                    <strong>{copy().effectiveTitle}</strong>
                    <span class="memory-protection-effective" title={current().inactiveReason ?? undefined}>
                      {current().inactiveReason === null
                        ? copy().effective
                        : copy().inactive(memoryProtectionReasonText(current().inactiveReason!))}
                    </span>
                  </div>
                </div>
              )}
            </Show>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().thresholdTitle}</strong>
                <span>{copy().thresholdDescription}</span>
                <Show when={statusData()}>
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
                <strong>{copy().checkIntervalTitle}</strong>
                <span>{copy().checkIntervalDescription}</span>
              </div>
              <NumberField label={copy().checkIntervalLabel} ariaLabel={copy().checkIntervalTitle} value={settings().checkIntervalSeconds}
                min={1} max={86400} step={0.1} onChange={(checkIntervalSeconds) => update({ checkIntervalSeconds })} />
            </div>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().actionModeTitle}</strong>
                <span>{copy().actionModeDescription}</span>
              </div>
              <SegmentedControl<HostMemoryProtectionActionMode>
                value={settings().actionMode}
                options={(["dumpThenTerminate", "terminateOnly"] as const).map((id) => ({ id, ...copy().actionModes[id] }))}
                ariaLabel={copy().actionModeTitle}
                onChange={(actionMode) => update({ actionMode })}
              />
            </div>

            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().pointTitle}</strong>
                <span>{copy().pointDescription}</span>
                <Show
                  when={pointAccepted()}
                  fallback={<span role="status">{pointSnapshot().status === "error" ? copy().statusUnavailable : copy().statusLoading}</span>}
                >
                  <Show when={pointSnapshot().data} fallback={<span class="memory-protection-point-none">{copy().pointNone}</span>}>
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
                </Show>
                <Show when={pointMessage()}>
                  {(message) => <span role={message().kind}>{message().text()}</span>}
                </Show>
              </div>
              <button type="button" class="secondary" disabled={busy() !== null} onClick={() => void createPoint()}>
                <StableLabel labels={{ idle: copy().createPoint, busy: copy().creatingPoint }} current={busy() === "point" ? "busy" : "idle"} />
              </button>
            </div>

            <div class="settings-row settings-row-block">
              <div class="settings-row-copy">
                <strong>{copy().reserveTitle}</strong>
                <span>{copy().reserveDescription}</span>
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

            <Show when={statusData()}>
              {(current) => (
                <>
                  <div class="settings-row settings-row-block memory-protection-reserve-status">
                    <div class="settings-row-copy">
                      <strong>{copy().reserveStatusTitle}</strong>
                      <span>{copy().reserveStatusDescription}</span>
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
                      <strong>{copy().recordsTitle}</strong>
                      <span>{copy().recordsDescription(current().actionRecordLimit)}</span>
                    </div>
                    <Show when={current().actions.length > 0} fallback={<p class="memory-protection-empty">{copy().recordsEmpty}</p>}>
                      <ol class="memory-protection-list">
                        <For each={current().actions}>
                          {(record) => (
                            <li class="memory-protection-item memory-protection-record" data-outcome={record.outcome}>
                              <div class="memory-protection-item-head">
                                <strong>{copy().recordProcess(record.name, record.processId)}</strong>
                                <span class="memory-protection-outcome">{memoryProtectionOutcomeText(record.outcome)}</span>
                              </div>
                              <dl class="memory-protection-facts">
                                <Fact label={copy().recordAt}>{userFacingDateTime(record.at)}</Fact>
                                <Fact label={copy().recordStartKey}>{record.processStartKey}</Fact>
                                <Fact label={copy().recordBaseline}>{formatBytes(record.baselineBytes, "memory")}</Fact>
                                <Fact label={copy().recordCurrent}>{formatBytes(record.currentBytes, "memory")}</Fact>
                                <Fact label={copy().recordGrowth}>{bytesAsSent(record.growthBytes)}</Fact>
                                <Fact label={copy().recordBytesWritten}>{formatBytes(record.bytesWritten, "memory")}</Fact>
                                <Show when={record.reason}>
                                  {(reason) => (
                                    <Fact label={copy().recordReason} wide title={reason()}>{memoryProtectionReasonText(reason())}</Fact>
                                  )}
                                </Show>
                                <Fact label={copy().recordDumpPaths} wide>
                                  <Show when={record.dumpPaths.length > 0} fallback={copy().recordNoDumpPaths}>
                                    <ol class="memory-protection-paths">
                                      <For each={record.dumpPaths}>{(path) => <li><code class="memory-protection-path">{path}</code></li>}</For>
                                    </ol>
                                  </Show>
                                </Fact>
                              </dl>
                            </li>
                          )}
                        </For>
                      </ol>
                    </Show>
                  </div>

                  <div class="settings-row settings-row-block memory-protection-exclusions">
                    <div class="settings-row-copy">
                      <strong>{copy().excludedTitle}</strong>
                      <span>{copy().excludedDescription}</span>
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
              )}
            </Show>
          </>
        )}
      </Show>
    </div>
  );
}

function Fact(props: { label: string; wide?: boolean; title?: string; children: JSX.Element }) {
  return (
    <div class="memory-protection-fact" classList={{ wide: props.wide === true }}>
      <dt>{props.label}</dt>
      <dd title={props.title}>{props.children}</dd>
    </div>
  );
}

function BuiltState(props: { built: boolean; reason: string | null }) {
  return (
    <span class="memory-protection-built" classList={{ failed: !props.built }} title={props.reason ?? undefined}>
      {props.built
        ? uiText.memoryProtection.reserveBuilt
        : props.reason === null
          ? uiText.memoryProtection.reserveNotBuilt
          : `${uiText.memoryProtection.reserveNotBuilt} · ${memoryProtectionReasonText(props.reason)}`}
    </span>
  );
}
