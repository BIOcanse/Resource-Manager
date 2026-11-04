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

// Share the backend record list between settings
// and reports, preserving newest-first order.

/** Group by actionId while preserving backend order. */
export function MemoryProtectionActionList(props: { records: readonly HostMemoryActionRecord[] }) {
  return (
    <ol class="memory-protection-list">
      <For each={groupMemoryProtectionActions(props.records)}>
        {(group) => <ActionGroup actionId={group.actionId} records={group.records} />}
      </For>
    </ol>
  );
}

// Each action groups its process records,
// including skipped members, in backend order.
// Show the shared selection evidence once per action.
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

// Display backend ranking values and the minimum growth used for comparison.
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

/** fullContent preserves the complete value, including untranslated codes. */
/** Keep empty cells allocated to prevent content shifts. */
export function MemoryProtectionFact(props: { label: string; wide?: boolean; empty?: boolean; fullContent?: string; children?: JSX.Element }) {
  return (
    <div class="memory-protection-fact" classList={{ wide: props.wide === true, empty: props.empty === true }}
      aria-hidden={props.empty === true ? "true" : undefined}>
      <dt>{props.label}</dt>
      <dd data-full-content={props.fullContent}>{props.children}</dd>
    </div>
  );
}
