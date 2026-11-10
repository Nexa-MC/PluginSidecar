# Loading contracts and SDK

Bootstrap 1 is a frozen managed entry contract. ABI 100 is a separate SDK package; future versions must not extend Bootstrap interfaces with version-specific members. The uploaded proposal is preserved under specifications as design input, not an assertion of implemented behavior.

Decision: Host communication uses the existing NexaCL Sidecar binary ABI, authenticated transport and generated numeric dispatch. The proposal's JSON envelope, transport numbering and JSON framing are NOT adopted. No new wire IDs are allocated by this SDK. A Host adapter must map these logical calls to reviewed upstream operations. Existing Host binaries do not yet implement this new loading contract.

Managed plugin assemblies require CoreCLR. The memory loader is a separate runtime adapter, never linked into the NativeAOT CLI. Only the frozen Bootstrap assembly is shared; plugin dependencies load from verified memory payloads. Native dependency loading is rejected. The loader consumes already verified bytes; it does not itself grant authorization or verify encrypted packages.

The six normal stages are Checksum (loader only), PreInitialize, Initialize, PostInitialize, Checkup, Finalize. Ready contexts do not mean Active. Registration is staged until a Host-authorized group commit. Required dependency components commit atomically; failure rolls back the group and independent groups continue. Host Checkup failure precedes stopping hot-conflict peers and their required consumers; an earlier-stage failure must not stop old peers. Finalize cannot register new content. Activated is an event after commit, not another load stage.

Host owns permission checks, handle/session ownership, final Make decisions, idempotency, staged registry and commit decisions. The SDK never implements a local Make success fallback. Experimental IDs and supported old base combinations are permanent; promotion never removes an adapter. Error codes are authoritative; exceptions do not cross the transport boundary.

Implemented here: public contracts, ABI 100 typed dispatch wrappers, CoreCLR in-memory entry loading, package generation and an example. Package decryption/permits, group scheduler, Host broker/registry, binary transport integration, UI renderer integration and telemetry remain implementation work. No signed installable .nsc is produced by this change.
