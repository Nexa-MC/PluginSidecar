# PluginSidecar

[English](README.md) | 简体中文

面向衍生开发者和贡献者的 NexaCL 插件加载契约及 SDK。

当前提供两个可打包的预览 SDK：`Nexa.Plugin.Bootstrap.V1` 与 `Nexa.Plugin.Sdk.Abi100`，版本为 `0.1.0-alpha.2`。包括六阶段定义、协商与生命周期入口、类型化句柄、错误码、Host 提交接口，以及事件/UI/插件间/诊断/资源能力的 22 个可调用方法封装。方法采用类型化输入和封闭 `AbiValue` 输出，具体负载映射仍需随 Host 适配器联调后冻结。

宿主通信以现有 NexaCL 二进制 Sidecar ABI 为准，不采用附件中的 JSON 封套。当前未实现 Host broker、传输接入、实际 Market/Host 授权接线、组调度及原子注册；不能作为可安装的 `.nsc` 使用，也未上传 NuGet.org。

2026-11-11 修订改变了未正式发布的预览版签名，**alpha.1 插件需要重新编译**。Initialize、PostInitialize、Checkup、Finalize 接收同一个最终 `ABIContext` 和各自的 `StageContext`。使用 `abi.ForStage100(stage)` 获取阶段 SDK 视图，使用 `abi.AsAbi100()` 获取运行期视图；能力分为 `Events`、`UI`、`Plugins`、`Diagnostics`、`Resources`。服务导出移到 `Plugins.ExportService`（方法 6），事件槽位 3 保留不可调用。实验访问器只读取已确认能力，不会再次协商或授权。

阶段视图传递调用标识、取消信号和剩余预算；释放后，其旧代理拒绝调用。Host 仍必须检查实际阶段、已失效标识、权限和实验集合，不能仅凭 SDK 检查授权。[迁移与完整边界说明](docs/loading-contract.md)。

`PluginSidecar.RuntimeAdapter` 使用 CoreCLR，从调用方已校验的内存负载加载托管插件，共享 Bootstrap 程序集并拒绝原生依赖。NativeAOT CLI 与此适配器分离。示例位于 `samples/HelloPlugin`。

```sh
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

将 `artifacts/packages` 加入本地 NuGet 源即可引用 SDK。权限、阶段、句柄归属和最终 Make/提交必须由 Host 裁决；SDK 不会自行宣布授权成功。内存加载不是安全沙箱，卸载也不能保证插件线程立即退出。

参阅[加载契约](docs/loading-contract.md)、[架构](docs/architecture.md)、[贡献指南](CONTRIBUTING.zh-CN.md)。采用 Apache-2.0。

## Market 与离线

新增 `Nexa.Plugin.MarketDelivery`，提供签名加密交付验证与原子离线缓存。[集成状态与离线策略](docs/market-offline.md)。实际 Market/Host 路由尚未接通。
