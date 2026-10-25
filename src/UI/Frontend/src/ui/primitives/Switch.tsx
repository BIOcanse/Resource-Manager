import { Dynamic } from "solid-js/web";
import { switchToggleDefinition } from "../../interactions/components/switchToggle.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";

/**
 * 开关：原生复选框加轨道和滑块。复选框负责点击、键盘、标签文字和无障碍；滑块由交互协议的运行时带
 * （回点弹簧，可以按住拖，见 interactions/components/switchToggle.ts）。
 * 外层默认是 label（开关自己就是可点的整块）；放在别的 label 里时用 span。
 */
export function SwitchControl(props: {
  checked: boolean;
  disabled?: boolean;
  ariaLabel?: string;
  title?: string;
  as?: "label" | "span";
  onChange: (checked: boolean) => void;
}) {
  let root!: HTMLElement;
  let track!: HTMLSpanElement;
  let knob!: HTMLSpanElement;
  // 滑块的行程：外框宽减去滑块宽和两侧留白。
  const travel = () => root.clientWidth - knob.offsetWidth - 2 * knob.offsetLeft;
  const view = (): Presentation => ({
    element: root,
    bindingLabel: "位移",
    // 逻辑中心是滑块圆心：开度 0 在左端，开度 1 多出一个行程。
    geometry: () => {
      const rect = root.getBoundingClientRect();
      const atMin = { x: rect.left + knob.offsetLeft + knob.offsetWidth / 2, y: rect.top + knob.offsetTop + knob.offsetHeight / 2 };
      return { center: "滑块圆心", atMin, atMax: { x: atMin.x + travel(), y: atMin.y } };
    },
    // 开度同时写到轨道上：轨道颜色按它在关、开两色之间混合（协议 3.3 颜色由驱动量映射），拖动中跟着滑块走。
    render: ({ x }) => {
      knob.style.translate = `${(x * travel()).toFixed(2)}px 0`;
      track.style.setProperty("--switch-openness", x.toFixed(4));
    },
    // 复选框的 checked 由 Solid 写，这里不另写无障碍状态。
    aria: (value) => `checked=${value >= 0.5}`
  });
  useInteraction("设置开关", switchToggleDefinition, view, solidHost<number>({
    value: () => (props.checked ? 1 : 0),
    enabled: () => props.disabled !== true,
    // 拖回原处松手也会调用：值没变就不算改动。
    call: (port, args) => {
      const next = Number(args[0]) >= 0.5;
      if (port === "select" && next !== props.checked) props.onChange(next);
    }
  }));
  return (
    <Dynamic component={props.as ?? "label"} ref={(element: HTMLElement) => { root = element; }} class="settings-switch">
      <input
        type="checkbox"
        aria-label={props.ariaLabel}
        title={props.title}
        checked={props.checked}
        disabled={props.disabled}
        onChange={(event) => props.onChange(event.currentTarget.checked)}
      />
      <span ref={track} class="settings-switch-track">
        <span ref={knob} class="settings-switch-knob" />
      </span>
    </Dynamic>
  );
}
