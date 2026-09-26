import { createEffect, createMemo, createSignal, on, onCleanup } from "solid-js";
import type { Accessor, Setter } from "solid-js";
import {
  confirmPortableSoftwareRoot,
  createProcessDumps,
  getGpuPlacementSoftwareSettings,
  observeGpuPlacementProcesses,
  openPath,
  openProperties,
  postJson,
  saveGpuPlacementProcessPolicy,
  saveGpuPlacementSoftwarePolicy,
  searchOnline,
  terminateProcesses
} from "../api";
import { migrationRestoreCommand } from "../data/operations/operationCommands";
import type { OperationRegistry } from "../frontendRuntime/operations/OperationRegistry";
import { isTerminalOperation } from "../frontendRuntime/operations/operationMerge";
import type { SourceFamily } from
  "../frontendRuntime/source/SourceDescriptor";
import type { SourceSnapshot } from
  "../frontendRuntime/source/SourceSnapshot";
import type { ConfirmDialogRequest, ToastInput } from "../components/AppFeedback";
import {
  createSoftwareContextMenuModel,
  fileLocationSelectMode,
  normalizedProcessTargets,
  primaryFileLocation,
  primaryPropertiesPath
} from "../components/SoftwareContextMenu";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import type { StandardContextMenuModel } from "../components/StandardContextMenu";
import {
  isManagementInventorySubpage,
  type ManagementSubpageId
} from "../features/management/managementNavigation";
import type { ManagementStore } from "../stores/managementStore";
import type { MigrationWorkbenchStore } from "../stores/migrationStore";
import type { MonitorStore } from "../stores/monitorStore";
import { managementRoleLabel, softwareDisplayKindLabel, uiText } from "../text.ts";
import type {
  DetailNotice,
  GpuPlacementObservedProcessInput,
  GpuPlacementProcessPolicy,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareSettingsSnapshot,
  ManagedComponent,
  OperationSnapshot,
  OptimizationReportItem,
  PageId,
  SoftwareDataMigrationRecord,
  SoftwareDetailModel,
  SoftwareRecord,
  SystemProcessOperationResult
} from "../types";
import {
  formatBoolean,
  formatCapabilityFlags,
  getRootMigrationPaths,
  isComponentInstalled,
  normalizeName,
  pathLooksUsable,
  textOrEmpty,
  uniqueTextValues
} from "../utils";
import { pickShellFolder } from "../utils";
import { requestDeviceTopologySelection } from "../features/deviceTopology/deviceTopologyStore";
import {
  SoftwareMetadataLeaseBinding
} from "../features/softwareMetadata/SoftwareMetadataLeaseBinding";
import type { SoftwareMetadataQuery } from
  "../features/softwareMetadata/softwareMetadataApi";
import type {
  SoftwareMetadataLookupResult
} from "../features/softwareMetadata/softwareMetadataDecoder";
import { projectSoftwareMetadataDetail } from
  "../features/softwareMetadata/softwareMetadataDetailProjection";
import {
  componentCategory,
  componentDisplayName,
  componentPurpose,
  userFacingErrorMessage,
  userFacingMessage,
  userFacingState
} from "../presentation/userFacingText";

interface UseSoftwareActionsOptions {
  monitor: MonitorStore;
  management: ManagementStore;
  migration: MigrationWorkbenchStore;
  setActivePage: Setter<PageId>;
  openManagementSubpage: (subpage: ManagementSubpageId) => void;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  showToast: (input: ToastInput) => void;
  showErrorToast: (error: unknown, fallback: string) => void;
  mutablePersistenceEnabled: Accessor<boolean>;
  gpuPlacementEnabled: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  operations: Pick<OperationRegistry, "submit" | "waitForTerminal">;
  softwareMetadataSource: SourceFamily<
    SoftwareMetadataQuery,
    SoftwareMetadataLookupResult
  >;
  softwareMetadataLanguage: Accessor<string>;
}

type SoftwareContextMatchTarget = Pick<SoftwareContextMenuTarget, "name"> & Partial<Pick<SoftwareContextMenuTarget, "softwareId" | "softwareName">>;

export function useSoftwareActions(options: UseSoftwareActionsOptions) {
  const { monitor, management, migration, setActivePage, openManagementSubpage, confirmDialog, showToast, showErrorToast } = options;
  const [softwareDetail, setSoftwareDetail] = createSignal<SoftwareDetailModel | null>(null);
  const [softwareDetailNotice, setSoftwareDetailNotice] = createSignal<DetailNotice | null>(null);
  const [softwareDetailActionInProgress, setSoftwareDetailActionInProgress] = createSignal(false);
  const [gpuPlacementSettings, setGpuPlacementSettings] = createSignal<GpuPlacementSoftwareSettingsSnapshot | null>(null);
  const [gpuPlacementLoading, setGpuPlacementLoading] = createSignal(false);
  const [contextMenu, setContextMenu] = createSignal<StandardContextMenuModel | null>(null);
  const [highlightedSoftwareId, setHighlightedSoftwareId] = createSignal<string | null>(null);
  let highlightClearTimer: number | undefined;
  let softwareDetailRequestId = 0;
  const softwareMetadataBinding = new SoftwareMetadataLeaseBinding(
    options.softwareMetadataSource,
    projectSoftwareMetadataSnapshot);

  const softwareDetailMigrationRecords = createMemo(() => {
    const detail = softwareDetail();
    return detail ? migrationRecordsForDetail(detail, migration.records()) : [];
  });

  onCleanup(() => {
    if (highlightClearTimer !== undefined) {
      window.clearTimeout(highlightClearTimer);
    }
    softwareMetadataBinding.release();
  });

  createEffect(on(options.softwareMetadataLanguage, (language) => {
    const model = softwareDetail();
    if (model?.type === "software" && model.softwareIdentityId) {
      bindSoftwareMetadataForDetail(model, language);
    }
  }, { defer: true }));

  async function inspectOptimizationTarget(report: OptimizationReportItem) {
    if (report.target.targetType !== "Software") {
      requestDeviceTopologySelection(report.target.targetKey);
      setActivePage("details");
      return;
    }

    if (!options.mutablePersistenceEnabled()) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.policyEditUnavailable });
      return;
    }

    if (management.software().length === 0) {
      await management.refreshSoftware();
    }

    const target = report.target;
    const matched = management.software().find((item) =>
      textOrEmpty(item.id).toLowerCase() === textOrEmpty(target.softwareId ?? target.targetKey).toLowerCase()
      || normalizeName(item.name) === normalizeName(target.displayName));
    if (!matched) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.softwareListNoMatch });
      return;
    }

    openSoftwareDetail("software", matched);
  }

  async function openSoftwareSettingsById(softwareId: string, softwareName: string) {
    if (!options.mutablePersistenceEnabled()) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.policyEditUnavailable });
      return;
    }

    let matched = management.software().find((item) =>
      item.id.localeCompare(softwareId, undefined, { sensitivity: "accent" }) === 0
      || normalizeName(item.name) === normalizeName(softwareName));
    if (!matched) {
      await management.refreshSoftware();
      matched = management.software().find((item) =>
        item.id.localeCompare(softwareId, undefined, { sensitivity: "accent" }) === 0
        || normalizeName(item.name) === normalizeName(softwareName));
    }
    if (!matched) {
      showToast({
        tone: "warning",
        title: uiText.feedback.warning,
        message: uiText.softwareActions.policySoftwareMissing
      });
      return;
    }

    if (isManagementInventorySubpage(matched.kind as ManagementSubpageId)) {
      openManagementSubpage(matched.kind as ManagementSubpageId);
    }
    setActivePage("components");
    openSoftwareDetail("software", matched);
  }

  function openResourceTableSoftwareContextMenu(
    event: MouseEvent,
    target: SoftwareContextMenuTarget,
    returnFocusTarget?: HTMLElement | null)
  {
    openSoftwareContextMenu(event, target, returnFocusTarget);
  }

  function openManagementSoftwareContextMenu(event: MouseEvent, target: SoftwareContextMenuTarget) {
    openSoftwareContextMenu(event, target);
  }

  function openSoftwareContextMenu(
    event: MouseEvent,
    target: SoftwareContextMenuTarget,
    explicitReturnFocusTarget?: HTMLElement | null)
  {
    event.preventDefault();
    const enrichedTarget = enrichSoftwareContextTarget(target);
    const returnFocusTarget = explicitReturnFocusTarget
      ?? (event.currentTarget instanceof HTMLElement
      ? event.currentTarget
      : document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null);
    setContextMenu(createSoftwareContextMenuModel(event.clientX, event.clientY, enrichedTarget, {
      allowSystemActions: options.runtimeEffectsEnabled(),
      onExpand: expandSoftwareContextTarget,
      onTerminate: (current) => void terminateSoftwareContextTarget(current),
      onCreateDump: (current) => void createDumpForSoftwareContextTarget(current),
      onGoToDetails: goToProcessDetailsForSoftware,
      onOpenFileLocation: (current) => void openFileLocationForSoftwareContextTarget(current),
      onSearchOnline: (current) => void searchSoftwareContextTarget(current),
      onProperties: (current) => void openPropertiesForSoftwareContextTarget(current)
    }, returnFocusTarget));
  }

  function expandSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (target.softwareId) {
      monitor.toggleResourceTableExpand(target.softwareId);
    }
  }

  async function terminateSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const processTargets = normalizedProcessTargets(target);
    if (processTargets.length === 0) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noRunningProcess });
      return;
    }

    if (!await confirmDialog({
      title: uiText.softwareActions.endTaskTitle(target.softwareName ?? target.name),
      message: uiText.softwareActions.endTaskMessage(processTargets.length),
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await terminateProcesses(processTargets);
      showSystemProcessResult(result, uiText.softwareActions.endTaskDone);
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.endTaskFailed);
    }
  }

  async function createDumpForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const processTargets = normalizedProcessTargets(target);
    if (processTargets.length === 0) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noDumpProcess });
      return;
    }

    if (!await confirmDialog({
      title: uiText.softwareActions.dumpTitle(target.softwareName ?? target.name),
      message: uiText.softwareActions.dumpMessage(processTargets.length),
      details: processTargets.map((item) => `PID ${item.processId}`).slice(0, 12),
      tone: "warning"
    })) {
      return;
    }

    try {
      const result = await createProcessDumps(processTargets);
      showSystemProcessResult(result, uiText.softwareActions.dumpDone);
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.dumpFailed);
    }
  }

  function goToProcessDetailsForSoftware(target: SoftwareContextMenuTarget) {
    if (!target.softwareId) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noStableSoftwareIdentity });
      return;
    }

    setActivePage("monitor");
    monitor.changeResourceTableMode("process");
    setHighlightedSoftwareId(target.softwareId);
    if (highlightClearTimer !== undefined) {
      window.clearTimeout(highlightClearTimer);
    }
    highlightClearTimer = window.setTimeout(() => {
      setHighlightedSoftwareId((current) => current === target.softwareId ? null : current);
    }, 9000);
    showToast({ tone: "info", title: uiText.feedback.info, message: uiText.softwareActions.markedProcesses(target.softwareName ?? target.name) });
  }

  async function openFileLocationForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryFileLocation(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noInstallDirectory });
      return;
    }

    try {
      const result = await openPath(path, fileLocationSelectMode(target));
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, uiText.softwareActions.openedFileLocation), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.openFileLocationFailed);
    }
  }

  async function searchSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    try {
      const result = await searchOnline(target.softwareName ?? target.name);
      showToast({ tone: "info", title: uiText.feedback.info, message: uiText.softwareActions.openedOnlineSearch, details: [result.url] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.onlineSearchFailed);
    }
  }

  async function openPropertiesForSoftwareContextTarget(target: SoftwareContextMenuTarget) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const path = primaryPropertiesPath(target);
    if (!path) {
      showToast({ tone: "warning", title: uiText.feedback.warning, message: uiText.softwareActions.noPropertiesPath });
      return;
    }

    try {
      const result = await openProperties(path);
      showToast({ tone: "success", title: uiText.feedback.success, message: userFacingMessage(result.message, uiText.softwareActions.openedProperties), details: [result.openedPath ?? path] });
    } catch (error) {
      showErrorToast(error, uiText.softwareActions.openPropertiesFailed);
    }
  }

  function enrichSoftwareContextTarget(target: SoftwareContextMenuTarget): SoftwareContextMenuTarget {
    const liveRows = monitor.resourceTableSnapshot()?.rows ?? [];
    const matchingRows = liveRows.filter((row) => row.kind === "software" || row.kind === "process")
      .filter((row) => softwareContextTargetMatchesRow(target, row));
    const softwareRow = matchingRows.find((row) => row.kind === "software");
    const processRows = matchingRows.filter((row) => row.kind === "process");
    return {
      ...target,
      softwareId: target.softwareId ?? softwareRow?.softwareId,
      softwareName: target.softwareName ?? softwareRow?.softwareName ?? softwareRow?.name,
      processIds: uniqueNumbers([
        ...(target.processIds ?? []),
        ...(softwareRow?.processIds ?? []),
        ...processRows.flatMap((row) => row.processIds?.length ? row.processIds : row.processId ? [row.processId] : [])
      ]),
      processTargets: uniqueProcessTargets([
        ...(target.processTargets ?? []),
        ...processRows.flatMap((row) => row.processId && row.processStartKey
          ? [{
              processId: row.processId,
              processStartKey: row.processStartKey
            }]
          : [])
      ]),
      processNames: uniqueStrings([
        ...(target.processNames ?? []),
        ...(softwareRow?.processNames ?? []),
        ...processRows.flatMap((row) => row.processNames?.length ? row.processNames : [row.name])
      ]),
      executablePaths: uniqueStrings([
        ...(target.executablePaths ?? []),
        ...(softwareRow?.executablePaths ?? []),
        ...processRows.flatMap((row) => row.executablePaths ?? [])
      ]),
      rootPaths: uniqueStrings(target.rootPaths ?? [])
    };
  }

  function uniqueProcessTargets(
    values: Array<{ processId: number; processStartKey: string }>
  ) {
    return [...new Map(values.map((value) => [
      `${value.processId}:${value.processStartKey}`,
      value
    ])).values()];
  }

  function showSystemProcessResult(result: SystemProcessOperationResult, title: string) {
    const failed = result.items.filter((item) => item.state === "Failed");
    showToast({
      tone: failed.length > 0 ? "warning" : "success",
      title,
      message: userFacingMessage(result.message, uiText.softwareActions.processActionDone),
      details: uniqueStrings([
        result.directoryPath ?? "",
        ...result.items.map((item) => item.path || item.processName || (item.processId ? uiText.softwareActions.processItem(item.processId) : ""))
      ]).slice(0, 10)
    });
  }

  function openSoftwareDetail(type: "component" | "software", value: ManagedComponent | SoftwareRecord) {
    const model = createSoftwareDetailModel(type, value);
    setSoftwareDetail(model);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    if (options.runtimeEffectsEnabled()) {
      void migration.refreshRecords();
    }
    const requestId = ++softwareDetailRequestId;
    if (options.gpuPlacementEnabled() && model.type === "software" && model.id) {
      void loadGpuPlacementSettingsForDetail(model, requestId);
    } else {
      setGpuPlacementLoading(false);
    }
    if (model.type === "software" && model.softwareIdentityId) {
      bindSoftwareMetadataForDetail(
        model,
        options.softwareMetadataLanguage());
    } else {
      softwareMetadataBinding.release();
    }
  }

  function closeSoftwareDetail() {
    setSoftwareDetail(null);
    setSoftwareDetailNotice(null);
    setGpuPlacementSettings(null);
    setGpuPlacementLoading(false);
    softwareDetailRequestId += 1;
    softwareMetadataBinding.release();
  }

  function createSoftwareDetailModel(type: "component" | "software", value: ManagedComponent | SoftwareRecord): SoftwareDetailModel {
    return type === "component"
      ? createComponentDetailModel(value as ManagedComponent)
      : createInstalledSoftwareDetailModel(value as SoftwareRecord);
  }

  function bindSoftwareMetadataForDetail(
    model: SoftwareDetailModel,
    language: string)
  {
    softwareMetadataBinding.switch({
      softwareIdentityId: model.softwareIdentityId,
      language
    });
  }

  function projectSoftwareMetadataSnapshot(
    query: SoftwareMetadataQuery,
    snapshot: SourceSnapshot<SoftwareMetadataLookupResult>)
  {
    setSoftwareDetail((current) =>
      projectSoftwareMetadataDetail(current, query, snapshot));
  }

  async function loadGpuPlacementSettingsForDetail(model: SoftwareDetailModel, requestId: number) {
    if (!options.gpuPlacementEnabled()) {
      setGpuPlacementSettings(null);
      setGpuPlacementLoading(false);
      return;
    }

    setGpuPlacementLoading(true);
    try {
      const observations = collectGpuPlacementProcessObservations(model);
      if (observations.length > 0) {
        try {
          await observeGpuPlacementProcesses({
            softwareId: model.id,
            softwareName: model.name,
            processes: observations
          });
        } catch (error) {
          if (isCurrentSoftwareDetailRequest(model, requestId)) {
            setSoftwareDetailNotice({
              message: uiText.softwareActions.processHistoryFailed,
              tone: "warning",
              details: []
            });
          }
        }
      }

      const settingsSnapshot = await getGpuPlacementSoftwareSettings(model.id, model.name, model.kind);
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setGpuPlacementSettings(settingsSnapshot);
      }
    } catch (error) {
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setSoftwareDetailNotice({
          message: uiText.softwareActions.policyReadFailed,
          tone: "error",
          details: []
        });
      }
    } finally {
      if (isCurrentSoftwareDetailRequest(model, requestId)) {
        setGpuPlacementLoading(false);
      }
    }
  }

  function isCurrentSoftwareDetailRequest(model: SoftwareDetailModel, requestId: number) {
    const current = softwareDetail();
    return requestId === softwareDetailRequestId
      && current?.type === model.type
      && current?.id === model.id;
  }

  function collectGpuPlacementProcessObservations(model: SoftwareDetailModel): GpuPlacementObservedProcessInput[] {
    const liveRows = monitor.resourceTableSnapshot()?.rows ?? [];
    const target = {
      name: model.name,
      softwareId: model.id,
      softwareName: model.name
    };
    const matchingRows = liveRows
      .filter((row) => row.kind === "software" || row.kind === "process")
      .filter((row) => softwareContextTargetMatchesRow(target, row));
    const processRows = matchingRows.filter((row) => row.kind === "process");
    const softwareRows = matchingRows.filter((row) => row.kind === "software");
    const observations = [
      ...processRows.map((row) => ({
        processName: textOrEmpty(row.processNames?.[0]) || textOrEmpty(row.name),
        executablePath: textOrEmpty(row.executablePaths?.[0]) || null,
        architecture: null,
        processId: row.processId ?? row.processIds?.[0] ?? null,
        evidenceSources: ["resource-table", "software-detail"]
      })),
      ...softwareRows.flatMap((row) => {
        const names = row.processNames ?? [];
        const paths = row.executablePaths ?? [];
        const ids = row.processIds ?? [];
        return names.map((name, index) => ({
          processName: textOrEmpty(name),
          executablePath: textOrEmpty(paths[index]) || null,
          architecture: null,
          processId: ids[index] ?? null,
          evidenceSources: ["resource-table-software-row", "software-detail"]
        }));
      })
    ];
    const seen = new Set<string>();
    return observations
      .filter((item) => item.processName || item.executablePath)
      .filter((item) => {
        const key = `${normalizeName(item.processName)}|${textOrEmpty(item.executablePath).toLowerCase()}`;
        if (seen.has(key)) {
          return false;
        }

        seen.add(key);
        return true;
      });
  }

  async function saveGpuPlacementSoftwarePolicyFromDetail(policy: GpuPlacementSoftwarePolicy) {
    if (!options.gpuPlacementEnabled()) {
      return;
    }

    try {
      const saved = await saveGpuPlacementSoftwarePolicy(policy);
      setGpuPlacementSettings((current) => current
        ? {
          ...current,
          softwareId: saved.softwareId,
          softwareName: saved.softwareName,
          softwarePolicy: saved
        }
        : current);
      setSoftwareDetailNotice({ message: uiText.softwareActions.softwarePolicySaved, tone: "success" });
    } catch (error) {
      setSoftwareDetailNotice({
        message: uiText.softwareActions.softwarePolicySaveFailed,
        tone: "error",
        details: []
      });
    }
  }

  async function saveGpuPlacementProcessPolicyFromDetail(policy: GpuPlacementProcessPolicy) {
    if (!options.gpuPlacementEnabled()) {
      return;
    }

    try {
      const saved = await saveGpuPlacementProcessPolicy(policy);
      setGpuPlacementSettings((current) => {
        if (!current) {
          return current;
        }

        const nextInterceptions = saved.startupInterception
          ? [
            ...(current.startupInterceptions ?? []).filter(
              (item) => item.processKey !== saved.startupInterception?.processKey),
            saved.startupInterception
          ]
          : current.startupInterceptions;
        return {
          ...current,
          processPolicies: [
            ...current.processPolicies.filter((item) => item.processKey !== saved.policy.processKey),
            saved.policy
          ].sort((left, right) => left.processName.localeCompare(right.processName)),
          startupInterceptions: nextInterceptions
        };
      });
      const runtimePending = saved.runtimeApplicationDisposition !== "committedAndApplied";
      const interceptionPending = saved.startupInterceptionDisposition !== "applied"
        || Boolean(saved.startupInterception?.requested && !saved.startupInterception.registered);
      setSoftwareDetailNotice({
        message: runtimePending
          ? uiText.softwareActions.processPolicySavedRuntimePending
          : interceptionPending
            ? uiText.softwareActions.processPolicySavedLaunchPending
            : uiText.softwareActions.processPolicySaved,
        tone: runtimePending || interceptionPending ? "warning" : "success",
        details: undefined
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: uiText.softwareActions.processPolicySaveFailed,
        tone: "error",
        details: []
      });
    }
  }

  function createComponentDetailModel(component: ManagedComponent): SoftwareDetailModel {
    const definition = component.definition ?? {};
    const name = componentDisplayName(definition.id, definition.name);
    const purpose = componentPurpose(definition.id);
    const installRoot = textOrEmpty(component.installRoot);
    const state = isComponentInstalled(component)
      ? uiText.softwareActions.installed
      : userFacingState(component.stateLabel || component.state);
    const roleLabel = managementRoleLabel(definition.managementRole);
    const rootPaths = uniqueTextValues([installRoot]).filter(pathLooksUsable);
    const rootMigrationPaths = getRootMigrationPaths(rootPaths, migration.roots());
    return {
      type: "component",
      id: textOrEmpty(definition.id),
      name,
      kind: "Managed",
      displayKind: roleLabel,
      state,
      message: purpose,
      dataSearchName: name,
      rootPaths,
      suggestedRootPaths: [],
      executablePaths: [],
      requiresRootPathConfirmation: false,
      identityConfirmed: true,
      issues: [],
      rootMigrationPaths,
      rootMigrationDisabledReason: rootMigrationPaths.length > 0
        ? ""
        : rootPaths.length > 0
          ? uiText.softwareActions.directoryAlreadyTarget
          : uiText.softwareActions.noKnownRoot,
      baseRows: [
        [uiText.softwareActions.field.name, name],
        [uiText.softwareActions.field.relation, roleLabel],
        [uiText.softwareActions.field.state, state],
        [uiText.softwareActions.field.vendor, definition.vendor],
        [uiText.softwareActions.field.category, componentCategory(definition.id)],
        [uiText.softwareActions.field.purpose, purpose]
      ],
      softwareIdentityId: "",
      metadataRows: [],
      pathRows: [
        [uiText.softwareActions.field.installRoot, rootPaths],
        [uiText.softwareActions.field.sourcePage, definition.sourcePageUrl],
        [uiText.softwareActions.field.terms, definition.externalTermsUrl]
      ],
      operationRows: [
        [uiText.softwareActions.field.downloadInstall, formatCapabilityFlags([
          [component.canDownload, uiText.softwareActions.capability.downloadable],
          [component.installerAvailable, uiText.softwareActions.capability.installerReady],
          [component.canInstall, uiText.softwareActions.capability.installable],
          [component.canVerify, uiText.softwareActions.capability.verifiable]
        ])],
        [uiText.softwareActions.field.requiresElevation, formatBoolean(definition.requiresElevation)],
        [uiText.softwareActions.field.requiresExternalTerms, formatBoolean(definition.requiresExternalTermsAcknowledgement)]
      ],
      capabilities: [],
      providers: []
    };
  }

  function createInstalledSoftwareDetailModel(record: SoftwareRecord): SoftwareDetailModel {
    const name = textOrEmpty(record.name) || "--";
    const rootPaths = uniqueTextValues(record.rootPaths ?? []).filter(pathLooksUsable);
    const rootMigrationPaths = getRootMigrationPaths(rootPaths, migration.roots());
    const operations = record.operations ?? {};
    const displayKind = softwareDisplayKindLabel(record.kind, record.displayKind);

    return {
      type: "software",
      id: textOrEmpty(record.id),
      name,
      kind: textOrEmpty(record.kind) || "Other",
      displayKind,
      state: userFacingState(record.state),
      message: userFacingMessage(record.message, displayKind),
      dataSearchName: name,
      rootPaths,
      suggestedRootPaths: uniqueTextValues(record.suggestedRootPaths ?? []).filter(pathLooksUsable),
      executablePaths: uniqueTextValues(record.executablePaths ?? []).filter(pathLooksUsable),
      requiresRootPathConfirmation: record.requiresRootPathConfirmation === true,
      identityConfirmed: record.identityConfirmed !== false,
      issues: record.issues ?? [],
      rootMigrationPaths,
      rootMigrationDisabledReason: rootMigrationPaths.length > 0
        ? ""
        : rootPaths.length > 0
          ? uiText.softwareActions.directoryAlreadyTarget
          : uiText.softwareActions.noUsableRoot,
      baseRows: [
        [uiText.softwareActions.field.name, name],
        [uiText.softwareActions.field.type, displayKind],
        [uiText.softwareActions.field.state, userFacingState(record.state)],
        [uiText.softwareActions.field.stateDescription, userFacingMessage(record.message, displayKind)]
      ],
      softwareIdentityId: textOrEmpty(record.softwareIdentityId),
      metadataRows: [],
      pathRows: [
        [uiText.softwareActions.field.rootDirectory, rootPaths],
        [uiText.softwareActions.field.pendingDirectory, record.requiresRootPathConfirmation ? record.suggestedRootPaths ?? [] : []],
        [uiText.softwareActions.field.executable, record.executablePaths ?? []]
      ],
      operationRows: [
        [uiText.softwareActions.field.uninstallCapability, operations.canUninstall
          ? uiText.softwareActions.canUninstall
          : userFacingMessage(operations.uninstallMessage, uiText.softwareActions.cannotUninstall)],
        [uiText.softwareActions.field.uninstallDescription, userFacingMessage(operations.uninstallMessage, operations.canUninstall ? uiText.softwareActions.uninstallViaSoftware : uiText.softwareActions.cannotUninstall)]
      ],
      capabilities: [],
      providers: []
    };
  }

  async function confirmPortableRootFromDetail() {
    if (!options.mutablePersistenceEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail || !detail.requiresRootPathConfirmation || !detail.id) {
      return;
    }

    try {
      const selectedPath = await pickShellFolder(uiText.softwareActions.pickRootTitle, detail.suggestedRootPaths[0]);
      if (!selectedPath) {
        return;
      }

      setSoftwareDetailActionInProgress(true);
      const result = await confirmPortableSoftwareRoot(detail.id, selectedPath);
      await management.refreshSoftware(true);
      const updated = management.software().find((record) => record.id === detail.id);
      if (updated) {
        const updatedModel = createInstalledSoftwareDetailModel(updated);
        setSoftwareDetail(updatedModel);
        if (updatedModel.softwareIdentityId) {
          bindSoftwareMetadataForDetail(
            updatedModel,
            options.softwareMetadataLanguage());
        }
      }
      setSoftwareDetailNotice({
        message: userFacingMessage(result.message, uiText.softwareActions.rootConfirmed),
        tone: result.requiresRootPathConfirmation ? "warning" : "success",
        details: [result.rootPath]
      });
    } catch (error) {
      setSoftwareDetailNotice({
        message: userFacingErrorMessage(error, uiText.softwareActions.rootConfirmFailed),
        tone: "error"
      });
    } finally {
      setSoftwareDetailActionInProgress(false);
    }
  }

  async function openLocalPath(path: string) {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    try {
      const result = await postJson<{ message?: string; openedPath?: string }>("/api/system/open-path", { path }, uiText.softwareActions.openPathFailed);
      setSoftwareDetailNotice({ message: userFacingMessage(result.message, uiText.softwareActions.openedPath), tone: "success", details: [result.openedPath ?? path] });
    } catch (error) {
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, uiText.softwareActions.openPathFailed), tone: "error", details: [path] });
    }
  }

  async function migrateSoftwareDataFromDetail() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail) {
      return;
    }

    await runSoftwareDetailAction(uiText.softwareActions.findingMigratableContent, async () => {
      migration.setSoftwareName(detail.dataSearchName);
      await migration.findCandidates();
      const candidates = migration.candidates();
      if (!candidates.length) {
        setSoftwareDetailNotice({ message: uiText.softwareActions.noMigratableContent, tone: "warning" });
        return;
      }

      const first = candidates[0];
      openManagementSubpage("Migration");
      migration.fillDataMigration(detail.dataSearchName, [first.path ?? ""]);
      migration.setKind(first.recommendedMigrationKind ?? "Data");
      migration.setTargetCategory(first.recommendedTargetCategory ?? "UserData");
      setSoftwareDetailNotice({
        message: uiText.softwareActions.migratableContentFound,
        tone: "success",
        details: candidates.slice(0, 6).map((candidate) => candidate.path ?? candidate.name ?? "")
      });
    });
  }

  async function migrateSoftwareRootFromDetail() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const detail = softwareDetail();
    if (!detail || detail.rootMigrationPaths.length === 0) {
      return;
    }

    openManagementSubpage("Migration");
    migration.fillRootMigration(detail.name, detail.rootMigrationPaths);
    setSoftwareDetailNotice({
      message: uiText.softwareActions.rootMigrationPrefilled,
      tone: "success",
      details: detail.rootMigrationPaths
    });
  }

  async function runSoftwareDetailAction(progressMessage: string, action: () => Promise<void>) {
    if (softwareDetailActionInProgress()) {
      return;
    }

    setSoftwareDetailActionInProgress(true);
    setSoftwareDetailNotice({ message: progressMessage, tone: "progress" });
    try {
      await action();
    } catch (error) {
      setSoftwareDetailNotice({ message: userFacingErrorMessage(error, uiText.softwareActions.actionFailed), tone: "error" });
    } finally {
      setSoftwareDetailActionInProgress(false);
    }
  }

  async function restoreSoftwareDetailMigrationRecord(record: SoftwareDataMigrationRecord) {
    if (!options.runtimeEffectsEnabled() || !await confirmDialog({
      title: uiText.softwareActions.restoreMigrationTitle,
      message: uiText.softwareActions.restoreMigrationMessage,
      tone: "warning"
    })) {
      return;
    }

    await runSoftwareDetailAction(uiText.softwareActions.restoringMigrationRecord, async () => {
      const result = await restoreMigrationRecord(record);
      setSoftwareDetailNotice({
        message: userFacingMessage(operationResultText(result), uiText.softwareActions.restoreDone),
        tone: "success",
        details: [textOrEmpty(record.sourcePath)].filter(Boolean)
      });
      await Promise.all([migration.refreshRecords(), management.refreshSoftware()]);
    });
  }

  async function restoreAllSoftwareDetailMigrations() {
    if (!options.runtimeEffectsEnabled()) {
      return;
    }

    const records = softwareDetailMigrationRecords().filter(isActiveMigrationRecord);
    if (records.length === 0) {
      setSoftwareDetailNotice({ message: uiText.softwareActions.noRestorableRecord, tone: "warning" });
      return;
    }

    if (!await confirmDialog({
      title: uiText.softwareActions.restoreSoftwareRecordsTitle,
      message: uiText.softwareActions.restoreSoftwareRecordsMessage(records.length),
      tone: "warning"
    })) {
      return;
    }

    await runSoftwareDetailAction(uiText.softwareActions.restoringSoftwareRecords, async () => {
      const restoredPaths: string[] = [];
      for (const record of records) {
        await restoreMigrationRecord(record);
        restoredPaths.push(textOrEmpty(record.sourcePath));
      }

      setSoftwareDetailNotice({
        message: uiText.softwareActions.restoredRecords(restoredPaths.length),
        tone: "success",
        details: restoredPaths.filter(Boolean)
      });
      await Promise.all([migration.refreshRecords(), management.refreshSoftware()]);
    });
  }

  async function restoreMigrationRecord(record: SoftwareDataMigrationRecord) {
    let operation = await options.operations.submit(
      migrationRestoreCommand(record.id, true));
    if (!isTerminalOperation(operation)) {
      operation = await options.operations.waitForTerminal(operation.id);
    }
    if (operation.state !== "succeeded") {
      throw new Error(operation.error
        ?? (operation.state === "stateUncertain"
          ? uiText.softwareActions.restoreUncertain
          : uiText.softwareActions.restoreFailed));
    }
    return operation;
  }

  return {
    softwareDetail,
    softwareDetailNotice,
    softwareDetailMigrationRecords,
    softwareDetailActionInProgress,
    gpuPlacementSettings,
    gpuPlacementLoading,
    contextMenu,
    highlightedSoftwareId,
    inspectOptimizationTarget,
    openSoftwareSettingsById,
    openResourceTableSoftwareContextMenu,
    openManagementSoftwareContextMenu,
    openSoftwareDetail,
    closeSoftwareDetail,
    migrateSoftwareDataFromDetail,
    migrateSoftwareRootFromDetail,
    restoreSoftwareDetailMigrationRecord,
    restoreAllSoftwareDetailMigrations,
    openLocalPath,
    confirmPortableRootFromDetail,
    saveGpuPlacementSoftwarePolicyFromDetail,
    saveGpuPlacementProcessPolicyFromDetail,
    closeContextMenu: () => setContextMenu(null)
  };
}

function operationResultText(operation: OperationSnapshot) {
  return typeof operation.result === "string" ? operation.result : null;
}

function softwareContextTargetMatchesRow(
  target: SoftwareContextMatchTarget,
  row: { softwareId?: string | null; softwareName?: string | null; name: string })
{
  if (target.softwareId && row.softwareId && target.softwareId.toLowerCase() === row.softwareId.toLowerCase()) {
    return true;
  }

  const targetNames = [target.softwareName, target.name].map(normalizeName).filter(Boolean);
  if (targetNames.length === 0) {
    return false;
  }

  const rowNames = [row.softwareName, row.name].map(normalizeName).filter(Boolean);
  return targetNames.some((targetName) => rowNames.some((rowName) => rowName === targetName));
}

function migrationRecordsForDetail(
  detail: SoftwareDetailModel,
  records: SoftwareDataMigrationRecord[])
{
  const detailNames = new Set([
    detail.name,
    detail.dataSearchName,
    detail.id
  ].map(normalizeName).filter(Boolean));

  return records
    .filter((record) => detailNames.has(normalizeName(record.softwareName)))
    .sort(compareMigrationRecords);
}

function compareMigrationRecords(left: SoftwareDataMigrationRecord, right: SoftwareDataMigrationRecord) {
  const leftActive = isActiveMigrationRecord(left);
  const rightActive = isActiveMigrationRecord(right);
  if (leftActive !== rightActive) {
    return leftActive ? -1 : 1;
  }

  return Date.parse(right.createdAt ?? "") - Date.parse(left.createdAt ?? "");
}

function isActiveMigrationRecord(record: SoftwareDataMigrationRecord) {
  return textOrEmpty(record.state).toLowerCase() !== "restored";
}

function uniqueNumbers(values: number[]) {
  return [...new Set(values.filter((value) => Number.isFinite(value) && value > 0))];
}

function uniqueStrings(values: string[]) {
  return [...new Set(values.map(textOrEmpty).filter(Boolean))];
}
