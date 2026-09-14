# Resource Manager

Resource Manager is a Windows desktop application for monitoring resource usage and controlling how applications share CPU, memory, and GPUs.

## Features

- **Application-level monitoring.** View CPU, memory, GPU, disk, and network activity by application or individual process. Sort resource tables and explore visual usage breakdowns.
- **CPU scheduling.** Use automatic scheduling or per-application settings to control CPU priorities, core placement, and exclusive allocation.
- **Memory management.** Configure per-application memory policies and periodic working-set trimming.
- **GPU placement.** Set preferred GPUs and adjust GPU performance scores for scheduling. Optional GPU Shim support provides additional placement options for supported applications.
- **Shared GPU memory accounting.** Distinguish private and shared allocations, with shared allocations apportioned among the processes using them.
- **Customizable monitoring.** Choose dashboard items, layouts, colors, and update frequencies.
- **Software recognition and profiles.** Bundled software definitions provide classifications and default scores. User overrides are stored separately from defaults and automatically discovered installation paths.
- **Scheduling visibility.** Inspect scheduling scores, actions, and optimization reports.
- **Background operation.** Run the backend as a Windows service, keep the desktop interface in the system tray, and optionally start at sign-in.
- **Local APIs and adapter SDK.** Access monitoring data and integrate with the resource-management service.

Hardware readings depend on available drivers and providers. GPU placement support depends on the application's rendering API and device lifecycle.

## Download

Get the Windows x64 beta from the [latest release](https://github.com/BIOcanse/Resource-Manager/releases/latest).

Microsoft Edge WebView2 Runtime is required. The beta includes the .NET runtime. Preserve your Config and UserData folders when updating.

## License and Acknowledgements

Resource Manager is licensed under [Apache-2.0](LICENSE). Third-party components retain their own licenses.

Our sincere thanks to Microsoft, the .NET Foundation, and every upstream author and maintainer whose work makes this project possible. All dependencies are acknowledged, including those whose licenses do not require attribution. See the application's Credits page and [Third-Party Notices](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices/README.md).

This project is maintained by the author. External contributions and pull requests are not accepted.
