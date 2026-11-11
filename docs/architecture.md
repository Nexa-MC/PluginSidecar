# Loading contracts and SDK

Bootstrap 1 is a preview managed entry contract intended to freeze at formal publication. ABI 100 is a separate SDK package; future versions must not extend Bootstrap interfaces with version-specific members. The uploaded proposal is preserved under specifications as design input, not an assertion of implemented behavior.

Decision: Host communication uses the existing NexaCL Sidecar binary ABI, authenticated transport and generated numeric dispatch. The proposal's JSON envelope, transport numbering and JSON framing are NOT adopted. No new wire IDs are allocated by this SDK. A Host adapter must map these logical calls to reviewed upstream operations. Existing Host binaries do not yet implement this new loading contract.

The trusted development fixture for managed plugin assemblies uses CoreCLR. The memory loader is a separate runtime adapter, never linked into the NativeAOT CLI. Only the frozen Bootstrap assembly is shared; plugin dependencies load from verified memory payloads. Native dependency loading is rejected. The loader consumes already verified bytes; it does not itself grant authorization or verify encrypted packages.

The six normal stages are Checksum (loader only), PreInitialize, Initialize, PostInitialize, Checkup, Finalize. Ready contexts do not mean Active. Registration is staged until a Host-authorized group commit. Required dependency components commit atomically; failure rolls back the group and independent groups continue. Host Checkup failure precedes stopping hot-conflict peers and their required consumers; an earlier-stage failure must not stop old peers. Finalize cannot register new content. Activated is an event after commit, not another load stage.

Host owns permission checks, handle/session ownership, final Make decisions, idempotency, staged registry and commit decisions. The SDK never implements a local Make success fallback. Experimental IDs and supported old base combinations are permanent; promotion never removes an adapter. Error codes are authoritative; exceptions do not cross the transport boundary.

Implemented here: public contracts, ABI 100 typed dispatch wrappers, CoreCLR in-memory entry loading, package generation and an example. Host permits, group scheduler, Host broker/registry, binary transport integration, UI renderer integration and telemetry remain implementation work. No signed installable .nsc is produced by this change.

## Market delivery and offline authorization

Market owns catalog, ownership, encrypted acquisition and issuing signed offline grants. PluginSidecar independently verifies grants and decrypted bytes on every load. Host owns trusted publisher keys, device key custody, current subject selection and the final LoadPermit; plugins receive neither bearer tokens nor decryption keys. Delivery/cache binary encoding is a package format, not a replacement for the existing Host IPC ABI.

A successful acquisition persists ciphertext and a signed grant with a device-wrapped content key in one atomic cache file. Offline open has no network or login-session dependency. Perpetual ownership has no invented refresh TTL; a grant may carry the actual entitlement end time. Expiry is checked on load only. Running plugins are not periodically stopped. Offline revocation and adversarial local-clock rollback cannot be guaranteed without a trusted clock/online authority; local wall-clock checking is not DRM proof. Logout does not delete owned encrypted packages; Host must still select the authorized local subject.

The current Market backend disables plugin artifact delivery and has no signed offline-grant endpoint. This change implements the cryptographic delivery/cache component and Market acquisition port, not a live endpoint or an installed launcher route. Existing ownership, account eligibility, artifact security and publisher checks must remain authoritative before grant issuance. No production keys are generated or deployed here.

## 2026-11-11 preview revision: unified ABI context

The revised proposal moves negotiated capability access into the stable local
`ABIContext` facade (`Info`, `GetAbility`, `GetExperiment`). Get returns a Draft
facade; Host-confirmed Make returns the final facade. Initialize, PostInitialize,
Checkup and Finalize receive that same final object and a separate StageContext.
Only the opaque handle in Info and explicit data cross the existing Host binary
ABI. Stage invocation IDs, cancellation and budgets belong to individual calls,
not the immutable final context. Abort may have no confirmed context; Stop must
carry the original context. Checkup input is read-only data, not an executable port.

The ABI 100 SDK provides lazy Events/UI/Plugins/Diagnostics/Resources views and
static-contract experimental wrappers without reflection. A disposable per-stage
SDK view carries the StageInvocationId and bounds calls by the stage cancellation
and remaining budget. Disposing that view retires its saved proxies; runtime views
carry no completed-stage ID or deadline. The future coordinator must invalidate
IDs when callbacks return, and Host must enforce ownership, lifecycle state,
permission and experiment confirmation on EVERY call, including raw facade calls.
SDK guards and Info metadata never replace that authority. This revision does not
implement the coordinator, Host registry or a local successful Make fallback.

Event method 3 is reserved. Service export is InterPlugin method 6; diagnostic
probe is InterPlugin method 7. Diagnostic methods 1–4 and resource method 1 use
separate versioned contracts. Both cleanup entry points must map to the same
Host-owned instance resource table. Bootstrap and SDK packages advance to
0.1.0-alpha.2; old alpha.1 plugin binaries must be rebuilt. This is an explicitly
requested correction to the unpublished preview, not binary compatibility with
alpha.1 or permission to change a formally frozen ABI later.

## v0.2 public surface (2026-11-11)

The v0.2 proposal is the current authoring-surface design. v0.1 remains historical
loading detail where it does not conflict. ABI generation stays 100; preview
packages advance to 0.2.0-alpha.1. Public non-native capabilities have identical
contracts for plugins and Sidecars. There is no plugin function patch, private
object resolver, arbitrary shell/native-library/address executor, or fallback
that assigns new business types exclusively to Sidecars.

The SDK gains a machine-readable ContractCatalog and generated typed group APIs,
with explicit data schemas, phase/permission/idempotency metadata and bounded
AbiValue codecs. Catalog method numbers are SDK-local draft dispatch contracts,
NOT new NexaCL transport opcodes or proof of an implemented Host route. Every
catalog entry is marked HostMappingPending until a reviewed adapter and actual
consumer exist. Existing binary transport and generated numeric routing remain
authoritative. No direct project references to launcher-private implementations
are introduced.

Definitions shared by code and declarative packs, workflow nodes, view contracts,
Provider/consumer contracts and public business plans are data. Registration is
inert; a library returning successful registration cannot imply execution or
atomic Host publication. Dynamic schema values remain explicit AbiValue fields;
ordinary public operations have typed request/result models, scoped refs and
opaque revisions. Raw credentials, private launch objects and private controls are not
part of these models; public launch profiles remain validated data. Host still owns permissions, expected revisions, idempotency,
plan revalidation, operation completion and recovery.

Raw CoreCLR loading cannot enforce v0.2's prohibition on native execution: denial
of LoadUnmanagedDll alone does not cover framework process/interop/dynamic paths.
The existing loader is therefore explicitly a TRUSTED DEVELOPMENT fixture. Its
API is renamed accordingly; it is not admitted as a production plugin runtime.
The import-free Wasm profile below is the controlled runtime implementation.
Desktop enablement still requires authenticated Host composition and business
policy adapters; a permission boolean is not proof of that integration. Market verification authenticates bytes, not their runtime isolation.

## Controlled execution profile: WebAssembly continuation ABI 1

The production execution profile is a Wasmtime WebAssembly module with **zero
imports**, no WASI, no CLR exposure and no host function callbacks. Its linear
memory, tables, instruction fuel and callback/request counts are bounded. The
module exports `memory`, `nexa_alloc(i32)->i32` and
`nexa_resume(i32,i32)->i64`; the latter packs an unsigned output address in the
high 32 bits and length in the low 32 bits. The runtime copies bounded closed
binary values in/out. This is an in-process guest adapter, not a replacement IPC
protocol or a new Host transport message.

A guest returns a continuation request for a previously negotiated public
capability, and the runner awaits that call asynchronously before resuming the
guest. Host identity, phase, cancellation and deadlines are supplied by the runner,
never by guest memory. Arbitrary algorithms can execute in WebAssembly, while
network/files/UI/business effects go through the same public capability authority.
Native libraries, process execution and private Host references have no guest
entry point. Verified packages are compiled from Wasm bytes, never deserialized
native compiler artifacts. The managed SDK remains the Host/adapter contract;
language-specific Wasm bindings use the same schema and numeric catalog.

## Composed kernel and definition access

`PluginHostRuntime` composes the capability authority, atomic definition registry,
private storage and coordinator. Its route table contains actual implementations
only; unknown methods remain unavailable. The trusted embedding Host supplies
schema and registry admission policies and additional business routes. Package
requests cannot install those policies or grant themselves permissions.

Definition reads include the caller's own loading group before commit, and only
published definitions otherwise. Writes require ownership, matching revision and
valid references. Deletion cannot strand a required reference. Pagination binds
the registry revision and filter. Both abort and stop retire storage leases after
sealing the instance; persistent bytes survive. This kernel does not by itself
register a Desktop Sidecar or expose launcher business handlers.

## Declarative package profile

`nexa-content-1` packages contain a closed-binary `DeclarativeContentPack` named by
`declarativeEntryPath`, with no code entry. `nexa-wasm-1` may include the same entry
alongside its Wasm module. Both use `Definitions.RegisterBatch` during Initialize,
with the same schema policy, permission checks and group commit as code. Static
packs request the definitions capability and write permission in their signed
manifest. Their bytes are authenticated and hashed by the same delivery path;
loading never executes registration effects early. Binary content uses the
versioned AbiValue codec, not a new Sidecar message format.

## Guest authoring SDK

The freestanding C SDK uses bounded byte views and writers for the same closed
value format. It emits asynchronous continuation requests; it has no system
imports, host object handles, ambient filesystem or network access. Its generated
contract/method constants come from `contracts/abi100.json`. The compiled example
is loaded by the same package/runtime path used in the end-to-end tests. NativeAOT
validation runs the kernel and Wasmtime, not only the version-printing CLI.

## Complete SDK language bindings (2026-11-11)

SDK completeness is checked independently from application business availability.
The ABI catalog generates C# facades and C/Wasm typed model views, constructors,
array/optional accessors, operation continuations and typed result readers.
Both use the existing closed value codec and numeric method slots. Cross-language
fixtures exercise every catalog operation through an actual compiled guest.

The guest invocation adapter also exposes negotiated context metadata and a
separate typed callback port for events, services and other Host-dispatched
handlers. The embedding Host must authorize dispatch and provide its context;
the guest cannot supply identity, phase or deadline. This is an SDK/adapter
boundary and does not install business handlers, subscriptions or UI in Desktop.

## Runtime capability services (2026-11-11)

The kernel will own scoped state, public service definitions and bindings, callback
scheduling, events, operation records and diagnostics. These are executable routes,
not generated dispatch stubs. Declarations participate in the same publication
lock and group commit as content; abort and stop invalidate bindings, subscriptions
and callbacks. The coordinator is the only dispatcher of plugin callbacks and
supplies the admitted identity/context, deadline and causality. Calls never wait
while holding the publication lock. Watch handles are consumed by a trusted Host
subscription port; this adds no wire opcode. Application effects still belong to
the existing launcher services and require explicit public Host adapters.

The watch contract had a missing consumption path: `SubscriptionRef` could be
created but the SDK had no way to read or release it. ABI 100 now appends
Resources methods 4 (`ReadSubscription`) and 5 (`ReleaseSubscription`), preserving
all existing method slots. These are capability operations over the existing
binary boundary, not transport message IDs. Reads are bounded, owner checked and
report queue gaps; callback subscriptions return their gap status without stealing
callback messages. SDK packages advance to 0.2.0-alpha.3 for this additive change.

## Scheduled and suspended workflows (2026-11-11)

The kernel owns interval, absolute-time and event-triggered schedules, bounded
missed-trigger policies and debounce. A Host clock/pump supports deterministic
verification; the normal runtime starts and stops the pump with its lifetime.
Every scheduled workflow re-enters the existing capability authority as the
schedule owner. Schedule definitions and subscriptions remain staged until group
publication; disable/stop fences new dispatch. Persistent next-due state advances
before dispatch, so a crash never causes an uncertain trigger to be replayed.

Workflow concurrency is scoped to the admitted caller and workflow ID. Queue and
ReplacePending serialize running work; replacement cancels only queued work.
RejectDuplicate fails admission while a matching run is queued or executing.
Resume uses the same admission path. Cancellation while queued cannot start work.
WaitEvent and AskUser store waiting data in persistent journals. The trusted Host
supplies a user-interaction port; unavailable UI is an explicit error. No plugin
receives a renderer object or permission to impersonate a user's answer.

## Durable event delivery and availability (2026-11-11)

Durable events keep a bounded, atomically replaced binary history. Resumable
subscriptions return an opaque checkpoint cursor bound to the subscriber ID,
event stream and exact scope. Successful callbacks advance that checkpoint;
a crash before acknowledgement may redeliver. The checkpoint never advances
past an outstanding parallel delivery. Retention loss or queue overflow is an
explicit resynchronization signal. No new transport message or callback identity
is introduced. Callback retries use the existing admitted dispatcher and end at
subscription release or plugin retirement. Staged replay cannot dispatch before
successful activation. Availability subscriptions use the same owned resource
queue and publish snapshots only at atomic publication/retirement boundaries.

## Resolved content registries (2026-11-11)

A dynamic registry must bind a real trusted Host consumer. The consumer validates
closed data before publication and accepts immutable snapshots through a local
state-publication port; it cannot be supplied by a plugin manifest. Consumer faults
are exposed in resolution reports. The built-in `nexa:merge/closed-v1` contract
implements bounded map/array addition, recursive map merge, replacement and masking.
Other merge contracts require a Host adapter and cannot be silently interpreted.

Tags resolve same-registry members with cycle detection. Overlays participate in
explicit topological ordering; ambiguous or cyclic order is rejected according to
the registry policy. Foreign-owned targets require both AuditedOverlay registry
policy and trusted Host approval. Resolved reads, CAS revisions, tagged queries,
previews and watch snapshots share one resolver. Publication/retirement changes
both the visible data and its revision under the existing authority gate.

## Cleanup and registration release (2026-11-11)

Bootstrap cleanup and SDK Resources.RegisterCleanup share the same instance-owned
cleanup table and reverse registration order. Explicit release takes a cleanup
registration before invoking it, so cancellation/failure cannot replay it during
stop. Cleanup callback execution is bounded and remains permitted after business
callbacks are sealed. Wasm uses the existing handler envelope with the current
lifecycle phase; no transport opcode is added. An optional local cleanup adapter
interface separates this path from ordinary active-only business dispatch.

Registration handles use one Host allocator across definitions, runtime services
and cleanup tables. Release validates the full owner/load/definition identity,
rejects in-use definitions and invalidates relevant snapshots. A numeric handle
from one registration family cannot collide with another family's handle.

The optional cleanup adapter and new Host composition ports advance the SDK
packages to 0.2.0-alpha.4. Package dependencies require the matching preview so
a new runtime cannot accidentally bind an older Bootstrap without the cleanup
interface. Bootstrap assembly identity and existing numeric ABI slots are retained.

## Action confirmation (2026-11-11)

Actions requiring user confirmation use a trusted, optional Host interaction port.
The immutable request identifies the admitted caller, actual provider, descriptor
and exact input/target. Plugins cannot supply an approval. Confirmation runs outside
the publication lock, has a bounded deadline and is cancelled on caller/provider
retirement. A late answer cannot execute an action. Execution rechecks permissions
and registration identity after approval. The per-load action idempotency table
coalesces concurrent confirmations as well as execution; a declined request needs
a new key to prompt again. Persistent workflow effect markers are flushed only
after approval, immediately before dispatch, so declining a prompt does not leave
an uncertain business effect. Desktop still supplies the actual state/intent UI.
