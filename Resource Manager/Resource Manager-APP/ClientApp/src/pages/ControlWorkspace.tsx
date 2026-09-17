import { createSignal, For, onCleanup, onMount, Show } from "solid-js";
import {
  applyControlDesiredState,
  deleteControlPreset,
  forgetControlInstance,
  getControlInstances,
  getControlObjects,
  getControlOverclockConsent,
  getControlPresets,
  getControlState,
  setControlOverclockConsent,
  refreshControlInstances,
  saveControlPreset
} from "../control/controlApi.ts";
import { ControlCapabilityEditor } from "../control/ControlCapabilityEditor";
import {
  controlItemStatusOf,
  hasPendingChanges
} from "../control/controlStateMachine.ts";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { SegmentedControl } from "../ui/primitives/SegmentedControl";
import { uiText } from "../text.ts";
import type {
  ControlActualState,
  ControlInstanceCatalog,
  ControlPreset,
  ControlObject,
  ControlObjectCatalog,
  ControlObjectDesiredState,
  ControlPresetCatalog,
  ControlSetting,
  ControlStateView
} from "../control/controlTypes.ts";

/**
 * 控制面。
 *
 * 这一片**只读**：认出这台机器上有哪些可控对象、各自能控什么、控不了的为什么。
 * 写入（风扇曲线、超频）是后面的片，所以现在每一项都标着还缺什么 ——
 * 这不是占位，是如实陈述，用户应该看得出来差在哪。
 *
 * 风扇、GPU、CPU 在同一页（按设计文档要求），按对象种类分组。
 */
/**
 * 实际状态订多勤。
 *
 * 后端那一侧读取只返回当前值、不触发采样（采样是后台独立的一条路），
 * 所以订得勤一点也不会多碰一次硬件；这个间隔只决定界面多久刷新一次。
 */
const ActualStateIntervalMs = 2000;

export function ControlWorkspace(props: { onNotice: (message: string) => void }) {
  const runtime = useFrontendRuntime();
  const [catalog, setCatalog] = createSignal<ControlObjectCatalog | null>(null);
  const [state, setState] = createSignal<ControlStateView | null>(null);
  const [instances, setInstances] = createSignal<ControlInstanceCatalog | null>(null);
  const [failed, setFailed] = createSignal(false);
  const [managingInstances, setManagingInstances] = createSignal(false);
  const [presets, setPresets] = createSignal<ControlPresetCatalog | null>(null);
  /**
   * 超频免责声明答没答应。
   *
   * 这是**整机一份**的开关，不挂在某个对象上 —— 后端就是这么存的。
   * 之所以要有它：Intel 的 IGCL 在用户接受之前拒绝一切超频调用，
   * 并要求应用先把"会缩短部件寿命"这件事告诉用户。所以话要说在前面，
   * 而且要能收回 —— 给得出去的同意也要收得回来。
   */
  const [overclockAccepted, setOverclockAccepted] = createSignal<boolean | null>(null);

  /**
   * 正在编的那一份，**整页共用一份**。
   *
   * 改动只留在本地，点「应用」才写下去 —— 拖曲线、拉滑块不会每动一下就走一次往返
   * （那样回包还会把正在拖的值盖掉，反而拖不动）。
   *
   * 页面级而不是每张卡一份，是因为用户面对的本来就是一整套设定：
   * 点一份配置载入的是整套，点应用下发的也是整套。
   */
  const [draft, setDraft] = createSignal<readonly ControlObjectDesiredState[] | null>(null);
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

  const pending = () => edited().some((entry) => hasPendingChanges(
    entry.settings,
    savedObjects().find((row) => row.objectId === entry.objectId)?.settings ?? []))
    || savedObjects().some((entry) => !edited().some(
      (row) => row.objectId === entry.objectId));

  /**
   * 这台机器**现在实际**是什么样。
   *
   * 和期望值并排显示 —— 它不是期望的回声：固件会按温度自己调度，
   * 用户也可能用别的软件改过，两者对不上是常态，而且正是要让用户看见的信息。
   */
  const [actual, setActual] = createSignal<ControlActualState | null>(null);
  onMount(() => {
    const unsubscribe = runtime.sources.controlActualState.subscribe(
      ActualStateIntervalMs,
      setActual);
    onCleanup(unsubscribe);
  });

  const apply = () => void applyControlDesiredState(runtime.requestClient, edited())
    .then((next) => { setState(next); setDraft(null); })
    .catch(() => props.onNotice(uiText.control.saveFailed));

  onMount(() => {
    const controller = new AbortController();
    const read = () => {
      void getControlObjects(runtime.requestClient, controller.signal)
        .then((next) => { setCatalog(next); setFailed(false); })
        .catch(() => setFailed(true));
      // 期望状态和回执分开取：对象清单是硬件事实，这份是用户设过什么。
      void getControlState(runtime.requestClient, controller.signal)
        .then(setState)
        .catch(() => undefined);
      void getControlInstances(runtime.requestClient, controller.signal)
        .then(setInstances)
        .catch(() => undefined);
      void getControlPresets(runtime.requestClient, controller.signal)
        .then(setPresets)
        .catch(() => undefined);
      void getControlOverclockConsent(runtime.requestClient, controller.signal)
        .then(setOverclockAccepted)
        .catch(() => undefined);
    };

    read();
    // 显卡会热插拔，组件装上之后风扇会突然可控，所以回到窗口时重读一次。
    const refresh = () => read();
    window.addEventListener("focus", refresh);
    onCleanup(() => {
      window.removeEventListener("focus", refresh);
      controller.abort();
    });
  });

  const groups = () => {
    const current = catalog();
    if (!current) {
      return [];
    }
    // 按种类分组，组内保持后端给的顺序（显卡按索引，风扇跟着它的宿主）。
    return ["gpu", "cpu", "fan"]
      .map((kind) => ({
        kind,
        label: uiText.control.kind[kind as keyof typeof uiText.control.kind] ?? kind,
        objects: current.objects.filter((object) => object.kind === kind)
      }))
      .filter((group) => group.objects.length > 0);
  };

  return (
    <div class="page control-page">
      <div class="panel">
        <Show when={failed()}>
          <div class="disk-usage-warning" role="note">
            <strong>{uiText.control.loadFailed}</strong>
          </div>
        </Show>

        <Show when={catalog() && groups().length === 0 && !failed()}>
          <p class="control-empty">{uiText.control.empty}</p>
        </Show>

        {/*
          超频免责声明。整机一份，所以放在页面上而不是某张卡里。

          先把后果说清楚，用户点了才算同意 —— 这是 Intel IGCL 的硬性要求，
          不是我们加的流程。同意之后这一块仍然留着，因为**同意要能收回**。
        */}
        <Show when={overclockAccepted() !== null}>
          <section class="control-overclock-consent">
            <strong>{uiText.control.overclock.title}</strong>
            <p class="control-capability-reason">{uiText.control.overclock.body}</p>
            <div class="control-overclock-consent-actions">
              <Show when={overclockAccepted()}>
                <span class="control-capability-tag">
                  {uiText.control.overclock.accepted}
                </span>
              </Show>
              <button
                class="secondary"
                type="button"
                onClick={() => void setControlOverclockConsent(
                  runtime.requestClient,
                  overclockAccepted() !== true)
                  .then(setOverclockAccepted)
                  .catch(() => props.onNotice(uiText.control.saveFailed))}
              >
                {overclockAccepted()
                  ? uiText.control.overclock.revoke
                  : uiText.control.overclock.accept}
              </button>
            </div>
          </section>
        </Show>

        {/*
          实例管理平时收着。活动设备的卡片下面本来就会全部列出来，
          这一块只在要清理插过的旧卡时才用得上，没必要一直占着页面顶部。
        */}
        <div class="control-instances-entry">
          <button
            class="secondary"
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
            onRefresh={() => void refreshControlInstances(runtime.requestClient)
              .then(setInstances)
              .catch(() => undefined)}
            onForget={(instanceId) => void forgetControlInstance(
              runtime.requestClient,
              instanceId)
              .then(setInstances)
              // 删不成是**流程提示**：点了才说，说完就过去，不在页面上留一块常驻的红字。
              .catch(() => props.onNotice(uiText.control.forgetFailed))}
          />
        </Show>

        {/* 改了才出现。没改动时摆一个按不动的按钮只是占地方。 */}
        <Show when={pending()}>
          <div class="control-page-actions">
            <button class="secondary" type="button" onClick={() => setDraft(null)}>
              {uiText.control.discard}
            </button>
            <button type="button" onClick={apply}>{uiText.control.apply}</button>
          </div>
        </Show>

        <For each={groups()}>
          {(group) => (
            <section class="control-group">
              <h3>{group.label}</h3>
              <div class="control-object-grid">
                <For each={group.objects}>
                  {(object) => (
                    <ControlObjectCard
                      object={object}
                      state={state()}
                      edited={settingsOf(object.id)}
                      actual={actual()}
                      presets={(presets()?.presets ?? []).filter(
                        (preset) => preset.objectId === object.id)}
                      onEdit={(capabilityId, next) =>
                        editCapability(object.id, capabilityId, next)}
                      onReplaceSettings={(settings) => setDraft([
                        ...edited().filter((entry) => entry.objectId !== object.id),
                        ...(settings.length > 0
                          ? [{ objectId: object.id, settings }]
                          : [])
                      ])}
                      onSavePreset={(name) => void saveControlPreset(
                        runtime.requestClient,
                        object.id,
                        name,
                        settingsOf(object.id))
                        .then(setPresets)
                        .catch(() => props.onNotice(uiText.control.saveFailed))}
                      onDeletePreset={(presetId) => void deleteControlPreset(
                        runtime.requestClient,
                        presetId)
                        .then(setPresets)
                        .catch(() => props.onNotice(uiText.control.saveFailed))}
                    />
                  )}
                </For>
              </div>
            </section>
          )}
        </For>
      </div>
    </div>
  );
}

function ControlObjectCard(props: {
  object: ControlObject;
  state: ControlStateView | null;
  edited: readonly ControlSetting[];
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
  const [takingOver, setTakingOver] = createSignal(false);
  const firmwareOwned = () => props.edited.length === 0 && !takingOver();
  const outcomes = () => props.state?.lastApply.outcomes
    .filter((entry) => entry.objectId === props.object.id) ?? [];

  const edited = () => props.edited;

  /** 这一项现在实际是多少。读不到就不显示。 */
  const actualOf = (capabilityId: string) => {
    const value = props.actual?.values.find((entry) =>
      entry.objectId === props.object.id && entry.capabilityId === capabilityId);
    return value && typeof value.number === "number" ? value : null;
  };

  const itemStatus = (capabilityId: string) => controlItemStatusOf(
    capabilityId,
    edited(),
    saved(),
    outcomes(),
    false).status;

  /**
   * 整张卡的能力都卡在同一件事上时，返回那句话，让卡片只说一遍。
   * 各项原因不一样就返回 null，那时才逐项说明。
   */
  const sharedReason = () => {
    const capabilities = props.object.capabilities;
    if (capabilities.length === 0 || capabilities.some((entry) => entry.supported)) {
      return null;
    }
    const first = capabilities[0];
    const reason = first.requiredComponentName
      ? uiText.control.needsComponent(first.requiredComponentName)
      : first.unavailableReason;
    const same = capabilities.every((entry) =>
      entry.unavailableReason === first.unavailableReason
      && entry.requiredComponentName === first.requiredComponentName);
    return same ? reason : null;
  };

  return (
    <article
      class="control-object"
      classList={{ "control-object-inactive": !props.object.isControllable }}
    >
      <header>
        <strong>{props.object.displayName}</strong>
        <span class="control-object-platform">
          {/* 每个对象自带自己的情况，不是全局的：一台机器上 A 卡和 N 卡各是一种。 */}
          {props.object.platform.operatingSystem}
          {" · "}
          {props.object.platform.vendor}
          {/* 核显能调的比独显少，所以要标出来。 */}
          <Show when={props.object.gpuAttachment}>
            {(attachment) => <>
              {" · "}
              {uiText.control.attachment[
                attachment() as keyof typeof uiText.control.attachment
              ] ?? attachment()}
            </>}
          </Show>
          <Show when={props.object.detail}>{(detail) => <> · {detail()}</>}</Show>
        </span>
      </header>

      {/*
        整张卡都因为同一件事用不了时，这句话只说一遍。
        先前是每项能力各说一遍，一张卡四行一模一样的小字。
      */}
      <Show when={sharedReason()}>
        {(reason) => <p class="control-capability-reason">{reason()}</p>}
      </Show>

      {/*
        控制归属。切回固件是**一直保留的出路** —— 任何时候都能把这块硬件还给它
        自己的固件，而不是只能靠一项项撤销设定。
      */}
      <div class="control-ownership">
        <SegmentedControl
          value={firmwareOwned() ? "firmware" : "app"}
          ariaLabel={uiText.control.ownership.label}
          class="control-ownership-choice"
          options={[
            { id: "firmware", label: uiText.control.ownership.firmware },
            { id: "app", label: uiText.control.ownership.app }
          ]}
          onChange={(next) => {
            if (next === "firmware") {
              setTakingOver(false);
              // 交还固件就是把这个对象的期望设定整份清掉。
              props.onReplaceSettings([]);
              return;
            }
            setTakingOver(true);
          }}
        />
        <Show when={firmwareOwned()}>
          <span class="control-capability-reason">
            {uiText.control.ownership.firmwareNote}
          </span>
        </Show>
      </div>

      {/*
        为这个实例存过的配置。**点一份只是把它载入草稿**，要落到硬件还得点应用 ——
        应用只有一条路，不给配置开后门。
      */}
      <div class="control-presets">
        <For each={props.presets}>
          {(preset) => (
            <span class="control-preset">
              <button
                class="secondary"
                type="button"
                onClick={() => {
                  setTakingOver(preset.settings.length === 0 ? false : true);
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

      <ul class="control-capabilities">
        <For each={props.object.capabilities}>
          {(capability) => (
            <li classList={{
              "control-capability-off": !capability.supported || firmwareOwned()
            }}>
              <span class="control-capability-label">
                {capability.label}
                {/* 设过的才报状态。每行都挂个「未设定」就是纯噪音。 */}
                <Show when={itemStatus(capability.id) !== "unset"}>
                  <span class="control-capability-tag">
                    {uiText.control.status[itemStatus(capability.id)]}
                  </span>
                </Show>
                {/*
                  机器现在实际是多少。读得到才显示 —— 读不到时挂一句
                  "读不到"只是噪音，用户要的是数字。
                */}
                <Show when={actualOf(capability.id)}>
                  {(value) => (
                    <span class="control-capability-actual">
                      {uiText.control.actual}
                      {value().number}
                      {value().unit ? ` ${value().unit}` : ""}
                    </span>
                  )}
                </Show>
              </span>
              {/*
                控不了的项照样能编：期望状态和写入器是分开的，
                组件装上之后那一轮重新施加就会把它写下去。
              */}
              <Show when={!capability.supported && !sharedReason()}>
                <span class="control-capability-reason">
                  {capability.unavailableReason}
                </span>
              </Show>
              <ControlCapabilityEditor
                capability={capability}
                setting={edited().find((entry) => entry.capabilityId === capability.id)}
                // 归固件管的时候这些设定不生效，所以不给动。
                held={firmwareOwned()}
                onChange={(next) => props.onEdit(capability.id, next)}
              />
            </li>
          )}
        </For>
      </ul>

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
              <span class="control-capability-label">{view.instance.displayName}</span>
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
