import type { DeviceTopologySystemIdentity } from "../../types.ts";
import { uiText } from "../../text.ts";

/** 整机栏底部的一枚标签：label 说明这是什么（主板厂商、主板型号、BIOS），value 是取值；title 是完整原文。 */
export interface MachineIdentityChip {
  readonly label: string | null;
  readonly value: string;
  readonly title: string;
}

/**
 * 整机栏里的识别信息，每样只说一遍（用户「不要重复一遍」）：
 * - 示意图上是标志字、厂商和系列；厂商和标志字相同（不分大小写）时不再写厂商；
 * - 下面的标签只放上面没写过的：主板厂商和已写的厂商相同时不列；主板型号和系列相同时不列，以系列开头时只列系列后面的部分
 *   （完整原文在标签的悬停提示里）；BIOS 版本照列；
 * - 后端没发的项仍显示“未知”，不靠省略表达缺失。
 * 比较只看去掉首尾空白后不分大小写是否相同，不做别的猜测。
 */
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

/**
 * 主板型号去掉开头已经写过的系列和紧跟的分隔符；和系列相同时返回空串。
 * 系列后面必须是分隔符或结尾（“Z”不会把“Z790-A”截成“790-A”），否则原样返回。
 */
function afterSeries(product: string, series: string): string {
  const text = product.trim();
  const prefix = series.trim();
  if (prefix.length === 0 || !text.toLocaleUpperCase("en-US").startsWith(prefix.toLocaleUpperCase("en-US"))) return text;
  const rest = text.slice(prefix.length);
  const separator = /^[\s\-_/·:]+/.exec(rest);
  if (rest.length > 0 && !separator) return text;
  return rest.slice(separator?.[0].length ?? 0);
}
