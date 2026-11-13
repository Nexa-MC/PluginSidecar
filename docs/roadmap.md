# ABI v0.2 implementation and deployment status

This repository owns the plugin kernel and execution worker. Plugin authors use the
[public SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK), whose generated API
reference and `contracts/host-support.json` identify the tested Desktop revision.
A catalog binding alone is not evidence of a working Host integration.

## Standalone Sidecar cutover

The launcher-to-Sidecar migration is not yet complete. The production worker still
serves the existing remote Wasm execution contract; the launcher's plugin manager
and UI remain active. SDK Beta publication is pending the cutover and its release
checks. The table below describes implementation ownership, not installed-product
availability.

| Component | Current state | Remaining work |
| --- | --- | --- |
| Application and admission | `PluginSidecar.Packages/Application` owns package admission, runtime lifecycle, guest resources and presentation data. A real UI guest loads and retires in managed and NativeAOT tests. | Compose it in the worker after authenticated Host capability discovery. |
| Business adapters | `PluginSidecar.Business` uses versioned generic Host contracts without `Nexa.Services` assemblies. Focused tests cover account revision fencing, caller isolation, process cancellation, watches, state loss and bounded retirement. | Bind the remaining platform/theme ports and verify every migrated business path, including configured network policy and download behavior. |
| Offline installations | The Sidecar owns encrypted installation records and OS key protection. New key envelopes use a separate catalog scope; legacy Host envelopes remain readable when the OS grants access. Windows DPAPI and the portable envelope/restore tests run in CI. | Supply the installation directory in production composition and exercise startup restoration through the real worker. macOS Keychain and Linux Secret Service still depend on an available, unlocked OS store; tests with an in-memory store do not establish those deployment conditions. |
| Dynamic presentation | Host commit `a3c0b8d7d60401481e96e63e357dfe693b59aead` exposes a generic session-owned NXML replacement catalog. The Host compiles and renders documents without guest SDK models. | Migrate the manager, pages, model/action translation, dialogs, drawing, shortcuts and contributions. Additional model-update work in the Host is not yet a published Sidecar dependency. |
| Worker distribution | Existing builds verify authenticated remote execution and NativeAOT deployment on Windows, Linux and macOS. | Stage the standalone worker and its native dependencies in signed launcher releases, then verify missing, disabled, failed and crashed workers leave ordinary launcher functions usable. |

Keep the existing guest ABI and SDK schemas unchanged during this migration.
Generic Host ABI additions do not allocate a guest `EXP_ID`. After SDK Beta is
published, guest-facing contract changes must follow the frozen SDK's experiment
registry and compatibility rules.

## Existing catalog integration

The current catalog contains 358 operations and 261 models. The previous 346
operations retain their IDs and model layouts; 12 additive operations express
explicit restore destinations, versioned settings, trash previews and provider
consumers. The 22 historical dispatch slots remain reserved and callable through
the compatibility layer. There are 380 current and legacy C bindings.

| Layer | Responsibility and verification |
| --- | --- |
| Kernel | Capability admission, staged definitions, private storage/CAS, event subscriptions, services, owned tasks and durable workflows. Callback, ownership, retirement and recovery tests run both in process and through the authenticated worker. |
| Desktop | Instance clone/delete; backup and world restore; install templates/recovery/loader catalogs; verified Java archives; launch contributions/preflights; content inspection/trash; catalog collections/translations; account sign-out/providers; plugin settings/migrations; theme resources; download resolvers/transfers; diagnostic rules. Integration tests exercise actual consumers and owner routes. |
| Native owners | Generated numeric binary dispatch for account and Minecraft mutations. Independent NativeAOT callers verify that the caller does not embed owner implementations. |
| SDK | Generated C# and C contracts, bounded canonical codecs, lifecycle callbacks and typed results. The public SDK supplies executable tutorials and operation-specific limits and errors. |
| Distribution | Versioned NuGet development packages, checksummed bundles, pinned source inputs and three-platform CI. Package integrity does not establish publisher authenticity. |

## Separate deployment requirements

Completing the catalog does not provision production Market credentials, device-key
custody, entitlements or release signing keys. The signed/encrypted delivery and
offline cache have local contract tests; real acquisition and product admission
must be verified against their deployed services. A signed worker `.nsc` and its
native dependencies must be admitted by the launcher before remote execution.
The trusted CoreCLR adapter is a development tool, not an untrusted-plugin sandbox.

Ordinary idempotency is bounded and scoped to one load. Durable workflows retain
completed outputs and uncertain-effect markers across restart. Neither task retry
nor recovery replays an effect whose outcome is unknown. A plugin-owned failed task
can be reset; arbitrary Host operations cannot be replayed merely by calling Retry.

See [Desktop integration](desktop-integration.md), [runtime limits](runtime-services.md)
and [Market/offline delivery](market-offline.md). The original proposal remains in
[specifications](specifications/plugin-abi-v0.2.proposal.zh-CN.md) as historical input.
