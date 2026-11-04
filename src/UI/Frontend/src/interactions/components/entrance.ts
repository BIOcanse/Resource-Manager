// Each entering node owns a fresh 0-1 opacity channel.

// Entry changes opacity only, preserving the final hit geometry.
// Start at zero opacity and zero velocity without inheriting a previous node.
// The animation style selects the spring or easing for each part.
// Disabled animation modes reach the terminal value immediately.
import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

/** Entry parts keyed by their animation parameter prefix. */
export type EntrancePart = "dialog" | "page";

const NAMES: Readonly<Record<EntrancePart, string>> = { dialog: "弹窗进场", page: "内容进场" };

function entranceDefinition(part: EntrancePart): PartDefinition<number, "进场退场"> {
  return {
    id: `${part}-entrance`,
    name: NAMES[part],
    motion: "进场退场",
    modes: ["静止"],
    exclusive: [],
    rows: [],
    channel: {
      id: "openness", label: "开度", unit: "", min: 0, max: 1, hand: [],
      field: carryField(part)
    },
    ports: [],
    values: {}
  };
}

export const entranceDefinitions: Readonly<Record<EntrancePart, PartDefinition<number, "进场退场">>> = {
  dialog: entranceDefinition("dialog"),
  page: entranceDefinition("page")
};
