import assert from "node:assert/strict";
import {
  decimalsForStep,
  effectiveStep,
  formatActualNumber,
  formatControlNumber,
  sliderThumbValue,
  snapControlNumber,
  snapToStep
} from "../src/features/control/controlNumber.ts";

// 步长缺失或不合法按 1 走，和原生 input 一致。
assert.equal(effectiveStep(undefined), 1);
assert.equal(effectiveStep(0), 1);
assert.equal(effectiveStep(-5), 1);
assert.equal(effectiveStep(Number.NaN), 1);
assert.equal(effectiveStep(0.5), 0.5);

/*
 * **可设精度和显示精度必须一致。**
 * 按一下方向键走一个步长，显示上就得有一位看得见的变化 ——
 * 否则用户会看到"按了没反应"。
 */
assert.equal(decimalsForStep(1), 0);
assert.equal(decimalsForStep(5), 0);
assert.equal(decimalsForStep(0.5), 1);
assert.equal(decimalsForStep(0.1), 1);
assert.equal(decimalsForStep(0.05), 2);
// 0.005 这种步长要三位才看得见一格，写死两位就会"按了没反应"。
assert.equal(decimalsForStep(0.005), 3);
assert.equal(decimalsForStep(0.001), 3);

// 显示位数就是这个精度，不多也不少。
assert.equal(formatControlNumber(115, 1), "115");
assert.equal(formatControlNumber(115, undefined), "115");
assert.equal(formatControlNumber(1.05, 0.005), "1.050");
assert.equal(formatControlNumber(50.4, 0.1), "50.4");

// 走一个步长，显示确实变了一格：这是上面那条不变量的可执行版本。
for (const step of [1, 0.5, 0.1, 0.05, 0.005, 0.001]) {
  const bounds = { minimum: 0, maximum: 10, step };
  const here = snapControlNumber(1, bounds);
  const next = snapControlNumber(1 + step, bounds);
  assert.notEqual(
    formatControlNumber(here, step),
    formatControlNumber(next, step),
    `步长 ${step} 走一格之后显示没变，方向键会像没反应`
  );
}

// 滑块用的归一：先夹进范围，再对齐步长。
assert.equal(snapControlNumber(999, { minimum: 0, maximum: 50, step: 1 }), 50);
assert.equal(snapControlNumber(-40, { minimum: 0, maximum: 50, step: 1 }), 0);
assert.equal(snapControlNumber(37.4, { minimum: 0, maximum: 50, step: 1 }), 37);
assert.equal(snapControlNumber(37.6, { minimum: 0, maximum: 50, step: 1 }), 38);
// 下界不是 0 时，档位从下界起算，不是从 0 起算。
assert.equal(snapControlNumber(46, { minimum: 45, maximum: 105, step: 5 }), 45);
assert.equal(snapControlNumber(48, { minimum: 45, maximum: 105, step: 5 }), 50);

/*
 * **打字可以超出量程。**
 * 量程是滑块画得出来的那一段，不是闸；对齐步长照旧（寄存器就一个字节，
 * 半瓦设不出来），但上下界不拿来截用户敲的数。
 */
const watts = { minimum: 0, maximum: 50, step: 1 };
assert.equal(snapToStep(60, watts), 60);
assert.equal(snapToStep(-10, watts), -10);
assert.equal(snapToStep(60.4, watts), 60);
assert.equal(snapToStep(60.6, watts), 61);

/*
 * **超出量程时把手停在端点，值本身不动。**
 * 滑块画不出那么远，不代表那个数不作数。
 */
assert.equal(sliderThumbValue(60, watts), 50);
assert.equal(sliderThumbValue(-10, watts), 0);
assert.equal(sliderThumbValue(37, watts), 37);
// 把手夹了，值没被夹 —— 这两件事必须分开。
assert.notEqual(sliderThumbValue(60, watts), snapToStep(60, watts));

// 浮点尾巴要截掉：min + n*step 会攒出 15.000000000000002 这种数。
const tenth = snapControlNumber(15, { minimum: 0, maximum: 30, step: 0.1 });
assert.equal(tenth, 15);
assert.equal(String(tenth), "15");
assert.equal(snapControlNumber(0.30000000000000004, { minimum: 0, maximum: 1, step: 0.1 }), 0.3);

/*
 * **实际值不按步长取整。**
 * 步长管"能设成什么"，不该拿去修剪"现在是什么" ——
 * 机器在 113.6 W 就得写 113.6 W。
 */
assert.equal(formatActualNumber(113.6), "113.6");
assert.equal(formatActualNumber(115), "115");
assert.equal(formatActualNumber(0), "0");

console.log("controlNumber 契约通过");
