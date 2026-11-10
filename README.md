# PluginSidecar

English | [简体中文](README.zh-CN.md)

Development repository for the NexaCL plugin Sidecar. This repository is for derivative developers and contributors.

Status: initial scaffold. The CLI builds and publishes with NativeAOT. The experimental plugin lifecycle is available as source. NexaCL Host handshake, binary dispatch, plugin admission, packaging and signing are not implemented. This is not an installable launcher plugin or a released SDK.

## Build

Install .NET 10 SDK, then run:

```sh
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release
dotnet run --project src/PluginSidecar -- --version
dotnet publish src/PluginSidecar -c Release -r linux-x64 -p:PublishAot=true -o artifacts/linux-x64
```

NativeAOT requires the native compiler toolchain for the target OS. CI publishes Windows, Linux and macOS smoke builds. Cross-architecture distribution is future work.

## Layout

- `src/PluginSidecar`: process entry point.
- `src/PluginSidecar.Contracts`: experimental in-process plugin lifecycle; not an IPC protocol.
- `tests`: dependency-free contract smoke checks.
- `docs`: architecture and development roadmap.

Read [CONTRIBUTING.md](CONTRIBUTING.md), [architecture](docs/architecture.md), [roadmap](docs/roadmap.md) and [security policy](SECURITY.md).

Licensed under Apache-2.0. No NexaCL implementation is vendored here.
