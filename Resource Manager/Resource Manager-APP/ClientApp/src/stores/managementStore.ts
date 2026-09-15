import { Accessor, createMemo, createSignal, Setter } from "solid-js";
import {
  addManualSoftware,
  fetchComponentVersionOptions,
  getComponents,
  getSoftware,
  openPath,
} from "../api";
import { getBrowserRuntimeSnapshot } from "../browserRuntimes/browserRuntimeApi";
import {
  sharedBrowserRuntimeComponentId,
  type BrowserRuntimeSnapshot
} from "../browserRuntimes/browserRuntimeTypes";
import type { ComponentAcquisitionRequest } from "../components/ComponentAcquisitionDialog";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import { managementActionKey, managementActionKeyFromOperation } from "../components/ManagementPage";
import type { ManagementSubpageId } from "../management/managementNavigation";
import { readManagementSnapshotCache, writeManagementSnapshotCache } from "../management/managementSnapshotCache";
import { uiText } from "../text.ts";
import {
  cachedObservation,
  failedObservation,
  loadingObservation,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { userFacingErrorMessage, userFacingMessage } from "../presentation/userFacingText";
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
import { textOrEmpty } from "../utils";

export interface ManagementStoreOptions {
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
  manualSoftwareActionInProgress: Accessor<boolean>;
  openManualSoftwareModal: (kind: ManagementKind) => void;
  closeManualSoftwareModal: () => void;
  submitManualSoftware: (request: ManualSoftwareRequest) => Promise<void>;
  refreshComponents: () => Promise<void>;
  refreshSoftware: (refreshPortableRegistrations?: boolean) => Promise<void>;
  refreshBrowserRuntimes: (forceRefresh?: boolean) => Promise<void>;
  refreshState: (userInitiated: boolean) => Promise<void>;
  installComponent: (component: ManagedComponent) => Promise<void>;
  /** 当前打开的组件获取对话框（条款 + 版本选择）；null 表示没有打开。 */
  acquisitionRequest: Accessor<ComponentAcquisitionRequest | null>;
  cancelAcquisition: () => void;
  confirmAcquisition: (versionChoice: string | null) => Promise<void>;
  openAcquisitionLink: (url: string) => Promise<void>;
  uninstallSoftware: (software: SoftwareRecord, actionKey?: string) => Promise<void>;
  dispose: () => void;
}

export function createManagementStore(options: ManagementStoreOptions): ManagementStore {
  const cachedSnapshot = readManagementSnapshotCache();
  const [activeSubpage, setActiveSubpage] = createSignal<ManagementSubpageId>("Dependency");
  const [components, setComponents] = createSignal<ManagedComponent[]>(cachedSnapshot?.components ?? []);
  const [software, setSoftware] = createSignal<SoftwareRecord[]>(cachedSnapshot?.software ?? []);
  const cachedState = cachedSnapshot
    ? cachedObservation(cachedSnapshot.capturedAt)
    : loadingObservation();
  const [componentsObservation, setComponentsObservation] =
    createSignal<ObservationState>(cachedState);
  const [softwareObservation, setSoftwareObservation] =
    createSignal<ObservationState>(options.mutablePersistenceEnabled()
      ? cachedState
      : profileDisabledObservation(
        uiText.stores.writableRegistryDisabled));
  const [browserRuntimes, setBrowserRuntimes] = createSignal<BrowserRuntimeSnapshot | null>(null);
  const [browserRuntimesObservation, setBrowserRuntimesObservation] =
    createSignal<ObservationState>(loadingObservation());
  const [browserRuntimeRefreshInProgress, setBrowserRuntimeRefreshInProgress] = createSignal(false);
  const [refreshInProgress, setRefreshInProgress] = createSignal(false);
  const [temporaryActionLabels, setTemporaryActionLabels] =
    createSignal<Record<string, string>>({});
  const [operationProjection, setOperationProjection] =
    createSignal<OperationRegistrySnapshot>(options.operations.snapshot);
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

    setManualSoftwareKind(kind);
    if (software().length === 0) {
      void refreshSoftware();
    }
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

  async function refreshComponents() {
    setComponentsObservation(refreshingObservation);
    try {
      const next = await getComponents();
      setComponents(next);
      writeManagementSnapshotCache(next, software());
      setComponentsObservation(readyObservation());
    } catch (error) {
      setComponentsObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, uiText.stores.componentCatalogRefreshFailed)));
    }
  }

  async function refreshSoftware(refreshPortableRegistrations = false) {
    if (!options.mutablePersistenceEnabled()) {
      setSoftwareObservation(profileDisabledObservation(
        uiText.stores.writableRegistryDisabled));
      return;
    }

    setSoftwareObservation(refreshingObservation);
    try {
      const next = await getSoftware(refreshPortableRegistrations);
      setSoftware(next);
      writeManagementSnapshotCache(components(), next);
      setSoftwareObservation(readyObservation());
    } catch (error) {
      setSoftwareObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, uiText.stores.softwareRegistryRefreshFailed)));
      if (refreshPortableRegistrations) {
        throw error;
      }
    }
  }

  async function refreshBrowserRuntimes(forceRefresh = false) {
    if (browserRuntimeRefreshInProgress()) {
      return;
    }

    setBrowserRuntimeRefreshInProgress(true);
    setBrowserRuntimesObservation(refreshingObservation);
    try {
      const next = await getBrowserRuntimeSnapshot(forceRefresh);
      setBrowserRuntimes(next);
      setBrowserRuntimesObservation(readyObservation(next.capturedAt));
    } catch (error) {
      setBrowserRuntimesObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(error, uiText.stores.browserRuntimeRefreshFailed)));
    } finally {
      setBrowserRuntimeRefreshInProgress(false);
    }
  }

  async function refreshState(userInitiated: boolean) {
    if (refreshInProgress()) {
      return;
    }

    setRefreshInProgress(true);
    try {
      const refreshes: Promise<void>[] = [refreshComponents()];
      if (options.mutablePersistenceEnabled()) {
        refreshes.push(refreshSoftware(userInitiated));
      } else {
        setSoftwareObservation(profileDisabledObservation(
          uiText.stores.writableRegistryDisabled));
      }
      await Promise.all(refreshes);
    } catch (error) {
      if (userInitiated) {
        showErrorToast(error, uiText.stores.refreshFailed);
      }
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

    // 安装器已经在缓存里：不需要联网，也没有版本可选，直接装。
    if (component.installerAvailable && !component.installed) {
      await runComponentInstall(component, null);
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
        ? { ...current, versions: versionOptions.options ?? [], loading: false }
        : current);
    } catch (error) {
      // 版本列表拿不到时不把对话框关掉：用户仍然可以读条款并按已验证版本继续。
      const reason = userFacingErrorMessage(error, uiText.componentAcquisition.versionLookupFailed);
      setAcquisitionRequest((current) => current && current.component.definition?.id === id
        ? {
          ...current,
          loading: false,
          versions: [
            { choice: "verified", available: true, version: null, assetName: null, unavailableReason: null },
            { choice: "latest", available: false, version: null, assetName: null, unavailableReason: reason }
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

    await runComponentInstall(request.component, versionChoice);
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
      const refreshes = [refreshComponents()];
      if (options.mutablePersistenceEnabled()) {
        refreshes.push(refreshSoftware());
      }
      if (id === sharedBrowserRuntimeComponentId) {
        refreshes.push(refreshBrowserRuntimes(true));
      }
      await Promise.all(refreshes);
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
      options.showToast({ tone: "warning", title: uiText.feedback.warning, message: userFacingMessage(operations.uninstallMessage, uiText.stores.cannotUninstall) });
      return;
    }

    const actionText = operations.uninstallKind === "WindowsUninstaller"
      ? uiText.stores.uninstallerWarning
      : uiText.stores.deleteDirectoryWarning;
    if (!await options.confirmDialog({
      title: softwareRecord.name,
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
        message: userFacingMessage(operationResultText(operation), uiText.stores.operationCompleted)
      });
      return;
    }

    if (operation.state === "stateUncertain") {
      options.showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: userFacingMessage(operation.error, uiText.stores.operationUncertain)
      });
      return;
    }

    if (operation.state === "failed") {
      options.showToast({
        tone: "error",
        title: uiText.feedback.error,
        message: userFacingMessage(operation.error, uiText.stores.operationIncomplete)
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
    manualSoftwareActionInProgress,
    openManualSoftwareModal,
    closeManualSoftwareModal,
    submitManualSoftware,
    refreshComponents,
    refreshSoftware,
    refreshBrowserRuntimes,
    refreshState,
    installComponent,
    acquisitionRequest,
    cancelAcquisition,
    confirmAcquisition,
    openAcquisitionLink,
    uninstallSoftware,
    dispose: unsubscribeOperations
  };
}

function isManualSoftwareKind(kind: ManagementKind): kind is ManualSoftwareKind {
  return kind === "Adapted" || kind === "Game" || kind === "HighPerformance" || kind === "Other";
}

function labelForOperation(operation: OperationSnapshot) {
  const stage = operation.progress?.stage ?? "";
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

function operationResultText(operation: OperationSnapshot) {
  return typeof operation.result === "string" ? operation.result : null;
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
  if (snapshot.status === "disposed") {
    return failedObservation(
      loadingObservation(),
      uiText.stores.operationStateStopped);
  }
  return loadingObservation();
}
