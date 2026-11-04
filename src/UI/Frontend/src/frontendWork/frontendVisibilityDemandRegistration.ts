import {
  createEffect,
  createMemo,
  on,
  onCleanup,
  untrack,
  type Accessor
} from "solid-js";
import type { FrontendWorkId } from "./frontendWorkIds";

interface FrontendVisibilityDemandRegistrar {
  registerVisibilityDemand: (
    demandId: string,
    workIds: readonly FrontendWorkId[]
  ) => () => void;
}

export interface FrontendVisibilityDemandRegistration {
  demandId: string;
  workIds: readonly FrontendWorkId[];
}

export function bindFrontendVisibilityDemand(
  controller: FrontendVisibilityDemandRegistrar,
  registration: Accessor<FrontendVisibilityDemandRegistration>
) {
  const semanticKey = createMemo(() => registrationKey(registration()));
  createEffect(on(semanticKey, () => {
    const { demandId, workIds } = normalizeRegistration(untrack(registration));
    const unregister = untrack(
      () => controller.registerVisibilityDemand(demandId, workIds));
    onCleanup(() => untrack(unregister));
  }));
}

function normalizeRegistration(
  registration: FrontendVisibilityDemandRegistration
): FrontendVisibilityDemandRegistration {
  return {
    demandId: registration.demandId.trim(),
    workIds: [...new Set(registration.workIds)].sort()
  };
}

function registrationKey(registration: FrontendVisibilityDemandRegistration) {
  const normalized = normalizeRegistration(registration);
  return JSON.stringify([normalized.demandId, normalized.workIds]);
}
