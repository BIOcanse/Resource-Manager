# Resource Manager 使用的 FreedomPoint 包与许可材料

## 适用包

本目录的许可附件仅对应下列四个原始包。`materials.json` 记录包长度、SHA-256、原 NuSpec、依赖和第三方 DLL 摘要；接入仓库内 feed 时逐项核对，不重新打包。

| 版本 | 包 | 实际用途 |
|---|---|---|
| 0.1.0-dev.41 | FreedomPoint.Core | Resource Manager 产品依赖 |
| 0.1.0-dev.41 | FreedomPoint.SourceTracking | `src/Development/FreedomPointCheck` 开发期检查 |
| 0.1.0-dev.39 | FreedomPoint.Core | Ledger dev.2 构建时依赖 |
| 0.1.0-dev.39 | FreedomPoint.Generators | Ledger dev.2 构建时生成器；依赖同版 Core |

已核对 SourceTracking dev.41 的真实 NuSpec 和程序集引用：它依赖同版 Core，**不依赖 Compilation 或 Generators dev.41**，因此这两包不属于本次接入范围。SourceTracking 自带两份 Roslyn DLL，需连同第三方文本保留；该检查工具的编译器库不因此进入 Resource Manager 产品运行依赖。

包进入仓库 feed 及构建路径调整由 RM 主线程负责；此许可材料交付不表示全新克隆构建已经验证。包字节、版本与现用 pin 不变，提交由仓库维护者处理。

## 第一方许可

权利人 BIOcanse 于 2026-10-21 确认 FreedomPoint 第一方代码采用 **Apache-2.0**。本目录 `LICENSE` 是完整文本，`NOTICE` 是归属告示，`authorization.json` 将授权绑定到上述四个包。分发原包时同时提供这些附件。

旧包 NuSpec 没有许可字段这一事实保持；补充附件不改写旧包、不重发版本。Apache-2.0 只适用于 FreedomPoint 第一方内容，不将 Roslyn 或其他第三方内容重新许可。

## 第三方文本

- `third-party/dotnet/LICENSE.txt`、`ThirdPartyNotices.txt`：与原 DLL 来源 Windows .NET SDK 10.0.300 安装对应的原始文本，保持字节不变。
- `third-party/roslyn-vmr/LICENSE.txt`、`THIRD-PARTY-NOTICES.txt`：锁定 VMR 源码版本的原始告示，单独保留，**不替换 Windows 二进制来源条款**。
- `third-party/provenance.json`：两份包内 DLL 的实际摘要、版本和固定 VMR/上游映射及文本来源。

Roslyn ProductVersion 为 `5.6.0-2.26230.102+caa81fa4971f74880cdab61990cb1b11420939ec`；提交属于 dotnet/dotnet VMR，映射到 dotnet/roslyn 的 `538ae5e66f82b114e58a5deb6472649e687143db`。完整对应源码归档或同字节重建未在此交付中证明。本目录提供实际来源文本和核对材料，不把第一方许可确认外推为所有混合发行条件的新增裁定。

## 开发期检查

`scripts/validation/Test-FreedomPointSources.ps1` 从真实托管消费端取得编译上下文，导出视图和源码绑定；缺少生产消费或生成位置失效应返回错误。自定义分组保存在 `backend.groups.json`，SDK 身份、绑定和分组引用使用 PointPath。既有编译器地址字符串在原消费方替换前继续作为宿主别名。
