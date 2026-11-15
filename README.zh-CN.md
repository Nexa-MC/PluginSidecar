# PluginSidecar

[English](README.md) | 简体中文

面向运行时开发者和贡献者的独立 NexaCL 插件运行时。插件作者使用
[公共 SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK)。当前设计依据为
[插件 ABI v0.2](docs/specifications/plugin-abi-v0.2.proposal.zh-CN.md)。

## SDK 0.2.0-alpha.13

- `Nexa.Plugin.Bootstrap.V1`：统一 ABIContext、必需/可选能力协商、六阶段加载、激活/清理回调、结构化错误和 Host 提交接口。
- `Nexa.Plugin.Sdk.Abi100`：33 个能力分组、358 个新增类型化操作、261 个数据模型、有界编解码、不透明 ID/版本，并保留之前 22 个预览调用槽位。
- `Nexa.Plugin.MarketDelivery`：签名加密交付验证及离线缓存，独立包版本仍为 `0.1.0-alpha.1`。
- `Nexa.Plugin.Hosting`：能力授权、依赖组加载、定义原子发布和私有持久存储。
- `PluginSidecar.WasmRuntime` / `PluginSidecar.Packages`：无导入的 Wasm 执行、已认证包解析和声明式内容入口。
- `sdk/wasm`：完整 C/Wasm SDK：358 个新版操作及 22 个保留槽位的类型化调用、模型构造/读取、回调、schema 和操作元数据，参阅 [Wasm 开发说明](docs/wasm-sdk.md)。
- `PluginSidecar.RuntimeAdapter`：**仅用于可信开发测试**的 CoreCLR 内存加载器，不是 v0.2 要求的受控执行环境。

本仓库保存运行库及其使用的契约副本。插件作者应使用独立的
[NexaCL Plugin SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK)；其中
`contracts/host-support.json` 逐项记录已验证的宿主提交。宿主通信继续使用现有
NexaCL 二进制 Sidecar ABI，目录编号不是新的传输消息编号。SDK 开发包尚未上传 NuGet.org。

旧预览插件需重新编译：新增 `OnActivated`，协商支持必需/可选能力，部分新重载要求显式写出请求类型，不能继续使用简写 `new(...)`。ABI 代际仍为 100；Bootstrap 程序集标识保持 1.0.0.0，不代表旧预览二进制兼容。[加载与迁移说明](docs/loading-contract.md)。

运行时行为、恢复规则与剩余限制见 [运行时说明](docs/runtime-services.md) 和 [验收进度](docs/roadmap.md)。

生产源码入口现已选择独立运行时，负责准入、业务适配、离线管理和插件界面。
它使用锁定版本的通用宿主 ABI，包括继承宿主网络配置的 HTTP 通道，不加载宿主服务
或 UI 程序集。对应宿主改动移除旧内嵌插件所有者和固定插件导航。
启动顺序、清理规则、真实进程验证及部署限制见 [Worker 组合说明](docs/worker-composition.md)。
Market 获取链路和签名发布验证仍是 SDK Beta 的发布前置条件。

## 构建与验证

需要 .NET 10、Python 3.12+，以及支持 Wasm 的 Clang/LLD。以下使用 POSIX shell 语法：

```sh
python eng/generate_abi100.py --verify
python eng/build_wasm_sample.py --clang /path/to/clang
python eng/prepare_host_reference.py
dotnet build PluginSidecar.slnx -c Release
NEXA_PLUGIN_WORKER=src/PluginSidecar/bin/Release/net10.0/PluginSidecar.dll NEXA_WASM_SAMPLE=artifacts/wasm-sample/plugin.zip NEXA_WASM_CONFORMANCE=artifacts/wasm-sample/conformance.wasm NEXA_WASM_CALLBACK=artifacts/wasm-sample/callback.wasm NEXA_WASM_CODEC=artifacts/wasm-sample/codec.wasm dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

修改 `contracts/abi100.json` 后运行生成器，不直接修改生成文件；CI 会检查生成结果一致性。将 `artifacts/packages` 加入本地 NuGet 源，即可引用 `0.2.0-alpha.13` SDK。

生命周期回调使用 `abi.ForStage100(stage)`；运行期使用 `abi.AsAbi100()`。
`api.TryGet<Network100>()` 返回能力或结构化错误；`IsAvailable` 提供便捷检查。
访问器不协商、不授权，旧代理每次调用仍需检查宿主许可。权限、并发版本、幂等、原子提交和引用归属均由 Host 裁决。

参阅[架构](docs/architecture.md)、[验收状态](docs/roadmap.md)、[Market 与离线](docs/market-offline.md)、[贡献指南](CONTRIBUTING.zh-CN.md)。Apache-2.0。

组合运行库现已实现 13 个分组的 98 项操作，包含内容注册表、标签、覆盖解析与订阅、状态/CAS、持久事件回调、公开服务、动作确认、条件、任务，以及支持原子检查点、显式重启恢复、调度、并发策略与等待节点的工作流。服务支持 Checkup 只读自检和自身加载报告。C 示例和跨语言调用经过真实编译及加载验证；新增工作流示例串起连续注册、动作回调、定时触发和停用清理，CI 在 Windows、Linux、macOS 执行 NativeAOT 测试。Desktop 的业务适配和 UI 消费者已经接入；生产准入及发行要求见[验收状态](docs/roadmap.md)。

运行 `dotnet pack PluginSidecar.slnx -c Release -o artifacts/packages` 和
`python eng/package_sdk.py` 后，可通过 `python eng/verify_packages.py` 验证
独立工程仅引用 NuGet 包即可加载实际编译的 Wasm 示例。验证使用隔离包缓存，
不会借用仓库内项目引用。`python eng/bundle_sdk.py` 将 NuGet 包、C SDK、文档与
SHA-256 清单打入 `artifacts/packages`；三平台 CI 会执行验证并上传开发包。

CI 同时打包 Hosting、WasmRuntime、Packages 和仅用于可信开发的 RuntimeAdapter，并包含版本化依赖；宿主开发者无需直接引用本仓库源码项目。

SDK 版本为 `0.2.0-alpha.13`。`python eng/package_sdk.py` 生成独立 C/Wasm SDK ZIP；C# NuGet 包也包含契约 schema 和 C 头文件。跨语言测试覆盖全部 380 个调用和 300 类模型/集合，并检查回调生命周期、错误传播及非法编码。测试中的 Host 业务端口是明确的测试端口，SDK 完整不代表启动器所有业务功能已经接通。

完整测试需先运行 `python eng/build_wasm_sample.py --clang /path/to/clang`，再设置 `NEXA_WASM_SAMPLE`、`NEXA_WASM_CONFORMANCE`、`NEXA_WASM_CALLBACK`、`NEXA_WASM_CODEC` 指向 `artifacts/wasm-sample` 中对应的 `plugin.zip`、`conformance.wasm`、`callback.wasm`、`codec.wasm`。CI 自动构建并执行这些测试。

认证独立工作进程、包内 UI 文件引用及 Desktop 示例见 [Desktop 接入说明](docs/desktop-integration.md)。

[Business provider callback contract / 业务 Provider 回调契约](docs/business-providers.md)
