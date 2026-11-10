# PluginSidecar

[English](README.md) | 简体中文

面向衍生开发者和贡献者的 NexaCL 插件加载契约及 SDK。

当前提供两个可打包的预览 SDK：`Nexa.Plugin.Bootstrap.V1` 与 `Nexa.Plugin.Sdk.Abi100`，版本为 `0.1.0-alpha.1`。包括六阶段定义、协商与生命周期入口、类型化句柄、错误码、Host 提交接口，以及事件/UI/插件间能力的全部 16 个方法封装。方法采用类型化输入和封闭 `AbiValue` 输出，具体负载映射仍需随 Host 适配器联调后冻结。

宿主通信以现有 NexaCL 二进制 Sidecar ABI 为准，不采用附件中的 JSON 封套。当前未实现 Host broker、传输接入、加密包授权、组调度及原子注册；不能作为可安装的 `.nsc` 使用，也未上传 NuGet.org。

`PluginSidecar.RuntimeAdapter` 使用 CoreCLR，从调用方已校验的内存负载加载托管插件，共享 Bootstrap 程序集并拒绝原生依赖。NativeAOT CLI 与此适配器分离。示例位于 `samples/HelloPlugin`。

```sh
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

将 `artifacts/packages` 加入本地 NuGet 源即可引用 SDK。权限、阶段、句柄归属和最终 Make/提交必须由 Host 裁决；SDK 不会自行宣布授权成功。内存加载不是安全沙箱，卸载也不能保证插件线程立即退出。

参阅[加载契约](docs/loading-contract.md)、[架构](docs/architecture.md)、[贡献指南](CONTRIBUTING.zh-CN.md)。采用 Apache-2.0。
