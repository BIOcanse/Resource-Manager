import { Show } from "solid-js";
import { BrowserRuntimePage } from "../browserRuntimes/BrowserRuntimePage";
import { sharedBrowserRuntimeComponentId } from "../browserRuntimes/browserRuntimeTypes";
import { ComponentAcquisitionDialog } from "./components/ComponentAcquisitionDialog";
import { ManagementPage, managementActionKey } from "./components/ManagementPage";
import type { SoftwareContextMenuTarget } from "../../components/SoftwareContextMenu";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { FrontendVisibilityDemandBinding } from "../../frontendWork/useFrontendVisibilityDemand";
import {
  browserRuntimeManagementSubpageId,
  isManagementInventorySubpage
} from "./managementNavigation";
import type { ManagementStore } from "../../stores/managementStore";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import type { ManagedComponent, SoftwareRecord } from "../../types";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../../components/ObservationStateNotice";
import { uiText } from "../../text.ts";

const browserRuntimeDemandId = "management.browser-runtime";

interface ManagementWorkspaceProps {
  management: ManagementStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  onOpenSoftwareDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onManagementSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
}

export function ManagementWorkspace(props: ManagementWorkspaceProps) {
  const management = props.management;
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
          <section
            {...frontendVisibilitySurface(
              "visible.management.browser-runtime.surface",
              [browserRuntimeDemandId])}
            id="browserRuntimePage"
            class="page active-page browser-runtime-page"
            aria-label={uiText.workspace.runtimeManagement}
          >
            <FrontendVisibilityDemandBinding
              demandId={browserRuntimeDemandId}
              workIds={[frontendWorkIds.managementBrowserRuntimes]}
            />
            <ObservationStateBoundary
              state={management.browserRuntimesObservation()}
              label={uiText.workspace.browserRuntimeState}
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
      )}
    >
      {(activeKind) => (
        <>
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
            onClassifySoftware={management.classifySoftware}
            onRefresh={() => void management.refreshState(true)}
            onInstallComponent={(component) => void management.installComponent(component)}
            onVerifyComponent={(component) => void management.verifyComponent(component)}
            onUninstallSoftware={(softwareRecord, actionKey) => void management.uninstallSoftware(softwareRecord, actionKey)}
            onOpenDetail={props.onOpenSoftwareDetail}
            onSoftwareContextMenu={props.onManagementSoftwareContextMenu}
          />
          <ComponentAcquisitionDialog
            request={management.acquisitionRequest()}
            onOpenLink={(url) => void management.openAcquisitionLink(url)}
            onCancel={management.cancelAcquisition}
            onConfirm={(versionChoice) => void management.confirmAcquisition(versionChoice)}
          />
        </>
      )}
    </Show>
  );
}
