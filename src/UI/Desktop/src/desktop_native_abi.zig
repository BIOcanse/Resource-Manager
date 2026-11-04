const launcher_workflow = @import("launcher/workflow_abi.zig");
const launcher_installation = @import("launcher/installation_abi.zig");
const child_pipe = @import("window_process/child_pipe_abi.zig");

comptime {
    _ = launcher_workflow.rm_launcher_workflow_start;
    _ = launcher_installation.rm_launcher_installation_read;
    _ = child_pipe.rm_window_child_pipe_workspace_bytes;
    _ = child_pipe.rm_window_child_pipe_create;
    _ = child_pipe.rm_window_child_pipe_run;
    _ = child_pipe.rm_window_child_pipe_start;
    _ = child_pipe.rm_window_child_pipe_send;
    _ = child_pipe.rm_window_child_pipe_stop;
    _ = child_pipe.rm_window_child_pipe_destroy;
    _ = child_pipe.rm_window_child_pipe_live_owners;
    _ = child_pipe.rm_window_child_pipe_decode;
    _ = child_pipe.rm_window_child_pipe_decode_bytes;
}
