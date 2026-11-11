# ABI v0.2 acceptance status

The full proposal is not implemented. A typed method, a stored declaration and a
working launcher integration are distinct acceptance milestones.

| Area | Implemented and tested | Remaining acceptance |
| --- | --- | --- |
| SDK | 33 groups, 339 new typed operations, 236 models; static schema fingerprints/validators; historical 22 slots retained | Desktop Host mapping and compatibility freeze |
| Loading | Real authority, exact Make, phase leases, idempotency, dependency groups, atomic activation, hot-conflict stop closure and resource retirement | Authenticated session/permit port and Desktop composition |
| Content | Wasm and static content packages share one registry; six typed definition routes, schema policy, CAS, bounded pagination and reference-protected deletion | Dynamic registries/consumers, tags, overlays, watches and full content scenarios |
| Storage | Seven private namespace operations, durable atomic writes, transactions, CAS, ownership and restart tests | Workflow-backed migration and application key/storage policy |
| Workflows | Typed node/action/predicate/schedule models | Execution, persistence, budgets, cancellation and recovery |
| Providers/UI | Versioned service/provider, full page replacement and draw/input contracts | Active discovery, actual consumers, Host callback scheduling and protected recovery controls |
| Business | Typed Minecraft/Catalog/Accounts/Settings/Themes/Backups/Files/Network APIs | Actual Host adapters and end-to-end author scenarios |
| Market/offline | Signed encrypted delivery, device binding, atomic cache and offline-restart package loading | Real acquisition endpoint/UI, protected key custody and final Host admission |
| Controlled execution | Import-free Wasmtime runner, fuel/memory/request limits, async continuations, native/WASI import rejection | Business effect policies and deployed integration |
| Guest authoring | Full C/Wasm model/build/result bindings for 361 calls; compiled cross-language conformance and lifecycle callback tests | Additional language SDKs are outside the current C#/C SDK scope |
| Distribution | SDK packaging, C sample build and three-OS NativeAOT runtime CI | Published signed `.nsc`, product integration and architecture certification |

The default kernel exposes only its 13 implemented definition/storage routes.
Additional routes must be explicitly installed by the trusted embedding Host.
All catalog `HostMappingPending` labels refer to product integration, including
operations that now have tested local kernel implementations.

Idempotency is retained per load instance within bounded memory; it is not durable
across process restart. Unknown outcomes are not automatically retried. Storage
persists across restart, but transaction/namespace handles do not. No live Market
service or signing key is provisioned by the tests.

Acceptance scenarios remain in sections 16 and 18 of the
[supplied proposal](specifications/plugin-abi-v0.2.proposal.zh-CN.md).
