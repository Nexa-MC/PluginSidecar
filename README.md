# PluginSidecar

English | [简体中文](README.zh-CN.md)

NexaCL plugin Sidecar loading contracts and SDK, for derivative developers and contributors.

## SDK 0.1.0-alpha.2

- `Nexa.Plugin.Bootstrap.V1`: seven entry callbacks (five plugin load callbacks, Abort, Stop), six load-stage identifiers including loader-owned Checksum, a unified ABIContext facade, per-callback StageContext, negotiation, opaque handles, closed values, manifests and Host commit ports. Bootstrap assembly identity is fixed at 1.0.0.0.
- `Nexa.Plugin.Sdk.Abi100`: 22 callable Event/UI/InterPlugin/Diagnostic/Resource method slots with typed inputs and bounded-data-compatible `AbiValue` outputs. Logical payload field mappings are documented by the wrapper source and remain preview until validated against a Host adapter.
- `PluginSidecar.RuntimeAdapter`: CoreCLR-only memory loading from preverified payloads with shared Bootstrap identity and native dependency rejection.
- `samples/HelloPlugin`: explicit ABI 100 Get/Make negotiation, with Host failures propagated.

Host communication follows the **existing NexaCL binary Sidecar ABI**. The attached proposal's JSON framing is not adopted. This repository does not yet implement the Host broker, authenticated transport mapping, live Market/Host authorization integration, group scheduler or atomic registry. The SDK packages are preview development artifacts, not published to NuGet.org; the CLI is not an installable `.nsc`.

The 2026-11-11 revision changes the unpublished preview signatures: **rebuild
alpha.1 plugins**. Initialize, PostInitialize, Checkup and Finalize take the same
final `ABIContext` plus separate `StageContext`. Use `abi.ForStage100(stage)` for
scoped SDK access, or `abi.AsAbi100()` for runtime views. Capabilities are grouped as
`Events`, `UI`, `Plugins`, `Diagnostics`, `Resources`. Service export moved to
`Plugins.ExportService` (method 6); event slot 3 is reserved. See the
[loading contract](docs/loading-contract.md) for scope, experiments and migration.

## Build, test and package

.NET 10 SDK is required:

```sh
dotnet build PluginSidecar.slnx -c Release
dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

Use `artifacts/packages` as a local NuGet source and reference `Nexa.Plugin.Sdk.Abi100` version `0.1.0-alpha.2` (Bootstrap is a package dependency). Implement `IPluginBootstrapV1` as shown in the sample. SDK methods forward asynchronously to the supplied capability facade; permissions, stage checks, session ownership and commit decisions remain authoritative in Host. Do not fabricate a local successful Make result.

Managed plugins use CoreCLR, not NativeAOT. The independent CLI remains AOT-publishable; the runtime adapter is not referenced by it. `LoadVerifiedPayloads` assumes caller-verified identity, authorization and digest, takes assembly simple names mapped to copied bytes, and loads no plugin files from disk. Dispose requests collectible-context unloading; it cannot stop plugin threads or guarantee immediate reclamation. Process isolation is not a security sandbox.

See [architecture](docs/architecture.md), [loading contract](docs/loading-contract.md), [roadmap](docs/roadmap.md), [contributing](CONTRIBUTING.md), and [security](SECURITY.md). Apache-2.0.

## Market / offline component

`Nexa.Plugin.MarketDelivery` supplies signed encrypted delivery verification and an atomic offline cache. See [integration status and offline policy](docs/market-offline.md). Live Market/Host routes remain unimplemented.
