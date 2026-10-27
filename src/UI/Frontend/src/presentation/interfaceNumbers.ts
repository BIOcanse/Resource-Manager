import { currentLanguage } from "../text.ts";

// Retain only immutable language/precision metadata, never formatted readings.
// Every entry point still reads the current language and formats its input now.
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
    // Alias the canonical options entry, so the general API shares this same
    // formatter. Only the first use builds/sorts/serializes precision options.
    formatter = numberFormatter(options, formatting);
    formatting.formatters.set(key, formatter);
  }
  return formatter;
}

/** General locale formatting; callers retain their existing grouping and precision options. */
export function formatInterfaceNumber(value: number | bigint, options: Intl.NumberFormatOptions = {}): string {
  return numberFormatter(options).format(value);
}

/** Preserve the existing binary toFixed rounding, including negative zero and trailing zeroes. */
export function formatFixedDigits(value: number, digits: number): string {
  const rounded = Number(value.toFixed(digits));
  return precisionFormatter("fixed", digits).format(rounded);
}

/** Preserve toFixed rounding but omit trailing zeroes. */
export function formatTrimmedDigits(value: number, digits: number): string {
  const rounded = Number(value.toFixed(digits));
  return precisionFormatter("trimmed", digits).format(rounded);
}

export function formatSignificantDigits(value: number, digits: number): string {
  const rounded = Number(value.toPrecision(digits));
  return precisionFormatter("significant", digits).format(rounded);
}

/** Legacy decimal buffers must substitute only the separator, never parse and round again. */
export function interfaceDecimalSeparator(): string {
  const formatting = currentFormatting();
  if (formatting.decimalSeparator === undefined) {
    formatting.decimalSeparator = numberFormatter({ useGrouping: false }, formatting)
      .formatToParts(1.1).find(part => part.type === "decimal")!.value;
  }
  return formatting.decimalSeparator;
}
