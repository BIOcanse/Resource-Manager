import type { JSX } from "solid-js";

interface MonitorWorkRegionProps {
  label: string;
  children: JSX.Element;
}

export function MonitorWorkRegion(props: MonitorWorkRegionProps) {
  return (
    <section
      class="monitor-work-region"
      aria-label={props.label}
    >
      {props.children}
    </section>
  );
}
