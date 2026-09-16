import { createSignal, For, onCleanup, onMount, Show } from "solid-js";
import { getControlObjects, getControlState } from "../control/controlApi.ts";
import { controlItemStatusOf } from "../control/controlStateMachine.ts";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { uiText } from "../text.ts";
import type {
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
export function ControlWorkspace() {
  const runtime = useFrontendRuntime();
  const [catalog, setCatalog] = createSignal<ControlObjectCatalog | null>(null);
  const [state, setState] = createSignal<ControlStateView | null>(null);
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
        <div class="panel-header">
          <div class="optimization-heading">
            <h2>{uiText.control.title}</h2>
            <span>{uiText.control.intro}</span>
          </div>
        </div>

        <Show when={failed()}>
          <div class="disk-usage-warning" role="note">
            <strong>{uiText.control.loadFailed}</strong>
          </div>
        </Show>

        <Show when={catalog() && groups().length === 0 && !failed()}>
          <p class="control-empty">{uiText.control.empty}</p>
        </Show>

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

      <ul class="control-capabilities">
        <For each={props.object.capabilities}>
          {(capability) => (
            <li classList={{ "control-capability-off": !capability.supported }}>
              <span class="control-capability-label">{capability.label}</span>
              {/* 状态是算出来的，不是另存一份标志位。 */}
              <Show
                when={capability.supported}
                fallback={
                  <span class="control-capability-reason">
                    {capability.unavailableReason}
                    <Show when={capability.requiredComponentName}>
                      {(name) => <> · {uiText.control.needsComponent(name())}</>}
                    </Show>
                  </span>
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
