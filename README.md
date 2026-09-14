# Resource Manager

See where your PC's resources go, and decide which applications get priority.

Resource Manager brings application-level monitoring and resource scheduling together on Windows. Find demanding applications, manage competing workloads, and choose how your software shares CPU, memory, and GPUs.

## Features

- **CPU scheduling that puts important applications first.** Give games and demanding software priority while keeping background applications under control. Automatic scheduling manages CPU priorities and core placement; per-application settings and exclusive allocation let you decide how CPU resources are shared.
- **Memory optimization for competing workloads.** Reclaim resident memory from background applications and adjust their memory treatment, leaving more RAM available for the software you are actively using. Use automatic management or choose a memory policy for each application.
- **GPU scheduling that frees up dedicated GPU memory.** Direct compatible background applications to the integrated GPU instead of letting them occupy the dedicated GPU. This leaves more VRAM available for games and graphics-heavy workloads, with per-application GPU preferences when you want direct control.
- **Reports that help you find abnormal applications.** Review reported application behavior, resource-use evidence, and the actions taken by the manager. Investigate software that is consuming resources unexpectedly rather than trying to catch it in a live process list.
- **Fast, detailed resource monitoring.** Go beyond Task Manager's standard views with application-level summaries, process-level detail, and customizable CPU, RAM, GPU, GPU memory, disk, and network displays. A responsive panel designed for low overhead makes it practical to keep an eye on your system while you work or play.

Built-in software recognition reduces manual setup. Tray operation and optional startup at sign-in keep monitoring and scheduling available without leaving a window open.

Hardware readings depend on available drivers and providers. GPU placement depends on application and driver support; not every running application can switch GPUs.

## Download

Get the Windows x64 beta from the [latest release](https://github.com/BIOcanse/Resource-Manager/releases/latest).

The Windows 11 x64 package includes the .NET runtime. It uses your system's WebView2 runtime; if WebView2 is missing, it automatically downloads and installs the official Microsoft runtime on first startup. An internet connection is needed only for this setup. Preserve your Config and UserData folders when updating.

Extract the complete release folder to a permanent location. Run `Install.cmd` to register that directory, then `Start.cmd` to start the service and desktop interface. `Restart.cmd` restarts the service. Service management requests administrator permission; the interface runs as a standard user.

## License and Acknowledgements

Resource Manager is licensed under [Apache-2.0](LICENSE). Third-party components retain their own licenses.

This project would not be possible without the work of Microsoft, the .NET Foundation, and the many upstream authors and maintainers behind its dependencies. We are deeply grateful for their work. Full acknowledgements and license information are available in the application's Credits page and [Third-Party Notices](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices/README.md).

## Integrations and Source

Local APIs and the adapter SDK let you connect your own monitoring and management tools.

This project is maintained by the author. External contributions and pull requests are not accepted.
