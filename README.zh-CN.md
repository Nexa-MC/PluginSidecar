# PluginSidecar

[English](README.md) | 简体中文

面向衍生开发者和贡献者的 NexaCL 插件契约与 SDK。当前设计依据为
[插件 ABI v0.2](docs/specifications/plugin-abi-v0.2.proposal.zh-CN.md)。

## SDK 0.2.0-alpha.2

- `Nexa.Plugin.Bootstrap.V1`：统一 ABIContext、必需/可选能力协商、六阶段加载、激活/清理回调、结构化错误和 Host 提交接口。
- `Nexa.Plugin.Sdk.Abi100`：33 个能力分组、339 个新增类型化操作、236 个数据模型、有界编解码、不透明 ID/版本，并保留之前 22 个预览调用槽位。
- `Nexa.Plugin.MarketDelivery`：签名加密交付验证及离线缓存，独立包版本仍为 `0.1.0-alpha.1`。
- `Nexa.Plugin.Hosting`：能力授权、依赖组加载、定义原子发布和私有持久存储。
- `PluginSidecar.WasmRuntime` / `PluginSidecar.Packages`：无导入的 Wasm 执行、已认证包解析和声明式内容入口。
- `sdk/wasm`：完整 C/Wasm SDK：339 个新版操作及 22 个保留槽位的类型化调用、模型构造/读取、回调、schema 和操作元数据，参阅 [Wasm 开发说明](docs/wasm-sdk.md)。
- `PluginSidecar.RuntimeAdapter`：**仅用于可信开发测试**的 CoreCLR 内存加载器，不是 v0.2 要求的受控执行环境。

当前类型化目录属于 **SDK 草案接口**，不代表 339 项宿主能力已完成接入。
新增操作均标记为 `HostMappingPending`。宿主通信继续使用现有 NexaCL 二进制 Sidecar ABI；目录编号是 SDK 内部调用槽位，不是新传输消息编号。CLI 尚不是可安装的 `.nsc`，SDK 尚未上传 NuGet.org。

旧预览插件需重新编译：新增 `OnActivated`，协商支持必需/可选能力，部分新重载要求显式写出请求类型，不能继续使用简写 `new(...)`。ABI 代际仍为 100；Bootstrap 程序集标识保持 1.0.0.0，不代表旧预览二进制兼容。[加载与迁移说明](docs/loading-contract.md)。

## 构建与验证

需要 .NET 10 与 Python 3.12+：

```sh
python eng/generate_abi100.py --verify
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

修改 `contracts/abi100.json` 后运行生成器，不直接修改生成文件；CI 会检查生成结果一致性。将 `artifacts/packages` 加入本地 NuGet 源，即可引用 `0.2.0-alpha.2` SDK。

生命周期回调使用 `abi.ForStage100(stage)`；运行期使用 `abi.AsAbi100()`。
`api.TryGet<Network100>()` 返回能力或结构化错误；`IsAvailable` 提供便捷检查。
访问器不协商、不授权，旧代理每次调用仍需检查宿主许可。权限、并发版本、幂等、原子提交和引用归属均由 Host 裁决。

参阅[架构](docs/architecture.md)、[验收状态](docs/roadmap.md)、[Market 与离线](docs/market-offline.md)、[贡献指南](CONTRIBUTING.zh-CN.md)。Apache-2.0。

组合运行库已实现 13 项操作：6 项定义操作（注册、批量注册、读取、查询、按版本更新、移除）与 7 项私有存储操作。C 示例已通过真实编译和加载测试；CI 会在 Windows、Linux、macOS 发布并执行 NativeAOT 运行库测试。这些进展不代表桌面业务接入已完成：动态注册表、标签/覆盖、工作流、Provider、完整页面替换与启动器业务适配仍见[验收状态](docs/roadmap.md)。

SDK 版本为 `0.2.0-alpha.2`。`python eng/package_sdk.py` 生成独立 C/Wasm SDK ZIP；C# NuGet 包也包含契约 schema 和 C 头文件。跨语言测试覆盖全部 361 个调用和 290 类模型/集合，并检查回调生命周期、错误传播及非法编码。测试中的 Host 业务端口是明确的测试端口，SDK 完整不代表启动器所有业务功能已经接通。

完整测试需先运行 `python eng/build_wasm_sample.py --clang /path/to/clang`，再设置 `NEXA_WASM_SAMPLE`、`NEXA_WASM_CONFORMANCE`、`NEXA_WASM_CALLBACK`、`NEXA_WASM_CODEC` 指向 `artifacts/wasm-sample` 中对应的 `plugin.zip`、`conformance.wasm`、`callback.wasm`、`codec.wasm`。CI 自动构建并执行这些测试。
