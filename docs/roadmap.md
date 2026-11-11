# ABI v0.2 acceptance status

The full proposal is not implemented. A typed method, a stored declaration and a
working launcher integration are distinct acceptance milestones.

| Area | Implemented and tested | Remaining acceptance |
| --- | --- | --- |
| SDK | 33 groups, 341 new typed operations, 238 models; static schema fingerprints/validators; historical 22 slots retained | Desktop Host mapping and compatibility freeze |
| Loading | Real authority, exact Make, phase leases, idempotency, dependency groups, atomic activation, hot-conflict stop closure and resource retirement | Authenticated session/permit port and Desktop composition |
| Content | Wasm and static content packages share one registry; all 15 definition routes, trusted consumers, tags, resolved overlays, previews, watches, CAS and reference-protected deletion | Desktop/plugin consumer adapters, additional merge/deletion policies and product content scenarios |
| Storage | Seven private namespace operations, durable atomic writes, transactions, CAS, ownership and restart tests | Workflow-backed migration and application key/storage policy |
| Workflows | Executable action/predicate callbacks, bounded workflow interpreter, trace, cancel, atomic persistent checkpoints, explicit safe recovery, durable event waits, schedules, all concurrency policies and waiting nodes | Desktop user-interaction port, migration consumers and product workflows |
| Providers/UI | Published public contracts, owned service binding/calls, typed results, callback scheduling, availability streams and required-consumer stop closure | Provider selection, UI renderer/consumers and protected recovery controls |
| Business | Typed Minecraft/Catalog/Accounts/Settings/Themes/Backups/Files/Network APIs | Actual Host adapters and end-to-end author scenarios |
| Market/offline | Signed encrypted delivery, device binding, atomic cache and offline-restart package loading | Real acquisition endpoint/UI, protected key custody and final Host admission |
| Controlled execution | Import-free Wasmtime runner, fuel/memory/request limits, async continuations, native/WASI import rejection | Business effect policies and deployed integration |
| Guest authoring | Full C/Wasm model/build/result bindings for 363 calls; compiled cross-language conformance and lifecycle callback tests | Additional language SDKs are outside the current C#/C SDK scope |
| Distribution | SDK packaging, C sample build and three-OS NativeAOT runtime CI | Published signed `.nsc`, product integration and architecture certification |

The default kernel exposes 86 implemented routes across 13 groups, including
state/CAS, durable/resumable event callbacks, subscription read/release, owned tasks,
diagnostics, contracts, services, actions, predicates and workflows in addition to
definitions/storage. See [runtime behavior and limits](runtime-services.md).
Additional routes must be explicitly installed by the trusted embedding Host.
All catalog `HostMappingPending` labels refer to product integration, including
operations that now have tested local kernel implementations.

Ordinary call idempotency is retained per load instance within bounded memory.
Persistent workflow checkpoints also retain completed node outputs and uncertain
effect markers across restart; a pending effect prevents automatic or manual replay. Unknown outcomes are not automatically retried. Storage
persists across restart, but transaction/namespace handles do not. No live Market
service or signing key is provisioned by the tests.

Acceptance scenarios remain in sections 16 and 18 of the
[supplied proposal](specifications/plugin-abi-v0.2.proposal.zh-CN.md).
