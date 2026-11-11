# Loading contracts and SDK

Bootstrap 1 is a preview managed entry contract intended to freeze at formal publication. ABI 100 is a separate SDK package; future versions must not extend Bootstrap interfaces with version-specific members. The uploaded proposal is preserved under specifications as design input, not an assertion of implemented behavior.

Decision: Host communication uses the existing NexaCL Sidecar binary ABI, authenticated transport and generated numeric dispatch. The proposal's JSON envelope, transport numbering and JSON framing are NOT adopted. No new wire IDs are allocated by this SDK. A Host adapter must map these logical calls to reviewed upstream operations. Existing Host binaries do not yet implement this new loading contract.

Managed plugin assemblies require CoreCLR. The memory loader is a separate runtime adapter, never linked into the NativeAOT CLI. Only the frozen Bootstrap assembly is shared; plugin dependencies load from verified memory payloads. Native dependency loading is rejected. The loader consumes already verified bytes; it does not itself grant authorization or verify encrypted packages.

The six normal stages are Checksum (loader only), PreInitialize, Initialize, PostInitialize, Checkup, Finalize. Ready contexts do not mean Active. Registration is staged until a Host-authorized group commit. Required dependency components commit atomically; failure rolls back the group and independent groups continue. Host Checkup failure precedes stopping hot-conflict peers and their required consumers; an earlier-stage failure must not stop old peers. Finalize cannot register new content. Activated is an event after commit, not another load stage.

Host owns permission checks, handle/session ownership, final Make decisions, idempotency, staged registry and commit decisions. The SDK never implements a local Make success fallback. Experimental IDs and supported old base combinations are permanent; promotion never removes an adapter. Error codes are authoritative; exceptions do not cross the transport boundary.

Implemented here: public contracts, ABI 100 typed dispatch wrappers, CoreCLR in-memory entry loading, package generation and an example. Package decryption/permits, group scheduler, Host broker/registry, binary transport integration, UI renderer integration and telemetry remain implementation work. No signed installable .nsc is produced by this change.

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
