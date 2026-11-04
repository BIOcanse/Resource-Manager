import { bindPushSource } from "../frontendRuntime/push/usePushSource.ts";
import { createEffect, onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import type { ManagementInventoryRevisionSnapshot } from "../data/managementInventoryRevision.ts";
import type { CurrentValueSource } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import type { OperationRegistry } from "../frontendRuntime/operations/OperationRegistry.ts";

interface InventoryRead {
  readonly active: Accessor<boolean>;
  invalidate(): Promise<unknown>;
}
export interface ManagementInventoryRevisionBindingOptions {
  readonly inventoryRevisionSource: CurrentValueSource<ManagementInventoryRevisionSnapshot>;
  readonly operations: Pick<OperationRegistry, "subscribeInvalidations">;
  readonly components: InventoryRead;
  readonly software: InventoryRead;
  readonly browserRuntimes: InventoryRead;
}

/** Revision and operation owners invalidate sources directly; the store is not a read owner. */
export function bindManagementInventoryRevisions(options: ManagementInventoryRevisionBindingOptions): void {
  createEffect(() => {
    if (!options.components.active() && !options.software.active() && !options.browserRuntimes.active()) return;
    let previous: ManagementInventoryRevisionSnapshot | null = null;
    const unsubscribe = bindPushSource(options.inventoryRevisionSource, { active: true }, snapshot => {
      const next = snapshot.data;
      if (!next) return;
      const before = previous;
      previous = next;
      // Initial reads belong to demand, never to this baseline.
      if (!before) return;
      if (before.componentRevision !== next.componentRevision) invalidate(options.components);
      if (before.softwareRevision !== next.softwareRevision) invalidate(options.software);
      if (before.browserRuntimeRevision !== next.browserRuntimeRevision) invalidate(options.browserRuntimes);
    });
    onCleanup(unsubscribe);
  });
  const unsubscribe = options.operations.subscribeInvalidations(event => {
    if (event.targets.includes("components")) invalidate(options.components);
    if (event.targets.includes("software")) invalidate(options.software);
    if (event.targets.includes("dependencies")) invalidate(options.browserRuntimes);
  });
  onCleanup(unsubscribe);
}
function invalidate(source: InventoryRead): void {
  if (source.active()) void source.invalidate();
}
