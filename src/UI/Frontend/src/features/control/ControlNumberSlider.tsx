import { controlSliderDefinition, thumbHalfWidth } from "../../interactions/components/controlSlider.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { setInteractionComponentBox, solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";
import { effectiveStep, snapControlNumber } from "./controlNumber.ts";
import type { ControlNumberBounds } from "./controlNumber.ts";

/**
 * 数值滑条：还是原来那个原生 `<input type="range">`（大小、颜色、滑块、滑轨都由浏览器和 control.css 照旧画），
 * 行为交给交互协议的运行时（interactions/components/controlSlider.ts）：
 * 按在滑块上拖动保持按下时指针相对圆心的偏移（和原生 range 一样），按在滑轨上拖动滑块圆心跟着指针；
 * 拖动中只通过 onPreview 预览；松开才 onCommit 一次；单击照原生 range（点在滑块上不改，点在滑轨上到那里）；
 * Esc、右键取消，什么都不写，回到拖动前的值；键盘照原生 range 一步一写。滑块停在哪儿由运行时写 input.value，原生 range 按步长画，读屏读到的也是它。
 *
 * value 是滑块停的值（设定值夹进量程）。提交的值按档位取整、夹进量程；和滑块现在停的值相同就不提交
 * （原生 range 值没变也不发 input）。
 */
export function ControlNumberSlider(props: {
  bounds: ControlNumberBounds;
  value: number;
  disabled: boolean;
  ariaLabel: string;
  /** 拖动中滑块所在的值（已取整）；拖动结束（松开或取消）时为 null。 */
  onPreview: (value: number | null) => void;
  onCommit: (value: number) => void;
}) {
  let input!: HTMLInputElement;
  const span = () => props.bounds.maximum - props.bounds.minimum;
  const valueAt = (ratio: number) => snapControlNumber(props.bounds.minimum + ratio * span(), props.bounds);
  const commit = (next: number) => {
    props.onPreview(null);
    if (next !== props.value) props.onCommit(next);
  };

  const view = (): Presentation => {
    // 运行时第一次画之前先按宿主值放好。
    input.value = String(props.value);
    // 组件是滑块：外框是滑块此刻画在的那一块（圆心左右各半个滑块、滑条整个高度），按在里面是抓住，外面是滑轨。
    // 原生 range 按取整后的 input.value 画滑块，所以从它算。
    setInteractionComponentBox(input, () => {
      const rect = input.getBoundingClientRect();
      const ratio = span() > 0 ? (Number(input.value) - props.bounds.minimum) / span() : 0;
      const x = rect.left + thumbHalfWidth + ratio * (rect.width - 2 * thumbHalfWidth);
      return { left: x - thumbHalfWidth, top: rect.top, right: x + thumbHalfWidth, bottom: rect.bottom };
    });
    return {
      element: input,
      bindingLabel: "滑块位置",
      // 逻辑中心是滑块圆心：最小值时在左端往里半个滑块，最大值时在右端往里半个滑块（原生 range 的画法）。
      geometry: () => {
        if (span() <= 0) return null;
        const rect = input.getBoundingClientRect();
        const y = (rect.top + rect.bottom) / 2;
        return { center: "滑块圆心", atMin: { x: rect.left + thumbHalfWidth, y }, atMax: { x: rect.right - thumbHalfWidth, y } };
      },
      // 原生 range 按 min / step 把这个数对齐后画，所以拖动中滑块按档位走，和原来一样。
      render: ({ x }) => { input.value = String(props.bounds.minimum + x * span()); },
      // aria-valuenow 由原生 range 按 input.value 给出，这里不另写。
      aria: () => `value=${input.value}`
    };
  };

  useInteraction(`数值滑条「${props.ariaLabel}」`, controlSliderDefinition, view, solidHost<number>({
    value: () => (span() > 0 ? (props.value - props.bounds.minimum) / span() : 0),
    // 量程只有一个值时没有可拖的东西（原生 range 也拖不动）：和禁用一样不参与路由。
    enabled: () => !props.disabled && span() > 0,
    call: (port, args) => {
      if (port === "select") {
        const value = valueAt(Number(args[0]));
        if (args[1] === true) commit(value);
        else props.onPreview(value);
      } else if (port === "step") {
        // 和原生 range 一样：一大步是一步和量程十分之一里大的那个。
        const unit = effectiveStep(props.bounds.step);
        const by = args[1] === true ? Math.max(unit, span() / 10) : unit;
        commit(snapControlNumber(props.value + Number(args[0]) * by, props.bounds));
      } else if (port === "cancel") {
        props.onPreview(null);
      }
    }
  }));

  return (
    <input
      ref={input}
      type="range"
      aria-label={props.ariaLabel}
      min={props.bounds.minimum}
      max={props.bounds.maximum}
      step={props.bounds.step}
      disabled={props.disabled}
      // 键盘和指针都由运行时认领，原生不会自己改值；还会发 input 的只有辅助技术直接设值，照原来一样提交。
      onInput={(event) => commit(snapControlNumber(Number(event.currentTarget.value), props.bounds))}
    />
  );
}
