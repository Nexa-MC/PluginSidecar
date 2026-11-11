# PluginSidecar

English | [简体中文](README.zh-CN.md)

NexaCL plugin contracts and SDK for derivative developers and contributors.
The current design is [Plugin ABI v0.2](docs/specifications/plugin-abi-v0.2.proposal.zh-CN.md).

## SDK 0.2.0-alpha.2

- `Nexa.Plugin.Bootstrap.V1`: unified ABIContext, required/optional capability negotiation, six loading stages, activation/cleanup callbacks, errors and Host commit ports.
- `Nexa.Plugin.Sdk.Abi100`: 33 capability groups, 339 new typed operations, 236 data models, bounded codecs, opaque IDs/revisions, and the 22 previous preview dispatch slots.
- `Nexa.Plugin.MarketDelivery`: signed encrypted delivery verification and an offline cache; independent package version `0.1.0-alpha.1`.
- `Nexa.Plugin.Hosting`: capability authority, dependency-group loading, atomic definitions and durable private storage.
- `PluginSidecar.WasmRuntime` / `PluginSidecar.Packages`: import-free Wasm execution, authenticated package parsing and declarative content entries.
- `sdk/wasm`: complete C/Wasm bindings for 339 current and 22 legacy operations, typed models/builders/results, callbacks and schema/operation metadata; see [guest development](docs/wasm-sdk.md).
- `PluginSidecar.RuntimeAdapter`: **trusted development only** CoreCLR memory loading. This is not the controlled execution environment required by v0.2.

The typed catalog is a **draft SDK surface**, not 339 working Host integrations.
Every new operation records `HostMappingPending`. Host transport continues to use
the existing NexaCL binary Sidecar ABI; catalog numbers are local dispatch slots,
not new transport opcodes. The CLI is not yet an installable `.nsc`.
Packages are development artifacts, not published on NuGet.org.

Rebuild plugins from earlier previews: `OnActivated` is now explicit, negotiation
supports capability requirements, and new typed overloads sometimes require an
explicit request type instead of target-typed `new(...)`. ABI generation remains
100. Bootstrap assembly identity remains 1.0.0.0, which does not make old preview
binaries compatible. See [loading and migration](docs/loading-contract.md).

## Build and validate

Requires .NET 10 and Python 3.12+:

```sh
python eng/generate_abi100.py --verify
python eng/build_wasm_sample.py --clang /path/to/clang
dotnet build PluginSidecar.slnx -c Release
NEXA_WASM_SAMPLE=artifacts/wasm-sample/plugin.zip NEXA_WASM_CONFORMANCE=artifacts/wasm-sample/conformance.wasm NEXA_WASM_CALLBACK=artifacts/wasm-sample/callback.wasm NEXA_WASM_CODEC=artifacts/wasm-sample/codec.wasm dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

Run `python eng/package_sdk.py` for the standalone C/Wasm SDK ZIP. The managed SDK
NuGet package also includes its schemas and C headers. Cross-language tests cover
all 361 calls and 290 model/collection shapes, including callbacks and malformed
wire values. These tests exercise SDK semantics against explicit test Host ports.

Use `artifacts/packages` as a local NuGet source and reference
`Nexa.Plugin.Sdk.Abi100` version `0.2.0-alpha.2`. Edit
[contracts/abi100.json](contracts/abi100.json), then run the generator without
`--verify`; do not edit generated sources. CI checks reproducibility on all three
supported operating systems. The managed sample demonstrates Get/Make negotiation and
separate stage/activation contexts. The C sample compiles to an import-free Wasm
module and runs through the actual package loader and Host kernel. CI publishes
and executes the runtime tests using NativeAOT on Windows, Linux and macOS.

Use `abi.ForStage100(stage)` inside lifecycle callbacks and `abi.AsAbi100()` in
Active. `api.TryGet<Network100>()` preserves capability errors;
`api.IsAvailable<Network100>()` is a convenience check. Access never negotiates.
Saved proxies still recheck Host admission. Host remains authoritative for
permissions, revisions, idempotency, staged publication and scoped references.

See [architecture](docs/architecture.md), [acceptance status](docs/roadmap.md),
[Market/offline](docs/market-offline.md), [contributing](CONTRIBUTING.md), and
[security](SECURITY.md). Apache-2.0.

The composed kernel has 13 implemented operations: six definition operations
(Register/RegisterBatch/Get/Query/UpdateOwned/RemoveOwned) and seven private
storage operations. These are not Desktop business adapters. Dynamic registries,
tags/overlays, workflows, providers, UI replacement and launcher business bindings
still have acceptance work tracked in [the status table](docs/roadmap.md).
