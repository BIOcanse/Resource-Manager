<div align="center">

# 🖥️ Resource Manager

**See every resource in your PC — and decide who actually gets it.**

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D6.svg)](#-download)
[![Release](https://img.shields.io/badge/download-latest%20release-brightgreen.svg)](https://github.com/BIOcanse/Resource-Manager/releases/latest)

**English** · [简体中文](README.zh-CN.md)

</div>

---

## 📖 What it is

Just as the name suggests, **Resource Manager** is a tool that tries to monitor and manage every resource on your computer: CPU compute, GPU compute, RAM, VRAM, and plenty of other resources you may or may not have thought about.

Start using Resource Manager and take real control of your PC! 🚀

> ⚠️ Hardware readings depend on the drivers and providers available on your system. GPU placement depends on application and driver support — not every running application can switch GPUs.

> 🌐 The interface language follows your system by default and can be set explicitly in Settings → Appearance → Interface language. The screenshots below are from the running application in English; machine model, peripherals and personal paths are covered with solid bars.

---

## 📊 Monitoring Console

The console helps you monitor a wide range of system data. As shown below, all the commonly used information is here, and you can freely adjust both the layout and which data is monitored.

![Monitoring console](docs/screenshots/en/01-dashboard.png)

### 📈 Bar-chart overview

The lower half of the screenshot above uses bar charts so you can judge the state of your machine at a glance. Beyond the usual CPU, GPU, memory and VRAM views, you can also watch **virtual memory**. There are more monitoring items available here than the screenshot shows, and you can configure them freely.

### 🧮 Task-Manager-style view

This page is similar to the Windows Task Manager, except that you can inspect **many more monitoring items**. The layout and content follow Task Manager as closely as possible, and you can look at both application and process information — with lower overhead and smoother interaction, even though it uses WebView rather than WinUI 😏.

It also fixes Task Manager's **double counting of shared memory**: the yellow part of each bar is the application's estimated share of shared memory. This is only meant to help you roughly analyze each application — nobody can divide shared memory between applications with perfect accuracy.

![Resource list](docs/screenshots/en/02-resource-list.png)

---

## 🧩 Components and Software

This page lists most of the software on your computer. If an application was not installed in the traditional way, you can add it manually by specifying its main executable and root directory so Resource Manager can bring it under management.

![Components and software](docs/screenshots/en/03-software.png)

From here you can quickly:

- 📍 See where an application lives
- 📦 Migrate supported application data in one click (move it off `C:` to the drive the application is on, easing pressure on the system drive)
- ⚙️ Configure optimization settings

![Per-application scheduling policy](docs/screenshots/en/09-app-policy.png)

Every application can carry its own scheduling policy: its base score, how far the manager is allowed to adjust it, how the GPU is chosen at launch and at runtime, and whether an application's own GPU choice should be respected.

![Migration workbench](docs/screenshots/en/10-migration.png)

---

## ⚡ Performance Optimization

Three buttons switch between the automatic optimization modes: **Normal**, **Memory only**, and **Smart optimization**.

![Performance optimization](docs/screenshots/en/04-optimization.png)

- **🧠 Memory only** — performs memory optimization and management only.
- **⚡ Smart optimization** — manages CPU, GPU, memory and VRAM together.

**VRAM and memory pressure.** When you launch a game or a professional application that eats GPU resources, have you ever run into VRAM or memory pressure? Smart scheduling detects that pressure and automatically moves unimportant, low-load applications from the discrete GPU to the integrated GPU so they stop occupying VRAM — Chrome, Edge, or the NVIDIA App, for instance (I have no idea why that one still holds VRAM while sitting in the tray 🤷).

**Multiple GPUs.** If your computer has several GPUs, Resource Manager can lock an application to a specific GPU or balance load between them.

**Memory.** Smart scheduling also keeps optimizing memory continuously.

**CPU.** Cores are assigned intelligently: important applications get the less-contended or higher-performance cores first, and you can trigger **exclusive core allocation** and **core locking** to help hold a stable high frame rate.

> 💡 Worth noting: although Resource Manager uses highly optimized algorithms, scheduling still costs something. On CPUs with very weak multicore performance there may be no obvious CPU-side benefit — on a very early i3 or Ryzen 5, for example.

> 🧪 GPU placement uses **GPU shim** technology, a fairly stable and broadly compatible virtualization-based GPU-switching method — but it is not guaranteed to work in every case:
> - It is not applied to games or professional applications by default.
> - If another application has compatibility problems, turn scheduling off for that application.
> - On a small number of motherboards and laptops, discrete-GPU-direct mode may disable the integrated GPU. Without another available GPU in that situation, GPU scheduling is unavailable.

### 📑 Optimization reports

Monitoring history is used to identify abnormal software, or software that is quietly stealing your PC's performance 🕵️:

- 💽 Abnormal disk writes
- 🌐 Unusually frequent network communication
- 📡 Excessive network traffic
- 🧠 Excessive memory usage
- 🔥 Excessive CPU usage
- ⚡ Frequent or long system interrupts
- …and other abnormal resource behavior

This helps you catch **memory leaks**, **misbehaving software**, **disk killers**, **problematic drivers**, and software that keeps talking to the network in the background.

> 📌 Abnormal network behavior points you at software worth investigating, but Resource Manager does not automatically decide that such communication is telemetry, nor infer what the transmitted data is for.

---

## 🔌 Device Topology

Resource Manager displays every interface on the device — internal and external — along with its information and the devices connected to it.

![Device topology](docs/screenshots/en/05-device-topology.png)

You can quickly check the **protocol that was actually negotiated**, which in many cases is the real limit on transfer speed.

---

## 🔬 Detailed Information

Detailed CPU and GPU monitoring, instead of a single utilization percentage that never tells you where the real bottleneck is.

**CPU topology**: per-core load laid out by CCD, cache level and physical core, with the processes using each logical processor.

![CPU topology](docs/screenshots/en/06-cpu-topology.png)

**GPU scheduling**: performance score, utilization, VRAM and clocks for each GPU, plus specialized-unit occupancy beyond rasterization (NVIDIA RT / CUDA / Tensor, AMD GCN / RDNA / CDNA — where the GPU and driver expose that counter).

![GPU scheduling](docs/screenshots/en/07-gpu-scheduling.png)

---

## ⚙️ Settings

Configure the scheduling policies you want.

![Settings](docs/screenshots/en/08-settings.png)

---

## 🧑‍💻 Developer API

Efficient resource management takes cooperation 🤝.

Local APIs and the adapter SDK let you connect your own monitoring and management tools. The developer API documentation is still being prepared.

---

## ⬇️ Download

Get the Windows x64 beta from GitHub Releases:

**[👉 Download the latest release](https://github.com/BIOcanse/Resource-Manager/releases/latest)**

### Requirements

- Windows 11 x64
- Microsoft Edge WebView2 Runtime

The release package includes the required .NET runtime. It uses your system's WebView2 runtime; if WebView2 is missing, the official Microsoft runtime is downloaded and installed automatically on first startup — an internet connection is only needed for that step.

---

## 📦 Installation

1. Download the latest Windows x64 release archive.
2. Extract the **complete folder** to a permanent location.
3. Run `Install.cmd` to register that directory.
4. Run `Start.cmd` to start the service and the desktop interface.
5. Run `Restart.cmd` to restart the service when needed.

Service management requests administrator permission; the interface runs as a standard user.

When updating, preserve:

```text
Config/
UserData/
```

---

## 📄 License and Acknowledgements

Resource Manager is licensed under the **[Apache License 2.0](LICENSE)**. Third-party components retain their own licenses.

This project would not be possible without the work of Microsoft, the .NET Foundation, and the many upstream authors and maintainers behind its dependencies. We are deeply grateful for their work 🙏. Full acknowledgements and license information are available in the application's Credits page and in the [Third-Party Notices](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices).

---

## 🗂️ Repository

This project is maintained by the author. **External contributions and pull requests are not accepted.**

https://github.com/BIOcanse/Resource-Manager
