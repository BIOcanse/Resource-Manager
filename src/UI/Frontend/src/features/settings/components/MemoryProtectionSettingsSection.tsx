import { createSignal, For, Index, Show, type JSX } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext.tsx";
import { usePushSource } from "../../../frontendRuntime/push/usePushSource.ts";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { createNormalRunningPoint, rebuildMemoryProtectionReserve } from "../../../data/memoryProtection/memoryProtectionApi.ts";
import type {
  HostMemoryActionRecord,
  HostMemoryProtectionActionMode,
  HostMemoryProtectionLiveStatus,
  HostMemoryProtectionSettings,
  HostMemoryReserveSettings,
  HostMemorySelectionBasis,
  HostMemoryTreeRoot
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
  groupMemoryProtectionActions,
  memoryProtectionDecisionText,
  memoryProtectionExclusionText,
  memoryProtectionOutcomeText,
  memoryProtectionReasonText,
  memoryProtectionRecordReasonText
} from "../memoryProtectionPresentation.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";

interface MemoryProtectionSettingsSectionProps {
  /** 草稿里的内存保护设置：开关、阈值、间隔、处理方式、最小进程增长量、进程白名单和保留区列表在这里编辑，随设置页一起保存。 */
  settings: HostMemoryProtectionSettings | null | undefined;
  /** 已保存的内存保护设置：和草稿比较，判断有没有未保存的更改。 */
  committed: HostMemoryProtectionSettings | null | undefined;
  runtimeFailureCode: string | null;
  onChange: (settings: HostMemoryProtectionSettings) => void;
}

type ActionMessage = { kind: "status" | "alert"; text: () => string };

// 主机内存保护分区（docs/host_memory_protection_2026-10-26.md）。可编辑的设置走设置页草稿；
// 状态、处理记录和正常运行点都来自订阅 /api/memory-protection/subscribe，由 runtime.sources.memoryProtection
// 一个来源合成当前值（memoryProtectionSubscriptionSource），这里只读，不重读、不合并帧。数值照原样显示，
// 触发点、剩余量、是否达到阈值、合计都不在前端计算。
export function MemoryProtectionSettingsSection(props: MemoryProtectionSettingsSectionProps) {
  const copy = () => uiText.memoryProtection;
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  const protection = usePushSource(runtime.sources.memoryProtection, () => ({ active: work.frontendVisible() }));
  const view = () => protection.snapshot().data;
  const statusData = () => view()?.status ?? null;
  const unavailable = () => protection.snapshot().status === "error" || protection.snapshot().status === "unavailable";
  const [busy, setBusy] = createSignal<"point" | "rebuild" | null>(null);
  const [pointMessage, setPointMessage] = createSignal<ActionMessage | null>(null);
  const [rebuildMessage, setRebuildMessage] = createSignal<ActionMessage | null>(null);

  const unsaved = () => JSON.stringify(props.settings ?? null) !== JSON.stringify(props.committed ?? null);
  // 设置被拒时后端给出原因码：认得的内存保护原因，或本分区有未保存的更改（拒绝的就是这次更改）时显示。
  // 值类型不符（invalid-settings-value-type）是设置整体的拒绝码，带出错的路径，由设置页顶部的保存状态显示。
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
      await createNormalRunningPoint(runtime.requestClient);
      setPointMessage({ kind: "status", text: () => copy().pointCreated });
    } catch (error) {
      setPointMessage({ kind: "alert", text: () => userFacingErrorMessage(error, copy().createPointFailed) });
    } finally {
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
              {(code) => <p role="alert" data-full-content={code()}>{copy().rejected(memoryProtectionReasonText(code()))}</p>}
            </Show>
            <Show when={!view()}>
              <Show
                when={unavailable()}
                fallback={<ContentState kind="waiting" title={copy().statusLoading} />}
              >
                <ContentState kind="unavailable" title={copy().statusUnavailable} detail={copy().statusUnavailableDetail} />
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
              {(current) => <ProtectionState status={current()} />}
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

            {/* 字节数和保留区大小一样按字节输入、原样发送（不取整、不换算），旁边是同一数值的可读写法。 */}
            <div class="settings-row">
              <div class="settings-row-copy">
                <strong>{copy().minimumGrowthTitle}</strong>
                <span>{copy().minimumGrowthDescription}</span>
              </div>
              <div class="memory-protection-byte-control">
                <NumberField label={copy().minimumGrowthLabel} ariaLabel={copy().minimumGrowthTitle}
                  value={settings().minimumProcessGrowthBytes} min={0} max={Number.MAX_SAFE_INTEGER} step={1}
                  onChange={(minimumProcessGrowthBytes) => update({ minimumProcessGrowthBytes })} />
                <span class="memory-protection-size-readout">{formatBytes(settings().minimumProcessGrowthBytes, "memory")}</span>
              </div>
            </div>

            {/* 每项按输入原样发送：空白、首尾空格和重复由后端以 invalid-protected-processes 拒绝，这里不预先改写。 */}
            <div class="settings-row settings-row-block memory-protection-protected">
              <div class="settings-row-copy">
                <strong>{copy().protectedTitle}</strong>
                <span>{copy().protectedDescription}</span>
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
                <strong>{copy().pointTitle}</strong>
                <span>{copy().pointDescription}</span>
                <Show
                  when={view()}
                  fallback={<span role="status">{unavailable() ? copy().statusUnavailable : copy().statusLoading}</span>}
                >
                  {(currentView) => (
                    <Show when={currentView().normalRunningPoint} fallback={<span class="memory-protection-point-none">{copy().pointNone}</span>}>
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
                  )}
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

            <Show when={view()}>
              {(currentView) => {
                const current = () => currentView().status;
                return (
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
                      <span>{copy().recordsDescription(currentView().recordLimit)}</span>
                    </div>
                    <Show when={currentView().records.length > 0} fallback={<p class="memory-protection-empty">{copy().recordsEmpty}</p>}>
                      <ol class="memory-protection-list">
                        <For each={groupMemoryProtectionActions(currentView().records)}>
                          {(group) => <ActionGroup actionId={group.actionId} records={group.records} />}
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
                );
              }}
            </Show>
          </>
        )}
      </Show>
    </div>
  );
}

// 保护是否生效和保留区是否建成分开显示：保留区在运行中出错时 active 仍为 true、reserve.built 为 false。
// 两项都照后端给的值显示，不由一项推出另一项。
function ProtectionState(props: { status: HostMemoryProtectionLiveStatus }) {
  const copy = () => uiText.memoryProtection;
  const threshold = () => props.status.atOrAboveThreshold === null
    ? copy().thresholdStateUnknown
    : props.status.atOrAboveThreshold ? copy().thresholdStateReached : copy().thresholdStateBelow;
  return (
    <div class="settings-row settings-row-block memory-protection-state">
      <div class="settings-row-copy" role="status">
        <div class="memory-protection-title">
          <strong>{copy().effectiveTitle}</strong>
          <HelpHint label={copy().effectiveHintLabel}>{copy().effectiveHint}</HelpHint>
        </div>
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
          <Fact label={copy().currentAction}>
            {props.status.currentActionId === null
              ? copy().currentActionNone
              : <code class="memory-protection-path">{props.status.currentActionId}</code>}
          </Fact>
          <Fact label={copy().decisionTitle} wide>
            <span class="memory-protection-decision" data-reason={props.status.decisionReason ?? ""}>
              {props.status.decisionReason === null ? copy().decisionNone : memoryProtectionDecisionText(props.status.decisionReason)}
            </span>
          </Fact>
        </dl>
      </div>
    </div>
  );
}

// 一次处理（同一个 actionId）一组：单个进程一组只有一条记录；进程树一组是树里每个进程一条，
// 包括被跳过的进程（结果为“已跳过”，原因是排除原因）。记录保持后端发来的顺序。
// 选择依据每组显示一次：同一次处理的记录（包括被跳过的成员）带的是同一个依据。
function ActionGroup(props: { actionId: string; records: readonly HostMemoryActionRecord[] }) {
  const copy = () => uiText.memoryProtection;
  const first = () => props.records[0]!;
  const rootName = (root: HostMemoryTreeRoot) => copy().recordProcess(root.name ?? copy().treeRootNameUnknown, root.processId);
  return (
    <li class="memory-protection-item memory-protection-action" data-target-kind={first().targetKind} data-action-id={props.actionId}>
      <div class="memory-protection-item-head">
        <strong class="memory-protection-action-title">{copy().targetKinds[first().targetKind]}</strong>
      </div>
      <dl class="memory-protection-facts">
        <Fact label={copy().actionId}><code class="memory-protection-path">{props.actionId}</code></Fact>
        <Show when={first().treeRoot}>
          {(root) => (
            <>
              <Fact label={copy().treeRoot}><span class="memory-protection-tree-root">{rootName(root())}</span></Fact>
              <Fact label={copy().treeRootStartKey}>{root().processStartKey ?? copy().valueNotProvided}</Fact>
            </>
          )}
        </Show>
        <SelectionBasis basis={first().selectionBasis} />
      </dl>
      <ol class="memory-protection-list">
        <For each={props.records}>{(record) => <ActionRecord record={record} />}</For>
      </ol>
    </li>
  );
}

function ActionRecord(props: { record: HostMemoryActionRecord }) {
  const copy = () => uiText.memoryProtection;
  const record = () => props.record;
  const memory = (bytes: number | null) => bytes === null ? copy().valueNotProvided : formatBytes(bytes, "memory");
  return (
    <li class="memory-protection-item memory-protection-record" data-outcome={record().outcome}>
      <div class="memory-protection-item-head">
        <strong>{copy().recordProcess(record().name, record().processId)}</strong>
        <span class="memory-protection-outcome">{memoryProtectionOutcomeText(record().outcome)}</span>
      </div>
      <dl class="memory-protection-facts">
        <Fact label={copy().recordAt}>{userFacingDateTime(record().at)}</Fact>
        <Fact label={copy().recordStartKey}>{record().processStartKey}</Fact>
        <Fact label={copy().recordBaseline}>{memory(record().baselineBytes)}</Fact>
        <Fact label={copy().recordCurrent}>{memory(record().currentBytes)}</Fact>
        <Fact label={copy().recordGrowth}>{record().growthBytes === null ? copy().valueNotProvided : bytesAsSent(record().growthBytes!)}</Fact>
        <Fact label={copy().recordBytesWritten}>{formatBytes(record().bytesWritten, "storage")}</Fact>
        <Show when={record().reason}>
          <Fact label={copy().recordReason} wide fullContent={record().reason!}>
            <span class="memory-protection-reason" data-reason={record().reason!}>{memoryProtectionRecordReasonText(record())}</span>
          </Fact>
        </Show>
        <Show when={record().detail}>
          {(detail) => (
            <Fact label={copy().recordDetail} wide fullContent={detail()}>
              <span class="memory-protection-detail">{memoryProtectionReasonText(detail())}</span>
            </Fact>
          )}
        </Show>
        <Fact label={copy().recordDumpPaths} wide>
          <Show when={record().dumpPaths.length > 0} fallback={copy().recordNoDumpPaths}>
            <ol class="memory-protection-paths">
              <For each={record().dumpPaths}>{(path) => <li><code class="memory-protection-path">{path}</code></li>}</For>
            </ol>
          </Show>
        </Fact>
      </dl>
    </li>
  );
}

// 依据照后端给的值显示：规则用的排序值（单个进程的增长量或直接子进程内存增量）和这次比较用的最小进程增长量。
function SelectionBasis(props: { basis: HostMemorySelectionBasis }) {
  const copy = () => uiText.memoryProtection;
  return (
    <>
      <Fact label={copy().selectionRule} wide>
        <span class="memory-protection-selection-rule" data-rule={props.basis.rule}>{copy().selectionRules[props.basis.rule]}</span>
      </Fact>
      <Show when={props.basis.processGrowthBytes !== null}>
        <Fact label={copy().selectionProcessGrowth}>{bytesAsSent(props.basis.processGrowthBytes!)}</Fact>
      </Show>
      <Show when={props.basis.directChildrenMemoryGrowthBytes !== null}>
        <Fact label={copy().selectionChildrenGrowth}>{bytesAsSent(props.basis.directChildrenMemoryGrowthBytes!)}</Fact>
      </Show>
      <Fact label={copy().selectionMinimum}>{formatBytes(props.basis.minimumProcessGrowthBytes, "memory")}</Fact>
    </>
  );
}

/** 一项记录。fullContent：悬停这一项的值看的完整内容（例如翻译前的原始原因码）。 */
function Fact(props: { label: string; wide?: boolean; fullContent?: string; children: JSX.Element }) {
  return (
    <div class="memory-protection-fact" classList={{ wide: props.wide === true }}>
      <dt>{props.label}</dt>
      <dd data-full-content={props.fullContent}>{props.children}</dd>
    </div>
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
