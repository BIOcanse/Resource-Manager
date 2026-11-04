import type { DeviceTopologySystemIdentity } from "../../types.ts";
import { uiText } from "../../text.ts";

/** Identity tag with label, value, and complete original text in title. */
export interface MachineIdentityChip {
  readonly label: string | null;
  readonly value: string;
  readonly title: string;
}

/** Deduplicate vendor, series, and board identity using trimmed case-insensitive comparisons; retain BIOS and explicit unknowns without guessing. */
export interface MachineIdentity {
  readonly wordmark: string;
  readonly vendor: string | null;
  readonly series: string;
  readonly chips: readonly MachineIdentityChip[];
}

export function machineIdentity(system: DeviceTopologySystemIdentity): MachineIdentity {
  const copy = uiText.deviceTopology;
  const vendor = system.manufacturer.trim().length > 0 && !same(system.manufacturer, system.brandLogoText) ? system.manufacturer : null;
  const shown = [system.brandLogoText, system.manufacturer, system.model];
  const chips: MachineIdentityChip[] = [];

  const boardVendor = system.baseBoardManufacturer;
  if (boardVendor == null) {
    chips.push({ label: null, value: copy.baseBoardManufacturerUnknown, title: copy.baseBoardManufacturerUnknown });
  } else if (!shown.some((text) => same(text, boardVendor))) {
    chips.push({ label: copy.baseBoardManufacturerLabel, value: boardVendor, title: boardVendor });
  }

  const boardProduct = system.baseBoardProduct;
  if (boardProduct == null) {
    chips.push({ label: null, value: copy.baseBoardProductUnknown, title: copy.baseBoardProductUnknown });
  } else {
    const rest = afterSeries(boardProduct, system.model);
    if (rest.length > 0) chips.push({ label: copy.baseBoardProductLabel, value: rest, title: boardProduct });
  }

  chips.push(system.biosVersion == null
    ? { label: null, value: copy.biosUnknown, title: copy.biosUnknown }
    : { label: copy.biosLabel, value: system.biosVersion, title: system.biosVersion });

  return { wordmark: system.brandLogoText, vendor, series: system.model, chips };
}

function same(left: string, right: string): boolean {
  return left.trim().toLocaleUpperCase("en-US") === right.trim().toLocaleUpperCase("en-US");
}

/** Remove a repeated series prefix only at a separator boundary or end; preserve the complete model otherwise. */
function afterSeries(product: string, series: string): string {
  const text = product.trim();
  const prefix = series.trim();
  if (prefix.length === 0 || !text.toLocaleUpperCase("en-US").startsWith(prefix.toLocaleUpperCase("en-US"))) return text;
  const rest = text.slice(prefix.length);
  const separator = /^[\s\-_/·:]+/.exec(rest);
  if (rest.length > 0 && !separator) return text;
  return rest.slice(separator?.[0].length ?? 0);
}
