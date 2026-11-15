# ABI v0.2 implementation and deployment status

This repository owns the plugin kernel and execution worker. Plugin authors use the
[public SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK), whose generated API
reference and `contracts/host-support.json` identify the tested Desktop revision.
A catalog binding alone is not evidence of a working Host integration.

## Standalone Sidecar cutover

The production source entry now selects the standalone runtime. The coordinated
Host source change removes the embedded plugin owner, fixed plugin navigation
and SDK build dependency. Signed distribution, production Market integration
and SDK Beta release checks remain pending. The table distinguishes code and
local verification from installed-product availability.

| Component | Current state | Remaining work |
| --- | --- | --- |
| Application and admission | `PluginSidecar.Packages/Application` owns package admission, runtime lifecycle, guest resources and presentation data. A real UI guest loads and retires in managed and NativeAOT tests. | The standalone runtime composes it after authenticated Host discovery and native-window readiness. Validate its signed distribution with the matching Host. |
| Business adapters | `PluginSidecar.Business` uses versioned generic Host contracts without `Nexa.Services` assemblies. The runtime composes business state, account and process operations, installation/catalog services, shell, clipboard, theme and file-picker ports. Its default HTTP handler uses the Host-configured transport, with bounded uploads, streamed responses and independent retirement. Contract tests cover ownership, revision fencing, cancellation and uncertain effects. | Verify the complete installed application against real platform and service dependencies. |
| Offline installations | The Sidecar owns encrypted installation records and OS key protection. The runtime opens the existing installation directory and supervises restoration. New key envelopes use a separate catalog scope; legacy Host envelopes remain readable when the OS grants access. | Complete Market acquisition and normal-mode trusted installation. Verify Windows DPAPI, macOS Keychain and Linux Secret Service on supported machines; an in-memory store does not establish OS-store availability. |
| Dynamic presentation | Pinned shared packages expose generic NXML publication, retained sources and drafts, page/modal control, native confirmation, notifications, clipboard, shortcuts and raster resources. The Sidecar owns guest validation, action tokens, drawing interpretation and all SDK-to-native translation. The runtime composes the sole publisher, visibility/navigation loop and these native ports. Real Wasm tests cover import, action and disable without loading Host service or UI assemblies. | The coordinated Host change removes its old controller and fixed navigation. Exercise native input and display on the supported platforms. |
| Worker distribution | The frame reader supervises activation, commands, background publication and retirement. Incoming commands wait for activation; retirement seals admission, drains Host replies and enforces a five-second local cleanup bound. Managed and NativeAOT tests cover real Wasm composition and peer failure paths. The executable selects the standalone runtime; real-process tests cover authenticated import/action/disable and graceful shutdown. | Stage native dependencies in signed launcher releases and verify that missing, disabled, failed or crashed workers leave ordinary launcher functions usable. |

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
