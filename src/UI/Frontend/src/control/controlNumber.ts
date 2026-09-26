/**
 * 数值型控制点的取值换算。
 *
 * 和 `fanCurve.ts` 一样是纯计算，不碰界面 —— 这样"能设成什么"这件事
 * 只有一个说法，滑条、数字框、键盘微调三处共用，不会各算各的。
 */

/** 数值型控制点的取值范围。 */
export interface ControlNumberBounds {
  minimum: number;
  maximum: number;
  step?: number;
  unit?: string;
}

/** 步长缺失或不合法时按 1 走 —— 和原生 input 的默认一致。 */
export function effectiveStep(step: number | undefined): number {
  return step !== undefined && Number.isFinite(step) && step > 0 ? step : 1;
}

/**
 * 这个步长该显示几位小数。
 *
 * **可设精度和显示精度是同一件事**，所以只有这一个地方算它：
 * 按一下方向键走一个步长，框里就该多出一位看得见的变化。
 * 两边各算各的就会出现"按了没反应"（步长比显示精度细）
 * 或者"跳两格"（步长比显示精度粗）。
 */
export function decimalsForStep(step: number | undefined): number {
  const value = effectiveStep(step);
  if (value >= 1) {
    return 0;
  }
  // 不写死两位：0.005 这种步长要三位才看得见一格的变化。
  const text = value.toFixed(10).replace(/0+$/, "");
  const dot = text.indexOf(".");
  return dot < 0 ? 0 : Math.min(text.length - dot - 1, 6);
}

/**
 * **设定值**按步长决定显示几位小数。
 *
 * 这个数是用户能拖到的那个档位，步长 1 W 时第三位小数根本不可设，
 * 摆出来就成了"精确到毫瓦"的假象。所以显示精度不超过可设精度。
 */
export function formatControlNumber(value: number, step: number | undefined): string {
  return value.toFixed(decimalsForStep(step));
}

/**
 * **实际值**按它本来的样子显示，不按步长取整。
 *
 * 机器现在是 113.6 W 就写 113.6 W。步长管的是"能设成什么"，
 * 不该拿它去修剪"现在是什么" —— 把 113.6 显示成 114，用户看到的
 * 就不是后端如实报上来的那个数了。
 *
 * 只是不留没意义的尾巴：整数就不写小数点。
 */
export function formatActualNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/**
 * 对齐到步长，**不夹范围**。
 *
 * 档位是硬件的真实约束（这些寄存器就是一个字节，半瓦根本设不出来），
 * 所以照样对齐；但上下界只是量程建议，打字时不拿它去截用户的数。
 */
export function snapToStep(value: number, bounds: ControlNumberBounds): number {
  const step = effectiveStep(bounds.step);
  const snapped = bounds.minimum + Math.round((value - bounds.minimum) / step) * step;
  // 步长 0.1 时 min + n*step 会攒出 15.000000000000002 这种尾巴，按精度截掉。
  return Number(snapped.toFixed(decimalsForStep(bounds.step)));
}

/**
 * 夹进范围并对齐步长 —— 给**滑块**用。
 *
 * 滑块画的就是这条量程，它的值天然落在里面。
 */
export function snapControlNumber(value: number, bounds: ControlNumberBounds): number {
  return snapToStep(
    Math.min(bounds.maximum, Math.max(bounds.minimum, value)),
    bounds
  );
}

/**
 * 滑块的**把手**停在哪儿。
 *
 * 值超出量程时把手停在对应那一端 —— 但值本身不动。
 * **量程是建议，不是闸**：用户直接敲一个更大的数是允许的，
 * 拉不到那么远只是滑块画不出来，不代表这个数不作数。
 * 真正拦得住的是硬件，而固件夹回多少会如实回报。
 */
export function sliderThumbValue(value: number, bounds: ControlNumberBounds): number {
  return Math.min(bounds.maximum, Math.max(bounds.minimum, value));
}
