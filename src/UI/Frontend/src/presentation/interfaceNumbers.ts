import { currentLanguage } from "../text.ts";

// Cache immutable language/precision metadata, never formatted readings.
// Each call uses the current language and input.
interface LanguageNumberFormatting {
  readonly language: string;
  readonly formatters: Map<string, Intl.NumberFormat>;
  decimalSeparator?: string;
}
const languages = new Map<string, LanguageNumberFormatting>();

function currentFormatting(): LanguageNumberFormatting {
  const language = currentLanguage();
  let formatting = languages.get(language);
  if (!formatting) {
    formatting = { language, formatters: new Map() };
    languages.set(language, formatting);
  }
  return formatting;
}

function numberFormatter(options: Intl.NumberFormatOptions, formatting = currentFormatting()): Intl.NumberFormat {
  const key = JSON.stringify(Object.entries(options).sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0));
  let formatter = formatting.formatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(formatting.language, options);
    formatting.formatters.set(key, formatter);
  }
  return formatter;
}

function precisionFormatter(kind: "fixed" | "trimmed" | "significant", digits: number): Intl.NumberFormat {
  const formatting = currentFormatting();
  const key = `${kind}:${digits}`;
  let formatter = formatting.formatters.get(key);
  if (!formatter) {
    const options: Intl.NumberFormatOptions = kind === "significant"
      ? { useGrouping: false, maximumSignificantDigits: digits }
      : kind === "fixed"
        ? { useGrouping: false, minimumFractionDigits: digits, maximumFractionDigits: digits }
        : { useGrouping: false, maximumFractionDigits: digits };
    // Alias canonical precision options to share the formatter;
    // build, sort, and serialize options only on first use.
    formatter = numberFormatter(options, formatting);
    formatting.formatters.set(key, formatter);
  }
  return formatter;
}

/** Locale formatting with caller-supplied grouping and precision. */
export function formatInterfaceNumber(value: number | bigint, options: Intl.NumberFormatOptions = {}): string {
  return numberFormatter(options).format(value);
}

/** Preserve binary toFixed rounding, negative zero, and trailing zeroes. */
export function formatFixedDigits(value: number, digits: number): string {
  const rounded = Number(value.toFixed(digits));
  return precisionFormatter("fixed", digits).format(rounded);
}

/** Preserve toFixed rounding while trimming trailing zeroes. */
export function formatTrimmedDigits(value: number, digits: number): string {
  const rounded = Number(value.toFixed(digits));
  return precisionFormatter("trimmed", digits).format(rounded);
}

export function formatSignificantDigits(value: number, digits: number): string {
  const rounded = Number(value.toPrecision(digits));
  return precisionFormatter("significant", digits).format(rounded);
}

/** Substitute only the decimal separator in legacy buffers; never parse and round again. */
export function interfaceDecimalSeparator(): string {
  const formatting = currentFormatting();
  if (formatting.decimalSeparator === undefined) {
    formatting.decimalSeparator = numberFormatter({ useGrouping: false }, formatting)
      .formatToParts(1.1).find(part => part.type === "decimal")!.value;
  }
  return formatting.decimalSeparator;
}
