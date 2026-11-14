# PluginSidecar

English | [简体中文](README.zh-CN.md)

NexaCL plugin contracts and SDK for derivative developers and contributors.
The current design is [Plugin ABI v0.2](docs/specifications/plugin-abi-v0.2.proposal.zh-CN.md).

## SDK 0.2.0-alpha.13

- `Nexa.Plugin.Bootstrap.V1`: unified ABIContext, required/optional capability negotiation, six loading stages, activation/cleanup callbacks, errors and Host commit ports.
- `Nexa.Plugin.Sdk.Abi100`: 33 capability groups, 358 new typed operations, 261 data models, bounded codecs, opaque IDs/revisions, and the 22 previous preview dispatch slots.
- `Nexa.Plugin.MarketDelivery`: signed encrypted delivery verification and an offline cache; independent package version `0.1.0-alpha.1`.
- `Nexa.Plugin.Hosting`: capability authority, dependency-group loading, atomic definitions and durable private storage, runtime state/events/services and durable workflow checkpoints.
- `PluginSidecar.WasmRuntime` / `PluginSidecar.Packages`: import-free Wasm execution, authenticated package parsing and declarative content entries.
- `sdk/wasm`: complete C/Wasm bindings for 358 current and 22 legacy operations, typed models/builders/results, callbacks and schema/operation metadata; see [guest development](docs/wasm-sdk.md).
- `PluginSidecar.RuntimeAdapter`: **trusted development only** CoreCLR memory loading. This is not the controlled execution environment required by v0.2.

The typed catalog is shared with the public [NexaCL Plugin SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK).
The public `contracts/host-support.json` pins the verified Desktop implementation.
Host transport continues to use
the existing NexaCL binary Sidecar ABI; catalog numbers are local dispatch slots,
not new transport opcodes. The CLI is not yet an installable `.nsc`.
Packages are development artifacts, not published on NuGet.org.

Rebuild plugins from earlier previews: `OnActivated` is now explicit, negotiation
supports capability requirements, and new typed overloads sometimes require an
explicit request type instead of target-typed `new(...)`. ABI generation remains
100. Bootstrap assembly identity remains 1.0.0.0, which does not make old preview
binaries compatible. See [loading and migration](docs/loading-contract.md).

Runtime behavior and remaining limits are documented in [runtime services](docs/runtime-services.md) and [acceptance status](docs/roadmap.md).

The standalone migration now has Sidecar-owned application, business and guest
presentation modules. [Guest presentation ownership](docs/presentation-migration.md)
explains page validation, scoped inputs, action translation, ordinary-page publication and Host confirmation. These modules are
tested independently of launcher service assemblies; the production worker still
uses its existing execution entry, and SDK Beta remains pending the full cutover.

After `dotnet pack PluginSidecar.slnx -c Release -o artifacts/packages` and
`python eng/package_sdk.py`, run `python eng/verify_packages.py` to restore and
execute an independent NuGet-only consumer. It uses an isolated package cache,
the compiled Wasm sample and no repository project references.
`python eng/bundle_sdk.py` creates a checksummed SDK bundle in `artifacts/packages`.
CI runs these checks and uploads the bundle on Windows, Linux and macOS.

## Build and validate

Requires .NET 10 and Python 3.12+:

```sh
python eng/generate_abi100.py --verify
python eng/build_wasm_sample.py --clang /path/to/clang
python eng/prepare_host_reference.py
dotnet build PluginSidecar.slnx -c Release
NEXA_PLUGIN_WORKER=src/PluginSidecar/bin/Release/net10.0/PluginSidecar.dll NEXA_WASM_SAMPLE=artifacts/wasm-sample/plugin.zip NEXA_WASM_CONFORMANCE=artifacts/wasm-sample/conformance.wasm NEXA_WASM_CALLBACK=artifacts/wasm-sample/callback.wasm NEXA_WASM_CODEC=artifacts/wasm-sample/codec.wasm dotnet run --project tests/PluginSidecar.Tests -c Release --no-build
dotnet pack src/Nexa.Plugin.Bootstrap.V1 -c Release -o artifacts/packages
dotnet pack src/Nexa.Plugin.Sdk.Abi100 -c Release -o artifacts/packages
```

Run `python eng/package_sdk.py` for the standalone C/Wasm SDK ZIP. The managed SDK
NuGet package also includes its schemas and C headers. Cross-language tests cover
all 380 calls and 317 model/collection shapes, including callbacks and malformed
wire values. These tests exercise SDK semantics against explicit test Host ports.

CI also packs Hosting, WasmRuntime, Packages and the trusted development
RuntimeAdapter with their versioned dependencies. Host embedders can use those
packages without a private project reference.

Use `artifacts/packages` as a local NuGet source and reference
`Nexa.Plugin.Sdk.Abi100` version `0.2.0-alpha.13`. Edit
[contracts/abi100.json](contracts/abi100.json), then run the generator without
`--verify`; do not edit generated sources. CI checks reproducibility on all three
supported operating systems. The managed sample demonstrates Get/Make negotiation and
separate stage/activation contexts. The C examples compile to import-free Wasm modules and run through the actual
package loader and Host kernel. The workflow example covers registration
continuations, action callbacks, scheduled execution and cleanup. CI publishes
and executes the runtime tests using NativeAOT on Windows, Linux and macOS.

Use `abi.ForStage100(stage)` inside lifecycle callbacks and `abi.AsAbi100()` in
Active. `api.TryGet<Network100>()` preserves capability errors;
`api.IsAvailable<Network100>()` is a convenience check. Access never negotiates.
Saved proxies still recheck Host admission. Host remains authoritative for
permissions, revisions, idempotency, staged publication and scoped references.

See [architecture](docs/architecture.md), [acceptance status](docs/roadmap.md),
[Market/offline](docs/market-offline.md), [contributing](CONTRIBUTING.md), and
[security](SECURITY.md). Apache-2.0.

The kernel owns lifecycle, definitions, storage, events, services, tasks and workflows.
Desktop adapters connect those facilities to the launcher business owners. The
public SDK support manifest records the tested Host revision for each operation;
see [acceptance status](docs/roadmap.md) for the separate deployment requirements.

See [Desktop integration](docs/desktop-integration.md) for the authenticated worker, package UI references and the runnable Desktop example.

[Business provider callback contract / 业务 Provider 回调契约](docs/business-providers.md)
