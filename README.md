# Resource Manager / 资源管理器

Windows 资源监控与调度工具。

本项目由作者维护，不接受外部贡献或 Pull Request。

## 构建

需要 Windows x64、.NET 10 SDK、Node.js 22.18 或更新版本、Zig 0.16.0 和 MinGW-w64 GCC/G++。将编译器加入 PATH。

在 `Resource Manager/Resource Manager-APP/ClientApp` 运行 `npm ci`，然后在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "Resource Manager/publish-all-win-x64.ps1" -SkipStop
```

输出位于 `Resource Manager/Bin`。桌面界面需要 Microsoft Edge WebView2 Runtime。

## 许可与致谢

项目采用 [Apache-2.0](LICENSE)。第三方组件保留各自许可，完整致谢见应用设置和 [第三方声明](Resource%20Manager/Resource%20Manager-APP/ThirdPartyNotices/README.md)。感谢每一位上游作者和维护者。

---

Resource monitoring and scheduling for Windows.

This project is maintained by the author. External contributions and pull requests are not accepted.

Build on Windows x64 with .NET 10 SDK, Node.js 22.18 or newer, Zig 0.16.0 and MinGW-w64 GCC/G++ on PATH. Run `npm ci` in the ClientApp directory, then the command above. Build output is in `Resource Manager/Bin`. The desktop interface requires Microsoft Edge WebView2 Runtime.

Licensed under Apache-2.0. Third-party components retain their own licenses. Our sincere thanks to every upstream author and maintainer.
