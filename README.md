# Resource Manager

See where your PC's resources go, and decide which applications get priority.

Resource Manager brings application-level monitoring and resource scheduling together on Windows. Find demanding applications, manage competing workloads, and choose how your software shares CPU, memory, and GPUs.

## Features

- **See the whole application, not just a list of processes.** Compare CPU, RAM, GPU, GPU memory, disk, and network usage by application, then drill down when you need more detail. Sort by resource use to find demanding software and tailor the dashboard to what you want to watch.
- **Manage competing workloads automatically.** Use resource scheduling to prioritize important applications and manage background CPU and memory use. Built-in software recognition provides a starting point without requiring you to configure every application from scratch.
- **Keep control over individual applications.** Choose automatic scheduling or set your own CPU and memory policies, core placement, exclusive allocation, and preferred GPU. Inspect scheduling decisions and reports to see what the manager is doing.

Resource Manager can stay in the system tray and start when you sign in, so you can leave it running without keeping a window open.

Hardware readings depend on available drivers and providers. GPU placement, including optional GPU Shim support, depends on application compatibility; it does not guarantee that a running application can switch GPUs.

## Download

Get the Windows x64 beta from the [latest release](https://github.com/BIOcanse/Resource-Manager/releases/latest).

Microsoft Edge WebView2 Runtime is required. The beta includes the .NET runtime. Preserve your Config and UserData folders when updating.

Extract the complete release folder to a permanent location. Run `Install.cmd` to register that directory, then `Start.cmd` to start the service and desktop interface. `Restart.cmd` restarts the service. Service management requests administrator permission; the interface runs as a standard user.

## License and Acknowledgements

Resource Manager is licensed under [Apache-2.0](LICENSE). Third-party components retain their own licenses.

This project would not be possible without the work of Microsoft, the .NET Foundation, and the many upstream authors and maintainers behind its dependencies. We are deeply grateful for their work. Full acknowledgements and license information are available in the application's Credits page and [Third-Party Notices](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices/README.md).

## Integrations and Source

Local APIs and the adapter SDK let you connect your own monitoring and management tools.

This project is maintained by the author. External contributions and pull requests are not accepted.
