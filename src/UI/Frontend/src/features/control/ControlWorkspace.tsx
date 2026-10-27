import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { controlReasonText } from "../../presentation/controlReasonText.ts";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { createMemo, createSignal, For, Match, onMount, onCleanup, Show, Switch } from "solid-js";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { Portal } from "solid-js/web";
import { CircleDashed, Download, Lock, X } from "lucide-solid";
import {
  acknowledgeControlNotice,
  setControlObjectSettings,
  deleteControlPreset,
  forgetControlInstance,
  retryControlObject,
  redetectControlPlatform,
  setControlOverclockConsent,
  refreshControlInstances,
  saveControlPreset
} from "./controlApi.ts";
import {
  ControlCapabilityEditor,
  actualRepeatsNumberEditor,
  formatActualNumber,
  initialSettingFor
} from "./ControlCapabilityEditor";
import { ControlFirstRunNotice } from "./ControlFirstRunNotice";
import { controlItemTone } from "../../presentation/statusTone.ts";
import { controlCapabilityLabel, controlDisplayText, controlFanGpuName, controlOutcomeReason, controlObjectName } from "./controlPresentation.ts";
import { GpuNameText } from "../monitor/GpuNameNote.tsx";
import {
  controlItemStatusOf,
  canRetryControlObject,
  hasPendingChanges
} from "./controlStateMachine.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { acceptSourceCommand } from "../../frontendRuntime/source/acceptSourceCommand.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { RadioGroupRoot, RadioGroupItem } from "../../ui/primitives/RadioGroup";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { uiText } from "../../text.ts";
import { currentLanguage } from "../../i18n/appTextStore.ts";
import { controlAccessLevelAllows } from "./controlTypes.ts";
import type {
  ControlAccessLevel,
  ControlActualState,
  ControlActualValue,
  ControlCapability,
  ControlPreset,
  ControlObject,
  ControlObjectDesiredState,
  ControlStateView,
  ControlInstanceCatalog,
  ControlSetting
} from "./controlTypes.ts";

export function ControlWorkspace(props: { onNotice: (message: string) => void }) {
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const frontendWork = useFrontendWork();
  useFrontendVisibilityDemand("control.workspace", [frontendWorkIds.controlActual]);
  const visible = () => frontendWork.isNeeded(frontendWorkIds.controlActual);
  const demand = () => ({ active: visible(), refreshIntervalMs: null });
  const objectsSource = useSource(runtime.sources.control.objects, demand);
  const stateSource = useSource(runtime.sources.control.state, demand);
  const instancesSource = useSource(runtime.sources.control.instances, demand);
  const presetsSource = useSource(runtime.sources.control.presets, demand);
  const consentSource = useSource(runtime.sources.control.overclockConsent, demand);
  const accessSource = useSource(runtime.sources.control.accessLevel, demand);
  const noticeSource = useSource(runtime.sources.control.notice, demand);
  const sources = [objectsSource, stateSource, instancesSource, presetsSource, consentSource, accessSource, noticeSource];
  const reloadState = () => { for (const source of sources) void source.refresh(); };
  onMount(() => {
    window.addEventListener("focus", reloadState);
    onCleanup(() => window.removeEventListener("focus", reloadState));
  });
  const catalog = () => objectsSource.snapshot().data;
  const state = () => stateSource.snapshot().data;
  const instances = () => instancesSource.snapshot().data;
  const presets = () => presetsSource.snapshot().data;
  const failed = () => ["error", "unavailable"].includes(objectsSource.snapshot().status);
  const stateLoad = () => sourceCanRender(stateSource.snapshot()) ? "ready"
    : ["error", "unavailable"].includes(stateSource.snapshot().status) ? "error" : "loading";
  const overclockAccepted = () => consentSource.snapshot().data;
  const accessLevel = () => accessSource.snapshot().data ?? "normal";
  const noticeSeen = () => noticeSource.snapshot().data;
  const noticeReadFailed = () => ["error", "unavailable"].includes(noticeSource.snapshot().status);
  const [managingInstances, setManagingInstances] = createSignal(false);
  const [noticeAckFailed, setNoticeAckFailed] = createSignal(false);
  const [acknowledging, setAcknowledging] = createSignal(false);

  /** 正在重新检测。按钮期间禁用，免得连点起一串子进程。 */
  const [detecting, setDetecting] = createSignal(false);

  /**
   * 正在编的那一份，**整页共用一份**。
   *
   * 改动只留在本地，点「应用」才写下去 —— 拖曲线、拉滑块不会每动一下就走一次往返
   * （那样回包还会把正在拖的值盖掉，反而拖不动）。
   *
   * 草稿留在页面，避免设备清单刷新丢失编辑。应用和撤销以对象为范围。
   */
  const [draft, setDraft] = createSignal<readonly ControlObjectDesiredState[] | null>(null);
  // Unsaved ownership choice belongs to the page draft, not a refreshed device card.
  const [fanModes, setFanModes] = createSignal<ReadonlyMap<string, string>>(new Map());
  const chooseFanMode = (id: string, mode: string | null) => setFanModes(current => {
    const next = new Map(current); if (mode === null) next.delete(id); else next.set(id, mode); return next;
  });
  const [takingOver, setTakingOver] = createSignal<ReadonlySet<string>>(new Set());
  const chooseOwnership = (objectId: string, enabled: boolean) => setTakingOver(current => {
    const next = new Set(current);
    if (enabled) next.add(objectId);
    else next.delete(objectId);
    return next;
  });
  const savedObjects = () => state()?.desired.objects ?? [];
  const edited = () => draft() ?? savedObjects();

  const settingsOf = (objectId: string) =>
    edited().find((entry) => entry.objectId === objectId)?.settings ?? [];

  const editCapability = (
    objectId: string,
    capabilityId: string,
    next: ControlSetting | null
  ) => {
    const rest = settingsOf(objectId)
      .filter((entry) => entry.capabilityId !== capabilityId);
    const settings = next ? [...rest, next] : rest;
    const others = edited().filter((entry) => entry.objectId !== objectId);
    // 空设定的对象整条去掉：状态机那边"不管这个对象"就是这么表达的。
    setDraft(settings.length > 0
      ? [...others, { objectId, settings }]
      : others);
  };

  const [saving, setSaving] = createSignal(false);
  const discard = (objectId: string) => {
    const saved = savedObjects().find(entry => entry.objectId === objectId);
    setDraft([...edited().filter(entry => entry.objectId !== objectId), ...(saved ? [saved] : [])]);
    chooseOwnership(objectId, false);
    chooseFanMode(objectId, null);
  };

  /**
   * 这台机器**现在实际**是什么样。
   *
   * 和期望值并排显示 —— 它不是期望的回声：固件会按温度自己调度，
   * 用户也可能用别的软件改过，两者对不上是常态，而且正是要让用户看见的信息。
   */
  const actualSource = usePushSource(runtime.sources.controlActualState, () => ({
    active: visible(), intervalMs: readIntervals.current()?.monitor
  })).snapshot;
  const actual = () => actualSource().data;

  const apply = async (objectId: string) => {
    if (saving() || stateLoad() !== "ready") return;
    const submitted = settingsOf(objectId);
    setSaving(true);
    try {
      const changed = hasPendingChanges(submitted, savedObjects().find(entry => entry.objectId === objectId)?.settings ?? []);
      const next = await acceptSourceCommand(runtime.sources.control.state, () => changed
        ? setControlObjectSettings(runtime.requestClient, objectId, submitted)
        : retryControlObject(runtime.requestClient, objectId), value => value);
      // Preserve other drafts and edits made while this request was in flight.
      const current = edited();
      const unchanged = !hasPendingChanges(settingsOf(objectId), submitted);
      const saved = next.desired.objects.find(entry => entry.objectId === objectId);
      setDraft(unchanged
        ? [...current.filter(entry => entry.objectId !== objectId), ...(saved ? [saved] : [])]
        : current);
    } catch {
      props.onNotice(uiText.control.saveFailed);
    } finally {
      setSaving(false);
    }
  };

  /**
   * 这台机器上有没有 Intel 核显。
   *
   * 只有它需要那份 IGCL 授权 —— 别家的卡不看这个同意。
   */
  const hasIntelIntegratedGpu = () => (catalog()?.objects ?? []).some(
    (object) => object.kind === "gpu"
      && object.gpuAttachment === "integrated"
      && object.platform.vendor === "intel");

  // Key by domain identity. A refreshed catalog must update existing cards,
  // including their drafts/focus, instead of replacing their DOM subtree.
  const objectById = createMemo(() => new Map((catalog()?.objects ?? []).map(object => [object.id, object])));
  const groups = () => ["gpu", "cpu", "fan"].filter(kind => (catalog()?.objects ?? []).some(object => object.kind === kind));
  const objectIds = (kind: string) => (catalog()?.objects ?? []).filter(object => object.kind === kind).map(object => object.id);
  return (
    <div class="page control-page" data-frontend-visibility-surface="control.workspace" data-frontend-visibility-demands="control.workspace">
      <Show when={noticeSeen() === false}>
        {/* 挂到 body：页面入场动画给 .page 加了 transform，留在页内时固定定位的弹窗会以页面为容器，前 0.3 秒错位后再跳回中间。 */}
        <Portal>
          <ControlFirstRunNotice busy={acknowledging()} failed={noticeAckFailed()} onAcknowledge={() => {
            if (acknowledging()) return;
            setAcknowledging(true);
            setNoticeAckFailed(false);
            void acceptSourceCommand(runtime.sources.control.notice,
              () => acknowledgeControlNotice(runtime.requestClient), () => true)
              .catch(() => setNoticeAckFailed(true))
              .finally(() => setAcknowledging(false));
          }} />
        </Portal>
      </Show>
      <div class="panel">
        <Show when={noticeReadFailed()}>
          <p role="alert">{uiText.control.recovery.noticeReadFailed}</p>
          <button type="button" class="secondary" onClick={reloadState}>{uiText.control.retryRead}</button>
        </Show>
        <Show when={stateLoad() !== "ready"}>
          <p role={stateLoad() === "error" ? "alert" : "status"}>{uiText.control.stateUnavailable}</p>
          <button type="button" class="secondary" onClick={reloadState}>{uiText.control.retryRead}</button>
        </Show>
        <Show when={failed()}>
          <div class="disk-usage-warning" role="note">
            <strong>{uiText.control.loadFailed}</strong>
          </div>
        </Show>

        <Show when={catalog() && groups().length === 0 && !failed()}>
          <p class="control-empty">{uiText.control.empty}</p>
        </Show>

        {/*
          Intel 核显调节授权。**只在这台机器真有 Intel 核显时才出现。**

          它不是通用的"超频声明"，而是 Intel IGCL 的硬性要求：用户没接受之前，
          它拒绝一切频率偏移调用，哪怕是负向的。没有 Intel 核显的机器上摆着它，
          用户只会以为这是本程序给所有调节加的一道关卡。

          同意之后这一块仍然留着，因为**同意要能收回**。
        */}
        <Show when={overclockAccepted() !== null && hasIntelIntegratedGpu()}>
          <section class="control-overclock-consent">
            <strong>{uiText.control.overclock.title}</strong>
            <p class="control-capability-reason">{uiText.control.overclock.body}</p>
            <div class="control-overclock-consent-actions">
              <Show when={overclockAccepted()}>
                <span class="control-capability-tag" data-tone="neutral">
                  {uiText.control.overclock.accepted}
                </span>
              </Show>
              <button
                class="secondary"
                type="button"
                onClick={() => void acceptSourceCommand(runtime.sources.control.overclockConsent, () => setControlOverclockConsent(
                  runtime.requestClient,
                  overclockAccepted() !== true), value => value)
                  .catch(() => props.onNotice(uiText.control.saveFailed))}
              >
                <StableLabel
                  labels={{ accept: uiText.control.overclock.accept, revoke: uiText.control.overclock.revoke }}
                  current={overclockAccepted() ? "revoke" : "accept"}
                />
              </button>
            </div>
          </section>
        </Show>

        {/* 页面级的两个操作都不常用，放在同一行、用小号次按钮。 */}
        <div class="control-toolbar">
          {/*
            重新检测这台机器。

            **为什么要有这个按钮**：每条通道能做什么是探出来的（问辅助进程、
            起风扇核心、用一次空写问驱动），探一次要碰硬件，所以结果记了缓存。
            可用户装上组件、插上外接显卡之后，缓存里还是旧答案 ——
            没有这个按钮他只能重启整个程序。
          */}
          <button
            class="panel-refresh-button secondary"
            type="button"
            disabled={detecting()}
            onClick={() => {
              setDetecting(true);
              void acceptSourceCommand(runtime.sources.control.objects,
                () => redetectControlPlatform(runtime.requestClient), value => value)
                .catch(() => props.onNotice(uiText.control.loadFailed))
                .finally(() => setDetecting(false));
            }}
          >
            <StableLabel labels={{ idle: uiText.control.detect, busy: uiText.control.detecting }} current={detecting() ? "busy" : "idle"} />
          </button>

          {/*
            实例管理平时收着。活动设备的卡片下面本来就会全部列出来，
            这一块只在要清理插过的旧卡时才用得上，没必要一直占着页面顶部。
          */}
          <button
            class="panel-refresh-button secondary"
            type="button"
            aria-expanded={managingInstances()}
            onClick={() => setManagingInstances((open) => !open)}
          >
            {uiText.control.instances.open}
          </button>
        </div>
        <Show when={managingInstances()}>
          <ControlInstancesPanel
            catalog={instances()}
            onRefresh={() => void acceptSourceCommand(runtime.sources.control.instances,
              () => refreshControlInstances(runtime.requestClient), value => value)
              .catch(() => undefined)}
            onForget={(instanceId) => void acceptSourceCommand(runtime.sources.control.instances, () => forgetControlInstance(
              runtime.requestClient,
              instanceId), value => value)
              // 删不成是**流程提示**：点了才说，说完就过去，不在页面上留一块常驻的红字。
              .catch(() => props.onNotice(uiText.control.forgetFailed))}
          />
        </Show>

        <Show when={stateLoad() === "ready"}>
        <For each={groups()}>
          {(kind) => (
            <section class="control-group">
              <h3>{uiText.control.kind[kind as keyof typeof uiText.control.kind] ?? kind}</h3>
              <div class="control-object-grid">
                <For each={objectIds(kind)}>
                  {(objectId) => (
                    <ControlObjectCard
                      object={objectById().get(objectId)!}
                      gpuName={controlFanGpuName(objectById().get(objectId)!, catalog()?.objects ?? [])}
                      fanModeChoice={fanModes().get(objectId)}
                      onFanModeChoice={(mode) => chooseFanMode(objectId, mode)}
                      takingOver={takingOver().has(objectId)}
                      onTakeOver={(enabled) => chooseOwnership(objectId, enabled)}
                      /*
                        同一条通道上、会被一起软件接管的其他风扇。
                        这台机器上"交给软件管"是整机一个开关时才非空 ——
                        卡片自己看不到别的对象，所以由这一层给。
                      */
                      takenOverWith={siblingFansTakenOverWith(
                        objectById().get(objectId)!,
                        catalog()?.objects ?? [])}
                      accessLevel={accessLevel()}
                      state={state()}
                      edited={settingsOf(objectId)}
                      saving={saving()}
                      onApply={() => void apply(objectId)}
                      onDiscard={() => discard(objectId)}
                      actual={actual()}
                      presets={(presets()?.presets ?? []).filter(
                        (preset) => preset.objectId === objectId)}
                      onEdit={(capabilityId, next) =>
                        editCapability(objectId, capabilityId, next)}
                      onReplaceSettings={(settings) => setDraft([
                        ...edited().filter((entry) => entry.objectId !== objectId),
                        ...(settings.length > 0
                          ? [{ objectId: objectId, settings }]
                          : [])
                      ])}
                      onSavePreset={(name) => void acceptSourceCommand(runtime.sources.control.presets, () => saveControlPreset(
                        runtime.requestClient,
                        objectId,
                        name,
                        settingsOf(objectId)), value => value)
                        .catch(() => props.onNotice(uiText.control.saveFailed))}
                      onDeletePreset={(presetId) => void acceptSourceCommand(runtime.sources.control.presets, () => deleteControlPreset(
                        runtime.requestClient,
                        presetId), value => value)
                        .catch(() => props.onNotice(uiText.control.saveFailed))}
                    />
                  )}
                </For>
              </div>
            </section>
          )}
        </For>
        </Show>
      </div>
    </div>
  );
}

/**
 * 选了软件接管会被一起接管的**其他**风扇的名字。
 *
 * 只有在目录声明这把风扇的软件接管覆盖同通道全部风扇时才有值 —— 那说明这个平台上
 * "交给软件管"是整机一个开关，不是每把风扇一个。那时没挂曲线的风扇会停在
 * 当时那个转速上不再自动调，**得在用户选之前点名告诉他是哪几把**。
 *
 * 只算同一条通道上的风扇（`detail` 就是通道名）：别的通道各管各的，
 * 拉进来只会吓人。
 */
function siblingFansTakenOverWith(
  object: ControlObject,
  all: readonly ControlObject[]
): readonly string[] {
  if (!object.softwareTakeoverCoversAllFans) {
    return [];
  }
  return all
    .filter((other) =>
      other.id !== object.id
      && other.kind === "fan"
      && other.detail === object.detail)
    .map((other) => controlObjectName(other));
}

function ControlObjectCard(props: {
  object: ControlObject;
  /** 显卡风扇控制器所属显卡的名字（controlFanGpuName），在编号旁用小字写出；没有为 null。 */
  gpuName: string | null;
  fanModeChoice?: string;
  onFanModeChoice: (mode: string | null) => void;
  takingOver: boolean;
  onTakeOver: (enabled: boolean) => void;
  /** 选了软件接管会被一起接管的其他风扇。不会连带就是空的。 */
  takenOverWith: readonly string[];
  /** 现在处在哪一档。够不着的项连勾选框都勾不上。 */
  accessLevel: ControlAccessLevel;
  state: ControlStateView | null;
  edited: readonly ControlSetting[];
  saving: boolean;
  onApply: () => void;
  onDiscard: () => void;
  actual: ControlActualState | null;
  presets: readonly ControlPreset[];
  onEdit: (capabilityId: string, next: ControlSetting | null) => void;
  /** 把这个对象的期望设定整份换掉。载入一份配置、以及交还固件（换成空的）都走它。 */
  onReplaceSettings: (settings: readonly ControlSetting[]) => void;
  onSavePreset: (name: string) => void;
  onDeletePreset: (presetId: string) => void;
}) {
  const [presetName, setPresetName] = createSignal("");
  // 用户对这个对象设过什么，以及最近一次施加的回执。
  const saved = () => props.state?.desired.objects
    .find((entry) => entry.objectId === props.object.id)?.settings ?? [];

  /**
   * 控制归属：这个对象现在归固件自己管，还是归本程序管。
   *
   * **这两个是并列的两个状态，不是一个开关的两个位置。** 归固件时下面的设定一概
   * 不生效，所以它们要置灰 —— 摆着能拖但拖了没用的控件是骗人。
   *
   * 状态本身不用另存一份：期望状态里没有这个对象的任何设定，就是"归固件管"
   * （统一写入层看到空的那一份就会把硬件恢复到默认，风扇则是交还自动模式）。
   * 下面这个信号只管一件事：用户已经切到本程序控制、但还没来得及设任何一项 ——
   * 那一小段时间里没法从期望状态看出他的选择。
   */
  const isFan = () => props.object.kind === "fan";
  const modes = () => props.object.fanManagementModes ?? [];
  const storedMode = () => props.edited.length === 0 ? "firmware-management"
    : modes().find(mode => mode.items.length > 0 && props.edited.every(setting => mode.items.some(item =>
        item.capabilityId === setting.capabilityId && item.curveExecution === (setting.curveExecution ?? null))))?.id ?? null;
  const selectedMode = () => props.edited.length > 0 ? storedMode() : props.fanModeChoice ?? storedMode();
  const mode = () => modes().find(mode => mode.id === selectedMode());
  const mixed = () => isFan() && selectedMode() === null;
  const firmwareOwned = () => isFan() ? selectedMode() === "firmware-management"
    : props.edited.length === 0 && !props.takingOver;
  const editSetting = (capabilityId: string, setting: ControlSetting | null) => {
    const item = mode()?.items.find(item => item.capabilityId === capabilityId);
    props.onEdit(capabilityId, setting && isFan() ? { ...setting, curveExecution: item?.curveExecution ?? null } : setting);
  };
  const outcomes = () => props.state?.lastApply.outcomes
    .filter((entry) => entry.objectId === props.object.id) ?? [];

  const edited = () => props.edited;

  /** 这一项现在实际是多少。读不到就不显示。 */
  const actualOf = (capabilityId: string) => {
    const value = props.actual?.values.find((entry) =>
      entry.objectId === props.object.id && entry.capabilityId === capabilityId);
    return value && !value.unreadableReason
      && ((typeof value.number === "number" && Number.isFinite(value.number))
        || typeof value.toggle === "boolean") ? value : null;
  };

  const actualReadoutOf = (capability: ControlCapability) => {
    const value = actualOf(capability.id);
    if (!value || capability.valueKind !== "number" || typeof value.number !== "number") return value;
    const setting = edited().find((entry) => entry.capabilityId === capability.id)?.number;
    return actualRepeatsNumberEditor(setting, value.number) ? null : value;
  };

  const actualText = (value: ControlActualValue) => typeof value.toggle === "boolean"
    ? (value.toggle ? uiText.control.presentation.toggleOn : uiText.control.presentation.toggleOff)
    : `${formatActualNumber(value.number!)}${value.unit ? ` ${controlDisplayText(value.unit)}` : ""}`;

  /**
   * 这一项超出现在这一档了吗。
   *
   * 超出就连勾选框都勾不上 —— 和"缺个组件"不一样：缺组件的项照样能编，
   * 组件装上那一轮就会写下去；超出档位的是用户自己还没答应碰这一类，
   * 先编好留着没有意义。
   */
  const outOfReach = (capability: ControlCapability) =>
    !controlAccessLevelAllows(props.accessLevel, capability.requiredAccessLevel);

  /** 这一行动不动得了。归固件管、档位够不着、这台机器上没有，都动不了。 */
  const locked = (capability: ControlCapability) =>
    firmwareOwned() || mixed() || (isFan() && !mode()?.available) || outOfReach(capability) || !capability.supported;

  /**
   * 动不了属于哪一类。
   *
   * 归固件管是**当前这张卡的选择**，用户点一下就换过来，所以算能解开的那一类，
   * 不看后端给的种类 —— 后端算这一项的时候并不知道用户把这张卡交给了谁。
   */
  const blockedKind = (capability: ControlCapability) => {
    if (firmwareOwned()) {
      return "firmware-owned" as const;
    }
    return capability.unavailableKind;
  };

  /**
   * 这一项为什么动不了。
   *
   * **只在锁上，不在行里。** 每一项后面跟一句说明，十几行叠起来就是一屏字，
   * 而其中大多数用户根本不关心 —— 他只想知道能不能调。
   * 想知道为什么的时候，停在那把锁上就有。
   */
  const lockReason = (capability: ControlCapability) => {
    /*
     * 归固件管的时候，**两条原因都要说**。
     *
     * 先前这里遇到"归固件管"就直接返回，于是一整页的锁提示都是同一句话；
     * 用户切到软件管理之后才发现这一项还差一个 root 档、或者还要先去
     * 把机器切到自定义模式 —— 一次只告诉他一层，他就得试一层撞一次墙。
     *
     * 两条一起给：先说眼下挡着的那条，再说满足之后还差什么。
     */
    const own = firmwareOwned() ? uiText.control.ownership.firmwareNote : null;
    const capabilityReason = capability.supported || !capability.unavailableReason
      ? null : controlReasonText(capability.unavailableReason);
    if (own && capabilityReason) {
      return `${own} ${capabilityReason}`;
    }
    return own ?? capabilityReason ?? controlCapabilityLabel(capability);
  };

  /**
   * 改不了、但当前值读得到的那些。
   *
   * 显卡的温度墙、降速阈值、保护关机阈值在消费级卡上就是这样：
   * 驱动如实报数，但一律不接受改动。**那几个数不该因为改不了就不显示。**
   *
   * **只认"这台机器做不到"那一类。** 档位锁着的项同样是 supported=false，
   * 但那不是只读 —— 用户切个档位就能调，把它挪进只读区等于告诉他没救了。
   * 同理"我们还没接"的那些也留在上面，它们将来会变成可调的。
   */
  const readOnly = () => props.object.capabilities
    .filter((capability) => !capability.supported
      && capability.unavailableKind === "platform")
    .map((capability) => ({ capability, value: actualOf(capability.id) }))
    .filter((entry): entry is { capability: ControlCapability; value: ControlActualValue } =>
      entry.value !== null);

  /** 可调的那些 —— 也就是剩下的。只读的已经在上面单独列过了。 */
  const adjustable = () => {
    const shown = new Set(readOnly().map((entry) => entry.capability.id));
    return props.object.capabilities.filter((capability) => !shown.has(capability.id)
      && (!isFan() || mode()?.items.some(item => item.capabilityId === capability.id)));
  };

  const itemView = (capabilityId: string) => controlItemStatusOf(
    capabilityId,
    edited(),
    saved(),
    outcomes(),
    false);
  const itemStatus = (capabilityId: string) => itemView(capabilityId).status;

  return (
    <article
      class="control-object"
      classList={{ "control-object-inactive": !props.object.isControllable }}
    >
      <header>
        <strong>{controlObjectName(props.object)}<GpuNameText name={props.gpuName} /></strong>
        <span class="control-object-platform">
          {/* 每个对象自带自己的情况，不是全局的：一台机器上 A 卡和 N 卡各是一种。 */}
          {uiText.control.term[props.object.platform.operatingSystem]}
          {" · "}
          {uiText.control.term[props.object.platform.vendor]}
          {/* 核显能调的比独显少，所以要标出来。 */}
          <Show when={props.object.gpuAttachment}>
            {(attachment) => <>
              {" · "}
              {uiText.control.attachment[
                attachment() as keyof typeof uiText.control.attachment
              ]}
            </>}
          </Show>
          {/*
            词条：这个对象属于哪几类。跟在厂商后面，和显卡的「核显/独显」并排 ——
            它们是同一件事，只是维度不同。名字在上面那一行，这里只放分类。
          */}
          <For each={props.object.terms}>
            {(term) => <>{" · "}{uiText.control.term[term]}</>}
          </For>
          <Show when={props.object.detail && uiText.control.term[props.object.detail]}>{(detail) => <> · {detail()}</>}</Show>
        </span>
      </header>

      <Show when={readOnly().length > 0}>
        <div class="control-readings">
          <dl>
            <For each={readOnly()}>
              {(entry) => (
                <div class="control-reading">
                  <dt>
                    {controlCapabilityLabel(entry.capability)}
                    <Show when={entry.capability.channel}>
                      {(channel) => (
                        <span class="control-capability-channel">
                          {uiText.control.channel[channel()] ?? channel()}
                        </span>
                      )}
                    </Show>
                  </dt>
                  <dd title={entry.capability.unavailableReason
                    ? controlReasonText(entry.capability.unavailableReason) : undefined}>
                    {actualText(entry.value)}
                  </dd>
                </div>
              )}
            </For>
          </dl>
        </div>
      </Show>


      {/*
        整张卡都因为同一件事用不了时，这句话只说一遍。
        先前是每项能力各说一遍，一张卡四行一模一样的小字。
      */}
      {/*
        控制归属。切回固件是**一直保留的出路** —— 任何时候都能把这块硬件还给它
        自己的固件，而不是只能靠一项项撤销设定。
      */}
      {/*
        风扇是三选一：固件管理／固件写入／软件管理。选中的那种下面说一句它是什么意思
        （尤其程序退出后还算不算数），用户选之前就该知道。
        软件管理会连带同通道别的风扇时，在选之前点名说是哪几把；同通道没有别的风扇时只说会连带。
      */}
      <Show when={isFan()}>
      <div class="control-ownership control-fan-ownership">
        <Show when={props.object.softwareTakeoverCoversAllFans}>
          <p class="control-curve-note" role="note">{props.takenOverWith.length > 0
            ? uiText.control.takeoverCoversOthers(
              new Intl.ListFormat(currentLanguage(), { type: "conjunction" }).format(props.takenOverWith))
            : uiText.control.term["takeover-all-fans"]}</p>
        </Show>
        <RadioGroupRoot class="segmented settings-segmented-control control-ownership-choice control-fan-modes"
          ariaLabel={uiText.control.ownership.label} value={selectedMode() ?? ""} onChange={id => {
            if (selectedMode() === id) return;
            props.onReplaceSettings([]);
            props.onFanModeChoice(id);
          }}>
          <For each={modes()}>{entry =>
            <RadioGroupItem value={entry.id} class="segment settings-segment" disabled={!entry.available}
              title={entry.unavailableReason ? controlReasonText(entry.unavailableReason) : undefined}>
              <Show when={!entry.available}>
                <Show when={entry.unavailableKind === "platform"} fallback={<Lock size={13} />}><X size={13} /></Show>
              </Show>
              {uiText.control.term[entry.id]}
            </RadioGroupItem>
          }</For>
        </RadioGroupRoot>
        <Show when={mode()}>{(chosen) =>
          <p class="control-capability-reason control-fan-mode-hint">
            {uiText.control.fanModeHint[chosen().id]}
          </p>
        }</Show>
        <For each={modes().filter(mode => !mode.available)}>{entry =>
          <p class="control-capability-reason">
            {uiText.control.fanModeUnavailable(uiText.control.term[entry.id], controlReasonText(entry.unavailableReason))}
          </p>
        }</For>
        <Show when={mixed()}>
          <p class="control-curve-note" role="alert">{controlReasonText({code: "fan-management-modes-mixed"})}</p>
        </Show>
      </div>
      </Show>
      <Show when={!isFan()}>
      <div class="control-ownership">
        <SegmentedControl
          value={firmwareOwned() ? "firmware" : "app"}
          ariaLabel={uiText.control.ownership.label}
          // 不给 itemClass 的话，每一段会退化成普通按钮、套上全局的主按钮样式，
          // 于是两段看起来都是选中的 —— 一组二选一里最不该出问题的就是这个。
          class="settings-segmented-control control-ownership-choice"
          itemClass="settings-segment"
          options={[
            { id: "firmware", label: uiText.control.ownership.firmware },
            { id: "app", label: uiText.control.ownership.app }
          ]}
          onChange={(next) => {
            if (next === "firmware") {
              props.onTakeOver(false);
              // 交还固件就是把这个对象的期望设定整份清掉。
              props.onReplaceSettings([]);
              return;
            }
            props.onTakeOver(true);
          }}
        />

      </div>

      </Show>
      {/*
        为这个实例存过的配置。**点一份只是把它载入草稿**，要落到硬件还得点应用 ——
        应用只有一条路，不给配置开后门。
      */}
      {/*
        归固件管、又一份配置都没存过的时候，这一行没有任何用处 ——
        没东西可载入，也没东西可保存。每张卡都挂一个空输入框只是占地方。
      */}
      <Show when={props.presets.length > 0 || !firmwareOwned()}>
      <div class="control-presets">
        <For each={props.presets}>
          {(preset) => (
            <span class="control-preset">
              <button
                class="secondary"
                type="button"
                onClick={() => {
                  props.onFanModeChoice(null);
                  props.onTakeOver(preset.settings.length > 0);
                  props.onReplaceSettings(preset.settings);
                }}
              >
                {preset.name}
              </button>
              <button
                class="control-preset-remove"
                type="button"
                aria-label={uiText.control.presets.remove}
                title={uiText.control.presets.remove}
                onClick={() => props.onDeletePreset(preset.id)}
              >
                ×
              </button>
            </span>
          )}
        </For>
        <input
          class="control-preset-name"
          type="text"
          value={presetName()}
          placeholder={uiText.control.presets.namePlaceholder}
          aria-label={uiText.control.presets.namePlaceholder}
          onInput={(event) => setPresetName(event.currentTarget.value)}
        />
        <button
          class="secondary"
          type="button"
          disabled={presetName().trim().length === 0}
          onClick={() => {
            props.onSavePreset(presetName().trim());
            setPresetName("");
          }}
        >
          {uiText.control.presets.save}
        </button>
      </div>
      </Show>

      {/*
        只读的那些值。**单独一块，不混在可调项里。**

        它们改不了，但那几个数本身有用 —— "这张卡 89 度就开始降频"是用户
        真想知道的事。夹在可调项中间当一行灰掉的东西，既看不清也占位置；
        而把它们藏起来，用户会以为我们连读都读不到。

        判据就是"改不了、但读得到当前值" —— 不需要后端另加字段。
      */}

      <ul class="control-capabilities">
        <For each={adjustable().map(capability => capability.id)}>
          {(capabilityId) => {
            const capability = () => props.object.capabilities.find(item => item.id === capabilityId)!;
            return (
            <li
              class="control-capability"
              classList={{
                // 三种状态各有各的样子，不能都长成一个没勾的方框：
                // 锁住的（动不了）、可勾但没勾的（下次应用交还固件）、
                // 勾上的（下次应用会落实）。
                "control-capability-locked": locked(capability()),
                "control-capability-on": props.edited.some(
                  (entry) => entry.capabilityId === capability().id),
                "control-capability-off": !capability().supported || firmwareOwned()
              }}
            >
              <span class="control-capability-label">
                {/*
                  （开关型以外的项）勾上才是「下次应用真的会落实」，不勾就是「交还固件或回到默认」。
                  这两件事本来就是期望状态里有没有这一项，所以勾选框直接读写它，
                  不另外存一份勾选状态 —— 存两份迟早对不上。
                */}
                {/*
                  动不了的项**不画勾选框，画一把锁**。

                  一个禁用的勾选框和一个没勾的勾选框长得几乎一样，
                  用户分不出"我还没勾"和"我勾不了"。锁一眼就说清楚了，
                  而且它自己说明了原因（title 里写着差什么）。
                */}
                <Show
                  when={!locked(capability())}
                  fallback={
                    /*
                      **能解开的挂锁，解不开的打叉。**

                      先前两种都画成锁，于是"切个档位就能用"和"这台机器压根
                      没这功能"长得一模一样 —— 用户对着锁去翻设置，翻遍了也解不开。
                      图标由后端给的种类决定，不由界面猜。
                    */
                    /*
                      **提示挂在外层这个 span 上，不挂在图标上。**

                      上面那句"停在那把锁上就有"先前只对读屏软件成立：
                      aria-label 只进可访问性树，鼠标悬停什么都不弹，
                      于是用眼睛看的用户对着一把锁毫无办法 ——
                      而他恰恰是最需要知道"差什么"的那个人。

                      而 title 不能加在图标上：这些图标渲染出来是 <svg>，
                      而 svg 的 title **属性**不弹提示（那要一个 <title> 子元素）。
                      套一层 span 把 title 挂在外面，一处覆盖四个图标，
                      也不用去动图标组件。
                    */
                    <span
                      class="control-capability-lock-slot"
                      title={lockReason(capability())}
                    >
                    <Switch
                      fallback={
                        <Lock
                          class="control-capability-lock"
                          size={13}
                          aria-label={lockReason(capability())}
                        />
                      }
                    >
                      {/* 这台机器做不到：没得解，打叉。 */}
                      <Match when={blockedKind(capability()) === "platform"}>
                        <X
                          class="control-capability-x"
                          size={14}
                          aria-label={lockReason(capability())}
                        />
                      </Match>
                      {/*
                        硬件做得到，是我们还没接。
                        **既不是锁也不是叉** —— 锁会让用户去翻设置，叉会让他以为
                        自己的机器不行，两个都指错了地方。用一个中性的虚圈，
                        意思是"这一格留着，只是还空着"。
                      */}
                      <Match when={blockedKind(capability()) === "not-implemented"}>
                        <CircleDashed
                          class="control-capability-pending"
                          size={13}
                          aria-label={lockReason(capability())}
                        />
                      </Match>
                      {/* 缺组件：装上就有，指向能装它的地方。 */}
                      <Match when={blockedKind(capability()) === "component"}>
                        <Download
                          class="control-capability-needs"
                          size={13}
                          aria-label={lockReason(capability())}
                        />
                      </Match>
                    </Switch>
                    </span>
                  }
                >
                  {/*
                    开关型的「交还固件」本身就是右边三选一里的一项，
                    行首只留同宽的空位，让名字和其他行对齐。
                  */}
                  <Show
                    when={capability().valueKind !== "toggle"}
                    fallback={<span class="control-capability-check" aria-hidden="true" />}
                  >
                    <input
                      type="checkbox"
                      class="control-capability-check"
                      checked={props.edited.some(
                        (entry) => entry.capabilityId === capability().id)}
                      aria-label={controlCapabilityLabel(capability())}
                      onChange={(event) => editSetting(
                        capability().id,
                        event.currentTarget.checked ? initialSettingFor(capability(), actualOf(capability().id)?.number) : null)}
                    />
                  </Show>
                </Show>
                {controlCapabilityLabel(capability())}
                {/*
                  档位标记**一直挂着**，不是锁着才出现 —— 它的用处之一
                  就是让用户看得出哪些项算危险。安全档的不标：那是默认，
                  每行都挂一个「安全」只是噪音。
                */}
                {/*
                  这一项属于哪一档。**一个点，不是两个字。**

                  先前每一行都挂着"普通"这两个字，一屏十来个，
                  而"普通"本身也说不出它危险在哪。点的颜色说明程度
                  （黄=重启能恢复，红=没有兜底），停上去才说细节。
                */}
                {/*
                  这一项走哪条链路。**明写出来。**

                  同一块卡上，频率偏移走 NVAPI、功耗上限走 NVML、
                  cTGP 走整机厂的 EC —— 三条路的能力和限制毫无共同点。
                  用户看到"这一项调不了"时，第一个该知道的就是是谁说不行；
                  我们自己排查时，一条链路挂了也能一眼看出哪些项会跟着受影响。
                */}
                <Show when={capability().channel}>
                  {(channel) => (
                    <span
                      class="control-capability-channel"
                      title={uiText.control.channelHint}
                    >
                      {uiText.control.channel[channel()] ?? channel()}
                    </span>
                  )}
                </Show>
                <Show when={capability().requiredAccessLevel === "normal"
                  ? null
                  : capability().requiredAccessLevel}>
                  {(level) => (
                    <span
                      class="control-capability-level"
                      classList={{
                        "control-capability-level-root": level() === "root"
                      }}
                      title={uiText.control.accessLevel.hint[level()]}
                    >
                      <span class="sr-only">
                        {uiText.control.accessLevel.name[level()]}
                      </span>
                    </span>
                  )}
                </Show>
              </span>

              <span class="control-capability-meta">
                {/* 设过的才报状态。每行都挂个「未设定」就是纯噪音。 */}
                <Show when={itemStatus(capability().id) !== "unset"}>
                  <span
                    class="control-capability-tag"
                    // 失败或不支持时，后端给的原因停在标签上就有（交还固件失败也走这里）。
                    title={itemStatus(capability().id) === "failed" || itemStatus(capability().id) === "unsupported"
                      ? controlOutcomeReason(outcomes().find(outcome => outcome.capabilityId === capability().id)) ?? undefined : undefined}
                    data-tone={controlItemTone(itemStatus(capability().id))}
                  >
                    {uiText.control.status[itemStatus(capability().id)]}
                  </span>
                </Show>
                {/*
                  机器现在实际是多少。读得到才显示 —— 读不到时挂一句
                  "读不到"只是噪音，用户要的是数字。
                */}
                {/*
                  机器现在实际是多少。**不写"当前"两个字。**
                  它靠一个实时点和位置说明自己是什么：点在读数前面，
                  同一个值还标在滑轨上，一眼就看得出设定值和实际差多少。
                  文字只留给读屏 —— 看得见的人从形状读得出来，读不见的人需要那句话。
                */}
                {/*
                  数字项的数字框已经显示这个值时（没设定，或设定值读出来相同）不再写第二遍，
                  规则见 controlNumber.ts 的 actualRepeatsNumberEditor。
                */}
                <Show when={actualReadoutOf(capability())}>
                  {(value) => (
                    <span
                      class="control-capability-actual"
                      title={`${uiText.control.actual}${actualText(value())}`}
                    >
                      <span class="control-capability-live" aria-hidden="true" />
                      <span class="sr-only">{uiText.control.actual}</span>
                      {actualText(value())}
                    </span>
                  )}
                </Show>
              </span>
              <ControlCapabilityEditor
                capability={capability()}
                objectId={props.object.id}
                firmwareCurveKind={props.object.firmwareCurveKind ?? null}
                curveExecution={mode()?.items.find(item => item.capabilityId === capability().id)?.curveExecution ?? null}
                takenOverWith={props.takenOverWith}
                setting={edited().find((entry) => entry.capabilityId === capability().id)}
                actual={actualOf(capability().id)?.number ?? null}
                // 归固件管的时候这些设定不生效，所以不给动。
                held={locked(capability())}
                onChange={(next) => editSetting(capability().id, next)}
              />
            </li>
          ); }}
        </For>
      </ul>
      <div class="control-object-actions">
        <button class="secondary" type="button"
          disabled={props.saving || (!hasPendingChanges(edited(), saved()) && !props.takingOver && !props.fanModeChoice)}
          onClick={props.onDiscard}>{uiText.control.discard}</button>
        <button type="button"
          disabled={props.saving || mixed() || (!hasPendingChanges(edited(), saved()) && !canRetryControlObject(props.state, props.object.id))
            || edited().some(setting => {
              const kind = props.object.capabilities.find(c => c.id === setting.capabilityId)?.valueKind;
              return kind === "number" ? typeof setting.number !== "number" || !Number.isFinite(setting.number)
                : kind === "curve" && !setting.curve?.length;
            })}
          onClick={props.onApply}>
          <StableLabel
            labels={{ apply: uiText.control.apply, retry: uiText.control.recovery.retry }}
            current={!hasPendingChanges(edited(), saved()) && canRetryControlObject(props.state, props.object.id) ? "retry" : "apply"}
          />
        </button>
      </div>
    </article>
  );
}

/**
 * 识别到过的设备。
 *
 * **在场的和不在场的都列出来** —— 用户要能看到这台机器连接过什么。
 * 不在场的仍然保留着自己的配置，插回来就生效；确定不用了可以删掉。
 */
function ControlInstancesPanel(props: {
  catalog: ControlInstanceCatalog | null;
  onRefresh: () => void;
  onForget: (instanceId: string) => void;
}) {
  return (
    <section class="control-group control-instances">
      <div class="control-instances-header">
        <h3>{uiText.control.instances.title}</h3>
        <button class="secondary" type="button" onClick={props.onRefresh}>
          {uiText.control.instances.refresh}
        </button>
      </div>

      <ul class="control-instance-list">
        <For each={props.catalog?.instances ?? []}>
          {(view) => (
            <li classList={{ "control-instance-absent": !view.isPresent }}>
              <span class="control-capability-label">
                {controlObjectName(view.instance)}
                <GpuNameText name={controlFanGpuName(view.instance, (props.catalog?.instances ?? []).map((other) => other.instance))} />
              </span>
              <span class="control-object-platform">
                {view.isPresent
                  ? uiText.control.instances.present
                  : uiText.control.instances.absent}
              </span>
              {/*
                删除按钮一直在。在场的点了会被后端拒绝，那时才说明为什么 ——
                比在每台设备旁边常驻一行"删不了"干净得多。
              */}
              <button
                class="secondary"
                type="button"
                onClick={() => props.onForget(view.instance.id)}
              >
                {uiText.control.instances.forget}
              </button>
            </li>
          )}
        </For>
      </ul>
    </section>
  );
}
