import { For, Show, type JSX } from "solid-js";
import type {
  HostMemoryActionRecord, HostMemorySelectionBasis, HostMemoryTreeRoot
} from "../../data/memoryProtection/memoryProtectionTypes.ts";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import {
  bytesAsSent, groupMemoryProtectionActions, memoryProtectionOutcomeText, memoryProtectionReasonText,
  memoryProtectionRecordReasonText
} from "../settings/memoryProtectionPresentation.ts";

// 主机内存保护的处理记录（docs/host_memory_protection_2026-10-26.md 第 7 条）：设置页“主机内存保护”分区和
// 详细信息的“内存保护记录”分页用同一份列表。records 是后端发来的全部记录，最新在前。

/** 处理记录，按 actionId 分组，保持后端顺序。 */
export function MemoryProtectionActionList(props: { records: readonly HostMemoryActionRecord[] }) {
  return (
    <ol class="memory-protection-list">
      <For each={groupMemoryProtectionActions(props.records)}>
        {(group) => <ActionGroup actionId={group.actionId} records={group.records} />}
      </For>
    </ol>
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
        <MemoryProtectionFact label={copy().actionId}><code class="memory-protection-path">{props.actionId}</code></MemoryProtectionFact>
        <Show when={first().treeRoot}>
          {(root) => (
            <>
              <MemoryProtectionFact label={copy().treeRoot}><span class="memory-protection-tree-root">{rootName(root())}</span></MemoryProtectionFact>
              <MemoryProtectionFact label={copy().treeRootStartKey}>{root().processStartKey ?? copy().valueNotProvided}</MemoryProtectionFact>
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
        <MemoryProtectionFact label={copy().recordAt}>{userFacingDateTime(record().at)}</MemoryProtectionFact>
        <MemoryProtectionFact label={copy().recordStartKey}>{record().processStartKey}</MemoryProtectionFact>
        <MemoryProtectionFact label={copy().recordBaseline}>{memory(record().baselineBytes)}</MemoryProtectionFact>
        <MemoryProtectionFact label={copy().recordCurrent}>{memory(record().currentBytes)}</MemoryProtectionFact>
        <MemoryProtectionFact label={copy().recordGrowth}>{record().growthBytes === null ? copy().valueNotProvided : bytesAsSent(record().growthBytes!)}</MemoryProtectionFact>
        <MemoryProtectionFact label={copy().recordBytesWritten}>{formatBytes(record().bytesWritten, "storage")}</MemoryProtectionFact>
        <Show when={record().reason}>
          <MemoryProtectionFact label={copy().recordReason} wide fullContent={record().reason!}>
            <span class="memory-protection-reason" data-reason={record().reason!}>{memoryProtectionRecordReasonText(record())}</span>
          </MemoryProtectionFact>
        </Show>
        <Show when={record().detail}>
          {(detail) => (
            <MemoryProtectionFact label={copy().recordDetail} wide fullContent={detail()}>
              <span class="memory-protection-detail">{memoryProtectionReasonText(detail())}</span>
            </MemoryProtectionFact>
          )}
        </Show>
        <MemoryProtectionFact label={copy().recordDumpPaths} wide>
          <Show when={record().dumpPaths.length > 0} fallback={copy().recordNoDumpPaths}>
            <ol class="memory-protection-paths">
              <For each={record().dumpPaths}>{(path) => <li><code class="memory-protection-path">{path}</code></li>}</For>
            </ol>
          </Show>
        </MemoryProtectionFact>
      </dl>
    </li>
  );
}

// 依据照后端给的值显示：规则用的排序值（单个进程的增长量或直接子进程内存增量）和这次比较用的最小进程增长量。
function SelectionBasis(props: { basis: HostMemorySelectionBasis }) {
  const copy = () => uiText.memoryProtection;
  return (
    <>
      <MemoryProtectionFact label={copy().selectionRule} wide>
        <span class="memory-protection-selection-rule" data-rule={props.basis.rule}>{copy().selectionRules[props.basis.rule]}</span>
      </MemoryProtectionFact>
      <Show when={props.basis.processGrowthBytes !== null}>
        <MemoryProtectionFact label={copy().selectionProcessGrowth}>{bytesAsSent(props.basis.processGrowthBytes!)}</MemoryProtectionFact>
      </Show>
      <Show when={props.basis.directChildrenMemoryGrowthBytes !== null}>
        <MemoryProtectionFact label={copy().selectionChildrenGrowth}>{bytesAsSent(props.basis.directChildrenMemoryGrowthBytes!)}</MemoryProtectionFact>
      </Show>
      <MemoryProtectionFact label={copy().selectionMinimum}>{formatBytes(props.basis.minimumProcessGrowthBytes, "memory")}</MemoryProtectionFact>
    </>
  );
}

/** 一项记录。fullContent：悬停这一项的值看的完整内容（例如翻译前的原始原因码）。 */
export function MemoryProtectionFact(props: { label: string; wide?: boolean; fullContent?: string; children: JSX.Element }) {
  return (
    <div class="memory-protection-fact" classList={{ wide: props.wide === true }}>
      <dt>{props.label}</dt>
      <dd data-full-content={props.fullContent}>{props.children}</dd>
    </div>
  );
}
