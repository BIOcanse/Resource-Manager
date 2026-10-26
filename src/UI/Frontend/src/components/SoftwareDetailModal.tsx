import { StableLabel } from "../ui/primitives/StableLabel.tsx";
import { formatGpuProviderCapability, formatGpuLaunchOutcome } from "./gpuPlacementPresentation.ts";
import { useFrontendReadIntervals } from "../app/FrontendReadIntervalsContext.tsx";
import { RuntimePolicyDeliveryNotice } from "./RuntimePolicyDeliveryNotice";
import { createEffect, createMemo, createSignal, For, on, onCleanup, Show } from "solid-js";
import { AlertTriangle, FolderOpen } from "lucide-solid";
import { getDefaultGpuPlacementSoftwarePolicy } from "../api";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../frontendWork/FrontendWorkContext";
import {
  DialogActions,
  DialogHeader,
  DialogRoot
} from "../ui/primitives/Dialog.tsx";
import {
  TabsList,
  TabsPanel,
  TabsRoot,
  TabsTrigger
} from "../ui/primitives/Tabs.tsx";
import { StandardSelect } from "./StandardSelect";
import { SoftwareProtectionControl } from "./SoftwareProtectionControl";
import { cpuMaximumOccupancyOptions, runtimeSwitchMethodOptions } from "./softwarePlacementOptions";
import type {
  CpuCcdModel,
  CpuTopologySnapshot,
  DetailNotice,
  DetailRow,
  DetailValue,
  CpuMaximumOccupancyMode,
  GpuPlacementRuntimeSwitchMethod,
  GpuPlacementObservedProcess,
  GpuPlacementProcessPolicy,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareSettingsSnapshot,
  SoftwareDetailModel
} from "../types";
import {
  userFacingDateTime,
  userFacingErrorMessage
} from "../presentation/userFacingText";
import {
  canConfigureRuntimePlacement,
  isUnavailableStartupGpuTarget,
  startupTargetGpuOptionsForSoftwarePolicy,
  type GpuPolicySelectOption
} from "./gpuPlacementTargetOptions";
import type { UserDetailSection } from "../presentation/userDetails";
import { isHttpUrl, pathLooksUsable, textOrEmpty, uniqueTextValues } from "../utils";
import { UserDetailsDialog } from "./UserDetailsDialog";
import { SoftwareIssueDetailSection } from "./SoftwareIssuePresentation";
import { TargetedRecordingControl } from "../features/targetedReport/TargetedRecordingControl.tsx";
import { uiText } from "../text.ts";
import { formatBytes } from "../presentation/byteUnits.ts";
import { SoftwareOverlaySettings } from "../features/overlay/SoftwareOverlaySettings.tsx";
import { moveRovingFocus } from "../ui/primitives/RovingFocus.ts";

type ScalarDetailValueType = Exclude<DetailValue, DetailValue[]>;
type DetailTabId = "overview" | "policy" | "processes" | "overlay";

interface SoftwareDetailModalProps {
  detail: SoftwareDetailModel | null;
  notice: DetailNotice | null;
  actionInProgress: boolean;
  gpuPlacementEnabled: boolean;
  preciseGpuPlacementEnabled: boolean;
  runtimeEffectsEnabled: boolean;
  mutablePersistenceEnabled: boolean;
  gpuPlacementSettings: GpuPlacementSoftwareSettingsSnapshot | null;
  gpuPlacementLoading: boolean;
  onClose: () => void;
  onRemoveManual: () => void;
  onRetryMetadata: () => void;
  onOpenPath: (path: string) => void;
  onConfirmPortableRoot: () => void;
  onSaveGpuPlacementSoftwarePolicy: (policy: GpuPlacementSoftwarePolicy) => Promise<void> | void;
  onSaveGpuPlacementProcessPolicy: (policy: GpuPlacementProcessPolicy) => void;
}

// 文案按当前语言求值，不能在模块顶层固化。
function detailTabLabels(): Record<DetailTabId, string> {
  return {
    overview: uiText.softwareDetail.tab.overview,
    policy: uiText.softwareDetail.tab.policy,
    processes: uiText.softwareDetail.tab.processes,
    overlay: uiText.performanceOverlay.tab,
  };
}

function normalizeSoftwarePolicyForProvider(
  _preciseGpuPlacementEnabled: boolean,
  policy: GpuPlacementSoftwarePolicy): GpuPlacementSoftwarePolicy {
  return policy;
}

function normalizeProcessPolicyForProvider(
  _preciseGpuPlacementEnabled: boolean,
  policy: GpuPlacementProcessPolicy): GpuPlacementProcessPolicy {
  return policy;
}

function cloneSoftwarePolicy(policy: GpuPlacementSoftwarePolicy): GpuPlacementSoftwarePolicy {
  return {
    ...policy,
    allowedProviders: [...policy.allowedProviders],
    cpuManualExclusivePositionIds: [...(policy.cpuManualExclusivePositionIds ?? [])],
    cpuManualLockedPositionIds: [...(policy.cpuManualLockedPositionIds ?? [])]
  };
}

function policyComparableKey(policy: GpuPlacementSoftwarePolicy) {
  return JSON.stringify({
    softwareId: policy.softwareId,
    softwareName: policy.softwareName,
    allowedProviders: sortedPolicyValues(policy.allowedProviders),
    startupTargetGpu: policy.startupTargetGpu,
    runtimeHotSwitchEnabled: policy.runtimeHotSwitchEnabled ?? null,
    preferredRuntimeSwitchMethod: policy.preferredRuntimeSwitchMethod,
    baseScoreOverride: policy.baseScoreOverride ?? null,
    processOverrideAllowed: policy.processOverrideAllowed,
    cpuSchedulingExcluded: policy.cpuSchedulingExcluded,
    memorySchedulingExcluded: policy.memorySchedulingExcluded,
    gpuSchedulingExcluded: policy.gpuSchedulingExcluded,
    cpuMaximumOccupancyMode: policy.cpuMaximumOccupancyMode,
    cpuManualExclusivePositionIds: sortedPolicyValues(policy.cpuManualExclusivePositionIds),
    cpuManualLockedPositionIds: sortedPolicyValues(policy.cpuManualLockedPositionIds)
  });
}

function sortedPolicyValues(values?: readonly string[] | null) {
  return [...(values ?? [])].sort((left, right) => left.localeCompare(right));
}

export function SoftwareDetailModal(props: SoftwareDetailModalProps) {
  let closeButton: HTMLButtonElement | undefined;
  const [activeTab, setActiveTab] = createSignal<DetailTabId>("overview");
  const [selectedProcessKey, setSelectedProcessKey] = createSignal<string | null>(null);
  const [noticeDetailsOpen, setNoticeDetailsOpen] = createSignal(false);
  const detail = () => props.detail;
  const tabs = createMemo<DetailTabId[]>(() => {
    const model = detail();
    const visibleTabs: DetailTabId[] = ["overview"];
    if (model?.type === "software" && props.gpuPlacementEnabled) {
      visibleTabs.push("policy", "processes");
    }
    if (model?.type === "software" && props.mutablePersistenceEnabled) {
      visibleTabs.push("overlay");
    }
    return visibleTabs;
  });
  const processHistory = () => props.gpuPlacementSettings?.processHistory?.processes ?? [];

  createEffect(() => {
    const model = detail();
    if (!model) {
      setActiveTab("overview");
      setSelectedProcessKey(null);
      setNoticeDetailsOpen(false);
      return;
    }

    setActiveTab("overview");
  });

  createEffect(() => {
    if (!tabs().includes(activeTab())) {
      setActiveTab("overview");
    }
  });

  createEffect(() => {
    const processes = processHistory();
    const selected = selectedProcessKey();
    if (processes.length === 0) {
      setSelectedProcessKey(null);
      return;
    }

    if (!selected || !processes.some((process) => process.processKey === selected)) {
      setSelectedProcessKey(processes[0].processKey);
    }
  });

  const saveSoftwarePolicy = (policy: GpuPlacementSoftwarePolicy) => {
    return props.onSaveGpuPlacementSoftwarePolicy(normalizeSoftwarePolicyForProvider(
      props.preciseGpuPlacementEnabled,
      policy));
  };

  const updateProcessPolicy = (process: GpuPlacementObservedProcess, patch: Partial<GpuPlacementProcessPolicy>) => {
    const settings = props.gpuPlacementSettings;
    if (!settings) {
      return;
    }

    const existing = settings.processPolicies.find((policy) => policy.processKey === process.processKey)
      ?? createDefaultProcessPolicy(settings.softwareId, process);
    props.onSaveGpuPlacementProcessPolicy(normalizeProcessPolicyForProvider(
      props.preciseGpuPlacementEnabled,
      {
        ...existing,
        ...patch
      }
    ));
  };

  return (
    <>
      <DialogRoot
        id="softwareDetailModal"
        open={detail() !== null}
        labelledBy="softwareDetailTitle"
        class="detail-modal"
        initialFocus={() => closeButton}
        onDismiss={props.onClose}
      >
        <DialogHeader
          title={detail()?.name ?? uiText.softwareDetail.fallbackTitle}
          titleId="softwareDetailTitle"
          class="detail-modal-header"
          headingClass="detail-modal-heading"
          closeButtonRef={(element) => { closeButton = element; }}
          onDismiss={props.onClose}
        >
            <p>{[detail()?.displayKind, detail()?.state].filter(Boolean).join(" · ")}</p>
        </DialogHeader>

        <TabsRoot
          value={activeTab()}
          class="software-detail-tab-shell"
          onChange={(value) => setActiveTab(value as DetailTabId)}
        >
          <Show when={detail()}>
            <TabsList class="software-detail-tabs" ariaLabel={uiText.softwareDetail.tabsLabel}>
              <For each={tabs()}>
                {(tab) => (
                  <TabsTrigger class="page-tab" value={tab}>
                    {detailTabLabels()[tab]}
                  </TabsTrigger>
                )}
              </For>
            </TabsList>
          </Show>

          <div class="software-detail-body">
          <Show when={props.notice}>
            {(notice) => (
              <div class={`software-detail-notice ${notice().tone ?? "info"}`}>
                <Show when={notice().onRuntimeApplied} fallback={<strong>{notice().message}</strong>}>
                  <RuntimePolicyDeliveryNotice onApplied={() => notice().onRuntimeApplied?.()} />
                </Show>
                <Show when={notice().details?.length}>
                  <button class="secondary details-button" type="button" onClick={() => setNoticeDetailsOpen(true)}>
                    {uiText.softwareDetail.details}
                  </button>
                </Show>
              </div>
            )}
          </Show>

          <Show when={detail()}>
            {(model) => (
              <>
                <TabsPanel value="overview" class="software-detail-tab-panel">
                  <SoftwareIssueDetailSection issues={model().issues} />
                  <DetailSection title={uiText.softwareDetail.section.basic} rows={model().baseRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <Show when={model().metadataStatus === "error" || model().metadataStatus === "unavailable" || model().metadataStatus === "stale"}>
                    <div class="software-detail-notice error" role="alert">
                      <p>{model().metadataRows.length ? uiText.managementFeedback.metadataStale : uiText.managementFeedback.metadataFailed}</p>
                      <button type="button" class="secondary" onClick={props.onRetryMetadata}>{uiText.managementFeedback.retry}</button>
                    </div>
                  </Show>
                  <Show when={model().metadataStatus === "loading" || model().metadataStatus === "refreshing"}>
                    <p role="status">{uiText.managementFeedback.metadataLoading}</p>
                  </Show>
                  <DetailSection title={uiText.softwareDetail.section.metadata} rows={model().metadataRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <DetailSection title={uiText.softwareDetail.section.paths} rows={model().pathRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <Show when={props.mutablePersistenceEnabled && model().type === "software" && model().requiresRootPathConfirmation}>
                    <section class="software-detail-section portable-root-warning">
                      <div>
                        <h3><AlertTriangle aria-hidden="true" size={18} /> {uiText.softwareDetail.rootNeedsConfirmation}</h3>
                        <p>{uiText.softwareDetail.rootNeedsConfirmationDetail}</p>
                      </div>
                      <button type="button" disabled={props.actionInProgress} onClick={props.onConfirmPortableRoot}>
                        <FolderOpen aria-hidden="true" size={17} />
                        {uiText.softwareDetail.chooseFolder}
                      </button>
                    </section>
                  </Show>
                  <Show when={props.mutablePersistenceEnabled && model().manualRegistration}>
                    <button type="button" class="secondary" disabled={props.actionInProgress} onClick={props.onRemoveManual}>
                      {uiText.managementFeedback.removeManual}
                    </button>
                  </Show>
                  <DetailSection title={uiText.softwareDetail.section.operations} rows={model().operationRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <Show when={props.mutablePersistenceEnabled && model().type === "software"}>
                    <TargetedRecordingControl softwareId={model().id} />
                  </Show>
                  <Show when={props.gpuPlacementEnabled && model().type === "software" && !props.preciseGpuPlacementEnabled}>
                    <section class="software-detail-section">
                      <h3>{uiText.softwareDetail.section.gpuScheduling}</h3>
                      <p class="software-detail-empty">{uiText.softwareDetail.ordinaryGpuMode}</p>
                    </section>
                  </Show>
                </TabsPanel>

                <TabsPanel value="policy" class="software-detail-tab-panel">
                  <Show when={model().type === "software"}>
                    <GpuPlacementPolicySection
                      softwareId={model().id}
                      protectionAvailable={props.runtimeEffectsEnabled && props.mutablePersistenceEnabled}
                      settings={props.gpuPlacementSettings}
                      loading={props.gpuPlacementLoading}
                      preciseGpuPlacementEnabled={props.preciseGpuPlacementEnabled}
                      softwareKind={model().kind}
                      onSave={saveSoftwarePolicy}
                    />
                  </Show>
                </TabsPanel>

                <TabsPanel value="processes" class="software-detail-tab-panel">
                  <Show when={model().type === "software"}>
                    <GpuPlacementProcessSection
                      settings={props.gpuPlacementSettings}
                      loading={props.gpuPlacementLoading}
                      preciseGpuPlacementEnabled={props.preciseGpuPlacementEnabled}
                      selectedProcessKey={selectedProcessKey()}
                      onSelectProcess={setSelectedProcessKey}
                      onUpdateProcess={updateProcessPolicy}
                    />
                  </Show>
                </TabsPanel>

                <TabsPanel value="overlay" class="software-detail-tab-panel">
                  <Show when={model().type === "software"}>
                    <SoftwareOverlaySettings softwareId={model().id} active={activeTab() === "overlay"} />
                  </Show>
                </TabsPanel>
              </>
            )}
          </Show>
          </div>
        </TabsRoot>

        <DialogActions class="detail-modal-actions">
          <button class="secondary" type="button" onClick={props.onClose}>{uiText.softwareDetail.close}</button>
        </DialogActions>
      </DialogRoot>
      <UserDetailsDialog
        open={noticeDetailsOpen()}
        title={uiText.softwareDetail.operationDetailTitle}
        summary={props.notice?.message}
        sections={noticeDetailSections(props.notice?.details)}
        onClose={() => setNoticeDetailsOpen(false)}
      />
    </>
  );
}

function noticeDetailSections(details?: readonly string[] | null): UserDetailSection[] {
  const values = details
    ?.flatMap((detail) => detail.split(/\r?\n/))
    .map((detail) => detail.trim())
    .filter(Boolean) ?? [];
  if (values.length === 0) {
    return [];
  }

  return [{
    title: uiText.softwareDetail.section.relatedInfo,
    items: values.map((value, index) => ({
      label: values.length === 1 ? uiText.softwareDetail.contentLabel : uiText.softwareDetail.infoLabel(index + 1),
      value
    }))
  }];
}

function GpuPlacementPolicySection(props: {
  softwareId: string;
  protectionAvailable: boolean;
  settings: GpuPlacementSoftwareSettingsSnapshot | null;
  loading: boolean;
  preciseGpuPlacementEnabled: boolean;
  softwareKind: string;
  onSave: (policy: GpuPlacementSoftwarePolicy) => Promise<void> | void;
}) {
  const frontendRuntime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const readIntervals = useFrontendReadIntervals();
  const [cpuTopology, setCpuTopology] = createSignal<CpuTopologySnapshot | null>(null);
  createEffect(() => {
    const intervalMs = readIntervals.current()?.monitor;
    if (!frontendWork.frontendVisible() || intervalMs === undefined) {
      return;
    }
    const unsubscribe = frontendRuntime.sources.cpuTopology.subscribeSnapshot(intervalMs,
      (snapshot) => setCpuTopology(snapshot.data));
    onCleanup(() => {
      unsubscribe();
          });
  });
  const [draft, setDraft] = createSignal<GpuPlacementSoftwarePolicy | null>(null);
  const [actionInProgress, setActionInProgress] = createSignal(false);
  const [localError, setLocalError] = createSignal<string | null>(null);
  const savedPolicy = () => props.settings?.softwarePolicy ?? null;
  const policy = () => draft() ?? savedPolicy();
  const exactTargets = () => props.settings?.targetInventory?.exactTargets ?? [];
  const startupCapabilityAvailable = () => props.settings?.processCapabilities?.some(
    (capability) => capability.startup.state === "supported") === true;
  const runtimeCapabilityAvailable = () => props.settings?.processCapabilities?.some(
    (capability) => canConfigureRuntimePlacement(capability.runtime.state)) === true;
  const unavailableCapabilityReasons = () => [...new Set((props.settings?.processCapabilities ?? []).flatMap(
    (capability) => [
      formatGpuProviderCapability(uiText.softwareDetail.providerPhase.startup, capability.startup),
      formatGpuProviderCapability(uiText.softwareDetail.providerPhase.runtime, capability.runtime)
    ]))];
  const dirty = () => {
    const saved = savedPolicy();
    const current = draft();
    return Boolean(saved && current && policyComparableKey(saved) !== policyComparableKey(current));
  };
  const updateDraft = (patch: Partial<GpuPlacementSoftwarePolicy>) => {
    setDraft((current) => current
      ? normalizeSoftwarePolicyForProvider(props.preciseGpuPlacementEnabled, { ...current, ...patch })
      : current);
  };
  const saveDraft = async () => {
    const current = draft();
    if (!current) {
      return;
    }

    setActionInProgress(true);
    setLocalError(null);
    try {
      await Promise.resolve(props.onSave(normalizeSoftwarePolicyForProvider(
        props.preciseGpuPlacementEnabled,
        current)));
    } catch (error) {
      setLocalError(userFacingErrorMessage(error, uiText.softwareDetail.saveFailed));
    } finally {
      setActionInProgress(false);
    }
  };
  const restoreDefaultDraft = async () => {
    const settings = props.settings;
    if (!settings) {
      return;
    }

    setActionInProgress(true);
    setLocalError(null);
    try {
      const defaultPolicy = await getDefaultGpuPlacementSoftwarePolicy(
        settings.softwareId,
        settings.softwareName,
        props.softwareKind);
      setDraft(normalizeSoftwarePolicyForProvider(props.preciseGpuPlacementEnabled, defaultPolicy));
    } catch (error) {
      setLocalError(userFacingErrorMessage(error, uiText.softwareDetail.restoreDefaultsFailed));
    } finally {
      setActionInProgress(false);
    }
  };

  const savedPolicyKey = createMemo(() => {
    const saved = savedPolicy();
    return saved ? policyComparableKey(saved) : null;
  });
  createEffect(on(savedPolicyKey, () => {
    const saved = savedPolicy();
    setDraft(saved ? cloneSoftwarePolicy(saved) : null);
    setLocalError(null);
  }));

  return (
    <section class="software-detail-section">
      <div class="software-policy-section-header">
        <h3>{uiText.softwareDetail.policyPanelTitle}</h3>
        <div class="software-policy-actions">
          <button
            class="secondary"
            type="button"
            disabled={props.loading || actionInProgress() || !props.settings}
            onClick={() => void restoreDefaultDraft()}
          >
            {uiText.softwareDetail.restoreDefaults}
          </button>
          <button
            type="button"
            disabled={props.loading || actionInProgress() || !dirty()}
            onClick={() => void saveDraft()}
          >
            <StableLabel labels={{ idle: uiText.softwareDetail.save, busy: uiText.softwareDetail.processing }} current={actionInProgress() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <Show when={!props.loading} fallback={<p class="software-detail-empty">{uiText.softwareDetail.loadingPolicy}</p>}>
        <Show when={policy()} fallback={<p class="software-detail-empty">{uiText.softwareDetail.noPolicyData}</p>}>
          {(current) => (
            <>
              <Show when={localError()}>
                {(message) => <p class="software-detail-hint error">{uiText.softwareDetail.policyActionFailed(message())}</p>}
              </Show>
              <SchedulingParticipationGroup
                softwareId={props.softwareId}
                protectionAvailable={props.protectionAvailable}
                policy={current()}
                onUpdate={updateDraft}
              />
              <div class="software-policy-group">
                <div class="software-policy-grid">
                  <PolicyNumberInput
                    label={uiText.softwareDetail.label.softwareBaseScore}
                    value={current().baseScoreOverride}
                    placeholder={uiText.softwareDetail.label.softwareBaseScorePlaceholder}
                    onChange={(baseScoreOverride) => updateDraft({ baseScoreOverride })}
                  />
                </div>
                <p class="software-detail-hint">
                  {uiText.softwareDetail.baseScoreHint}
                </p>
              </div>
              <div class="software-policy-group">
                <Show when={current().cpuSchedulingExcluded}>
                  <p class="software-detail-hint">{uiText.softwareDetail.participation.cpuExcludedHint}</p>
                </Show>
                <div class="software-policy-grid">
                  <PolicySelect
                    label={uiText.softwareDetail.label.cpuUsageRange}
                    value={current().cpuMaximumOccupancyMode}
                    options={cpuMaximumOccupancyOptions()}
                    disabled={current().cpuSchedulingExcluded}
                    onChange={(value) => updateDraft({ cpuMaximumOccupancyMode: value as CpuMaximumOccupancyMode })}
                  />
                </div>
                <CpuManualPlacementSection
                  topology={cpuTopology()}
                  policy={current()}
                  disabled={current().cpuSchedulingExcluded}
                  onUpdate={updateDraft}
                />
              </div>
              <div class="software-policy-group">
                <Show when={current().gpuSchedulingExcluded}>
                  <p class="software-detail-hint">{uiText.softwareDetail.participation.gpuExcludedHint}</p>
                </Show>
                <div class="software-policy-grid">
                  <PolicySelect
                    label={uiText.softwareDetail.label.startupGpu}
                    value={current().startupTargetGpu}
                    options={startupTargetGpuOptionsForSoftwarePolicy(
                      props.preciseGpuPlacementEnabled && startupCapabilityAvailable(),
                      current().startupTargetGpu,
                      exactTargets())}
                    disabled={current().gpuSchedulingExcluded}
                    onChange={(value) => updateDraft({ startupTargetGpu: value })}
                  />
                  <PolicyToggle
                    label={uiText.softwareDetail.label.allowRuntimeGpuSwitch}
                    checked={current().runtimeHotSwitchEnabled !== false}
                    disabled={current().gpuSchedulingExcluded
                      || (!props.preciseGpuPlacementEnabled || !runtimeCapabilityAvailable())
                        && current().runtimeHotSwitchEnabled === false}
                    onChange={(runtimeHotSwitchEnabled) => updateDraft({ runtimeHotSwitchEnabled })}
                  />
                  <PolicySelect
                    label={uiText.softwareDetail.label.gpuSwitchMethod}
                    value={current().preferredRuntimeSwitchMethod}
                    options={runtimeSwitchMethodOptions()}
                    disabled={current().gpuSchedulingExcluded
                      || !props.preciseGpuPlacementEnabled
                      || !runtimeCapabilityAvailable()}
                    onChange={(value) => updateDraft({ preferredRuntimeSwitchMethod: value as GpuPlacementRuntimeSwitchMethod })}
                  />
                </div>
                <Show when={current().runtimeHotSwitchEnabled !== false}>
                  <p class="gpu-runtime-compatibility-warning">
                    {uiText.softwareDetail.runtimeSwitchWarning}
                  </p>
                </Show>
                <Show when={!props.preciseGpuPlacementEnabled}>
                  <p class="software-detail-hint">
                    {uiText.softwareDetail.preciseDisabledHint}
                  </p>
                </Show>
                {/* 不可用时说后端给的真实原因（缺图形 API、缺 Provider 文件……），没记录到进程时才用通用提示。 */}
                <Show when={props.preciseGpuPlacementEnabled && !startupCapabilityAvailable() && !runtimeCapabilityAvailable()}>
                  <Show
                    when={(props.settings?.processCapabilities?.length ?? 0) > 0}
                    fallback={
                      <p class="software-detail-hint gpu-runtime-compatibility-warning">
                        {uiText.softwareDetail.noNativeProcessHint}
                      </p>
                    }
                  >
                    <For each={unavailableCapabilityReasons()}>
                      {(reason) => (
                        <p class="software-detail-hint gpu-runtime-compatibility-warning">{reason}</p>
                      )}
                    </For>
                  </Show>
                </Show>
              </div>
              <div class="software-policy-group">
                <div class="software-policy-grid">
                  <PolicyToggle
                    label={uiText.softwareDetail.label.allowPerProcessOverride}
                    checked={current().processOverrideAllowed}
                    onChange={(processOverrideAllowed) => updateDraft({ processOverrideAllowed })}
                  />
                </div>
              </div>
            </>
          )}
        </Show>
      </Show>
    </section>
  );
}

// 保护（只防降级，立即保存）与三个“不参与”开关（完全不管对应的域，进策略草稿）各自独立，互不推导、互不联动。
function SchedulingParticipationGroup(props: {
  softwareId: string;
  protectionAvailable: boolean;
  policy: GpuPlacementSoftwarePolicy;
  onUpdate: (patch: Partial<GpuPlacementSoftwarePolicy>) => void;
}) {
  return (
    <div class="software-policy-group">
      <h4>{uiText.softwareDetail.participation.title}</h4>
      <Show when={props.protectionAvailable}>
        <SoftwareProtectionControl softwareId={props.softwareId} />
      </Show>
      <div class="software-policy-grid">
        <PolicyToggle
          label={uiText.softwareDetail.participation.cpu}
          hint={uiText.softwareDetail.participation.cpuHint}
          checked={props.policy.cpuSchedulingExcluded}
          onChange={(cpuSchedulingExcluded) => props.onUpdate({ cpuSchedulingExcluded })}
        />
        <PolicyToggle
          label={uiText.softwareDetail.participation.memory}
          hint={uiText.softwareDetail.participation.memoryHint}
          checked={props.policy.memorySchedulingExcluded}
          onChange={(memorySchedulingExcluded) => props.onUpdate({ memorySchedulingExcluded })}
        />
        <PolicyToggle
          label={uiText.softwareDetail.participation.gpu}
          hint={uiText.softwareDetail.participation.gpuHint}
          checked={props.policy.gpuSchedulingExcluded}
          onChange={(gpuSchedulingExcluded) => props.onUpdate({ gpuSchedulingExcluded })}
        />
      </div>
    </div>
  );
}

function CpuManualPlacementSection(props: {
  topology: CpuTopologySnapshot | null;
  policy: GpuPlacementSoftwarePolicy;
  disabled: boolean;
  onUpdate: (patch: Partial<GpuPlacementSoftwarePolicy>) => void;
}) {
  const exclusiveIds = () => props.policy.cpuManualExclusivePositionIds ?? [];
  const lockedIds = () => props.policy.cpuManualLockedPositionIds ?? [];
  const topology = () => props.topology;
  return (
    <div class="cpu-manual-placement">
      <div class="cpu-manual-placement-header">
        <div>
          <strong>{uiText.softwareDetail.cpu.title}</strong>
          <span>{uiText.softwareDetail.cpu.description}</span>
        </div>
        <button
          class="secondary"
          type="button"
          disabled={props.disabled || exclusiveIds().length === 0 && lockedIds().length === 0}
          onClick={() => props.onUpdate({ cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [] })}
        >
          {uiText.softwareDetail.cpu.clear}
        </button>
      </div>
        <Show when={topology()} fallback={<p class="software-detail-empty">-</p>}>
          {(model) => (
            <>
              <div class="cpu-manual-spec-grid">
                <span>{model().cpuName}</span>
                <span>{model().physicalCoreCount}C / {model().logicalProcessorCount}T</span>
                <span>{model().ccdCount} CCD</span>
                <span>{uiText.softwareDetail.cpu.maxBoost(formatMhz(model().specification.maxClockSpeedMhz))}</span>
                <span>{uiText.softwareDetail.cpu.l1Cache}</span>
                <span>{uiText.softwareDetail.cpu.l2Cache(formatCacheKb(model().specification.l2CacheSizeKb))}</span>
                <span>{uiText.softwareDetail.cpu.l3Cache(formatCacheKb(model().specification.l3CacheSizeKb))}</span>
              </div>
              <div class="cpu-manual-ccd-grid">
                <For each={model().ccds}>
                  {(ccd) => (
                    <article class="cpu-manual-ccd">
                      <div class="cpu-manual-ccd-header">
                        <div>
                          <strong>{`CCD ${ccd.index}`}</strong>
                          <span>{uiText.softwareDetail.cpu.coreCount(ccd.physicalCoreIndexes.length)}</span>
                        </div>
                        <CpuManualPlacementToggles
                          id={ccd.id}
                          exclusiveIds={exclusiveIds()}
                          lockedIds={lockedIds()}
                          disabled={props.disabled}
                          exclusiveDisabled={isCpuManualExclusiveSelectionBlocked(exclusiveIds(), ccd.id, model())}
                          onExclusiveChange={(enabled) => props.onUpdate({
                            cpuManualExclusivePositionIds: toggleCpuManualExclusiveId(exclusiveIds(), ccd.id, enabled, model())
                          })}
                          onLockedChange={(enabled) => props.onUpdate({
                            cpuManualLockedPositionIds: toggleId(lockedIds(), ccd.id, enabled)
                          })}
                        />
                      </div>
                      <div class="cpu-manual-core-grid">
                        <For each={coresForCpuPlacementCcd(model(), ccd)}>
                          {(core) => (
                            <article class="cpu-manual-core">
                              <div>
                                <strong>{uiText.cpuTopology.coreName(core.index)}</strong>
                                <span>{uiText.softwareDetail.cpu.corePerformance(formatScoreValue(core.performanceScore), core.logicalProcessorIds.join(", "))}</span>
                              </div>
                              <CpuManualPlacementToggles
                                id={core.id}
                                exclusiveIds={exclusiveIds()}
                                lockedIds={lockedIds()}
                                disabled={props.disabled}
                                exclusiveDisabled={isCpuManualExclusiveSelectionBlocked(exclusiveIds(), core.id, model())}
                                onExclusiveChange={(enabled) => props.onUpdate({
                                  cpuManualExclusivePositionIds: toggleCpuManualExclusiveId(exclusiveIds(), core.id, enabled, model())
                                })}
                                onLockedChange={(enabled) => props.onUpdate({
                                  cpuManualLockedPositionIds: toggleId(lockedIds(), core.id, enabled)
                                })}
                              />
                            </article>
                          )}
                        </For>
                      </div>
                    </article>
                  )}
                </For>
              </div>
            </>
          )}
        </Show>
    </div>
  );
}

function CpuManualPlacementToggles(props: {
  id: string;
  exclusiveIds: string[];
  lockedIds: string[];
  disabled: boolean;
  exclusiveDisabled?: boolean;
  onExclusiveChange: (enabled: boolean) => void;
  onLockedChange: (enabled: boolean) => void;
}) {
  return (
    <div class="cpu-manual-toggles">
      <label
        classList={{ disabled: props.exclusiveDisabled === true }}
        title={props.exclusiveDisabled === true ? uiText.softwareDetail.cpu.exclusiveDisabled : undefined}
      >
        <input
          type="checkbox"
          checked={hasId(props.exclusiveIds, props.id)}
          disabled={props.disabled || props.exclusiveDisabled === true}
          onChange={(event) => props.onExclusiveChange(event.currentTarget.checked)}
        />
        <span>{uiText.softwareDetail.cpu.reserve}</span>
      </label>
      <label>
        <input
          type="checkbox"
          checked={hasId(props.lockedIds, props.id)}
          disabled={props.disabled}
          onChange={(event) => props.onLockedChange(event.currentTarget.checked)}
        />
        <span>{uiText.softwareDetail.cpu.pin}</span>
      </label>
    </div>
  );
}

function coresForCpuPlacementCcd(model: CpuTopologySnapshot, ccd: CpuCcdModel) {
  const coreIndexes = new Set(ccd.physicalCoreIndexes);
  return model.physicalCores.filter((core) => coreIndexes.has(core.index));
}

function hasId(ids: readonly string[], id: string) {
  return ids.some((item) => item.toLowerCase() === id.toLowerCase());
}

function toggleId(
  ids: readonly string[],
  id: string,
  enabled: boolean) {
  const current = ids.filter((item) => item.toLowerCase() !== id.toLowerCase());
  return enabled ? [...current, id] : current;
}

function toggleCpuManualExclusiveId(
  ids: readonly string[],
  id: string,
  enabled: boolean,
  model: CpuTopologySnapshot) {
  if (enabled && isCpuManualExclusiveSelectionBlocked(ids, id, model)) {
    return [...ids];
  }

  return toggleId(ids, id, enabled);
}

function isCpuManualExclusiveSelectionBlocked(
  ids: readonly string[],
  id: string,
  model: CpuTopologySnapshot) {
  if (hasId(ids, id)) {
    return false;
  }

  const nextIds = toggleId(ids, id, true);
  const selectedCoreIds = expandCpuManualPositionIdsToCoreIds(nextIds, model);
  const allCoreIds = model.physicalCores
    .map((core) => core.id)
    .filter(Boolean);
  if (allCoreIds.length > 0 && allCoreIds.every((coreId) => selectedCoreIds.has(coreId))) {
    return true;
  }

  const fullyReservedCcdCount = model.ccds.filter((ccd) => {
    const coreIds = coresForCpuPlacementCcd(model, ccd).map((core) => core.id);
    return coreIds.length > 0 && coreIds.every((coreId) => selectedCoreIds.has(coreId));
  }).length;
  return model.ccds.length > 1 && fullyReservedCcdCount >= model.ccds.length;
}

function expandCpuManualPositionIdsToCoreIds(
  ids: readonly string[],
  model: CpuTopologySnapshot) {
  const result = new Set<string>();
  for (const id of ids) {
    const ccd = model.ccds.find((item) => item.id.toLowerCase() === id.toLowerCase());
    if (ccd) {
      for (const core of coresForCpuPlacementCcd(model, ccd)) {
        result.add(core.id);
      }
      continue;
    }

    const core = model.physicalCores.find((item) => item.id.toLowerCase() === id.toLowerCase());
    if (core) {
      result.add(core.id);
    }
  }

  return result;
}

function formatMhz(value?: number | null) {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? `${value} MHz`
    : "--";
}

function formatCacheKb(value?: number | null) {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) {
    return "--";
  }

  // 同上：缓存大小以 KiB 计，还原成字节后按内存类换算。
  return formatBytes(value * 1024, "memory");
}

function formatScoreValue(value: number) {
  return Number.isFinite(value)
    ? value.toFixed(value % 1 === 0 ? 0 : 1)
    : "--";
}

function GpuPlacementProcessSection(props: {
  settings: GpuPlacementSoftwareSettingsSnapshot | null;
  loading: boolean;
  preciseGpuPlacementEnabled: boolean;
  selectedProcessKey: string | null;
  onSelectProcess: (processKey: string) => void;
  onUpdateProcess: (process: GpuPlacementObservedProcess, patch: Partial<GpuPlacementProcessPolicy>) => void;
}) {
  const processes = () => props.settings?.processHistory?.processes ?? [];
  const softwarePolicy = () => props.settings?.softwarePolicy ?? null;
  // 软件设为不参与 GPU 调度时，启动前选卡不会执行；勾选保留，但不能再改，也不显示“已就绪”。
  const gpuSchedulingExcluded = () => softwarePolicy()?.gpuSchedulingExcluded === true;
  const selectedProcess = () => processes().find((process) => process.processKey === props.selectedProcessKey) ?? null;
  const selectedPolicy = () => {
    const process = selectedProcess();
    if (!process || !props.settings) {
      return null;
    }

    return props.settings.processPolicies.find((policy) => policy.processKey === process.processKey)
      ?? createDefaultProcessPolicy(props.settings.softwareId, process);
  };
  const selectedInterception = () => {
    const process = selectedProcess();
    return process
      ? props.settings?.startupInterceptions?.find((status) => status.processKey === process.processKey) ?? null
      : null;
  };
  const selectedCapabilities = () => {
    const process = selectedProcess();
    return process
      ? props.settings?.processCapabilities?.find((capability) => capability.processKey === process.processKey) ?? null
      : null;
  };

  return (
    <section class="software-detail-section software-process-policy-section">
      <h3>{uiText.softwareDetail.process.title}</h3>
      <Show when={!props.loading} fallback={<p class="software-detail-empty">{uiText.softwareDetail.process.loading}</p>}>
        <Show when={processes().length > 0} fallback={<p class="software-detail-empty">{uiText.softwareDetail.process.empty}</p>}>
          <div class="process-policy-layout">
            <div
              class="process-policy-list"
              role="listbox"
              aria-label={uiText.softwareDetail.process.listLabel}
              // 单选列表：上下键、Home/End 在进程间移动焦点，选中跟着焦点走（选中只决定右边编辑哪个进程）。
              onKeyDown={(event) => moveRovingFocus(event, {
                selector: "[role='option']",
                previousKeys: ["ArrowUp"],
                nextKeys: ["ArrowDown"],
                ensureVisible: (element) => element.scrollIntoView({ block: "nearest" }),
                activate: (element) => {
                  const processKey = element.dataset.processKey;
                  if (processKey) {
                    props.onSelectProcess(processKey);
                  }
                }
              })}
            >
              <For each={processes()}>
                {(process, index) => (
                  <button
                    type="button"
                    role="option"
                    data-process-key={process.processKey}
                    aria-selected={props.selectedProcessKey === process.processKey}
                    // 只有一个选项在 Tab 顺序里：选中的那个；没有选中时是第一个。
                    tabIndex={props.selectedProcessKey === process.processKey
                      || (selectedProcess() === null && index() === 0) ? 0 : -1}
                    classList={{ active: props.selectedProcessKey === process.processKey }}
                    onClick={() => props.onSelectProcess(process.processKey)}
                  >
                    <strong>{process.processName}</strong>
                    <span>{process.executablePath || uiText.softwareDetail.process.noPath}</span>
                    <em>{formatProcessMeta(process)}</em>
                  </button>
                )}
              </For>
            </div>
            <Show when={selectedProcess() && selectedPolicy()}>
              {(_pair) => {
                const process = () => selectedProcess()!;
                const policy = () => selectedPolicy()!;
                return (
                  <div class="process-policy-editor">
                    <h4>{process().processName}</h4>
                    <DetailSection
                      title={uiText.softwareDetail.process.detailTitle}
                      rows={[
                        [uiText.softwareDetail.process.path, process().executablePath],
                        [uiText.softwareDetail.process.architecture, process().architecture],
                        [uiText.softwareDetail.process.lastPid, process().lastProcessId],
                        [uiText.softwareDetail.process.observationCount, process().observationCount],
                        [uiText.softwareDetail.process.firstObserved, formatDateTime(process().firstObservedAt)],
                        [uiText.softwareDetail.process.lastObserved, formatDateTime(process().lastObservedAt)]
                      ]}
                      onOpenPath={() => undefined}
                    />
                    <div class="software-policy-grid compact">
                      <label class="policy-field policy-toggle-field">
                        <span>{uiText.softwareDetail.label.inheritSoftwareDefault}</span>
                        <input
                          type="checkbox"
                          checked={policy().inherit}
                          onChange={(event) => props.onUpdateProcess(process(), {
                            inherit: event.currentTarget.checked,
                            baseScoreOverride: event.currentTarget.checked ? null : policy().baseScoreOverride
                          })}
                        />
                      </label>
                      <PolicyNumberInput
                        label={uiText.softwareDetail.label.processBaseScore}
                        value={policy().baseScoreOverride}
                        placeholder={uiText.softwareDetail.label.processBaseScorePlaceholder}
                        disabled={policy().inherit}
                        onChange={(baseScoreOverride) => props.onUpdateProcess(process(), { baseScoreOverride })}
                      />
                      <label class="policy-field policy-toggle-field">
                        <span>{uiText.softwareDetail.label.applyGpuBeforeLaunch}</span>
                        <input
                          type="checkbox"
                          checked={policy().startupInterceptionEnabled === true}
                          disabled={gpuSchedulingExcluded()
                            || !process().executablePath
                            || (!props.preciseGpuPlacementEnabled
                              || selectedCapabilities()?.startup.state !== "supported")
                              && policy().startupInterceptionEnabled !== true}
                          onChange={(event) => props.onUpdateProcess(process(), {
                            startupInterceptionEnabled: event.currentTarget.checked
                          })}
                        />
                      </label>
                    </div>
                    <Show when={gpuSchedulingExcluded()}>
                      <p class="software-detail-hint">{uiText.softwareDetail.participation.gpuExcludedHint}</p>
                    </Show>
                    <Show when={!props.preciseGpuPlacementEnabled}>
                      <p class="software-detail-hint">
                        {uiText.softwareDetail.processPreciseDisabledHint}
                      </p>
                    </Show>
                    <Show when={props.preciseGpuPlacementEnabled && selectedCapabilities()}>
                      {(capabilities) => (
                        <p classList={{
                          "software-detail-hint": true,
                          "gpu-runtime-compatibility-warning": capabilities().startup.state !== "supported"
                            || capabilities().runtime.state !== "supported"
                        }}>
                          {formatGpuProviderCapability(uiText.softwareDetail.providerPhase.startup, capabilities().startup)}；
                          {formatGpuProviderCapability(uiText.softwareDetail.providerPhase.runtime, capabilities().runtime)}
                        </p>
                      )}
                    </Show>
                    <Show when={!gpuSchedulingExcluded() && policy().startupInterceptionEnabled === true}>
                      <p classList={{
                        "software-detail-hint": true,
                        "gpu-runtime-compatibility-warning": isUnavailableStartupGpuTarget(softwarePolicy()?.startupTargetGpu)
                          || selectedInterception()?.registered !== true
                      }}>
                        {selectedCapabilities()?.startup.state !== "supported"
                          ? uiText.softwareDetail.startupGpuState.providerUnavailable
                          : isUnavailableStartupGpuTarget(softwarePolicy()?.startupTargetGpu)
                          ? uiText.softwareDetail.startupGpuState.targetNotExecutable
                          : selectedInterception()?.registered
                            ? uiText.softwareDetail.startupGpuState.ready
                            : uiText.softwareDetail.startupGpuState.preparing}
                      </p>
                    </Show>
                    <Show when={selectedInterception()?.recentLaunchResult}>
                      {(recent) => (
                        <p class="software-detail-hint">
                          {uiText.softwareDetail.startupGpuState.recentLaunch(formatGpuLaunchOutcome(recent().outcome), formatDateTime(recent().occurredAt))}
                          {recent().targetAdapterName ? ` · ${recent().targetAdapterName}` : ""}
                          {recent().processId ? ` · PID ${recent().processId}` : ""}
                        </p>
                      )}
                    </Show>
                  </div>
                );
              }}
            </Show>
          </div>
        </Show>
      </Show>
    </section>
  );
}

function PolicySelect(props: {
  label: string;
  value: string;
  options: readonly GpuPolicySelectOption[];
  disabled?: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <label class="policy-field">
      <span>{props.label}</span>
      <StandardSelect
        value={props.value}
        disabled={props.disabled}
        ariaLabel={props.label}
        options={props.options.map(([value, label, disabled]) => ({ value, label, disabled }))}
        onChange={props.onChange}
      />
    </label>
  );
}

function PolicyToggle(props: {
  label: string;
  hint?: string;
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div class="policy-field">
      <label class="policy-field policy-toggle-field">
        <span>{props.label}</span>
        <input
          type="checkbox"
          checked={props.checked}
          disabled={props.disabled}
          onChange={(event) => props.onChange(event.currentTarget.checked)}
        />
      </label>
      <Show when={props.hint}>
        <p class="software-detail-hint">{props.hint}</p>
      </Show>
    </div>
  );
}

function PolicyNumberInput(props: {
  label: string;
  value?: number | null;
  placeholder: string;
  disabled?: boolean;
  onChange: (value: number | null) => void;
}) {
  const textValue = () => typeof props.value === "number" && Number.isFinite(props.value)
    ? String(props.value)
    : "";
  const commit = (raw: string) => {
    const clean = raw.trim();
    if (!clean) {
      props.onChange(null);
      return;
    }

    const parsed = Number(clean);
    props.onChange(Number.isFinite(parsed) ? Math.max(0, Math.min(100, parsed)) : null);
  };

  return (
    <label class="policy-field">
      <span>{props.label}</span>
      <input
        type="number"
        min="0"
        max="100"
        step="1"
        value={textValue()}
        placeholder={props.placeholder}
        disabled={props.disabled}
        onChange={(event) => commit(event.currentTarget.value)}
      />
    </label>
  );
}

function DetailSection(props: { title: string; rows: DetailRow[]; onOpenPath?: (path: string) => void }) {
  const visibleRows = () => props.rows
    .map(([label, value]) => [label, normalizeDetailValue(value)] as DetailRow)
    .filter(([, value]) => hasDetailValue(value));
  return (
    <Show when={visibleRows().length > 0}>
      <section class="software-detail-section">
        <h3>{props.title}</h3>
        <div class="software-detail-grid">
          <For each={visibleRows()}>
            {([label, value]) => (
              <>
                <span class="software-detail-label">{label}</span>
                <div class="software-detail-value">
                  <DetailValue value={value} onOpenPath={props.onOpenPath} />
                </div>
              </>
            )}
          </For>
        </div>
      </section>
    </Show>
  );
}

function DetailValue(props: { value: DetailValue; onOpenPath?: (path: string) => void }) {
  if (Array.isArray(props.value)) {
    return (
      <div class="software-detail-list">
        <For each={props.value}>
          {(item) => (
            <div class="software-detail-list-item">
              <DetailValue value={item} onOpenPath={props.onOpenPath} />
            </div>
          )}
        </For>
      </div>
    );
  }

  return <ScalarDetailValue value={props.value} onOpenPath={props.onOpenPath} />;
}

function ScalarDetailValue(props: { value: ScalarDetailValueType; onOpenPath?: (path: string) => void }) {
  const text = () => String(props.value ?? "");
  return (
    <Show
      when={isHttpUrl(text()) && props.onOpenPath}
      fallback={
        <Show
          when={pathLooksUsable(text()) && props.onOpenPath}
          fallback={<span>{text()}</span>}
        >
          <div class="software-detail-path">
            <code>{text()}</code>
            <button class="software-detail-path-button secondary" type="button" onClick={() => props.onOpenPath?.(text())}>
              {uiText.softwareDetail.openPath}
            </button>
          </div>
        </Show>
      }
    >
      <a href={text()} target="_blank" rel="noopener noreferrer">{text()}</a>
    </Show>
  );
}

function normalizeDetailValue(value: DetailValue): DetailValue {
  if (Array.isArray(value)) {
    return uniqueTextValues(value).filter(Boolean);
  }

  return textOrEmpty(value);
}

function hasDetailValue(value: DetailValue) {
  return Array.isArray(value) ? value.length > 0 : textOrEmpty(value).length > 0;
}

function createDefaultProcessPolicy(
  softwareId: string,
  process: GpuPlacementObservedProcess): GpuPlacementProcessPolicy
{
  return {
    softwareId,
    processKey: process.processKey,
    processName: process.processName,
    executablePath: process.executablePath,
    inherit: true,
    allowedProviders: ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim"],
    baseScoreOverride: null,
    startupInterceptionEnabled: false
  };
}

function formatProcessMeta(process: GpuPlacementObservedProcess) {
  return [
    process.lastProcessId ? `PID ${process.lastProcessId}` : "",
    process.architecture,
    uiText.softwareDetail.process.observedTimes(process.observationCount),
    process.lastObservedAt ? uiText.softwareDetail.process.lastSeen(formatDateTime(process.lastObservedAt)) : ""
  ].filter(Boolean).join(" · ");
}

function formatDateTime(value?: string | null) {
  return value ? userFacingDateTime(value) : "";
}
