/* Separate Han or kana from adjacent Latin letters and digits in Chinese and Japanese copy.
 * Preserve existing whitespace and punctuation; Korean follows its own grammar. */

const cjkCharacter = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー]/u;
const latinCharacter = /[A-Za-z0-9]/;

function needsGap(before: string, after: string): boolean {
  return (cjkCharacter.test(before) && latinCharacter.test(after))
    || (latinCharacter.test(before) && cjkCharacter.test(after));
}

/** Add spacing only at interpolation boundaries; preserve interpolated data verbatim. */
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

/** Find unspaced Han or kana boundaries in rendered copy for validation. */
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
