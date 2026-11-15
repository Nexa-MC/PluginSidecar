# Loading contracts and SDK

## Beta ownership target

PluginSidecar owns plugin admission, SDK negotiation, grants, lifecycle, offline
installation records, business adapters and management UI. NexaCL exposes general
Sidecar Host capabilities and remains usable when this component is absent or
failed. Plugin-specific state and dispatch must not remain in the launcher.
`PluginSidecar.Packages/Application` now contains the portable application,
resource and presentation owner. Its encrypted catalog takes an asynchronous key
protection port, with no dependency on launcher account implementation classes.
Migration of business and UI adapters must complete before the Beta release.

`PluginSidecar.Presentation` owns guest NXML policy and translation. It uses the
canonical NXML compiler from the versioned public shared packages, without
Desktop, Avalonia or launcher service assemblies. A prepared page has a key space
derived from its plugin load identity and page ID. Input names and local anchors
are rewritten into that space. Guest action identifiers become opaque, page-local
tokens routed through one registered Sidecar command; the Host only captures a
general form envelope. Sidecar maps those fields back to the guest model and
constructs the SDK action request. Raw or compiled Host state bindings, images,
Host commands and dismissal/scroll command escape paths are rejected locally.

This compilation layer does not itself publish documents, admit plugins or
activate the worker. The production presentation owner must retire its published
action map with the corresponding load and exposure, and check the active load
identity before invoking the application. Advanced dialogs, contributions,
shortcuts, drawing and visibility subscriptions still require the complete UI
owner and the remaining generic Host ports before the old Desktop controller can
be removed.

`PluginSidecar.Business` now owns the SDK-to-launcher business adapters. It uses
only the versioned `Nexa.Sidecar.Host.Contracts` package, never launcher service
assemblies. The base client supplies ordinary domain routers and public state;
the independent effects client supplies credential-free account eligibility and
refresh, Java, power, preflight, public settings policy, game process cancellation,
and world snapshot lists. Guest identities, permissions, confirmation, plans,
opaque resources, callback providers, subscriptions and revisions remain local
to the Sidecar. Host state loss invalidates the cached projection; stale account
revisions cannot select a replacement account. The worker executable has not yet
switched to this application/business composition. Theme, platform and dynamic
presentation ports and complete parity verification remain required for cutover.

The business tests run without any `Nexa.Services` assemblies in both managed and
NativeAOT modes. They exercise caller isolation, revision fencing, cancellation
routing, state-loss recovery and plugin retirement. These focused tests are not a
claim that every SDK feature has been migrated to the independent worker.
Background settings queries carry the business owner's cancellation token.
Retirement cancels pending queries before awaiting watch workers, and repeated
disposal awaits the same cleanup task. A missing settings revision clears
developer mode instead of retaining a previous authorization decision.

The production catalog key protector belongs to Sidecar. New Windows records use
current-user DPAPI with catalog-specific entropy; Unix records use AES-GCM with
keys kept in the OS key store under a separate PluginSidecar namespace. Key-store
failure never falls back to a plaintext key file. Legacy Host catalog envelopes
(Windows format 1 and Unix format 2) remain readable with their original OS scope;
new writes use formats 3 and 4 respectively. Only 32-byte catalog encryption keys
enter this internal port. Plugin guests cannot request protection or unprotection.
This preserves record compatibility without importing launcher account services.
The worker still needs the correct catalog directory before offline restoration
can be enabled in its production composition.


Bootstrap 1 is a preview managed entry contract intended to freeze at formal publication. ABI 100 is a separate SDK package; future versions must not extend Bootstrap interfaces with version-specific members. The uploaded proposal is preserved under specifications as design input, not an assertion of implemented behavior.

Decision: Host communication uses the existing NexaCL Sidecar binary ABI, authenticated transport and generated numeric dispatch. The proposal's JSON envelope, transport numbering and JSON framing are NOT adopted. No new wire IDs are allocated by this SDK. A Host adapter must map these logical calls to reviewed upstream operations. Existing Host binaries do not yet implement this new loading contract.

The trusted development fixture for managed plugin assemblies uses CoreCLR. The memory loader is a separate runtime adapter, never linked into the NativeAOT CLI. Only the frozen Bootstrap assembly is shared; plugin dependencies load from verified memory payloads. Native dependency loading is rejected. The loader consumes already verified bytes; it does not itself grant authorization or verify encrypted packages.

The six normal stages are Checksum (loader only), PreInitialize, Initialize, PostInitialize, Checkup, Finalize. Ready contexts do not mean Active. Registration is staged until a Host-authorized group commit. Required dependency components commit atomically; failure rolls back the group and independent groups continue. Host Checkup failure precedes stopping hot-conflict peers and their required consumers; an earlier-stage failure must not stop old peers. Finalize cannot register new content. Activated is an event after commit, not another load stage.

Host owns permission checks, handle/session ownership, final Make decisions, idempotency, staged registry and commit decisions. The SDK never implements a local Make success fallback. Experimental IDs and supported old base combinations are permanent; promotion never removes an adapter. Error codes are authoritative; exceptions do not cross the transport boundary.

Implemented here: public contracts, C#/C ABI 100 bindings, controlled Wasm loading,
the local authority/coordinator, atomic registries, runtime services and SDK packages.
Authenticated Desktop composition, business/UI adapters and production Market
admission remain integration work. See the [current acceptance status](roadmap.md).
The dated decisions below record the preview's evolution. No signed installable
`.nsc` is produced by the SDK build.

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
SDK guards and Info metadata never replace that authority. This original context
revision preceded the coordinator and Host registry implemented below. It never
introduced a local successful Make fallback.

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
NOT new NexaCL transport opcodes or proof of an implemented Host route. Per-Host mappings are recorded in the public SDK host-support.json, with the
verified Host commit; the catalog retains neutral HostDefined metadata. Existing binary transport and generated numeric routing remain
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

## Service probes (2026-11-11)

A service may name an owned predicate as its Checkup probe. Binding ownership,
the declared probe ID and input schema are checked before dispatch. A literal
predicate runs without guest code; handler predicates use an optional local
`IPluginProbeInvocationV1` execution adapter. Probes may target a prepared member
of the same atomic group or an active provider. They cannot start normal service
work before activation.

The authority installs a revocable, query-only execution scope for the admitted
provider. Only catalog queries allowed in Checkup can execute; registration,
subscriptions and effects are denied even through saved ABI facades. The scope
expires at callback return or deadline, including in detached continuations.
Caller/provider cancellation and causal cycle limits apply. Wasm uses its existing
handler envelope with phase Checkup; no new IPC message number is introduced.

The load-report query returns only the admitted caller's identity, current phase,
retained load findings and required dependency IDs. Checkup findings survive into
the final report. It has no caller-supplied target plugin and cannot inspect an
unrelated plugin's private diagnostics.

## Desktop integration and remote execution

Desktop composes the trusted authority, lifecycle coordinator, business adapters
and UI state publisher. PluginSidecar owns Wasmtime instances and executes verified
guest bytes in its separate authenticated process. An execution adapter carries
bounded continuation messages over the existing Sidecar binary command channel;
it does not grant a remote process permission to impersonate a plugin's Host calls.
Each yielded capability request returns to the original admitted Host context.
No JSON IPC, private launcher reference or new transport opcode is introduced.

Payloads exceeding one Sidecar value are transferred in bounded sequential chunks,
with explicit transfer identity, length, digest and retirement. Callback IDs and
continuation sequence numbers prevent replay after an interrupted exchange. A
failed transport seals the guest; reconnecting never retries an uncertain effect.
Package validation and grants precede remote compilation. User-installed developer
packages require explicit admission; production Market authentication remains a
separate package-source port. Safe mode keeps extension execution disabled.

Desktop consumes committed plugin UI declarations and model snapshots through its
existing state/intent renderer boundary. It owns confirmation, file selection and
business policy. Plugin navigation and view replacement cannot remove the Host's
plugin-management/recovery entry. External SDK implementations stay in this
repository; the Host consumes a pinned package feed, not adjacent workspace source.

The alpha.5 addition `Files.OpenPackageFile` uses the unused Files method 11;
all prior IDs and schema fingerprints remain unchanged. It grants only an opaque
read-only reference to an already verified asset of the admitted load, never an
OS path. `VerifiedPluginPackage.CopyPayload` is a trusted embedding API: callers
own and clear copies. Host presentation dispatch uses `PluginHostRuntime.InvokeAsync`
and retains the original plugin identity, stage, capability and permission checks.
The Desktop authoring example exercises package files, staged UI and a confirmed action.

## Trusted application adapters

Host adapters can create bounded subscriptions and tracked operations in the existing
runtime service ledger, publish snapshots and dispatch registered callbacks. These
are trusted embedding ports, never new guest calls or transport messages. Creation
requires the admitted context/capability; publication and callbacks remain permission
and load scoped. Retirement releases queues and cancels operations. This lets Desktop
UI and domain services use the existing Resources/Tasks APIs rather than parallel
subscription or operation stores.

## Trusted Host resource lifetimes

Host adapters can register asynchronous cleanup in the existing admitted load
lifetime. These registrations share the global registration-handle allocator and
the Resources.Release operation, but keep callbacks separate from guest callback
handles. Owner checks, bounded registration counts, explicit release, load abort
and stop all apply. No numeric guest method or transport changes are introduced.

Storage migration is an owned pure transform over a bounded immutable snapshot, followed
by an atomic schema-version/data CAS. The migration Action must belong to the caller,
use the dedicated migration input/output schemas and declare ReadOnly with no interactive
confirmation. A concurrent write, cancellation, retired load or invalid output leaves the
old schema/data intact. A successful migration retires all prior namespace/transaction
handles and returns a fresh namespace handle. The existing Storage.Migrate signature is
retained; only its callback data models are added.

## Typed business-provider callbacks

Business providers retain the original ProviderDescriptor and receive an additive
BusinessProviderCall envelope: provider identity/kind, method, exact input/output
schemas and typed input. Host adapters supply the method table and validation; a
plugin cannot choose arbitrary callbacks or claim a built-in provider identity.
Registrations use the existing staged registry and release ledger, become visible
only after activation, and retire with their load. Read providers execute through
the coordinator's query-only probe boundary. They cannot use catalog queries to
perform nested business mutations. Callback results must match the Host's expected
schema and the provider instance remains held until callback completion.

Provider callback failures map to PROVIDER_UNAVAILABLE under the Unavailable policy;
FailOperation preserves the callback error code. Neither policy retries effects.
Closed Page/ Snapshot schema helpers are generated additively; existing model schema
fingerprints and operation numbers remain unchanged.

## ABI 100 completion adapters

Trusted business adapters may stage typed definitions in the existing publication
ledger and invoke typed read providers through the existing query-only boundary.
All registrations retain namespace ownership, atomic publication, in-use holds,
retirement cancellation and explicit release. Generic task retry is restricted to
plugin-owned terminal tasks and workflows without ambiguous effects; arbitrary
host mutations are not replayable unless their owner supplies a fresh admission.
Canonical SDK contracts are maintained in NexaCL-Plugin-SDK; additive extensions
must retain all existing operation IDs and model fingerprints.

## Sidecar-owned page publication

The presentation module owns an explicit asynchronous publisher over a general
Host presentation port. It is the sole writer of its Sidecar session's source set
and revision. Source replacement changes a publication epoch and retires all old
action tokens; model-only writes retain document identity and input drafts. A
failed or cancelled mutation has an unknown remote outcome: action admission is
closed until a subsequent explicit synchronization rereads the Host revision and
replaces the source set. The publisher does not replay navigation or actions.

Opening, closing and visibility reporting cross the generic Host page-control
catalog. Guest IDs, permissions and lifecycle watches remain local. A form must
resolve to a currently published page whose exact load identity and declaration
remain active. Guest actions retain the existing confirmation and task pipeline.
No publisher lock is held while a guest callback executes. Disposal closes local
admission and cancels outstanding Host work; the session owner must retire the
Host session to remove its presentation. Worker composition remains a separate
migration step, so this unit must not enable duplicate guest loading.

## Action confirmation over the general Host catalog

Sidecar formats each immutable guest action confirmation into bounded literal text
and invokes the Host's independent confirmation catalog. The Host receives no
ABI 100 action, permission or guest identity DTO. The Sidecar's existing action
coordinator still binds approval to the caller, provider, input and cancellation
scope. A declined decision returns false; unavailable UI and transport failures
never count as approval. The adapter does not execute or automatically retry an
action. This is an internal migration, with no guest operation or schema change.

## Asynchronous shell effects

Platform navigation and directory reveal await the general Host Shell catalog.
The business port takes cancellation-aware asynchronous callbacks so it cannot
report success before the Host acknowledges the action. Guest HTTPS validation,
file grants and action confirmation remain local. A failed or cancelled Host
request is not retried; late results cannot authorize another operation. Display
queries use the same independent catalog. File pickers and clipboard transport
remain separate migration work and are not advertised as implemented by this port.

## Typed theme configuration transport

The Sidecar binds save/reset/import through the general Host ThemeOptions catalog.
It parses user-selected NTP-options files locally into bounded typed values after
checking guest file ownership; no JSON command crosses IPC. The Host revalidates
source, current revision, option definitions and the imported header against its
prepared theme candidate before applying presentation changes. Guest plans,
confirmation and revision tokens remain local. This port does not claim preview
or activation ownership: those require a separate session-lifetime migration.

`SidecarThemeOptionsEffects.Create` adapts local theme effects to the discovered
ThemeOptions client. Save/reset preserve expected revision and owner error codes.
Import parses NTP-options/1 with a 1 MiB character limit, depth 16, strict field
sets and duplicate rejection before encoding typed values. This adapter owns no
UI state or preview lease and does not compose the production worker. Activation
and preview cleanup use the separate adapter described below. Compatibility
callbacks remain available while composition migrates. ABI 100 and the public SDK
are unchanged.

## Theme activation ownership

The general Host owns native activation and preview restoration; the Sidecar owns
guest preparation plans and cleanup registrations. The adapter assigns a positive
monotonic wire lease ID before sending a preview. If the result is uncertain, it
ends that same ID with an independent bounded cancellation budget; it never
replays activation. Guest retirement uses the existing cleanup registration.
Host retirement additionally covers process failure, when no guest callback can
run. Ordinary NXML replacement does not end a theme preview. The guest ABI stays
frozen and the production worker remains unchanged until all ports are composed.

## Acknowledged notifications

Guest UI and platform notification routes remain active-phase mutations with
permissions and idempotency enforced by the local runtime. The application accepts
an asynchronous notification port and awaits it with the admitted invocation's
cancellation token. The old synchronous constructor remains a compatibility adapter.
The presentation adapter adds the plugin identity to bounded literal text and
sends a general Host notification. Acknowledgement means Host feedback admission,
not screen paint; unknown outcomes are never retried. Unsupported guest actions
and localization arguments fail before IPC. No guest SDK type enters the Host.

## Native file selection ownership

The business adapter discovers the general Host file-picker catalog before
binding it. Both guest file-picker operations share this adapter. Guest title
arguments remain unsupported; literal text and filters are checked before IPC.
The adapter prefixes the guest ID, awaits the native result and checks the
invocation token again before creating an owner-scoped writable file grant.
A null selection becomes guest cancellation. Failed, cancelled and uncertain
queries are never retried automatically. The Host owns its native dialog slot
until the dialog actually closes; guest unload must not turn a late reply into
a new grant. This addition preserves guest ABI 100 and does not switch production
worker composition by itself.


## Host context and restoration readiness

`SidecarRuntimeContext` consumes the independent general Host Context catalog
(discovery 3327, query 3328). It validates the strict UTF-8 absolute data root and
waits separately for business readiness and, when required, a native window.
The caller supplies the activation token and a finite budget (at most ten minutes);
polling backs off from 100 ms to one second. Stopping, unsupported discovery,
invalid metadata and query failures stop the wait. Timeouts are distinct from
caller cancellation. No filesystem directories are created by this adapter.

The data root is trusted-native-Worker context, never a guest capability. Existing
plugin storage and offline catalog layout remain Sidecar-owned compatibility
policy. The Worker must finish activation before supervising this wait: the Host
waits for initial Sidecar activation during its own startup preparation. This
adapter does not enable a second restoration owner. Production Worker composition
and removal of the old Desktop owner remain a subsequent atomic migration.
The frozen ABI 100 receives no new operation or EXP_ID in this change.

## Ordinary-page contributions in the Sidecar

The presentation owner composes active, same-load component contributions before
publishing an ordinary page. Append, prepend, hide, replace-content and wrap use
the existing NXML composition semantics and the page's model. Ordering is a
stable topological sort by ordinal contribution ID; cycles, including self-edges,
fail before Host publication. Inactive or foreign components are rejected.
Compiled NXML and the final source retain the existing node, depth and byte
budgets. No plugin registration or guest SDK model crosses into the Host.

The publisher records the owner's active component/contribution handle set.
Changes invalidate old page actions immediately, even before synchronization;
publication rotates action epochs even when the rendered source is unchanged,
then reconciles the current source/model set through the existing
revision protocol. Registration changes for another owner do not invalidate the
page. The ordinary-page replacement extension is described below. Workspace
contributions/replacement, dialogs, drawing and shortcuts remain separate
migration steps. Production Worker composition is not switched by these local
presentation changes.

## Ordinary-page replacement ownership

The Sidecar resolves an active same-load `UIViewReplacement` against the active
page snapshot before composing ordinary-page contributions. Replacement is one
hop: the target supplies markup and render data, while the original page retains
its identity, action context, typed input model and contribution target. Both page
schemas must match the declared view contract. Missing targets and conflicting
replacements fail before publication; no Host plugin API is introduced.

Replacement bindings use private data aliases so components still read the
original page model. Aliases never enter guest action inputs. Data changes update
the existing Host model without changing source or control identities. Replacement
registration changes invalidate the publication immediately; a target model
revision change blocks old actions until synchronization has installed the new
projection. Target retirement also blocks actions and navigation. Workspace
replacement remains outside this ordinary-page implementation.

## Management and workspace publication

The same Sidecar presentation writer owns its management page, ordinary plugin
pages and per-load workspace fragments. The Host port accepts only NXML documents
and scalar models; a published management view has no fictitious guest identity.
Management actions have a separate command and token table and cannot enter the
guest action dispatcher. Current policy and exact load identity are checked again
when an action is accepted. Import/disable effects are supplied by the Worker,
which remains responsible for admission, confirmation and offline installations.

Workspace contributions and replacement are compiled against a virtual
`nexa:plugins/content` view for each active load. The fragment mounts into a private
slot in the Sidecar's management page, through the existing general NXML channel.
Each fragment has its own exposure and scoped form inputs. Registered commands
remain outside the guest-replaceable content and invoke actions with empty inputs.
Retired registrations invalidate their owner's actions before synchronization.
Workspace fragments cannot be opened as standalone pages or reported as ordinary
page visibility. All documents share the existing 32-document and byte budgets.
Production composition must explicitly enable this owner; no second manager or
automatic restoration owner is enabled by these library changes.

## Exact-load retirement

Management disable captures a load identity, not merely a plugin ID. The
application's `StopLoadAsync` validates that identity while holding the same
lifecycle gate used by loading, then retires the runtime and reconciles owned
resources before releasing the gate. It returns the exact identities retired by
dependency shutdown. A request queued behind a reload cannot stop the new load.
The existing ID-based stop remains available for deliberate shutdown-by-ID;
management effects use the exact-load operation. Offline-record changes remain
the responsibility of the single installation owner.

## Conditional offline-record updates

The encrypted installation format stays `NEXAPC01`. A successful read can also
return a SHA-256 revision of the complete encrypted record. Conditional save and
remove operations compare that revision while holding both the catalog's local
gate and a persistent file lock shared by cooperating catalog instances. A null
expected revision means that the record must not exist. Existing unconditional
operations use the same writer lock, so they cannot race a conditional mutation.
Lock contention has a finite wait and honors cancellation.

This gives the installation owner a rollback boundary: a failed activation may
restore the previous package/grants or remove a newly created record only while
the catalog still contains the record that this attempt wrote. It must not erase
a newer installation. The revision is native Sidecar metadata, never a guest
capability or a new SDK operation. It does not authenticate the publisher; package
approval and protected-key validation remain separate requirements. File locks
coordinate cooperating writers, not arbitrary filesystem edits.

## Approved installation attempts

`PluginApprovedInstaller` accepts only an already approved native package and
grant set. The picker, developer warning, permission display and remember choice
remain the responsibility of the trusted management owner; a guest cannot call
this port. The installer snapshots the supplied bytes, serializes its attempts,
and checks developer mode, readiness, safe mode and the captured session before
persisting, before loading and after activation. Replacing an active plugin is
rejected before touching its offline record.

Remembered installations preserve the prior decrypted record until activation
finishes. Failure or cancellation restores its bytes and grants, or removes a new
record, using only the revision written by that attempt. A competing newer record
is left intact. If policy changes during activation, the exact newly activated
load is retired before rollback. Cleanup failures are surfaced alongside the
original failure instead of being silently reported as a successful installation.
Session-only installations do not change a previous remembered installation.

This is the installation transaction used by the future management owner, not a
second startup or restoration loop. Production Worker composition and consent UI
are still required before removing the current Host owner. The single owner must
serialize restoration, disable and Market installation with these attempts.

## Single management controller

The Sidecar management controller owns developer import, exact-load disable,
offline restoration and policy retirement under one asynchronous gate. It binds
only to an application with no active loads. Picker and consent interactions are
trusted native ports; permission and remember decisions never come from guests.
The controller rechecks policy after each interaction and delegates approved
installation to the transactional installer. Shutdown cancels pending work,
drains it and retires active loads without deleting remembered installations.

Disable observes revisions before retiring the selected exact load, then removes
only matching records of loads actually retired, including dependents. A stale
button does not alter records. A record changed by another writer is retained and
reported. Policy retirement keeps records so re-enabling a permitted session can
restore them.

Offline restoration validates records independently, keeps only dependency
metadata during planning, and reopens one package at a time before loading.
Changed records, invalid packages, missing dependencies and dependency cycles
are reported without deleting approved records or blocking unrelated packages.
Restoration is attempted once per permitted session; policy revocation resets
that attempt. Production composition must drive policy synchronization and use
this controller as the sole owner. These library changes do not enable a second
Worker restoration loop while the Host still owns startup.

## Developer package interactions

The native Sidecar interaction adapter discovers the existing general file-picker
and confirmation catalogs. It requests a local ZIP file and validates the bounded
absolute UTF-8 path returned by the Host. Package parsing remains in the management
controller. The Host receives no manifest, guest grant object or plugin-specific
operation.

Installation uses two explicit general confirmations: first approve the verified
package identity and requested permissions; then choose whether to remember that
approval for later developer-mode sessions. Declining the first does not load.
Declining the second means session-only loading, stated in that dialog. Transport
failure, timeout or cancellation is not approval and never becomes a default
remember decision. Package-provided values are quoted and bounded literal text.
This uses the existing immutable Host catalogs and adds no guest ABI operation.

## General modal control port

`SidecarDialogHost` uses the Host's independently negotiated DialogControl catalog
(shared packages alpha 18). It addresses only source indices and the current
presentation revision; guest dialog IDs and view scopes stay inside Sidecar.
Creation performs explicit catalog discovery. The adapter validates the complete
state result before returning it, and does not retry opening after cancellation,
timeout or a lost reply. The presentation owner must reconcile state under the
same serialization gate used for source/model writes. This port alone does not
activate guest dialogs or introduce another presentation writer in the Worker.

## Retaining independently published views

The single presentation owner uses the general ViewReconcile catalog from shared
packages alpha 19. Guest pages, the trusted manager/navigation pair and each
load's workspace receive independently owned publication epochs. A source or
ownership change rotates only the affected view's epoch. Model-only updates keep
that view's keys and action tokens. An unchanged view is explicitly retained by
its prior source index, even when another view is added, removed or reordered.
No guest identity crosses the Host boundary.

The manager's workspace target depends on its epoch. Replacing that manager also
recompiles its navigation and all fragments mounted under the old target; those
fragments cannot be retained against a removed parent. Unrelated ordinary pages
keep their identities. Contribution stamps and replacement dependencies remain
part of action admission, so retention never revives a retired load or stale
replacement. Typed guest models are refreshed even when their rendered strings
have not changed.

One gate owns source reconciliation, model writes, revision changes and action
admission. Only a successful acknowledgement installs the candidate. A failed or
uncertain mutation clears admission and the remembered revision. An explicit
later recovery reads the Host revision and publishes fresh sources with fresh
epochs; it never assumes an uncertain retention succeeded or replays modal opens.
Legacy presentation ports retain their existing whole-set replacement behavior.
Production native composition must discover retention before using this path.
This change does not switch the Worker restoration or presentation owner.

## Compiling guest dialog projections

A dialog projection keeps its guest page ID and typed dialog model for action
translation, while its NXML target, local keys and action tokens additionally
include the exact dialog ID and view scope. The same page displayed ordinarily
and modally therefore has separate form ownership, as do successive dialog
instances in one load. The Sidecar converts the result to the general Dialog
operation; the Host receives no guest dialog reference or plugin model.

The canonical page compiler still validates source, expanded controls,
contributions and replacement contracts. Replacing a page's visual content does
not replace the logical dialog action model. Model-only dialog updates retain
source identity within the same publication epoch. The sole presentation owner
must check the current exact dialog reference and load before admitting actions;
compilation alone neither opens a window nor proves that a dialog is live.

## Single-owner dialog publication and closure

The page publisher may also receive a modal-control port, but only together with
the retained-presentation port. Its existing gate serializes native-close
observation, source/model publication and modal opens. A dialog's first open is
attempted once after source acknowledgement. A lost or cancelled open reply is
reconciled by reading actual native state; it never triggers an automatic retry.
Control failures do not discard an acknowledged source revision.

Only the current exact dialog reference and load may translate its actions.
Ordinary navigation and page visibility exclude modal documents. Model changes
retain the existing window. Changes that retire a dialog's contributing source
or replacement dependency retire that logical dialog rather than reopening a
replacement window from an earlier open-state snapshot. Dialog contribution
ownership is scoped to that page, so unrelated registrations do not retire it.

Source mutations retain their existing fail-closed recovery. Open-attempt history
survives an uncertain mutation: fresh-source recovery cannot replay an attempted
modal open. Native closure retires the exact application dialog and its lifecycle
subscriptions; malformed or incomplete native state never becomes synthetic
closure. Production Worker composition must use this sole publisher and drain
its operations before the old Host controller can be removed.

## Worker receive loop and retirement

The worker owns exactly one frame reader. Activation, admitted commands and
runtime retirement run outside that reader, including their synchronous prefixes.
The reader continues routing correlated Host replies, cancellation and health
traffic during retirement. Deactivation permanently closes command admission;
unregistration acknowledges only after admitted work and runtime disposal finish.
An asynchronous failure wakes the reader even if the Host sends no more frames.

The internal runtime boundary supplies registrations, negotiated capabilities,
activation and command execution. The existing remote Wasm runtime remains the
production implementation until application ownership is migrated. This boundary
does not add a guest SDK contract or a transport opcode. Host-request capability
is enabled only by runtimes that actually use it, and correlated replies go to
the existing shared request client. Host authorization still applies during
retirement: processing a reply does not authorize new business effects.

Shutdown or transport loss cancels work and closes the request client before
awaiting local cleanup, so a missing Host reply cannot strand disposal. Cleanup
is invoked once. A five-second local drain limit bounds a broken runtime; failure
is reported rather than acknowledged as successful retirement. Runtime cleanup
must be cancellation-aware and must not rely on a live Host after shutdown.

## Publishing scoped guest shortcuts

The sole page publisher owns an optional shortcut port. After acknowledging its
source/model set, it publishes gesture bindings to exact ordinary-page or owned
workspace documents. Shortcut and command registration handles, the load identity,
view scope and publication epoch contribute to an opaque callback token. The Host
receives only its own document index, gesture, registered Sidecar command and
token. It never parses UIShortcut or UICommand from the guest SDK.

Action admission checks both live registrations again, even before the next
publication tick. A shortcut invokes the ordinary asynchronous action pipeline
with Empty inputs; supplied form fields are rejected. Dialog views never inherit
ordinary-page shortcuts. Duplicate gestures in one document retain the first
registration in definition-ID order, matching the existing selection policy.
Removing a winning registration allows the next registration after synchronization.

A lost shortcut-set reply seals shortcut tokens and preserves the acknowledged
source revision. Explicit synchronization can re-establish the absolute binding
set without replaying a guest action. Source recovery creates fresh view epochs;
model-only updates retain token identity. Runtime disposal seals local admission;
the process owner must retire the native session to remove all native bindings.
This library integration precedes the production Worker composition change.
