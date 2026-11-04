import { Match, Switch } from "solid-js";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import type { ManageSubpage } from "./manageNavigation.ts";
import type { ManageSubpageProps } from "./subpages/manageSubpageParts.tsx";
import { SmartSchedulingSubpage } from "./subpages/SmartSchedulingSubpage.tsx";
import { MemoryProtectionSubpage } from "./subpages/MemoryProtectionSubpage.tsx";
import { CpuMonopolySubpage } from "./subpages/CpuMonopolySubpage.tsx";
import { FreezeProtectionSubpage } from "./subpages/FreezeProtectionSubpage.tsx";
import { KernelLeaksSubpage } from "./subpages/KernelLeaksSubpage.tsx";
import { DiskManagementSubpage } from "./subpages/DiskManagementSubpage.tsx";
import { TargetedSubpage } from "./subpages/TargetedSubpage.tsx";
import { CrashReportsSubpage } from "./subpages/CrashReportsSubpage.tsx";
import { PerformanceOverlaySubpage } from "./subpages/PerformanceOverlaySubpage.tsx";

/* Mount the selected management composition. Settings share one draft per visit; leaving management discards unsaved changes. */
export function ManagePage(props: ManageSubpageProps & { readonly activeSubpage: ManageSubpage }) {
  onPageLeave(props.settings.discardDraftChanges);
  return (
    <section id="managePage" class="page active-page manage-page">
      <Switch>
        <Match when={props.activeSubpage === "smartScheduling"}><SmartSchedulingSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "memoryProtection"}><MemoryProtectionSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "cpuMonopoly"}><CpuMonopolySubpage {...props} /></Match>
        <Match when={props.activeSubpage === "freezeProtection"}><FreezeProtectionSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "kernelLeaks"}><KernelLeaksSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "diskManagement"}><DiskManagementSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "targeted"}><TargetedSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "crashReports"}><CrashReportsSubpage {...props} /></Match>
        <Match when={props.activeSubpage === "performanceOverlay"}><PerformanceOverlaySubpage /></Match>
      </Switch>
    </section>
  );
}
