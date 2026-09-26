import { onCleanup, onMount } from "solid-js";
import type { FrontendWorkId } from "./frontendWorkIds";
import { bindFrontendVisibilityDemand } from "./frontendVisibilityDemandRegistration";
import { useFrontendWork } from "./FrontendWorkContext";

export function useFrontendVisibilityDemand(
  demandId: string,
  workIds: readonly FrontendWorkId[]
) {
  const mainController = useFrontendWork();
  onMount(() => {
    const unregister = mainController.registerVisibilityDemand(demandId, workIds);
    onCleanup(unregister);
  });
}

export function FrontendVisibilityDemandBinding(props: {
  demandId: string;
  workIds: readonly FrontendWorkId[];
}) {
  const mainController = useFrontendWork();
  bindFrontendVisibilityDemand(mainController, () => ({
    demandId: props.demandId,
    workIds: props.workIds
  }));
  return null;
}
