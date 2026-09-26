import { createEffect, createMemo, createSignal, onCleanup, Show } from "solid-js";
import { uiText } from "../../text.ts";
import { controlCapabilityLabel, controlDisplayText } from "./controlPresentation.ts";
import {
  curveFromStops,
  defaultStops,
  stopsFromCurve
} from "./fanCurve.ts";
import { FanCurveChart } from "./FanCurveChart";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { getControlObjectCurve } from "./controlApi.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import type { ControlCapability, ControlSetting } from "./controlTypes.ts";
/*
 * 数值换算搬到了 `controlNumber.ts`：那是纯计算，没有界面依赖，
 * 可以直接跑单元测试。这里转出去，原来从本文件取这几个函数的地方不用改。
 */
import {
  decimalsForStep,
  formatActualNumber,
  formatControlNumber,
  snapControlNumber,
  snapToStep,
  sliderThumbValue
} from "./controlNumber.ts";

export {
  decimalsForStep,
  formatActualNumber,
  formatControlNumber,
  snapControlNumber,
  snapToStep,
  sliderThumbValue
};


/**
 * 勾上这一项时给它什么初值。
 *
 * 就是它本来显示的那个数 —— 没勾的时候界面上显示的是默认值，
 * 勾上之后先停在同一个数，不会因为勾一下就把硬件改了。
 * 之后用户拖到哪儿是他的事。
 */
export function initialSettingFor(capability: ControlCapability): ControlSetting {
  if (capability.valueKind === "toggle") {
    return { capabilityId: capability.id, toggle: true };
  }
  if (capability.valueKind === "curve") {
    /*
     * **勾上的那一刻不给曲线。**
     *
     * 起点必须是机器现在真在跑的那条，而那条要去固件里读（十几秒）——
     * 这里是同步的，给不了。先前这里铺了一条 `defaultStops()` 编出来的曲线，
     * 于是用户一勾就看到一条和这台机器毫无关系的线，还以为那是现状。
     *
     * 留空，由曲线编辑器接手去读；读到之前它显示"正在读取"，
     * 读不到才退回默认曲线。
     */
    return { capabilityId: capability.id, curve: null };
  }
  return {
    capabilityId: capability.id,
    number: capability.range?.defaultValue ?? capability.range?.minimum ?? 0,
    unit: capability.range?.unit ?? ""
  };
}

/**
 * 一项能力的编辑器。
 *
 * **控不了的项也能编。** 这不是将就：期望状态和写入器是分开的 ——
 * 用户现在就可以把曲线编好，等 NBFC 装上、写入器接进来，
 * 后台重新施加那一轮就会把它写下去。所以编辑不需要等硬件就位。
 *
 * 但**没勾选的项不能编**：没勾就是"下次应用把它交还固件"，
 * 这时候还让人拖滑块，拖出来的数字既不生效也没地方去。
 */
export function ControlCapabilityEditor(props: {
  capability: ControlCapability;
  /** 这一项属于哪个对象。曲线要靠它去读固件现在跑的那条。 */
  objectId: string;
  /** 这个对象的词条。曲线要靠它判断能交给谁执行。 */
  terms: readonly string[];
  /**
   * 选了软件接管会被一起接管的**其他**风扇的名字。
   *
   * 这台机器上"交给软件管"不是每把风扇一个开关时才有值 ——
   * 那时没挂曲线的那几把会停在当时的转速上不再自动调，**得在他选之前说清楚**。
   */
  takenOverWith?: readonly string[];
  setting: ControlSetting | undefined;
  /**
   * 机器现在实际是多少。读不到就是 null。
   *
   * 它标在滑轨上而不是另写一行字：实际值和设定值是同一条量纲上的两个点，
   * 摆在同一条轨道上才看得出差多少。
   */
  actual?: number | null;
  /** 这个对象现在归固件管，设了也不生效。 */
  held?: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  // 控不了、归固件管、或者没勾选，就置灰。
  // 摆一个能拖但拖了没用的控件是骗人。
  const disabled = () =>
    !props.capability.supported || props.held === true || props.setting === undefined;
  return (
    <div
      class="control-editor-shell"
      classList={{
        "control-editor-off": disabled(),
        // 曲线要横向空间，滑块不要 —— 一条 ±30 档的滑块拉满一米宽没有任何用处。
        "control-editor-shell-wide": props.capability.valueKind === "curve"
      }}
    >
      {/*
        开关型的在这里什么都不画：行首那个勾选框就是它。
        「这次要不要落实这一项」和「开还是关」对一个开关来说是同一句话，
        摆两个一模一样的方框只会让人猜它们有什么区别。
      */}
      <Show when={props.capability.valueKind === "number"}>
        <NumberEditor
          capability={props.capability}
          setting={props.setting}
          actual={props.actual}
          disabled={disabled()}
          onChange={props.onChange}
        />
      </Show>
      <Show when={props.capability.valueKind === "curve"}>
        <CurveEditor
          capability={props.capability}
          objectId={props.objectId}
          terms={props.terms}
          takenOverWith={props.takenOverWith}
          setting={props.setting}
          disabled={disabled()}
          onChange={props.onChange}
        />
      </Show>
    </div>
  );
}

function NumberEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  actual?: number | null;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const range = () => props.capability.range;
  /*
   * 没设过就显示默认值，但**不写进期望状态** —— 显示什么和设了什么是两回事。
   *
   * 读不到默认值时落在下界，不是落在 0：温度上限的范围是 45~105，
   * 摆一个"0 °C"既不在范围里，也让人以为这台机器的温度墙是 0 度。
   */
  const shown = () =>
    props.setting?.number ?? range()?.defaultValue ?? range()?.minimum ?? 0;

  /*
   * 数字框里正在敲的那串字。
   *
   * **敲到一半的状态不能当设定值**：输入 "15" 会先经过 "1"，
   * 逐字提交等于用户还没敲完就把硬件调到 1 了。所以框里存字符串草稿，
   * 敲完（回车 / 失焦 / 按方向键微调）才归一并提交。
   * 草稿为 null 表示"没在编辑"，这时框里显示的是当前设定值。
   */
  const [draft, setDraft] = createSignal<string | null>(null);

  const commit = (text: string) => {
    const bounds = range();
    setDraft(null);
    if (!bounds) {
      return;
    }
    const parsed = Number(text.trim());
    if (text.trim() === "" || !Number.isFinite(parsed)) {
      // 敲了个不是数的东西：框子自己弹回当前值，**不动硬件**。
      return;
    }
    /*
     * **敲进来的数不夹量程。**
     *
     * 量程是滑块画得出来的那一段，不是一道闸 —— 用户想试更大的数是他的自由，
     * 这里把它截掉只会让人以为"设不了"。滑块那边把手停在端点就够了。
     *
     * 真正拦得住的是硬件：写下去固件夹回多少，写入器会如实回报，
     * 顺带把学到的上限用来收窄量程。所以"敲超出去"正是发现真实上限的途径。
     */
    props.onChange({
      capabilityId: props.capability.id,
      number: snapToStep(parsed, bounds),
      unit: bounds.unit
    });
  };

  /**
   * 实际值落在轨道的百分之几处。超出范围或读不到就不画。
   *
   * 画不出来的时候**什么都不画**，而不是把它夹到端点 ——
   * 夹到端点等于告诉用户"实际值正好在最小处"，那是假的。
   */
  const actualPercent = () => {
    const bounds = range();
    const value = props.actual;
    if (!bounds || value === null || value === undefined || !Number.isFinite(value)) {
      return null;
    }
    const span = bounds.maximum - bounds.minimum;
    if (span <= 0 || value < bounds.minimum || value > bounds.maximum) {
      return null;
    }
    return ((value - bounds.minimum) / span) * 100;
  };

  return (
    <div class="control-editor">
      <Show when={range()}>
        {(bounds) => (
          <div class="control-editor-track">
            <input
              type="range"
              aria-label={controlCapabilityLabel(props.capability)}
              min={bounds().minimum}
              max={bounds().maximum}
              step={bounds().step}
              // 值超出量程时**把手停在端点，值本身不动** —— 打字可以超出，
              // 滑块只是画不出那么远。
              value={sliderThumbValue(shown(), bounds())}
              disabled={props.disabled}
              onInput={(event) => props.onChange({
                capabilityId: props.capability.id,
                number: snapControlNumber(Number(event.currentTarget.value), bounds()),
                // 单位跟着值走，别让后端去猜这个数字是瓦还是档。
                unit: bounds().unit
              })}
            />
            {/*
              机器现在实际停在哪儿。**一条刻度，不是一行字。**
              文字留给读屏 —— 看得见的人从位置就读得出来，
              读不见的人需要那句话。
            */}
            <Show when={actualPercent()}>
              {(percent) => (
                <span
                  class="control-editor-actual-mark"
                  // 传**无单位的数**：下面的 calc 要拿它去乘轨道长度再除以 100，
                  // 带上 % 的话那个乘法就是非法表达式，刻度会整条落到最左端。
                  style={{ "--control-actual-percent": String(percent()) }}
                  role="img"
                  aria-label={`${uiText.control.actual}${formatActualNumber(
                    props.actual!)} ${controlDisplayText(bounds().unit)}`}
                />
              )}
            </Show>
          </div>
        )}
      </Show>
      {/*
        值不是只能拖出来的，**也能直接敲**。
        滑条适合粗调和看位置，精确到某一个数还是打字最快 ——
        115 W 这种数拖十几次也未必停得准。

        用原生 number 框，不是自己糊一个：min / max / step 交给它，
        上下方向键和微调钮就按同一个步长走，读屏和输入法也都是现成的。
        滑条那边左右方向键同样按这个步长走 —— 两处步长同一个来源，
        所以"按一下动一格"和框里显示的精度天然对得上。
      */}
      <Show when={range()} fallback={
        <span class="control-editor-value">{formatControlNumber(shown(), undefined)}</span>
      }>
        {(bounds) => (
          <label class="control-editor-value">
            <input
              class="control-editor-value-input"
              type="number"
              inputmode="decimal"
              /*
                **不给 min / max。**
                给了的话浏览器会把超出的输入判成非法（红框），方向键也会在
                端点上卡住 —— 而这里恰恰要允许敲出量程外的数。
                量程由旁边的滑块表达，能不能真写进去由硬件说了算。
              */
              step={bounds().step}
              value={draft() ?? formatControlNumber(shown(), bounds().step)}
              disabled={props.disabled}
              aria-label={controlCapabilityLabel(props.capability)}
              onInput={(event) => setDraft(event.currentTarget.value)}
              // 方向键微调、点微调钮、失焦都会走到 change。
              onChange={(event) => commit(event.currentTarget.value)}
              onBlur={(event) => commit(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  commit(event.currentTarget.value);
                }
              }}
            />
            <Show when={bounds().unit}>
              {(unit) => <span class="control-editor-value-unit">{controlDisplayText(unit())}</span>}
            </Show>
          </label>
        )}
      </Show>
    </div>
  );
}

/**
 * 曲线编辑：曲线上的点，上下拖。
 *
 * 交互沿用这类工具的通行做法（Afterburner / FanControl）：横轴温度、纵轴转速，
 * 点就落在曲线上。点的温度固定在每 5 度，只能上下动 —— 所以每次调整都落在
 * 同一组温度上，可预期、可对比。相邻点直线插值。
 */
function CurveEditor(props: {
  capability: ControlCapability;
  /** 这条曲线属于哪个对象。读固件当前曲线要用它。 */
  objectId: string;
  /**
   * 这个对象的词条。曲线能交给谁执行是从这里读的
   * （`curve-firmware` / `curve-software`，两条都有就两个都在）。
   */
  terms: readonly string[];
  /** 选了软件接管会被一起接管的其他风扇。没有就不提。 */
  takenOverWith?: readonly string[];
  setting: ControlSetting | undefined;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const runtime = useFrontendRuntime();
  const [reading, setReading] = createSignal(false);
  let mounted = true;
  onCleanup(() => { mounted = false; });

  const stops = createMemo(() => stopsFromCurve(props.setting?.curve));
  const configured = () => props.setting?.curve != null;

  /*
   * 这条曲线能交给谁执行。两条都有的机器上用户可以选。
   *
   * **默认选固件。** 固件那条写一次就一直有效，本程序关了、重启了都还在；
   * 软件那条得我们一直跑着，程序不在就没了 —— 前者是更好的默认。
   */
  const canFirmware = () => props.terms.includes("curve-firmware");
  const canSoftware = () => props.terms.includes("curve-software");
  const execution = () =>
    props.setting?.curveExecution ?? (canFirmware() ? "firmware" : "software");

  /*
   * 这台机器的固件表**只让改每一档的转速**，温度断点是固件定死的
   * （联想 Legion 那种 10 档表就是这样）。
   *
   * 图本来就只能上下拖，所以交互上没有差别；但横轴上那几个温度是**我们的刻度**，
   * 不是固件真正的断点 —— 不说出来，用户会以为那几个温度是这台机器在用的。
   * 只在交给固件执行时才提：交给软件跑的时候，温度是我们自己在判，那就是真的。
   */
  const fixedSteps = () =>
    props.terms.includes("curve-fixed-steps") && execution() === "firmware";

  const write = (next: number[], mode?: string) =>
    props.onChange({
      capabilityId: props.capability.id,
      curve: curveFromStops(next),
      curveExecution: mode ?? execution()
    });

  /*
   * 新建曲线时，起点是**机器现在真在跑的那条**。
   *
   * 拿一条我们编的默认曲线起步，用户调出来的东西和这台机器的实际行为毫无关系，
   * 也没法判断自己到底是调高了还是调低了。
   *
   * **读这一下只在按钮按下时发生，不在挂载时。** 读一条曲线要把固件的三张表
   * 都过一遍、几十次 EC 往返，而风扇核心那条通道一次只能走一个请求 ——
   * 挂载时就去读的话，两个风扇一上来各占十几秒，把整页要用的
   * describe / read 全堵在后面，控制页直接超时打不开。这一条是实测踩到的。
   *
   * 读不到就退回默认曲线：读不到不该让用户连新建都做不了。
   */
  const start = async () => {
    if (reading()) {
      return;
    }
    setReading(true);
    const requestedSetting = props.setting;
    const stillSelected = () => mounted && props.setting === requestedSetting && !props.disabled;
    try {
      const firmware = await getControlObjectCurve(runtime.requestClient, props.objectId);
      if (stillSelected()) write(firmware ? stopsFromCurve(firmware) : defaultStops());
    } catch {
      // 读不到就用默认曲线起步。这里不报错：用户要的是开始编曲线，
      // 而那件事并不依赖读得到固件当前值。
      if (stillSelected()) write(defaultStops());
    } finally {
      setReading(false);
    }
  };

  /*
   * 用户刚勾上这一项：立刻去读固件当前曲线。
   *
   * **这不是"挂载就读"** —— 那一版把每个风扇的曲线都在开页时读一遍，
   * 风扇核心那条通道一次只走一个请求，整页直接超时打不开。
   * 这里的触发条件是"这一项被勾上了但还没有曲线"，也就是用户自己动的手。
   */
  createEffect(() => {
    if (!props.disabled && props.setting !== undefined && props.setting.curve == null && !reading()) {
      void start();
    }
  });

  return (
    <div class="control-curve">
      {/*
        交给谁执行。**只在两条都有的时候才摆这个选择** ——
        只有一条路的机器上摆一个选不动的开关，纯粹是噪音；
        那时它走哪条由对象的词条说明，不必在这里再说一遍。
      */}
      <Show when={configured() && canFirmware() && canSoftware()}>
        <SegmentedControl
          class="settings-segmented-control control-curve-execution"
          itemClass="settings-segment"
          ariaLabel={uiText.control.curveExecutionLabel}
          value={execution()}
          disabled={props.disabled}
          options={[
            {
              id: "firmware",
              label: uiText.control.curveExecutionFirmware,
              description: uiText.control.curveExecutionFirmwareHint
            },
            {
              id: "software",
              label: uiText.control.curveExecutionSoftware,
              description: uiText.control.curveExecutionSoftwareHint
            }
          ]}
          onChange={(mode) => props.onChange({ ...props.setting!, curveExecution: mode })}
        />
      </Show>
      {/*
        软件接管会连带别的风扇时，**在他选之前就说，而且点名说是哪几把**。
        只写一句"可能影响其他风扇"等于没说 —— 用户要能对着机器数得出来。
        不会连带的平台上一个字都不显示。
      */}
      <Show when={execution() === "software"
        && (props.takenOverWith?.length ?? 0) > 0
        && props.takenOverWith}>
        {(others) => (
          <p class="control-curve-note" role="note">
            {uiText.control.takeoverCoversOthers(others().join("、"))}
          </p>
        )}
      </Show>
      {/*
        固件表是固定档位的：横轴那几个温度是我们的刻度，不是固件真正的断点。
        说出来，用户才不会以为拖动横轴能改断点。
      */}
      <Show when={fixedSteps()}>
        <p class="control-curve-note" role="note">
          {uiText.control.fixedStepsCurveNote}
        </p>
      </Show>
      <Show
        when={configured()}
        fallback={
          <button class="secondary" type="button"
            disabled={props.disabled || reading()}
            onClick={start}>
            {reading() ? uiText.control.readingFirmwareCurve : uiText.control.createCurve}
          </button>
        }
      >
        <FanCurveChart stops={stops()} disabled={props.disabled} onChange={write} />
      </Show>
    </div>
  );
}
