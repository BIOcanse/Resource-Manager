import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { AlertTriangle, FolderOpen } from "lucide-solid";
import { getDefaultGpuPlacementSoftwarePolicy } from "../api";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
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
import type {
  CpuCcdModel,
  CpuTopologySnapshot,
  DetailNotice,
  DetailRow,
  DetailValue,
  CpuMaximumOccupancyMode,
  GpuPlacementExplicitSelectionMode,
  GpuPlacementRuntimeSchedulingMode,
  GpuPlacementRuntimeSwitchMethod,
  GpuPlacementSchedulingMode,
  GpuPlacementObservedProcess,
  GpuPlacementProcessPolicy,
  GpuPlacementProviderCapability,
  GpuPlacementSoftwarePolicy,
  GpuPlacementSoftwareSettingsSnapshot,
  SoftwareDataMigrationRecord,
  SoftwareDetailModel
} from "../types";
import {
  migrationTargetCategoryLabel,
  userFacingDateTime,
  userFacingErrorMessage
} from "../presentation/userFacingText";
import {
  isOrdinarySystemGpuTarget,
  isUnavailableStartupGpuTarget,
  startupTargetGpuOptionsForSoftwarePolicy,
  targetGpuOptionsForSoftwarePolicy,
  toOrdinarySystemGpuTarget,
  type GpuPolicySelectOption
} from "../gpuPlacementTargetOptions";
import type { UserDetailSection } from "../presentation/userDetails";
import { isHttpUrl, pathLooksUsable, textOrEmpty, uniqueTextValues } from "../utils";
import { UserDetailsDialog } from "./UserDetailsDialog";
import { SoftwareIssueDetailSection } from "./SoftwareIssuePresentation";

type ScalarDetailValueType = Exclude<DetailValue, DetailValue[]>;
type DetailTabId = "overview" | "policy" | "processes" | "migration";

interface SoftwareDetailModalProps {
  detail: SoftwareDetailModel | null;
  notice: DetailNotice | null;
  migrationRecords: SoftwareDataMigrationRecord[];
  actionInProgress: boolean;
  gpuPlacementEnabled: boolean;
  preciseGpuPlacementEnabled: boolean;
  runtimeEffectsEnabled: boolean;
  mutablePersistenceEnabled: boolean;
  gpuPlacementSettings: GpuPlacementSoftwareSettingsSnapshot | null;
  gpuPlacementLoading: boolean;
  onClose: () => void;
  onMigrateData: () => void;
  onMigrateRoot: () => void;
  onRestoreRecord: (record: SoftwareDataMigrationRecord) => void;
  onRestoreAll: () => void;
  onOpenPath: (path: string) => void;
  onConfirmPortableRoot: () => void;
  onSaveGpuPlacementSoftwarePolicy: (policy: GpuPlacementSoftwarePolicy) => Promise<void> | void;
  onSaveGpuPlacementProcessPolicy: (policy: GpuPlacementProcessPolicy) => void;
}

const detailTabLabels: Record<DetailTabId, string> = {
  overview: "概览",
  policy: "策略",
  processes: "进程",
  migration: "迁移"
};

const policyModeOptions = [
  ["Inherit", "使用默认设置"],
  ["Disabled", "关闭"],
  ["Preview", "仅提示"],
  ["Auto", "自动"],
  ["Manual", "手动"]
] as const;

const riskOptions = [
  ["Low", "仅低风险调整"],
  ["Medium", "允许中等风险调整"],
  ["High", "允许高风险调整"]
] as const;

const schedulingModeOptions = [
  ["Precise", "精确调度"],
  ["Ordinary", "系统兼容调度"]
] as const;

const ordinaryOnlySchedulingModeOptions: readonly GpuPolicySelectOption[] = [
  ["Ordinary", "系统兼容调度"]
] as const;

const runtimeSchedulingModeOptions = [
  ["Precise", "精确调度"],
  ["Ordinary", "系统兼容调度"]
] as const;

const ordinaryOnlyRuntimeSchedulingModeOptions: readonly GpuPolicySelectOption[] = [
  ["Ordinary", "系统兼容调度"]
] as const;

const unavailableSchedulingModeOptions: readonly GpuPolicySelectOption[] = [
  ["Precise", "精确调度（当前不可用）", true],
  ["Ordinary", "系统兼容调度"]
] as const;

const explicitSelectionOptions = [
  ["DefaultSkip", "尊重软件选择"],
  ["PreviewOnly", "仅提示"],
  ["AllowLow", "允许低风险调整"],
  ["AllowMedium", "允许中等风险调整"],
  ["AllowHigh", "允许高风险调整"]
] as const;

const runtimeSwitchMethodOptions = [
  ["FutureFrameTakeover", "后续画面切换"],
  ["WindowRerender", "重新加载窗口画面"]
] as const;

const cpuMaximumOccupancyOptions = [
  ["SingleCcd", "单 CCD"],
  ["AllCores", "全核心"]
] as const;


function normalizeSoftwarePolicyForProvider(
  _preciseGpuPlacementEnabled: boolean,
  policy: GpuPlacementSoftwarePolicy): GpuPlacementSoftwarePolicy {
  return policy;
}

function resolveCpuMaximumOccupancyMode(policy: GpuPlacementSoftwarePolicy): CpuMaximumOccupancyMode {
  return policy.cpuMaximumOccupancyMode === "AllCores"
    ? "AllCores"
    : "SingleCcd";
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
    enabledMode: policy.enabledMode,
    maxRisk: policy.maxRisk,
    allowedProviders: sortedPolicyValues(policy.allowedProviders),
    schedulingMode: policy.schedulingMode,
    startupTargetGpu: policy.startupTargetGpu,
    targetGpu: policy.targetGpu,
    runtimeSchedulingMode: policy.runtimeSchedulingMode,
    explicitSelectionMode: policy.explicitSelectionMode,
    runtimeHotSwitchEnabled: policy.runtimeHotSwitchEnabled ?? null,
    preferredRuntimeSwitchMethod: policy.preferredRuntimeSwitchMethod,
    baseScoreOverride: policy.baseScoreOverride ?? null,
    processOverrideAllowed: policy.processOverrideAllowed,
    keepCpuProcessesOnMainCcd: policy.keepCpuProcessesOnMainCcd ?? null,
    gpuExclusive: policy.gpuExclusive,
    absolutePerformanceModeEnabled: policy.absolutePerformanceModeEnabled,
    cpuMaximumOccupancyMode: policy.cpuMaximumOccupancyMode,
    cpuExclusiveLocksAffinity: policy.cpuExclusiveLocksAffinity,
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
  const activeMigrationRecords = () => props.migrationRecords.filter(isActiveMigrationRecord);
  const tabs = createMemo<DetailTabId[]>(() => {
    const model = detail();
    const visibleTabs: DetailTabId[] = ["overview"];
    if (model?.type === "software" && props.gpuPlacementEnabled) {
      visibleTabs.push("policy", "processes");
    }
    if (props.runtimeEffectsEnabled) {
      visibleTabs.push("migration");
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
          title={detail()?.name ?? "软件详情"}
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
            <TabsList class="software-detail-tabs" ariaLabel="软件详情分页">
              <For each={tabs()}>
                {(tab) => (
                  <TabsTrigger value={tab}>
                    {detailTabLabels[tab]}
                  </TabsTrigger>
                )}
              </For>
            </TabsList>
          </Show>

          <div class="software-detail-body">
          <Show when={props.notice}>
            {(notice) => (
              <div class={`software-detail-notice ${notice().tone ?? "info"}`}>
                <strong>{notice().message}</strong>
                <Show when={notice().details?.length}>
                  <button class="secondary details-button" type="button" onClick={() => setNoticeDetailsOpen(true)}>
                    详细信息
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
                  <DetailSection title="基础信息" rows={model().baseRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <DetailSection title="软件信息" rows={model().metadataRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <DetailSection title="路径和来源" rows={model().pathRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <Show when={props.mutablePersistenceEnabled && model().type === "software" && model().requiresRootPathConfirmation}>
                    <section class="software-detail-section portable-root-warning">
                      <div>
                        <h3><AlertTriangle aria-hidden="true" size={18} /> 根目录需要确认</h3>
                        <p>当前只有可执行文件位置，尚未确认软件根目录。</p>
                      </div>
                      <button type="button" disabled={props.actionInProgress} onClick={props.onConfirmPortableRoot}>
                        <FolderOpen aria-hidden="true" size={17} />
                        选择文件夹
                      </button>
                    </section>
                  </Show>
                  <DetailSection title="操作能力" rows={model().operationRows} onOpenPath={props.runtimeEffectsEnabled ? props.onOpenPath : undefined} />
                  <Show when={props.gpuPlacementEnabled && model().type === "software" && !props.preciseGpuPlacementEnabled}>
                    <section class="software-detail-section">
                      <h3>GPU 调度</h3>
                      <p class="software-detail-empty">当前为普通配置模式，仅使用系统默认 GPU 选项。</p>
                    </section>
                  </Show>
                </TabsPanel>

                <TabsPanel value="policy" class="software-detail-tab-panel">
                  <Show when={model().type === "software"}>
                    <GpuPlacementPolicySection
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

                <TabsPanel value="migration" class="software-detail-tab-panel">
                  <MigrationRecordsSection
                    records={props.migrationRecords}
                    actionInProgress={props.actionInProgress}
                    onRestoreRecord={props.onRestoreRecord}
                    onOpenPath={props.onOpenPath}
                  />
                  <Show when={props.migrationRecords.length === 0}>
                    <section class="software-detail-section">
                      <h3>迁移和恢复</h3>
                      <p class="software-detail-empty">暂无迁移记录。</p>
                    </section>
                  </Show>
                </TabsPanel>
              </>
            )}
          </Show>
          </div>
        </TabsRoot>

        <DialogActions class="detail-modal-actions">
          <Show when={activeTab() === "migration"}>
            <Show when={activeMigrationRecords().length > 0}>
              <button type="button" disabled={props.actionInProgress} onClick={props.onRestoreAll}>
                {props.actionInProgress ? "处理中" : "一键恢复"}
              </button>
            </Show>
            <button class="secondary" type="button" disabled={props.actionInProgress || !detail()?.dataSearchName} onClick={props.onMigrateData}>
              {props.actionInProgress ? "处理中" : "一键迁移数据"}
            </button>
            <button
              type="button"
              disabled={props.actionInProgress || (detail()?.rootMigrationPaths.length ?? 0) === 0}
              title={(detail()?.rootMigrationPaths.length ?? 0) === 0 ? detail()?.rootMigrationDisabledReason ?? "" : ""}
              onClick={props.onMigrateRoot}
            >
              {props.actionInProgress ? "处理中" : "一键迁移整个软件"}
            </button>
          </Show>
          <button class="secondary" type="button" onClick={props.onClose}>关闭</button>
        </DialogActions>
      </DialogRoot>
      <UserDetailsDialog
        open={noticeDetailsOpen()}
        title="操作详情"
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
    title: "相关信息",
    items: values.map((value, index) => ({
      label: values.length === 1 ? "内容" : `信息 ${index + 1}`,
      value
    }))
  }];
}

function GpuPlacementPolicySection(props: {
  settings: GpuPlacementSoftwareSettingsSnapshot | null;
  loading: boolean;
  preciseGpuPlacementEnabled: boolean;
  softwareKind: string;
  onSave: (policy: GpuPlacementSoftwarePolicy) => Promise<void> | void;
}) {
  const frontendRuntime = useFrontendRuntime();
  const [cpuTopology, setCpuTopology] = createSignal<CpuTopologySnapshot | null>(null);
  onCleanup(frontendRuntime.sources.cpuTopology.subscribe(60_000, setCpuTopology));
  const [draft, setDraft] = createSignal<GpuPlacementSoftwarePolicy | null>(null);
  const [actionInProgress, setActionInProgress] = createSignal(false);
  const [localError, setLocalError] = createSignal<string | null>(null);
  const savedPolicy = () => props.settings?.softwarePolicy ?? null;
  const policy = () => draft() ?? savedPolicy();
  const exactTargets = () => props.settings?.targetInventory?.exactTargets ?? [];
  const startupCapabilityAvailable = () => props.settings?.processCapabilities?.some(
    (capability) => capability.startup.state === "supported") === true;
  const runtimeCapabilityAvailable = () => props.settings?.processCapabilities?.some(
    (capability) => capability.runtime.state === "supported") === true;
  const preciseCapabilityAvailable = () => startupCapabilityAvailable() || runtimeCapabilityAvailable();
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
      setLocalError(userFacingErrorMessage(error, "保存失败，请稍后重试"));
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
      setLocalError(userFacingErrorMessage(error, "恢复默认设置失败，请稍后重试"));
    } finally {
      setActionInProgress(false);
    }
  };

  createEffect(() => {
    const saved = savedPolicy();
    setDraft(saved ? cloneSoftwarePolicy(saved) : null);
    setLocalError(null);
  });

  return (
    <section class="software-detail-section">
      <div class="software-policy-section-header">
        <h3>软件调度策略</h3>
        <div class="software-policy-actions">
          <button
            class="secondary"
            type="button"
            disabled={props.loading || actionInProgress() || !props.settings}
            onClick={() => void restoreDefaultDraft()}
          >
            恢复默认
          </button>
          <button
            type="button"
            disabled={props.loading || actionInProgress() || !dirty()}
            onClick={() => void saveDraft()}
          >
            {actionInProgress() ? "处理中" : "保存"}
          </button>
        </div>
      </div>
      <Show when={!props.loading} fallback={<p class="software-detail-empty">正在加载策略...</p>}>
        <Show when={policy()} fallback={<p class="software-detail-empty">暂无策略数据。</p>}>
          {(current) => (
            <>
              <Show when={localError()}>
                {(message) => <p class="software-detail-hint error">策略操作失败：{message()}</p>}
              </Show>
              <div class="software-policy-grid">
                <PolicyNumberInput
                  label="软件基础分"
                  value={current().baseScoreOverride}
                  placeholder="按软件类型默认"
                  onChange={(baseScoreOverride) => updateDraft({ baseScoreOverride })}
                />
                <PolicySelect
                  label="GPU 选择策略"
                  value={current().enabledMode}
                  options={policyModeOptions}
                  onChange={(value) => updateDraft({ enabledMode: value as GpuPlacementSoftwarePolicy["enabledMode"] })}
                />
                <PolicySelect
                  label="允许调整范围"
                  value={current().maxRisk}
                  options={riskOptions}
                  onChange={(value) => updateDraft({ maxRisk: value as GpuPlacementSoftwarePolicy["maxRisk"] })}
                />
                <PolicySelect
                  label="显卡选择方式"
                  value={props.preciseGpuPlacementEnabled ? current().schedulingMode : "Ordinary"}
                  options={props.preciseGpuPlacementEnabled && preciseCapabilityAvailable()
                    ? schedulingModeOptions
                    : current().schedulingMode === "Precise"
                      ? unavailableSchedulingModeOptions
                      : ordinaryOnlySchedulingModeOptions}
                  onChange={(value) => {
                    const schedulingMode = value as GpuPlacementSchedulingMode;
                    updateDraft({
                      schedulingMode,
                      runtimeSchedulingMode: schedulingMode === "Ordinary"
                        ? "Ordinary"
                        : current().runtimeSchedulingMode,
                      startupTargetGpu: schedulingMode === "Ordinary" && !isOrdinarySystemGpuTarget(current().startupTargetGpu)
                        ? "SystemDefaultGpu"
                        : current().startupTargetGpu,
                      targetGpu: schedulingMode === "Ordinary" && !isOrdinarySystemGpuTarget(current().targetGpu)
                        ? "SystemDefaultGpu"
                        : current().targetGpu
                    });
                  }}
                />
                <PolicySelect
                  label="启动时 GPU"
                  value={current().startupTargetGpu}
                  options={startupTargetGpuOptionsForSoftwarePolicy(
                    props.preciseGpuPlacementEnabled && startupCapabilityAvailable(),
                    current().schedulingMode,
                    current().startupTargetGpu,
                    exactTargets())}
                  onChange={(value) => updateDraft({ startupTargetGpu: value })}
                />
                <PolicySelect
                  label="运行时选择方式"
                  value={props.preciseGpuPlacementEnabled ? current().runtimeSchedulingMode : "Ordinary"}
                  options={props.preciseGpuPlacementEnabled && runtimeCapabilityAvailable()
                    ? runtimeSchedulingModeOptions
                    : current().runtimeSchedulingMode === "Precise"
                      ? unavailableSchedulingModeOptions
                      : ordinaryOnlyRuntimeSchedulingModeOptions}
                  onChange={(value) => {
                    const runtimeSchedulingMode = value as GpuPlacementRuntimeSchedulingMode;
                    updateDraft({
                      runtimeSchedulingMode,
                      targetGpu: runtimeSchedulingMode === "Ordinary" && !isOrdinarySystemGpuTarget(current().targetGpu)
                        ? "SystemDefaultGpu"
                        : current().targetGpu
                    });
                  }}
                />
                <PolicySelect
                  label="运行时目标 GPU"
                  value={props.preciseGpuPlacementEnabled
                    ? current().targetGpu
                    : toOrdinarySystemGpuTarget(current().targetGpu)}
                  options={targetGpuOptionsForSoftwarePolicy(
                    props.preciseGpuPlacementEnabled && runtimeCapabilityAvailable(),
                    current().schedulingMode,
                    current().runtimeSchedulingMode,
                    props.preciseGpuPlacementEnabled
                      ? current().targetGpu
                      : toOrdinarySystemGpuTarget(current().targetGpu),
                    exactTargets())}
                  onChange={(value) => updateDraft({ targetGpu: value })}
                />
                <PolicySelect
                  label="软件已选显卡时"
                  value={current().explicitSelectionMode}
                  options={explicitSelectionOptions}
                  onChange={(value) => updateDraft({ explicitSelectionMode: value as GpuPlacementExplicitSelectionMode })}
                />
                <label class="policy-field policy-toggle-field">
                  <span>允许运行时切换显卡</span>
                  <input
                    type="checkbox"
                    checked={current().runtimeHotSwitchEnabled !== false}
                    disabled={(!props.preciseGpuPlacementEnabled || !runtimeCapabilityAvailable())
                      && current().runtimeHotSwitchEnabled === false}
                    onChange={(event) => updateDraft({ runtimeHotSwitchEnabled: event.currentTarget.checked })}
                  />
                </label>
                <label class="policy-field policy-toggle-field">
                  <span>优先保留目标显卡</span>
                  <input
                    type="checkbox"
                    checked={current().gpuExclusive === true}
                    onChange={(event) => updateDraft({ gpuExclusive: event.currentTarget.checked })}
                  />
                </label>
                <PolicySelect
                  label="显卡切换方式"
                  value={current().preferredRuntimeSwitchMethod}
                  options={runtimeSwitchMethodOptions}
                  disabled={!props.preciseGpuPlacementEnabled || !runtimeCapabilityAvailable()}
                  onChange={(value) => updateDraft({ preferredRuntimeSwitchMethod: value as GpuPlacementRuntimeSwitchMethod })}
                />
                <label class="policy-field policy-toggle-field">
                  <span>允许按进程单独设置</span>
                  <input
                    type="checkbox"
                    checked={current().processOverrideAllowed}
                    onChange={(event) => updateDraft({ processOverrideAllowed: event.currentTarget.checked })}
                  />
                </label>
                <PolicySelect
                  label="CPU 使用范围"
                  value={resolveCpuMaximumOccupancyMode(current())}
                  options={cpuMaximumOccupancyOptions}
                  onChange={(value) => {
                    const cpuMaximumOccupancyMode = value as CpuMaximumOccupancyMode;
                    updateDraft({
                      cpuMaximumOccupancyMode,
                      keepCpuProcessesOnMainCcd: cpuMaximumOccupancyMode === "SingleCcd"
                    });
                  }}
                />
                <label class="policy-field policy-toggle-field">
                  <span>优先保障性能</span>
                  <input
                    type="checkbox"
                    checked={current().absolutePerformanceModeEnabled === true}
                    onChange={(event) => updateDraft({ absolutePerformanceModeEnabled: event.currentTarget.checked })}
                  />
                </label>
                <label class="policy-field policy-toggle-field">
                  <span>固定到所选核心</span>
                  <input
                    type="checkbox"
                    checked={current().cpuExclusiveLocksAffinity === true}
                    onChange={(event) => updateDraft({ cpuExclusiveLocksAffinity: event.currentTarget.checked })}
                  />
                </label>
              </div>
              <Show when={current().runtimeHotSwitchEnabled !== false}>
                <p class="gpu-runtime-compatibility-warning">
                  运行时切换显卡可能导致黑屏、画面异常或软件崩溃。出现兼容问题时，请关闭此项并重启该软件。
                </p>
              </Show>
              <CpuManualPlacementSection
                topology={cpuTopology()}
                policy={current()}
                onUpdate={updateDraft}
              />
              <p class="software-detail-hint">
                软件基础分会作为所属进程的默认评分。核心独占会让其他软件避开所选核心；固定后，此软件只会使用所选核心。
              </p>
              <Show when={current().schedulingMode === "Precise" && current().runtimeSchedulingMode === "Ordinary"}>
                <p class="software-detail-hint">
                  当前使用系统兼容调度：启动设置仍会保留，运行中不会移动显卡。
                </p>
              </Show>
              <Show when={!props.preciseGpuPlacementEnabled}>
                <p class="software-detail-hint">
                  全局精确 GPU 选择当前关闭；软件偏好仍会保存，但只会应用 Windows 支持的显卡选择。
                </p>
              </Show>
              <Show when={props.preciseGpuPlacementEnabled && !startupCapabilityAvailable() && !runtimeCapabilityAvailable()}>
                <p class="software-detail-hint gpu-runtime-compatibility-warning">
                  尚未发现兼容的原生 x64 进程。精确 Provider 仅影响之后创建或重建的 D3D11 设备；已保存的精确设置会保留但不会执行。
                </p>
              </Show>
            </>
          )}
        </Show>
      </Show>
    </section>
  );
}

function CpuManualPlacementSection(props: {
  topology: CpuTopologySnapshot | null;
  policy: GpuPlacementSoftwarePolicy;
  onUpdate: (patch: Partial<GpuPlacementSoftwarePolicy>) => void;
}) {
  const exclusiveIds = () => props.policy.cpuManualExclusivePositionIds ?? [];
  const lockedIds = () => props.policy.cpuManualLockedPositionIds ?? [];
  const topology = () => props.topology;
  return (
    <div class="cpu-manual-placement">
      <div class="cpu-manual-placement-header">
        <div>
          <strong>CPU 核心分配</strong>
          <span>“保留”会让其他软件避开；“固定”会限制当前软件使用范围。</span>
        </div>
        <button
          class="secondary"
          type="button"
          disabled={exclusiveIds().length === 0 && lockedIds().length === 0}
          onClick={() => props.onUpdate({ cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [] })}
        >
          清空
        </button>
      </div>
        <Show when={topology()} fallback={<p class="software-detail-empty">-</p>}>
          {(model) => (
            <>
              <div class="cpu-manual-spec-grid">
                <span>{model().cpuName}</span>
                <span>{model().physicalCoreCount}C / {model().logicalProcessorCount}T</span>
                <span>{model().ccdCount} CCD</span>
                <span>最大加速 {formatMhz(model().specification.maxClockSpeedMhz)}</span>
                <span>L1 缓存 --</span>
                <span>L2 缓存 {formatCacheKb(model().specification.l2CacheSizeKb)}</span>
                <span>L3 缓存 {formatCacheKb(model().specification.l3CacheSizeKb)}</span>
              </div>
              <div class="cpu-manual-ccd-grid">
                <For each={model().ccds}>
                  {(ccd) => (
                    <article class="cpu-manual-ccd">
                      <div class="cpu-manual-ccd-header">
                        <div>
                          <strong>{ccd.label}</strong>
                          <span>{ccd.physicalCoreIndexes.length} 个核心</span>
                        </div>
                        <CpuManualPlacementToggles
                          id={ccd.id}
                          exclusiveIds={exclusiveIds()}
                          lockedIds={lockedIds()}
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
                                <strong>{core.label}</strong>
                                <span>性能等级 {formatScoreValue(core.performanceScore)} · 逻辑处理器 {core.logicalProcessorIds.join(", ")}</span>
                              </div>
                              <CpuManualPlacementToggles
                                id={core.id}
                                exclusiveIds={exclusiveIds()}
                                lockedIds={lockedIds()}
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
  exclusiveDisabled?: boolean;
  onExclusiveChange: (enabled: boolean) => void;
  onLockedChange: (enabled: boolean) => void;
}) {
  return (
    <div class="cpu-manual-toggles">
      <label
        classList={{ disabled: props.exclusiveDisabled === true }}
        title={props.exclusiveDisabled === true ? "不能独占全部核心或全部 CCD" : undefined}
      >
        <input
          type="checkbox"
          checked={hasId(props.exclusiveIds, props.id)}
          disabled={props.exclusiveDisabled === true}
          onChange={(event) => props.onExclusiveChange(event.currentTarget.checked)}
        />
        <span>保留</span>
      </label>
      <label>
        <input
          type="checkbox"
          checked={hasId(props.lockedIds, props.id)}
          onChange={(event) => props.onLockedChange(event.currentTarget.checked)}
        />
        <span>固定</span>
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

  return value >= 1024
    ? `${(value / 1024).toFixed(value >= 10240 ? 1 : 2)} MB`
    : `${value} KB`;
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
      <h3>历史进程与进程级覆盖</h3>
      <Show when={!props.loading} fallback={<p class="software-detail-empty">正在加载进程历史...</p>}>
        <Show when={processes().length > 0} fallback={<p class="software-detail-empty">暂无历史进程。打开软件运行一段时间后会逐步记录。</p>}>
          <div class="process-policy-layout">
            <div class="process-policy-list" role="listbox" aria-label="历史进程">
              <For each={processes()}>
                {(process) => (
                  <button
                    type="button"
                    role="option"
                    aria-selected={props.selectedProcessKey === process.processKey}
                    classList={{ active: props.selectedProcessKey === process.processKey }}
                    onClick={() => props.onSelectProcess(process.processKey)}
                  >
                    <strong>{process.processName}</strong>
                    <span>{process.executablePath || "无路径"}</span>
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
                      title="进程信息"
                      rows={[
                        ["路径", process().executablePath],
                        ["架构", process().architecture],
                        ["最近 PID", process().lastProcessId],
                        ["观察次数", process().observationCount],
                        ["首次观察", formatDateTime(process().firstObservedAt)],
                        ["最后观察", formatDateTime(process().lastObservedAt)]
                      ]}
                      onOpenPath={() => undefined}
                    />
                    <div class="software-policy-grid compact">
                      <label class="policy-field policy-toggle-field">
                        <span>继承软件默认</span>
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
                        label="进程基础分"
                        value={policy().baseScoreOverride}
                        placeholder="继承软件基础分"
                        disabled={policy().inherit}
                        onChange={(baseScoreOverride) => props.onUpdateProcess(process(), { baseScoreOverride })}
                      />
                      <PolicySelect
                        label="GPU 选择策略"
                        value={policy().enabledMode}
                        options={policyModeOptions}
                        onChange={(value) => props.onUpdateProcess(process(), { enabledMode: value as GpuPlacementProcessPolicy["enabledMode"] })}
                      />
                      <PolicySelect
                        label="允许调整范围"
                        value={policy().maxRisk}
                        options={riskOptions}
                        onChange={(value) => props.onUpdateProcess(process(), { maxRisk: value as GpuPlacementProcessPolicy["maxRisk"] })}
                      />
                      <PolicySelect
                        label="运行时目标 GPU"
                        value={props.preciseGpuPlacementEnabled
                          ? policy().targetGpu
                          : toOrdinarySystemGpuTarget(policy().targetGpu)}
                        options={targetGpuOptionsForSoftwarePolicy(
                          props.preciseGpuPlacementEnabled && selectedCapabilities()?.runtime.state === "supported",
                          softwarePolicy()?.schedulingMode ?? "Precise",
                          softwarePolicy()?.runtimeSchedulingMode ?? "Precise",
                          props.preciseGpuPlacementEnabled
                            ? policy().targetGpu
                            : toOrdinarySystemGpuTarget(policy().targetGpu),
                          props.settings?.targetInventory?.exactTargets ?? [])}
                        onChange={(value) => props.onUpdateProcess(process(), { targetGpu: value })}
                      />
                      <PolicySelect
                        label="软件已选显卡时"
                        value={policy().explicitSelectionMode}
                        options={explicitSelectionOptions}
                        onChange={(value) => props.onUpdateProcess(process(), { explicitSelectionMode: value as GpuPlacementExplicitSelectionMode })}
                      />
                      <label class="policy-field policy-toggle-field">
                        <span>启动前应用显卡设置</span>
                        <input
                          type="checkbox"
                          checked={policy().startupInterceptionEnabled === true}
                          disabled={!process().executablePath
                            || (!props.preciseGpuPlacementEnabled
                              || selectedCapabilities()?.startup.state !== "supported")
                              && policy().startupInterceptionEnabled !== true}
                          onChange={(event) => props.onUpdateProcess(process(), {
                            startupInterceptionEnabled: event.currentTarget.checked
                          })}
                        />
                      </label>
                    </div>
                    <Show when={!props.preciseGpuPlacementEnabled}>
                      <p class="software-detail-hint">
                        全局精确 GPU 选择当前关闭；进程设置仍会保存，但当前只会应用 Windows 支持的显卡选择。
                      </p>
                    </Show>
                    <Show when={props.preciseGpuPlacementEnabled && selectedCapabilities()}>
                      {(capabilities) => (
                        <p classList={{
                          "software-detail-hint": true,
                          "gpu-runtime-compatibility-warning": capabilities().startup.state !== "supported"
                            || capabilities().runtime.state !== "supported"
                        }}>
                          {formatProviderCapability("启动", capabilities().startup)}；
                          {formatProviderCapability("运行", capabilities().runtime)}
                        </p>
                      )}
                    </Show>
                    <Show when={policy().startupInterceptionEnabled === true}>
                      <p classList={{
                        "software-detail-hint": true,
                        "gpu-runtime-compatibility-warning": isUnavailableStartupGpuTarget(softwarePolicy()?.startupTargetGpu)
                          || selectedInterception()?.registered !== true
                      }}>
                        {selectedCapabilities()?.startup.state !== "supported"
                          ? "启动期精确 Provider 当前不可用；设置已保留但不会执行。"
                          : isUnavailableStartupGpuTarget(softwarePolicy()?.startupTargetGpu)
                          ? "当前启动目标不可执行；下次启动将保持系统默认显卡。"
                          : selectedInterception()?.registered
                            ? "已就绪，将在下次启动时应用启动显卡设置。"
                            : "正在准备启动显卡设置。"}
                      </p>
                    </Show>
                    <Show when={selectedInterception()?.recentLaunchResult}>
                      {(recent) => (
                        <p class="software-detail-hint">
                          最近启动：{formatGpuLaunchOutcome(recent().outcome)} · {formatDateTime(recent().occurredAt)}
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

function formatProviderCapability(
  phase: string,
  capability: GpuPlacementProviderCapability) {
  const state = capability.state === "supported"
    ? "可用"
    : capability.state === "unsupported"
      ? "不支持"
      : "未知";
  const architecture = capability.architecture ? ` / ${capability.architecture}` : "";
  return `${phase} ${state}（${capability.graphicsApi}${architecture}）：${capability.reason}`;
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

function MigrationRecordsSection(props: {
  records: SoftwareDataMigrationRecord[];
  actionInProgress: boolean;
  onRestoreRecord: (record: SoftwareDataMigrationRecord) => void;
  onOpenPath: (path: string) => void;
}) {
  return (
    <Show when={props.records.length > 0}>
      <section class="software-detail-section">
        <h3>迁移和恢复</h3>
        <div class="software-detail-migration-list">
          <For each={props.records}>
            {(record) => {
              const restored = () => !isActiveMigrationRecord(record);
              return (
                <article class="software-detail-migration-record" classList={{ restored: restored() }}>
                  <div class="software-detail-migration-main">
                    <strong>{formatMigrationRecordTitle(record)}</strong>
                    <span>{formatMigrationRecordMeta(record)}</span>
                  </div>
                  <div class="software-detail-migration-paths">
                    <PathLine label="原路径" path={record.sourcePath} onOpenPath={props.onOpenPath} />
                    <PathLine label="托管路径" path={record.destinationPath} onOpenPath={props.onOpenPath} />
                  </div>
                  <button
                    class="secondary"
                    type="button"
                    disabled={props.actionInProgress || restored()}
                    onClick={() => props.onRestoreRecord(record)}
                  >
                    {restored() ? "已恢复" : "恢复"}
                  </button>
                </article>
              );
            }}
          </For>
        </div>
      </section>
    </Show>
  );
}

function PathLine(props: { label: string; path?: string | null; onOpenPath: (path: string) => void }) {
  const path = () => textOrEmpty(props.path);
  return (
    <Show when={path()}>
      <div class="software-detail-migration-path">
        <span>{props.label}</span>
        <code>{path()}</code>
        <button class="software-detail-path-button secondary" type="button" onClick={() => props.onOpenPath(path())}>
          打开
        </button>
      </div>
    </Show>
  );
}

function formatMigrationRecordTitle(record: SoftwareDataMigrationRecord) {
  const kind = record.migrationKind === "Root" ? "整个软件" : "数据";
  const state = isActiveMigrationRecord(record) ? "已迁移" : "已恢复";
  return `${kind} · ${state}`;
}

function formatMigrationRecordMeta(record: SoftwareDataMigrationRecord) {
  return [
    migrationTargetCategoryLabel(record.targetCategory),
    record.createdAt ? `迁移于 ${new Date(record.createdAt).toLocaleString()}` : "",
    record.restoredAt ? `恢复于 ${new Date(record.restoredAt).toLocaleString()}` : ""
  ].filter(Boolean).join(" · ");
}

function isActiveMigrationRecord(record: SoftwareDataMigrationRecord) {
  return textOrEmpty(record.state).toLowerCase() !== "restored";
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
              打开
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
    enabledMode: "Inherit",
    maxRisk: "Low",
    allowedProviders: ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim"],
    targetGpu: "SystemDefaultGpu",
    explicitSelectionMode: "DefaultSkip",
    baseScoreOverride: null,
    startupInterceptionEnabled: false
  };
}

function formatProcessMeta(process: GpuPlacementObservedProcess) {
  return [
    process.lastProcessId ? `PID ${process.lastProcessId}` : "",
    process.architecture,
    `观察 ${process.observationCount} 次`,
    process.lastObservedAt ? `最后 ${formatDateTime(process.lastObservedAt)}` : ""
  ].filter(Boolean).join(" · ");
}

function formatDateTime(value?: string | null) {
  return value ? userFacingDateTime(value) : "";
}

function formatGpuLaunchOutcome(outcome: string) {
  switch (outcome) {
    case "provider-ready":
      return "显卡设置已就绪";
    case "pass-through-started":
      return "使用系统默认设置";
    case "provider-unavailable-fallback":
      return "已改用系统默认设置";
    case "bootstrap-failed-fallback":
      return "已改用系统默认设置";
    case "provider-timeout":
      return "准备超时，已改用系统默认设置";
    case "debugger-detach-failed":
      return "启动设置未能完成";
    case "recursion-blocked":
      return "已停止重复启动";
    case "launch-failed":
      return "软件启动失败";
    default:
      return "启动结果未知";
  }
}
