/*
 * 中文（简、繁）和日文文案的中西文间距：汉字或假名与拉丁字母、数字相邻时，中间空一个半角空格。
 * 例：“CPU 风扇”“正在加载 GPU 调度数据”“3 项”。标点和其他符号两侧不加。
 * 韩文不适用：助词和量词按语法直接接在拉丁字母和数字后面（“GPU를”“3개”）。
 *
 * 写死的文字按这条规则直接写好；插值两侧要看运行时的值是什么，所以插值紧贴汉字或假名的模板一律用 cjkText 标签，
 * 由它在边界上按同一条规则补空格。模板里已经写了空白的边界保持原样。
 * tests/cjkSpacingContract.test.ts 检查标签的用法，localizationCoverage 检查加载后的每条文案。
 */

const cjkCharacter = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー]/u;
const latinCharacter = /[A-Za-z0-9]/;

function needsGap(before: string, after: string): boolean {
  return (cjkCharacter.test(before) && latinCharacter.test(after))
    || (latinCharacter.test(before) && cjkCharacter.test(after));
}

/** 模板标签：把插值拼进文案，并在汉字或假名与拉丁字母、数字直接相接的插值边界上补一个空格。 */
export function cjkText(strings: TemplateStringsArray, ...values: readonly unknown[]): string {
  let text = strings[0];
  for (let index = 0; index < values.length; index += 1) {
    const value = String(values[index]);
    const literal = strings[index + 1];
    const valueStart = value.charAt(0);
    const valueEnd = value.charAt(value.length - 1);
    text += (value && needsGap(text.charAt(text.length - 1), valueStart) ? " " : "") + value;
    text += (literal && needsGap(valueEnd, literal.charAt(0)) ? " " : "") + literal;
  }
  return text;
}

/** 找出一段已经拼好的文案里汉字或假名与拉丁字母、数字直接相接的地方（测试用）。 */
export function cjkLatinGaps(text: string): string[] {
  const gaps: string[] = [];
  const characters = Array.from(text);
  for (let index = 1; index < characters.length; index += 1) {
    if (needsGap(characters[index - 1], characters[index])) {
      gaps.push(characters.slice(Math.max(0, index - 4), index + 4).join(""));
    }
  }
  return gaps;
}
