import { createSignal, For, onCleanup, onMount, Show } from "solid-js";
import {
  forgetControlInstance,
  getControlInstances,
  getControlObjects,
  getControlState,
  refreshControlInstances
} from "../control/controlApi.ts";
import { controlItemStatusOf } from "../control/controlStateMachine.ts";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { uiText } from "../text.ts";
import type {
  ControlInstanceCatalog,
  ControlObject,
  ControlObjectCatalog,
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
export function ControlWorkspace(props: { onNotice: (message: string) => void }) {
  const runtime = useFrontendRuntime();
  const [catalog, setCatalog] = createSignal<ControlObjectCatalog | null>(null);
  const [state, setState] = createSignal<ControlStateView | null>(null);
  const [instances, setInstances] = createSignal<ControlInstanceCatalog | null>(null);
  const [failed, setFailed] = createSignal(false);

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

        <For each={groups()}>
          {(group) => (
            <section class="control-group">
              <h3>{group.label}</h3>
              <div class="control-object-grid">
                <For each={group.objects}>
                  {(object) => <ControlObjectCard object={object} state={state()} />}
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
}) {
  // 用户对这个对象设过什么，以及最近一次施加的回执。
  const saved = () => props.state?.desired.objects
    .find((entry) => entry.objectId === props.object.id)?.settings ?? [];
  const outcomes = () => props.state?.lastApply.outcomes
    .filter((entry) => entry.objectId === props.object.id) ?? [];

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

      <ul class="control-capabilities">
        <For each={props.object.capabilities}>
          {(capability) => (
            <li classList={{ "control-capability-off": !capability.supported }}>
              <span class="control-capability-label">{capability.label}</span>
              {/* 状态是算出来的，不是另存一份标志位。 */}
              <Show
                when={capability.supported}
                fallback={
                  <Show when={!sharedReason()}>
                    <span class="control-capability-reason">
                      {capability.unavailableReason}
                      <Show when={capability.requiredComponentName}>
                        {(name) => <> · {uiText.control.needsComponent(name())}</>}
                      </Show>
                    </span>
                  </Show>
                }
              >
                <span class="control-capability-range">
                  {uiText.control.status[
                    controlItemStatusOf(
                      capability.id,
                      saved(),
                      saved(),
                      outcomes(),
                      false).status
                  ]}
                  <Show when={capability.range}>
                    {(range) => (
                      <>
                        {" · "}
                        {range().minimum} ~ {range().maximum} {range().unit}
                      </>
                    )}
                  </Show>
                </span>
              </Show>
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
