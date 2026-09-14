export type ActiveDescendantKey =
  | "ArrowLeft"
  | "ArrowRight"
  | "ArrowUp"
  | "ArrowDown"
  | "Home"
  | "End"
  | "Enter"
  | " ";

export interface ActiveDescendantTarget {
  readonly id: string;
  readonly index: number;
}

export function resolveActiveDescendantTarget(
  itemIds: readonly string[],
  activeId: string | null | undefined,
  key: string
): ActiveDescendantTarget | null {
  if (itemIds.length === 0 || !isActiveDescendantKey(key)) {
    return null;
  }
  const currentIndex = activeId ? itemIds.indexOf(activeId) : -1;
  let index: number;
  switch (key) {
    case "Home":
      index = 0;
      break;
    case "End":
      index = itemIds.length - 1;
      break;
    case "ArrowLeft":
    case "ArrowUp":
      index = currentIndex < 0 ? itemIds.length - 1 : Math.max(0, currentIndex - 1);
      break;
    case "ArrowRight":
    case "ArrowDown":
      index = currentIndex < 0 ? 0 : Math.min(itemIds.length - 1, currentIndex + 1);
      break;
    default:
      index = currentIndex < 0 ? 0 : currentIndex;
      break;
  }
  return Object.freeze({ id: itemIds[index], index });
}

export function activeDescendantOptionId(
  ownerId: string,
  itemId: string
): string {
  return `active-option-${domIdPart(ownerId)}-${domIdPart(itemId)}`;
}

function isActiveDescendantKey(value: string): value is ActiveDescendantKey {
  return value === "ArrowLeft"
    || value === "ArrowRight"
    || value === "ArrowUp"
    || value === "ArrowDown"
    || value === "Home"
    || value === "End"
    || value === "Enter"
    || value === " ";
}

function domIdPart(value: string): string {
  return encodeURIComponent(value).replace(/%/g, "_").replace(/[^A-Za-z0-9_.-]/g, "_");
}
