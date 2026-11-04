import type { MetricFieldCatalogDevice, MetricFieldDevice, MetricFieldValue } from "../../data/metricFields/metricFieldSource.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import { uiText } from "../../text.ts";
import { deviceLabel, fieldHint, fieldLabel } from "../monitor/fieldCards.ts";

/* Show one row per backend storage device using bound NVMe or Windows health fields. Preserve reported values and warning bits, including NVMe saturation semantics; missing and unavailable readings remain distinct. */

export const nvmePercentageUsedField = "storage_all_nvme_percentage_used_reported";
export const nvmeAvailableSpareField = "storage_all_nvme_available_spare_reported";
export const nvmeAvailableSpareThresholdField = "storage_all_nvme_available_spare_threshold_reported";
export const nvmeCriticalWarningField = "storage_all_nvme_critical_warning_reported";
export const nvmeCompositeTemperatureField = "storage_all_nvme_composite_temperature_celsius";
export const storageWearField = "storage_all_wear_reported";
export const storageHealthStatusField = "storage_all_health_status_reported";

const nvmeFields = [
  nvmePercentageUsedField,
  nvmeAvailableSpareField,
  nvmeAvailableSpareThresholdField,
  nvmeCriticalWarningField,
  nvmeCompositeTemperatureField
] as const;
const windowsFields = [storageWearField, storageHealthStatusField] as const;

/** All health fields requested by the card. */
export const diskHealthFieldNames: readonly string[] = Object.freeze([...nvmeFields, ...windowsFields]);

export const diskHealthDeviceKind = "storage";

export interface DiskHealthItem {
  /** Primary field identity for this entry. */
  readonly field: string;
  readonly label: string;
  /** Null until values arrive; rendered as a placeholder. */
  readonly value: string | null;
  readonly hint: string | null;
}

export type DiskHealthRowKind = "nvme" | "windows" | "notReported";

export interface DiskHealthRow {
  readonly deviceIdentity: string;
  readonly name: string;
  readonly kind: DiskHealthRowKind;
  readonly items: readonly DiskHealthItem[];
}

/** Build health rows from the backend catalog and subscribed values; null catalog yields a placeholder. */
export function diskHealthRows(
  catalog: readonly MetricFieldCatalogDevice[] | null | undefined,
  devices: readonly MetricFieldDevice[] | null | undefined
): DiskHealthRow[] | null {
  if (!catalog) return null;
  const valuesByDevice = new Map((devices ?? []).map((device) => [device.deviceIdentity, device]));
  return catalog
    .filter((device) => device.kind === diskHealthDeviceKind)
    .sort((left, right) => left.ordinal - right.ordinal || left.deviceIdentity.localeCompare(right.deviceIdentity))
    .map((device) => {
      const bound = new Set(device.fields.map((field) => field.name));
      const kind: DiskHealthRowKind = nvmeFields.some((name) => bound.has(name)) ? "nvme"
        : windowsFields.some((name) => bound.has(name)) ? "windows" : "notReported";
      const reported = valuesByDevice.get(device.deviceIdentity);
      const value = (name: string) => reported?.fields.find((field) => field.name === name) ?? null;
      return {
        deviceIdentity: device.deviceIdentity,
        name: device.displayName?.trim() || deviceLabel(device, catalog),
        kind,
        items: kind === "nvme" ? nvmeItems(bound, value, devices != null)
          : kind === "windows" ? simpleItems(windowsFields, bound, value, devices != null) : []
      };
    });
}

type ValueOf = (name: string) => MetricFieldValue | null;

function nvmeItems(bound: ReadonlySet<string>, value: ValueOf, arrived: boolean): DiskHealthItem[] {
  const items: DiskHealthItem[] = [];
  if (bound.has(nvmePercentageUsedField)) items.push(item(nvmePercentageUsedField, value, arrived, percentageUsedText));
  if (bound.has(nvmeAvailableSpareField)) {
    const threshold = bound.has(nvmeAvailableSpareThresholdField) ? value(nvmeAvailableSpareThresholdField) : null;
    const spare = item(nvmeAvailableSpareField, value, arrived);
    const thresholdText = threshold ? formatFieldValue(threshold) : null;
    items.push({
      ...spare,
      value: spare.value !== null && thresholdText !== null
        ? uiText.diskUsage.health.spareWithThreshold(spare.value, thresholdText) : spare.value,
      hint: joinHints(fieldHint(nvmeAvailableSpareField), bound.has(nvmeAvailableSpareThresholdField)
        ? fieldHint(nvmeAvailableSpareThresholdField) : null)
    });
  } else if (bound.has(nvmeAvailableSpareThresholdField)) {
    items.push(item(nvmeAvailableSpareThresholdField, value, arrived));
  }
  if (bound.has(nvmeCriticalWarningField)) items.push(item(nvmeCriticalWarningField, value, arrived, criticalWarningText));
  if (bound.has(nvmeCompositeTemperatureField)) items.push(item(nvmeCompositeTemperatureField, value, arrived));
  return items;
}

function simpleItems(names: readonly string[], bound: ReadonlySet<string>, value: ValueOf, arrived: boolean): DiskHealthItem[] {
  return names.filter((name) => bound.has(name)).map((name) => item(name, value, arrived));
}

/** Null means values have not arrived; missing or noncurrent device fields render unavailable. */
function item(name: string, value: ValueOf, arrived: boolean, special?: (field: MetricFieldValue) => string | null): DiskHealthItem {
  const field = value(name);
  const text = field ? special?.(field) ?? formatFieldValue(field) : null;
  return {
    field: name,
    label: fieldLabel(name),
    value: !arrived ? null : text ?? uiText.diskUsage.health.unavailable,
    hint: fieldHint(name)
  };
}

/** NVMe Percentage Used is a byte: 255 means above 254%; other values remain unchanged. */
function percentageUsedText(field: MetricFieldValue): string | null {
  const raw = field.integerValue !== null ? Number(field.integerValue) : field.numericValue;
  return raw === 255 ? uiText.diskUsage.health.percentageUsedSaturated : null;
}

/** Zero means no warnings; nonzero masks retain unknown bits in hexadecimal. */
function criticalWarningText(field: MetricFieldValue): string | null {
  const raw = field.integerValue !== null ? field.integerValue : field.numericValue !== null ? BigInt(Math.trunc(field.numericValue)) : null;
  return raw === 0n ? uiText.diskUsage.health.noCriticalWarning : null;
}

function joinHints(...hints: (string | null)[]): string | null {
  const present = hints.filter((hint): hint is string => Boolean(hint));
  return present.length > 0 ? present.join("\n") : null;
}
