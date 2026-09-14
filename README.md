# Resource Manager

Resource Manager is a Windows desktop application for monitoring resource usage and controlling how applications share CPU, memory, and GPUs.

## Features

- **Find what is using your PC.** Identify applications that are loading your CPU or GPU, taking up RAM or GPU memory, or generating disk and network traffic. Compare applications at a glance, then drill down into individual processes.
- **Give important applications priority.** Let automatic scheduling balance CPU resources, or choose priorities, core placement, and exclusive allocation for specific applications.
- **Manage background memory use.** Set memory policies for individual applications, including periodic working-set trimming to reclaim resident memory.
- **Choose which GPU an application uses.** Assign applications to a preferred GPU and customize scheduling preferences. Optional GPU Shim support extends placement control to compatible applications.
- **Build the dashboard you need.** Pick the metrics that matter to you and customize their layout, colors, and update frequency.
- **Spend less time configuring applications.** Built-in software recognition supplies classifications and starting policies for many applications. Customize individual applications to suit your workflow.
- **See what scheduling actually does.** Inspect application scores, scheduling actions, and reports to understand how resources are being managed.
- **Keep resource management out of your way.** Run in the background, access the interface from the system tray, and optionally start automatically when you sign in.
- **Connect your own tools.** Use local APIs and the adapter SDK to integrate resource monitoring and management into your workflow.

Hardware readings depend on available drivers and providers. GPU placement support depends on the application's rendering API and device lifecycle.

## Download

Get the Windows x64 beta from the [latest release](https://github.com/BIOcanse/Resource-Manager/releases/latest).

Microsoft Edge WebView2 Runtime is required. The beta includes the .NET runtime. Preserve your Config and UserData folders when updating.

Extract the complete release folder to a permanent location. Run `Install.cmd` to register that directory, then `Start.cmd` to start the service and desktop interface. `Restart.cmd` restarts the service. Service management requests administrator permission; the interface runs as a standard user.

## License and Acknowledgements

Resource Manager is licensed under [Apache-2.0](LICENSE). Third-party components retain their own licenses.

Our sincere thanks to Microsoft, the .NET Foundation, and every upstream author and maintainer whose work makes this project possible. All dependencies are acknowledged, including those whose licenses do not require attribution. See the application's Credits page and [Third-Party Notices](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices/README.md).

This project is maintained by the author. External contributions and pull requests are not accepted.
