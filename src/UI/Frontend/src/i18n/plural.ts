/* Select count-dependent wording with Intl.PluralRules for the interface language.
 * Each language supplies every required category; {count} is replaced with String(count). */
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

/** Required plural categories for each supported language. */
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
    // Reject unexpected Intl categories rather than substitute another form.
    throw new Error(`No ${category} plural form for ${locale}`);
  }
  return form.replaceAll("{count}", String(count));
}
