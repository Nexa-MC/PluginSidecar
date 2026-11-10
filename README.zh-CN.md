# PluginSidecar

[English](README.md) | 简体中文

NexaCL 插件 Sidecar 开发仓库，面向衍生开发者和贡献者。

当前为初始化骨架：提供可构建、可 NativeAOT 发布的命令行入口，以及实验性的插件生命周期契约。尚未实现 NexaCL Host 握手、二进制分发、插件准入、打包和签名，不能作为启动器插件安装，也不是已发布 SDK。

需要 .NET 10 SDK。构建与检查：

```sh
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release
dotnet run --project src/PluginSidecar -- --version
```

`src/PluginSidecar` 为进程入口，`src/PluginSidecar.Contracts` 为实验性进程内契约，`tests` 为检查程序，`docs` 为架构和路线图。NativeAOT 需要目标系统的原生编译工具链。

参阅[贡献指南](CONTRIBUTING.zh-CN.md)和[架构说明](docs/architecture.md)。采用 Apache-2.0 许可证。
