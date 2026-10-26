/*
 * 数量文案的单复数选择：唯一入口。
 *
 * 文案里和数字一致变化的词（"1 Eintrag" / "2 Einträge"，"1 ядро" / "2 ядра" / "5 ядер"）一律通过 plural()
 * 由 Intl.PluralRules 按界面语言选出对应的写法，形式里的 {count} 换成数字本身（String(count)，和直接插值一致）。
 * 每种语言必须给出该语言的全部类别：类别表在类型里，缺一种形式编译不过；表本身由测试对照 Intl 核验。
 * 中文、日文、韩文只有 other 一类，没有可选的形式，所以这些目录直接写数字；"标签：数字"这类写法
 * 数字前面是标签、没有跟数字一致变化的词，也不经过这里。
 */
export type PluralLocale = keyof PluralCategoryTable;

interface PluralCategoryTable {
  "zh-CN": "other";
  "zh-TW": "other";
  "en-US": "one" | "other";
  "ja-JP": "other";
  "ko-KR": "other";
  "fr-FR": "one" | "many" | "other";
  "de-DE": "one" | "other";
  "es-ES": "one" | "many" | "other";
  "ru-RU": "one" | "few" | "many" | "other";
}

export type PluralForms<L extends PluralLocale> = Readonly<Record<PluralCategoryTable[L], string>>;

/** 每种语言的类别，和上面的类型一一对应；测试要求它等于 Intl.PluralRules 给出的类别。 */
export const pluralCategories: { readonly [L in PluralLocale]: readonly PluralCategoryTable[L][] } = {
  "zh-CN": ["other"],
  "zh-TW": ["other"],
  "en-US": ["one", "other"],
  "ja-JP": ["other"],
  "ko-KR": ["other"],
  "fr-FR": ["one", "many", "other"],
  "de-DE": ["one", "other"],
  "es-ES": ["one", "many", "other"],
  "ru-RU": ["one", "few", "many", "other"]
};

const rulesByLocale = new Map<PluralLocale, Intl.PluralRules>();

function rulesFor(locale: PluralLocale) {
  let rules = rulesByLocale.get(locale);
  if (!rules) {
    rules = new Intl.PluralRules(locale);
    rulesByLocale.set(locale, rules);
  }
  return rules;
}

export function plural<L extends PluralLocale>(locale: L, count: number, forms: PluralForms<L>): string {
  const category = rulesFor(locale).select(count) as PluralCategoryTable[L];
  const form = forms[category];
  if (form === undefined) {
    // 只有运行时的 Intl 类别和上面的表不一致时才会到这里；明确报错，不拿别的形式顶替。
    throw new Error(`No ${category} plural form for ${locale}`);
  }
  return form.replaceAll("{count}", String(count));
}
