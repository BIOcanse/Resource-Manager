// Reflow animates layout changes through a noninteractive copy.

// The live node immediately occupies its final interactive geometry.
// The copy moves to the final geometry, then reveals the live node.
// Each reflow uses a fresh 0-1 channel to interpolate position and size.
// The slot animation style selects spring or easing.
// Disabled animation modes use the final layout without a copy.

import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

export const reflowDefinition: PartDefinition<number, "进场退场"> = {
  id: "reflow",
  name: "重排",
  motion: "进场退场",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: { id: "progress", label: "走了多少", unit: "", min: 0, max: 1, hand: [], field: carryField("slot") },
  ports: [],
  values: {}
};
