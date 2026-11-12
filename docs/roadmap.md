# ABI v0.2 implementation and deployment status

This repository owns the plugin kernel and execution worker. Plugin authors use the
[public SDK](https://github.com/Nexa-MC/NexaCL-Plugin-SDK), whose generated API
reference and `contracts/host-support.json` identify the tested Desktop revision.
A catalog binding alone is not evidence of a working Host integration.

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
