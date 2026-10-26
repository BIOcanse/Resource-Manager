import { Accessor, createMemo, createSignal, Setter } from "solid-js";
import {
  addManualSoftware,
  removeManualSoftware,
  fetchComponentVersionOptions,
  openPath,
  verifyComponent as requestComponentVerification,
} from "../api";
import {
  sharedBrowserRuntimeComponentId,
  type BrowserRuntimeSnapshot
} from "../features/browserRuntimes/browserRuntimeTypes";
import type { ComponentAcquisitionRequest } from "../features/management/components/ComponentAcquisitionDialog";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import { managementActionKey, managementActionKeyFromOperation } from "../features/management/components/ManagementPage";
import {
  type ManagementSubpageId
} from "../features/management/managementNavigation";
import { uiText } from "../text.ts";
import { softwareRecordName, softwareUninstallDescription } from "../presentation/softwareFacts.ts";
import {
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  type ObservationState
} from "../observation/observationState";
import { renderBackendMessage } from "../presentation/backendMessage.ts";
import { componentNeedsUpdate } from "../presentation/componentPresentation.ts";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import {
  compareUInt64Decimal,
  formatUInt64Bytes
} from "../data/operations/uint64Decimal";
import {
  componentInstallCommand,
  softwareUninstallCommand
} from "../data/operations/operationCommands";
import type {
  OperationRegistry,
  OperationRegistrySnapshot
} from "../frontendRuntime/operations/OperationRegistry";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import type {
  ManagedComponent,
  ManagementKind,
  ManualSoftwareKind,
  ManualSoftwareRequest,
  OperationSnapshot,
  SoftwareRecord
} from "../types";
import type { ManagementInventory } from "../features/management/useManagementInventory.ts";

export interface ManagementStoreOptions {
  inventory: ManagementInventory;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  mutablePersistenceEnabled: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  operations: Pick<
    OperationRegistry,
    "snapshot" | "subscribe" | "submit" | "waitForTerminal"
  >;
}

export interface ManagementStore {
  activeSubpage: Accessor<ManagementSubpageId>;
  setActiveSubpage: Setter<ManagementSubpageId>;
  components: Accessor<ManagedComponent[]>;
  componentsObservation: Accessor<ObservationState>;
  software: Accessor<SoftwareRecord[]>;
  softwareObservation: Accessor<ObservationState>;
  browserRuntimes: Accessor<BrowserRuntimeSnapshot | null>;
  browserRuntimesObservation: Accessor<ObservationState>;
  operationsObservation: Accessor<ObservationState>;
  browserRuntimeRefreshInProgress: Accessor<boolean>;
  refreshInProgress: Accessor<boolean>;
  actionLabels: Accessor<Record<string, string>>;
  manualSoftwareKind: Accessor<ManualSoftwareKind | null>;
  manualSoftwareSource: Accessor<SoftwareRecord | null>;
  classifySoftware: (software: SoftwareRecord) => void;
  manualSoftwareActionInProgress: Accessor<boolean>;
  openManualSoftwareModal: (kind: ManagementKind) => void;
  closeManualSoftwareModal: () => void;
  submitManualSoftware: (request: ManualSoftwareRequest) => Promise<void>;
  refreshComponents: () => Promise<void>;
  refreshSoftware: (refreshPortableRegistrations?: boolean) => Promise<void>;
  refreshBrowserRuntimes: (forceRefresh?: boolean) => Promise<void>;
  refreshState: (userInitiated: boolean) => Promise<void>;
  installComponent: (component: ManagedComponent) => Promise<void>;
  verifyComponent: (component: ManagedComponent) => Promise<void>;
  /** 当前打开的组件获取对话框（条款 + 版本选择）；null 表示没有打开。 */
  acquisitionRequest: Accessor<ComponentAcquisitionRequest | null>;
  cancelAcquisition: () => void;
  confirmAcquisition: (versionChoice: string | null) => Promise<void>;
  openAcquisitionLink: (url: string) => Promise<void>;
  removeManualRegistration: (software: SoftwareRecord) => Promise<boolean>;
  uninstallSoftware: (software: SoftwareRecord, actionKey?: string) => Promise<void>;
  dispose: () => void;
}

export function createManagementStore(options: ManagementStoreOptions): ManagementStore {
  const [activeSubpage, setActiveSubpage] = createSignal<ManagementSubpageId>("Dependency");
  const { components, software, browserRuntimes, componentsObservation, softwareObservation,
    browserRuntimesObservation, refreshComponents, refreshSoftware } = options.inventory;
  const [browserRuntimeRefreshInProgress, setBrowserRuntimeRefreshInProgress] = createSignal(false);
  const [refreshInProgress, setRefreshInProgress] = createSignal(false);
  const [temporaryActionLabels, setTemporaryActionLabels] =
    createSignal<Record<string, string>>({});
  const [operationProjection, setOperationProjection] =
    createSignal<OperationRegistrySnapshot>(options.operations.snapshot);
  const [manualSoftwareSource, setManualSoftwareSource] = createSignal<SoftwareRecord | null>(null);
  const [manualSoftwareKind, setManualSoftwareKind] = createSignal<ManualSoftwareKind | null>(null);
  const [manualSoftwareActionInProgress, setManualSoftwareActionInProgress] = createSignal(false);
  const operationsObservation = createMemo<ObservationState>(() =>
    operationObservation(
      operationProjection(),
      options.runtimeEffectsEnabled()));
  const actionLabels = createMemo<Record<string, string>>(() => {
    if (!options.runtimeEffectsEnabled()) {
      return {};
    }
    const labels = { ...temporaryActionLabels() };
    for (const operation of operationProjection().operations) {
      if (isTerminalOperation(operation)) {
        continue;
      }
      const key = managementActionKeyFromOperation(operation);
      if (key) {
        labels[key] = labelForOperation(operation);
      }
    }
    return labels;
  });
  const unsubscribeOperations = options.operations.subscribe(setOperationProjection);


  function openManualSoftwareModal(kind: ManagementKind) {
    if (!options.mutablePersistenceEnabled() || !isManualSoftwareKind(kind)) {
      return;
    }

    setManualSoftwareSource(null);
    setManualSoftwareKind(kind);
    if (software().length === 0) {
      void refreshSoftware();
    }
  }

  function classifySoftware(software: SoftwareRecord) {
    if (!options.mutablePersistenceEnabled()) return;
    setManualSoftwareSource(software);
    setManualSoftwareKind("other");
  }

  function closeManualSoftwareModal() {
    setManualSoftwareKind(null);
  }

  async function submitManualSoftware(request: ManualSoftwareRequest) {
    if (!options.mutablePersistenceEnabled() || manualSoftwareActionInProgress()) {
      return;
    }

    setManualSoftwareActionInProgress(true);
    try {
      await addManualSoftware(request);
      setManualSoftwareKind(null);
      await refreshSoftware();
    } catch (error) {
      showErrorToast(error, uiText.stores.addSoftwareFailed);
    } finally {
      setManualSoftwareActionInProgress(false);
    }
  }

  async function refreshBrowserRuntimes(forceRefresh = false) {
    setBrowserRuntimeRefreshInProgress(true);
    try { await options.inventory.refreshBrowserRuntimes(forceRefresh); }
    finally { setBrowserRuntimeRefreshInProgress(false); }
  }

  async function refreshState(userInitiated: boolean) {
    setRefreshInProgress(true);
    try {
      await Promise.all([refreshComponents(), refreshSoftware(userInitiated)]);
    } catch (error) {
      if (userInitiated) showErrorToast(error, uiText.stores.refreshFailed);
    } finally {
      setRefreshInProgress(false);
    }
  }

  // 组件获取：条款确认与版本选择由对话框一次收齐，之后才发命令。
  // 对话框里的"同意"是这次操作的一次性授权，不做持久化。
  const [acquisitionRequest, setAcquisitionRequest] =
    createSignal<ComponentAcquisitionRequest | null>(null);

  function componentSourceKind(component: ManagedComponent) {
    return component.installerSourceKind ?? "manual";
  }

  function cancelAcquisition() {
    setAcquisitionRequest(null);
  }

  async function openAcquisitionLink(url: string) {
    window.open(url, "_blank", "noopener");
  }

  async function installComponent(component: ManagedComponent) {
    if (!options.runtimeEffectsEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.stores.installDisabled
      });
      return;
    }

    const id = component.definition?.id;
    if (!id) {
      showErrorToast(new Error(uiText.stores.componentMissingIdentity), uiText.stores.actionFailed);
      return;
    }

    if (componentNeedsUpdate(component)) {
      setAcquisitionRequest({ component, versions: [], loading: false, manual: false });
      return;
    }

    // 安装器已经在缓存里：不用联网，也没有版本可选，但确认这一步不能省。
    // 厂商、条款、是否需要提权都只在这个弹窗里说过，跳过它等于替用户按了「装」。
    if (component.installerAvailable && !component.installed) {
      setAcquisitionRequest({ component, versions: [], loading: false, manual: false });
      return;
    }

    const manual = componentSourceKind(component) === "manual";
    setAcquisitionRequest({ component, versions: [], loading: !manual, manual });

    if (manual) {
      return;
    }

    try {
      const versionOptions = await fetchComponentVersionOptions(id);
      setAcquisitionRequest((current) => current && current.component.definition?.id === id
        ? { ...current, versions: versionOptions.options ?? [], loading: false,
            catalogStatus: versionOptions.catalogStatus, complete: versionOptions.complete }
        : current);
    } catch {
      // 版本列表拿不到时不把对话框关掉：用户仍然可以读条款并按已验证版本继续。
      setAcquisitionRequest((current) => current && current.component.definition?.id === id
        ? {
          ...current,
          loading: false,
          catalogStatus: "error",
          complete: false,
          versions: [
            { choice: "verified", available: true, version: null, assetName: null, unavailableReasonCode: null },
            { choice: "latest", available: false, version: null, assetName: null, unavailableReasonCode: "catalog-failure" }
          ]
        }
        : current);
    }
  }

  async function confirmAcquisition(versionChoice: string | null) {
    const request = acquisitionRequest();
    setAcquisitionRequest(null);
    if (!request) {
      return;
    }

    if (request.manual) {
      await guideManualAcquisition(request.component);
      return;
    }

    await runComponentInstall(request.component,
      componentNeedsUpdate(request.component) ? "verified" : versionChoice);
  }

  /** 手动获取：同时打开来源页和安装器缓存目录，并说清楚下一步做什么。 */
  async function guideManualAcquisition(component: ManagedComponent) {
    const sourcePageUrl = component.definition?.sourcePageUrl;
    if (sourcePageUrl) {
      window.open(sourcePageUrl, "_blank", "noopener");
    }

    const installerDirectory = component.installerDirectory;
    if (installerDirectory) {
      try {
        await openPath(installerDirectory);
      } catch (error) {
        showErrorToast(error, uiText.softwareActions.openPathFailed);
      }
    }

    options.showToast({
      tone: "info",
      title: uiText.feedback.info,
      message: uiText.componentAcquisition.manualNextStep,
      details: installerDirectory ? [installerDirectory] : undefined
    });
  }

  async function runComponentInstall(component: ManagedComponent, versionChoice: string | null) {
    const id = component.definition?.id;
    const key = managementActionKey("component", id);
    try {
      if (!id) {
        throw new Error(uiText.stores.componentMissingIdentity);
      }

      setActionLabel(key, uiText.stores.waiting);
      const operation = await options.operations.submit(
        componentInstallCommand(id, true, versionChoice));
      setActionLabel(key, labelForOperation(operation));
      const completed = await options.operations.waitForTerminal(operation.id);
      showOperationResult(completed);
    } catch (error) {
      showErrorToast(error, uiText.stores.actionFailed);
    } finally {
      clearActionLabel(key);
      const refreshes: Promise<void>[] = id === sharedBrowserRuntimeComponentId ? [] : [refreshComponents()];
      if (options.mutablePersistenceEnabled()) {
        refreshes.push(refreshSoftware());
      }
      if (id === sharedBrowserRuntimeComponentId) {
        refreshes.push(refreshBrowserRuntimes(true));
      }
      await Promise.all(refreshes);
    }
  }

  async function verifyComponent(component: ManagedComponent) {
    const id = component.definition?.id;
    if (!options.runtimeEffectsEnabled() || !component.canVerify || !id) return;
    const key = managementActionKey("component", id);
    if (actionLabels()[key]) return;
    setActionLabel(key, uiText.stores.progressWorking);
    try {
      const result = await requestComponentVerification(id);
      options.showToast({
        tone: result.state === "runtimeFilesUnavailable" ? "warning" : "info",
        title: uiText.management.verify,
        message: renderBackendMessage(result.message)
      });
    } catch (error) {
      showErrorToast(error, uiText.stores.actionFailed);
    } finally {
      clearActionLabel(key);
      await refreshComponents();
    }
  }

  async function removeManualRegistration(record: SoftwareRecord): Promise<boolean> {
    if (!options.mutablePersistenceEnabled() || record.registrationFacts?.kind !== "manual") return false;
    const key = managementActionKey("software", record.id);
    if (actionLabels()[key]) return false;
    if (!await options.confirmDialog({ title: record.name, message: uiText.managementFeedback.confirmManual, tone: "warning" })) return false;
    setActionLabel(key, uiText.stores.progressWorking);
    try {
      await removeManualSoftware(record.id);
      await refreshSoftware();
      options.showToast({ tone: "success", title: uiText.feedback.success, message: uiText.managementFeedback.manualRemoved });
      return true;
    } catch (error) {
      showErrorToast(error, uiText.managementFeedback.manualRemoveFailed);
      return false;
    } finally {
      clearActionLabel(key);
    }
  }

  async function uninstallSoftware(softwareRecord: SoftwareRecord, actionKey = managementActionKey("software", softwareRecord.id)) {
    if (!options.runtimeEffectsEnabled() || !options.mutablePersistenceEnabled()) {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.stores.uninstallDisabled
      });
      return;
    }

    const operations = softwareRecord.operations ?? {};
    if (!operations.canUninstall) {
      options.showToast({ tone: "warning", title: uiText.feedback.warning, message: softwareUninstallDescription(softwareRecord) });
      return;
    }

    const actionText = operations.uninstallKind === "WindowsUninstaller"
      ? uiText.stores.uninstallerWarning
      : uiText.stores.deleteDirectoryWarning;
    if (!await options.confirmDialog({
      title: softwareRecordName(softwareRecord),
      message: actionText,
      tone: "warning"
    })) {
      return;
    }

    setActionLabel(actionKey, uiText.stores.uninstalling);
    try {
      const operation = await options.operations.submit(
        softwareUninstallCommand(softwareRecord.id, true));
      setActionLabel(actionKey, labelForOperation(operation));
      const completed = await options.operations.waitForTerminal(operation.id);
      showOperationResult(completed);
    } catch (error) {
      showErrorToast(error, uiText.stores.softwareActionFailed);
    } finally {
      clearActionLabel(actionKey);
      await Promise.all([refreshComponents(), refreshSoftware()]);
    }
  }

  function setActionLabel(key: string, label: string) {
    setTemporaryActionLabels((current) => ({ ...current, [key]: label }));
  }

  function clearActionLabel(key: string) {
    setTemporaryActionLabels((current) => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  function showErrorToast(error: unknown, fallback: string) {
    options.showToast({
      tone: "error",
      title: uiText.feedback.error,
      message: userFacingErrorMessage(error, fallback)
    });
  }

  function showOperationResult(operation: OperationSnapshot) {
    if (operation.state === "succeeded") {
      options.showToast({
        tone: "success",
        title: uiText.feedback.success,
        message: renderBackendMessage(operation.resultMessage, uiText.stores.operationCompleted)
      });
      return;
    }

    if (operation.state === "stateUncertain") {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: renderBackendMessage(operation.errorMessage, uiText.stores.operationUncertain)
      });
      return;
    }

    if (operation.state === "failed") {
      options.showToast({
        tone: "error",
        title: uiText.feedback.error,
        message: renderBackendMessage(operation.errorMessage, uiText.stores.operationIncomplete)
      });
    }
  }

  return {
    activeSubpage,
    setActiveSubpage,
    components,
    componentsObservation,
    software,
    softwareObservation,
    browserRuntimes,
    browserRuntimesObservation,
    operationsObservation,
    browserRuntimeRefreshInProgress,
    refreshInProgress,
    actionLabels,
    manualSoftwareKind,
    manualSoftwareSource,
    classifySoftware,
    manualSoftwareActionInProgress,
    openManualSoftwareModal,
    closeManualSoftwareModal,
    submitManualSoftware,
    refreshComponents,
    refreshSoftware,
    refreshBrowserRuntimes,
    refreshState,
    installComponent,
    verifyComponent,
    acquisitionRequest,
    cancelAcquisition,
    confirmAcquisition,
    openAcquisitionLink,
    removeManualRegistration,
    uninstallSoftware,
    dispose: unsubscribeOperations
  };
}

function isManualSoftwareKind(kind: ManagementKind): kind is ManualSoftwareKind {
  return kind === "adapted" || kind === "game" || kind === "high_performance" || kind === "other";
}

function labelForOperation(operation: OperationSnapshot) {
  const stage = operation.progress?.stage === "Download" ? uiText.managementFeedback.stageDownload : "";
  if (operation.state === "queued" || operation.state === "startPending") {
    return uiText.stores.waiting;
  }
  if (operation.state === "running") {
    const percent = typeof operation.progress?.percent === "number"
      ? ` ${operation.progress.percent.toFixed(0)}%`
      : "";
    const speedValue = operation.progress?.speedBytesPerSecond ?? null;
    const speed = speedValue !== null
      && compareUInt64Decimal(speedValue, "0") > 0
      ? ` ${formatUInt64Bytes(speedValue)}/s`
      : "";
    return `${stage || uiText.stores.progressRunning}${percent}${speed}`;
  }
  if (operation.state === "cancelPending") {
    return uiText.stores.progressCanceling;
  }
  if (operation.state === "retryWait") {
    return uiText.stores.progressRetryWait;
  }
  if (operation.state === "recoveryPending") {
    return uiText.stores.progressRecovering;
  }
  if (operation.state === "succeeded") {
    return uiText.stores.progressCompleted;
  }
  if (operation.state === "failed") {
    return uiText.stores.progressFailed;
  }
  if (operation.state === "canceled") {
    return uiText.stores.progressCanceled;
  }
  if (operation.state === "stateUncertain") {
    return uiText.stores.progressUncertain;
  }

  return stage || uiText.stores.progressWorking;
}

function operationObservation(
  snapshot: OperationRegistrySnapshot,
  enabled: boolean
): ObservationState {
  if (!enabled) {
    return profileDisabledObservation(
      uiText.stores.operationStateDisabled);
  }
  if (snapshot.status === "ready") {
    return readyObservation(snapshot.capturedAt ?? undefined);
  }
  if (snapshot.status === "disposed" || snapshot.status === "error" || snapshot.status === "unavailable") {
    return failedObservation(
      loadingObservation(),
      uiText.stores.operationStateStopped);
  }
  return loadingObservation();
}
