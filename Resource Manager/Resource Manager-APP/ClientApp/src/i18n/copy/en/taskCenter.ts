import type { AppCopy } from "../zh/index.ts";

export const enTaskCenterCopy: Pick<AppCopy, "taskCenter"> = {
  taskCenter: {
    tabActive: "Active",
    tabHistory: "History",
    tabAll: "All",
    tabDiagnostics: "Diagnostics",
    title: "Task center",
    activeCount: (count: number) => `${count} in progress`,
    noActiveTasks: "No tasks are running right now",
    backendSyncing: "Backend tasks are syncing.",
    backendResyncing: "The local service is resyncing; backend tasks cannot be changed right now.",
    backendUnavailable: "Backend task state cannot be read; interface tasks still work.",
    filterLabel: "Task filter",
    noBackendTasks: "This startup profile provides no backend operation tasks; interface tasks still appear here",
    noTasksInFilter: "No tasks in this filter",
    itemCount: (count: number) => `${count} items`,
    close: "Close",
    sourceOperation: "Backend operation",
    sourceFrontend: "Interface task",
    progressLabel: (title: string) => `${title} progress`,
    needsResync: "The task state needs to be confirmed with the local service again.",
    taskFailed: "The task did not complete",
    taskCompleted: "The task completed",
    detailsSummary: "Details",
    kind: "Kind",
    target: "Target",
    createdAt: "Created",
    updatedAt: "Updated",
    identity: "Identity",
    canceling: "Canceling",
    cancelTask: "Cancel task",
    cancelWithTitle: (title: string, identity: string) => `Cancel ${title} (${identity})`,
    operation: {
      componentDownload: "Download component",
      componentInstall: "Install component",
      dependencyDownload: "Download dependency",
      dependencyLaunchInstaller: "Install dependency",
      softwareUninstall: "Uninstall software",
      migrationExecute: "Migrate software data",
      migrationRestore: "Restore software data",
      discoveryStart: "Discover migratable data",
      resourceBreakdownLayoutSettle: "Resource layout transition"
    },
    status: {
      queued: "Waiting",
      startPending: "Starting",
      running: "Running",
      cancelPending: "Canceling",
      retryWait: "Waiting to retry",
      recoveryPending: "Recovering",
      succeeded: "Completed",
      failed: "Not completed",
      canceled: "Canceled",
      superseded: "Superseded",
      stateUncertain: "State to be confirmed",
      unknown: "State unknown"
    },
    blocked: {
      cannotCancel: "This task cannot be canceled.",
      taskFinished: "The task has already finished.",
      sessionNotSynced: "The local service session is not synced yet.",
      cancelRequested: "A cancel request was already submitted.",
      operationCannotCancel: "This operation cannot be canceled.",
      operationFinished: "The operation has already finished."
    }
  }
};