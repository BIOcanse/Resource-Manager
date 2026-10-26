import { currentLanguage } from "../text.ts";

// Only formatters are cached: every call reads the current interface language
// and formats the supplied value. Options order does not create duplicate entries.
const formatters = new Map<string, Map<string, Intl.NumberFormat>>();

function numberFormatter(options: Intl.NumberFormatOptions): Intl.NumberFormat {
  const language = currentLanguage();
  let languageFormatters = formatters.get(language);
  if (!languageFormatters) {
    languageFormatters = new Map();
    formatters.set(language, languageFormatters);
  }
  const key = JSON.stringify(Object.entries(options).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0));
  let formatter = languageFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(language, options);
    languageFormatters.set(key, formatter);
  }
  return formatter;
}

/** General locale formatting; callers retain their existing grouping and precision options. */
export function formatInterfaceNumber(value: number | bigint, options: Intl.NumberFormatOptions = {}): string {
  return numberFormatter(options).format(value);
}

/** Preserve the existing binary toFixed rounding, including negative zero and trailing zeroes. */
export function formatFixedDigits(value: number, digits: number): string {
  return formatInterfaceNumber(Number(value.toFixed(digits)), {
    useGrouping: false, minimumFractionDigits: digits, maximumFractionDigits: digits
  });
}

/** Preserve toFixed rounding but omit trailing zeroes. */
export function formatTrimmedDigits(value: number, digits: number): string {
  return formatInterfaceNumber(Number(value.toFixed(digits)), { useGrouping: false, maximumFractionDigits: digits });
}

export function formatSignificantDigits(value: number, digits: number): string {
  return formatInterfaceNumber(Number(value.toPrecision(digits)), { useGrouping: false, maximumSignificantDigits: digits });
}

/** Legacy decimal buffers must substitute only the separator, never parse and round again. */
export function interfaceDecimalSeparator(): string {
  return numberFormatter({ useGrouping: false }).formatToParts(1.1).find(part => part.type === "decimal")!.value;
}
