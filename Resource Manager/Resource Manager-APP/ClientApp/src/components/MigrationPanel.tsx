import { createSignal, For, JSX, Show } from "solid-js";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import type {
  DiscoverySession,
  MigrationCandidate,
  MigrationKind,
  MigrationPlan,
  MigrationRoots,
  MigrationTargetCategory,
  SoftwareDataMigrationRecord
} from "../types";
import { StandardSelect } from "./StandardSelect";
import { UserDetailsDialog } from "./UserDetailsDialog";
import {
  migrationClassificationLabel,
  migrationKindLabel,
  migrationRecordDetails,
  migrationStateLabel,
  migrationTargetCategoryLabel,
  userFacingDateTime,
  userFacingRisk
} from "../presentation/userFacingText";
import type { UserDetailSection } from "../presentation/userDetails";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../presentation/userDetails";
import { formatBytes } from "../utils";
import { uiText } from "../text.ts";

interface MigrationPanelProps {
  status: string;
  softwareName: string;
  kind: MigrationKind | string;
  targetCategory: MigrationTargetCategory | string;
  sourcePaths: string;
  discoveryProgramRootPaths: string;
  discoveryProcessNames: string;
  allowMediumRisk: boolean;
  roots: MigrationRoots | null;
  plan: MigrationPlan | null;
  records: SoftwareDataMigrationRecord[];
  sessions: DiscoverySession[];
  candidates: MigrationCandidate[];
  activeSessionId: string | null;
  workbenchAvailable: boolean;
  operationActionsAvailable: boolean;
  previewInProgress: boolean;
  candidateLookupInProgress: boolean;
  executeInProgress: boolean;
  discoveryStartInProgress: boolean;
  discoveryStopInProgress: boolean;
  isRestoreInProgress: (recordId: string) => boolean;
  setSoftwareName: (value: string) => void;
  setKind: (value: string) => void;
  setTargetCategory: (value: string) => void;
  setSourcePaths: (value: string) => void;
  setDiscoveryProgramRootPaths: (value: string) => void;
  setDiscoveryProcessNames: (value: string) => void;
  setAllowMediumRisk: (value: boolean) => void;
  onPreview: () => void;
  onExecute: () => void;
  onFindCandidates: () => void;
  onStartDiscovery: () => void;
  onStopDiscovery: () => void;
  onUseCandidate: (candidate: MigrationCandidate) => void;
  onMigrateCandidate: (candidate: MigrationCandidate) => void;
  onRestore: (record: SoftwareDataMigrationRecord) => void;
}

export function MigrationPanel(props: MigrationPanelProps) {
  const demandId = "management.migration.workbench";
  useFrontendVisibilityDemand(demandId, [
    frontendWorkIds.migrationRoots,
    frontendWorkIds.migrationRecords,
    frontendWorkIds.migrationSessions
  ]);
  const [details, setDetails] = createSignal<{ title: string; summary?: string; sections: UserDetailSection[] } | null>(null);
  return (
    <section
      {...frontendVisibilitySurface(
        "visible.management.migration.workbench.surface",
        [demandId])}
      class="migration-panel"
      aria-busy={props.previewInProgress
        || props.candidateLookupInProgress
        || props.executeInProgress
        || props.discoveryStartInProgress
        || props.discoveryStopInProgress}
    >
      <div class="panel-header">
        <h2>{uiText.migrationPanel.title}</h2>
        <span id="migrationStatus" role="status" aria-live="polite">{props.status}</span>
      </div>
      <div class="migration-form">
        <label>
          <span>{uiText.migrationPanel.softwareName}</span>
          <input value={props.softwareName} type="text" placeholder={uiText.migrationPanel.softwareNamePlaceholder} onInput={(event) => props.setSoftwareName(event.currentTarget.value)} />
        </label>
        <label>
          <span>{uiText.migrationPanel.migrationKind}</span>
          <StandardSelect
            value={props.kind}
            ariaLabel={uiText.migrationPanel.migrationKind}
            options={[
              { value: "Data", label: uiText.migrationPanel.kindData },
              { value: "Root", label: uiText.migrationPanel.kindRoot }
            ]}
            onChange={props.setKind}
          />
        </label>
        <label>
          <span>{uiText.migrationPanel.targetCategory}</span>
          <StandardSelect
            value={props.targetCategory}
            ariaLabel={uiText.migrationPanel.targetCategory}
            options={[
              { value: "UserData", label: uiText.migrationPanel.targetUserData },
              { value: "Misc", label: uiText.migrationPanel.targetMisc }
            ]}
            onChange={props.setTargetCategory}
          />
        </label>
        <label class="migration-paths-field">
          <span>{uiText.migrationPanel.sourcePaths}</span>
          <textarea value={props.sourcePaths} rows={3} placeholder={uiText.migrationPanel.sourcePathsPlaceholder} onInput={(event) => props.setSourcePaths(event.currentTarget.value)} />
        </label>
        <label class="migration-risk-toggle">
          <input type="checkbox" checked={props.allowMediumRisk} onChange={(event) => props.setAllowMediumRisk(event.currentTarget.checked)} />
          <span>{uiText.migrationPanel.allowMediumRisk}</span>
        </label>
      </div>
      <div class="migration-discovery">
        <label>
          <span>{uiText.migrationPanel.manualRoots}</span>
          <textarea value={props.discoveryProgramRootPaths} rows={2} placeholder={uiText.migrationPanel.manualRootsPlaceholder} onInput={(event) => props.setDiscoveryProgramRootPaths(event.currentTarget.value)} />
        </label>
        <label>
          <span>{uiText.migrationPanel.processNames}</span>
          <input value={props.discoveryProcessNames} type="text" placeholder={uiText.migrationPanel.processNamesPlaceholder} onInput={(event) => props.setDiscoveryProcessNames(event.currentTarget.value)} />
        </label>
        <div class="migration-actions">
          <button
            class="secondary"
            type="button"
            disabled={!props.workbenchAvailable || props.candidateLookupInProgress}
            onClick={props.onFindCandidates}
          >
            {props.candidateLookupInProgress ? uiText.migrationPanel.lookingUp : uiText.migrationPanel.findMigratable}
          </button>
          <button
            class="secondary"
            type="button"
            disabled={!props.operationActionsAvailable
              || props.discoveryStartInProgress
              || Boolean(props.activeSessionId)}
            onClick={props.onStartDiscovery}
          >
            {props.discoveryStartInProgress ? uiText.migrationPanel.starting : uiText.migrationPanel.startMonitoring}
          </button>
          <button
            class="secondary"
            type="button"
            disabled={!props.operationActionsAvailable
              || !props.activeSessionId
              || props.discoveryStopInProgress}
            onClick={props.onStopDiscovery}
          >
            {props.discoveryStopInProgress ? uiText.migrationPanel.stopping : uiText.migrationPanel.stopMonitoring}
          </button>
        </div>
        <ListBlock className="discovery-sessions" items={props.sessions} empty="" render={(session) => (
          <div class="discovery-item">
            <div>
              <strong>{session.softwareName || uiText.migrationPanel.unnamedSoftware}</strong>
              <div class="discovery-meta">{migrationStateLabel(session.state)} · {uiText.migrationPanel.changeCount(session.observedWriteCount)}</div>
            </div>
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({
                title: uiText.migrationPanel.sessionDetailTitle(session.softwareName || uiText.migrationPanel.fallbackSoftwareName),
                sections: discoverySessionDetails(session)
              })}
            >
              {uiText.migrationPanel.details}
            </button>
          </div>
        )} />
        <ListBlock className="discovery-candidates" items={props.candidates} empty="" render={(candidate) => (
          <div class="discovery-item">
            <strong>{candidate.path ?? candidate.directory ?? candidate.name}</strong>
            <div class="migration-actions">
              <button
                class="secondary details-button"
                type="button"
                onClick={() => setDetails({ title: uiText.migrationPanel.candidateDetailTitle, sections: migrationCandidateDetails(candidate) })}
              >
                {uiText.migrationPanel.details}
              </button>
              <button class="secondary" type="button" onClick={() => props.onUseCandidate(candidate)}>{uiText.migrationPanel.fill}</button>
              <button
                type="button"
                disabled={!props.workbenchAvailable || props.previewInProgress}
                onClick={() => props.onMigrateCandidate(candidate)}
              >
                {uiText.migrationPanel.migrate}
              </button>
            </div>
          </div>
        )} />
      </div>
      <div class="migration-actions">
        <button
          class="secondary"
          type="button"
          disabled={!props.workbenchAvailable || props.previewInProgress}
          onClick={props.onPreview}
        >
          {props.previewInProgress ? uiText.migrationPanel.previewing : uiText.migrationPanel.preview}
        </button>
        <button
          type="button"
          disabled={!props.operationActionsAvailable
            || !props.plan?.canExecute
            || props.executeInProgress}
          onClick={props.onExecute}
        >
          {props.executeInProgress ? uiText.migrationPanel.executing : uiText.migrationPanel.execute}
        </button>
      </div>
      <div class="migration-roots">
        <Show when={props.roots}>
          {(roots) => (
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({ title: uiText.migrationPanel.savedLocationTitle, sections: migrationRootDetails(roots()) })}
            >
              {uiText.migrationPanel.viewSavedLocation}
            </button>
          )}
        </Show>
      </div>
      <div class="migration-plan">
        <Show when={props.plan}>
          {(plan) => (
            <>
              <div>{plan().canExecute ? uiText.migrationPanel.planReady : uiText.migrationPanel.planNeedsAttention}</div>
              <For each={plan().items ?? []}>
                {(item) => (
                  <div class="migration-item">
                    <strong>{userFacingRisk(item.risk)} · {migrationClassificationLabel(item.classification)} · {item.canExecute ? uiText.migrationPanel.canMigrate : uiText.migrationPanel.needsAttention}</strong>
                    <code>{item.sourcePath}</code>
                    <button
                      class="secondary details-button"
                      type="button"
                      onClick={() => setDetails({ title: uiText.migrationPanel.planItemDetailTitle, sections: migrationPlanItemDetails(item) })}
                    >
                      {uiText.migrationPanel.details}
                    </button>
                  </div>
                )}
              </For>
            </>
          )}
        </Show>
      </div>
      <div class="panel-header migration-record-header">
        <h2>{uiText.migrationPanel.restoreTitle}</h2>
        <span id="migrationRecordCount">{uiText.migrationPanel.recordCount(props.records.length)}</span>
      </div>
      <ListBlock className="migration-records" items={props.records} empty="" render={(record) => (
        <div class="migration-record">
          <div>
            <strong>{record.softwareName || uiText.migrationPanel.unnamedSoftware}</strong>
            <span>{migrationStateLabel(record.state)}</span>
          </div>
          <div class="migration-actions">
            <button
              class="secondary details-button"
              type="button"
              onClick={() => setDetails({ title: uiText.migrationPanel.recordDetailTitle, sections: migrationRecordDetails(record) })}
            >
              {uiText.migrationPanel.details}
            </button>
            <button
              class="secondary"
              type="button"
              disabled={!props.operationActionsAvailable || props.isRestoreInProgress(record.id)}
              onClick={() => props.onRestore(record)}
            >
              {props.isRestoreInProgress(record.id) ? uiText.migrationPanel.restoring : uiText.migrationPanel.restore}
            </button>
          </div>
        </div>
      )} />
      <UserDetailsDialog
        open={details() !== null}
        title={details()?.title ?? uiText.migrationPanel.detailFallbackTitle}
        summary={details()?.summary}
        sections={details()?.sections ?? []}
        onClose={() => setDetails(null)}
      />
    </section>
  );
}

function discoverySessionDetails(session: DiscoverySession) {
  return compactUserDetailSections([
    userDetailSection(uiText.migrationPanel.detail.monitoringState, [
      userDetailItem(uiText.migrationPanel.fallbackSoftwareName, session.softwareName || uiText.migrationPanel.unnamedSoftware),
      userDetailItem(uiText.migrationPanel.detail.state, migrationStateLabel(session.state)),
      userDetailItem(uiText.migrationPanel.detail.startedAt, userFacingDateTime(session.startedAt)),
      session.stoppedAt ? userDetailItem(uiText.migrationPanel.detail.stoppedAt, userFacingDateTime(session.stoppedAt)) : null,
      userDetailItem(uiText.migrationPanel.detail.observedChanges, uiText.migrationPanel.detail.changeTimes(session.observedWriteCount)),
      session.processNames?.length ? userDetailItem(uiText.migrationPanel.detail.observedProcesses, session.processNames.join("、")) : null
    ]),
    userDetailSection(uiText.migrationPanel.detail.observationScope, (session.programRootPaths ?? []).map((path, index) => userDetailItem(uiText.migrationPanel.detail.directory(index + 1), path)))
  ]);
}

function migrationCandidateDetails(candidate: MigrationCandidate) {
  return compactUserDetailSections([
    userDetailSection(uiText.migrationPanel.detail.migratableContent, [
      userDetailItem(uiText.migrationPanel.detail.location, candidate.path ?? candidate.directory ?? candidate.name),
      userDetailItem(uiText.migrationPanel.detail.recommendedContent, migrationKindLabel(candidate.recommendedMigrationKind)),
      userDetailItem(uiText.migrationPanel.detail.recommendedTarget, migrationTargetCategoryLabel(candidate.recommendedTargetCategory)),
      typeof candidate.observedWriteCount === "number" ? userDetailItem(uiText.migrationPanel.detail.observedChanges, uiText.migrationPanel.detail.changeTimes(candidate.observedWriteCount)) : null,
      candidate.lastObservedAt ? userDetailItem(uiText.migrationPanel.detail.lastObserved, userFacingDateTime(candidate.lastObservedAt)) : null,
      candidate.processNames?.length ? userDetailItem(uiText.migrationPanel.detail.relatedProcesses, candidate.processNames.join("、")) : null
    ])
  ]);
}

function migrationRootDetails(roots: MigrationRoots) {
  return compactUserDetailSections([
    userDetailSection(uiText.migrationPanel.detail.savedLocations, [
      userDetailItem(uiText.migrationPanel.targetUserData, roots.userDataRoot),
      userDetailItem(uiText.migrationPanel.targetMisc, roots.miscRoot),
      userDetailItem(uiText.migrationPanel.detail.softwareRoot, roots.managedSoftwareRoot)
    ])
  ]);
}

function migrationPlanItemDetails(item: MigrationPlan["items"][number]) {
  return compactUserDetailSections([
    userDetailSection(uiText.migrationPanel.detail.migrationJudgement, [
      userDetailItem(uiText.migrationPanel.detail.risk, userFacingRisk(item.risk)),
      userDetailItem(uiText.migrationPanel.detail.content, migrationClassificationLabel(item.classification)),
      userDetailItem(uiText.migrationPanel.detail.result, item.canExecute ? uiText.migrationPanel.canMigrate : uiText.migrationPanel.detail.needsAdjustment),
      typeof item.sizeBytes === "number" ? userDetailItem(uiText.migrationPanel.detail.size, formatBytes(item.sizeBytes)) : null
    ]),
    userDetailSection(uiText.migrationPanel.detail.location, [
      userDetailItem(uiText.migrationPanel.detail.sourceLocation, item.sourcePath),
      userDetailItem(uiText.migrationPanel.detail.destinationLocation, item.destinationPath)
    ])
  ]);
}

function ListBlock<T>(props: {
  className: string;
  items: T[];
  empty: string;
  render: (item: T) => JSX.Element;
}) {
  return (
    <div class={props.className}>
      <Show when={props.items.length > 0} fallback={props.empty ? <div>{props.empty}</div> : null}>
        <For each={props.items}>{props.render}</For>
      </Show>
    </div>
  );
}
