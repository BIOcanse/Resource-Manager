import type { JSX } from "solid-js";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import type { FrontendWorkId } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";

interface MonitorWorkRegionProps {
  label: string;
  workId: FrontendWorkId;
  children: JSX.Element;
}

export function MonitorWorkRegion(props: MonitorWorkRegionProps) {
  useFrontendVisibilityDemand(props.workId, [props.workId]);
  return (
    <section
      class="monitor-work-region"
      aria-label={props.label}
      {...frontendVisibilitySurface(props.workId, [props.workId])}
    >
      {props.children}
    </section>
  );
}
