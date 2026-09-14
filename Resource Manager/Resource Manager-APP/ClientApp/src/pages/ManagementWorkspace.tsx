import { Show } from "solid-js";
import { BrowserRuntimePage } from "../browserRuntimes/BrowserRuntimePage";
import { sharedBrowserRuntimeComponentId } from "../browserRuntimes/browserRuntimeTypes";
import { ManagementPage, managementActionKey } from "../components/ManagementPage";
import { MigrationPanel } from "../components/MigrationPanel";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { FrontendVisibilityDemandBinding } from "../frontendWork/useFrontendVisibilityDemand";
import {
  browserRuntimeManagementSubpageId,
  isManagementInventorySubpage
} from "../management/managementNavigation";
import type { ManagementStore } from "../stores/managementStore";
import type { MigrationWorkbenchStore } from "../stores/migrationStore";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import type { ManagedComponent, SoftwareRecord } from "../types";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../components/ObservationStateNotice";

const browserRuntimeDemandId = "management.browser-runtime";

interface ManagementWorkspaceProps {
  management: ManagementStore;
  migration: MigrationWorkbenchStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onManagementSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
}

export function ManagementWorkspace(props: ManagementWorkspaceProps) {
  const management = props.management;
  const migration = props.migration;
  const activeInventoryKind = () => {
    const subpage = management.activeSubpage();
    return isManagementInventorySubpage(subpage) ? subpage : null;
  };
  const activeInventoryObservation = () => {
    const kind = activeInventoryKind();
    return kind === "Dependency" || kind === "Support"
      ? management.componentsObservation()
      : management.softwareObservation();
  };

  return (
    <Show
      when={activeInventoryKind()}
      fallback={(
        <Show
          when={management.activeSubpage() === browserRuntimeManagementSubpageId}
          fallback={(
            <section
              id="migrationWorkbenchPage"
              class="page active-page"
              aria-label="迁移工作台"
            >
                <ObservationStateNotice
                  state={migration.rootsObservation()}
                  label="迁移根目录"
                  onRetry={() => void migration.refreshRoots()}
                />
                <ObservationStateNotice
                  state={migration.recordsObservation()}
                  label="迁移记录"
                  onRetry={() => void migration.refreshRecords()}
                />
                <ObservationStateNotice
                  state={migration.sessionsObservation()}
                  label="发现会话"
                  onRetry={() => void migration.refreshSessions()}
                />
                <MigrationPanel
                  status={migration.status()}
                  softwareName={migration.softwareName()}
                  kind={migration.kind()}
                  targetCategory={migration.targetCategory()}
                  sourcePaths={migration.sourcePaths()}
                  discoveryProgramRootPaths={migration.discoveryProgramRootPaths()}
                  discoveryProcessNames={migration.discoveryProcessNames()}
                  allowMediumRisk={migration.allowMediumRisk()}
                  roots={migration.roots()}
                  plan={migration.plan()}
                  records={migration.records()}
                  sessions={migration.sessions()}
                  candidates={migration.candidates()}
                  activeSessionId={migration.activeSessionId()}
                  workbenchAvailable={migration.workbenchAvailable()}
                  operationActionsAvailable={migration.operationActionsAvailable()}
                  previewInProgress={migration.previewInProgress()}
                  candidateLookupInProgress={migration.candidateLookupInProgress()}
                  executeInProgress={migration.executeInProgress()}
                  discoveryStartInProgress={migration.discoveryStartInProgress()}
                  discoveryStopInProgress={migration.discoveryStopInProgress()}
                  isRestoreInProgress={migration.isRestoreInProgress}
                  setSoftwareName={migration.setSoftwareName}
                  setKind={migration.setKind}
                  setTargetCategory={migration.setTargetCategory}
                  setSourcePaths={migration.setSourcePaths}
                  setDiscoveryProgramRootPaths={migration.setDiscoveryProgramRootPaths}
                  setDiscoveryProcessNames={migration.setDiscoveryProcessNames}
                  setAllowMediumRisk={migration.setAllowMediumRisk}
                  onPreview={migration.preview}
                  onExecute={migration.execute}
                  onFindCandidates={migration.findCandidates}
                  onStartDiscovery={migration.startSession}
                  onStopDiscovery={migration.stopSession}
                  onUseCandidate={migration.useCandidate}
                  onMigrateCandidate={migration.migrateCandidate}
                  onRestore={migration.restore}
                />
            </section>
          )}
        >
          <section
            {...frontendVisibilitySurface(
              "visible.management.browser-runtime.surface",
              [browserRuntimeDemandId])}
            id="browserRuntimePage"
            class="page active-page browser-runtime-page"
            aria-label="运行时管理"
          >
            <FrontendVisibilityDemandBinding
              demandId={browserRuntimeDemandId}
              workIds={[frontendWorkIds.managementBrowserRuntimes]}
            />
            <ObservationStateBoundary
              state={management.browserRuntimesObservation()}
              label="浏览器运行时状态"
            >
              <BrowserRuntimePage
                snapshot={management.browserRuntimes()}
                installComponent={props.runtimeCapabilities.runtimeEffectsEnabled()
                  ? management.components().find((component) =>
                    component.definition?.id === (management.browserRuntimes()?.installComponentId ?? sharedBrowserRuntimeComponentId))
                  : undefined}
                actionLabel={management.actionLabels()[managementActionKey("component", sharedBrowserRuntimeComponentId)]}
                refreshInProgress={management.browserRuntimeRefreshInProgress()}
                onRefresh={() => void management.refreshBrowserRuntimes(true)}
                onInstall={(component) => void management.installComponent(component)}
              />
            </ObservationStateBoundary>
          </section>
        </Show>
      )}
    >
      {(activeKind) => (
        <ManagementPage
            activeKind={activeKind()}
            components={management.components()}
            software={management.software()}
            actionLabels={management.actionLabels()}
            observation={activeInventoryObservation()}
            softwareObservation={management.softwareObservation()}
            operationsObservation={management.operationsObservation()}
            refreshInProgress={management.refreshInProgress()}
            mutablePersistenceEnabled={props.runtimeCapabilities.mutablePersistenceEnabled()}
            runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()}
            onAddManualSoftware={management.openManualSoftwareModal}
            onRefresh={() => void management.refreshState(true)}
            onInstallComponent={(component) => void management.installComponent(component)}
            onUninstallSoftware={(softwareRecord, actionKey) => void management.uninstallSoftware(softwareRecord, actionKey)}
            onOpenDetail={props.onOpenSoftwareDetail}
            onSoftwareContextMenu={props.onManagementSoftwareContextMenu}
        />
      )}
    </Show>
  );
}
