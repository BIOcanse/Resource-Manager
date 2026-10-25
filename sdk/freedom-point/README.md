# Resource Manager 使用的 FreedomPoint 包与许可材料

## 适用包

本目录仅保留当前实际使用的正式包，原始字节不重打包。`materials.json` 记录长度、SHA-256、原 NuSpec 摘要、依赖及交付回执；当前 RM pin 为 dev.45。

| 版本 | 包 | 实际用途 |
|---|---|---|
| 0.1.0-dev.45 | FreedomPoint.Core | 后端与 native 前端产品依赖 |
| 0.1.0-dev.45 | FreedomPoint.SourceTracking | 后端、前端开发期源码检查 |
| 0.1.0-dev.45 | FreedomPoint.Generators | 前端 TS 与 C# 正式投影 |
| 0.1.0-dev.39 | FreedomPoint.Core | Ledger dev.2 构建时依赖 |
| 0.1.0-dev.39 | FreedomPoint.Generators | Ledger dev.2 构建时生成器；依赖同版 Core |

RM 构建和测试直接调用已引用包的 SDK API，不运行 FreedomPoint.BuildCheckTool，也不携带其工具闭包或未消费的 Compilation 包。SourceTracking 自带的 Roslyn 用于源码检查，不进入产品运行图。Ledger 自身的 dev.39 pin 不变。

后端适配器独立 `Declare`，前端构建工具调用 `CreateExtensionLayout` / `Extend`，不插入后端先序遍历。后端源码检查不携带前端声明。前端的唯一默认值来源是 `src/UI/Frontend/preferences.points.json`；`scripts/build/Generate-FrontendPreferences.ps1` 用 SDK 正式投影生成 TS/native C#。构建前设置 `RM_LEDGER_WORK_ROOT` 为经维护入口登记且仍 running 的工作目录；native 构建使用同一 `ResourceManagerLedgerWorkRoot`。值文件不入库；structure 文件保留稳定身份。

## 第一方许可

权利人 BIOcanse 确认 FreedomPoint 第一方代码采用 **Apache-2.0**。dev.45 的真实 NuSpec 也声明 Apache-2.0 与 BIOcanse；本目录 `LICENSE`、`NOTICE` 和包内原始许可文本一并保留。`authorization.json` 绑定实际包摘要。

旧包 NuSpec 没有许可字段这一事实保持；补充附件不改写旧包、不重发版本。Apache-2.0 只适用于 FreedomPoint 第一方内容，不将 Roslyn 或其他第三方内容重新许可。

## 第三方文本

- `third-party/dotnet/LICENSE.txt`、`ThirdPartyNotices.txt`：与原 DLL 来源 Windows .NET SDK 10.0.300 安装对应的原始文本，保持字节不变。
- `third-party/roslyn-vmr/LICENSE.txt`、`THIRD-PARTY-NOTICES.txt`：锁定 VMR 源码版本的原始告示，单独保留，**不替换 Windows 二进制来源条款**。
- `third-party/provenance.json`：两份包内 DLL 的实际摘要、版本和固定 VMR/上游映射及文本来源。

Roslyn ProductVersion 为 `5.6.0-2.26230.102+caa81fa4971f74880cdab61990cb1b11420939ec`；提交属于 dotnet/dotnet VMR，映射到 dotnet/roslyn 的 `538ae5e66f82b114e58a5deb6472649e687143db`。完整对应源码归档或同字节重建未在此交付中证明。本目录提供实际来源文本和核对材料，不把第一方许可确认外推为所有混合发行条件的新增裁定。

## 开发期检查

`scripts/validation/Test-FreedomPointSources.ps1` 从真实托管消费端取得编译上下文，导出视图和源码绑定；缺少生产消费或生成位置失效应返回错误。自定义分组保存在 `backend.groups.json`，SDK 身份、绑定和分组引用使用 PointPath。既有编译器地址字符串在原消费方替换前继续作为宿主别名。

扩展检查入口为 `scripts/validation/Test-FrontendPreferenceSources.ps1 -WorkRoot <已登记运行目录> -LedgerWorkRoot <账本工作根>`。它先生成，再编译实际 native-shell，捕获实际 TypeScript 程序（含 node_modules junction 的物理依赖），调用 SDK 的源码分析与合同检查，要求 26 个自有点均有真实读取且零问题。可用 `-BuildProperties` 明确指定现有 NuGet 缓存；离线不另造依赖声明。
