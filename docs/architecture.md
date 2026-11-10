# Loading contracts and SDK

Bootstrap 1 is a frozen managed entry contract. ABI 100 is a separate SDK package; future versions must not extend Bootstrap interfaces with version-specific members. The uploaded proposal is preserved under specifications as design input, not an assertion of implemented behavior.

Decision: Host communication uses the existing NexaCL Sidecar binary ABI, authenticated transport and generated numeric dispatch. The proposal's JSON envelope, transport numbering and JSON framing are NOT adopted. No new wire IDs are allocated by this SDK. A Host adapter must map these logical calls to reviewed upstream operations. Existing Host binaries do not yet implement this new loading contract.

Managed plugin assemblies require CoreCLR. The memory loader is a separate runtime adapter, never linked into the NativeAOT CLI. Only the frozen Bootstrap assembly is shared; plugin dependencies load from verified memory payloads. Native dependency loading is rejected. The loader consumes already verified bytes; it does not itself grant authorization or verify encrypted packages.

The six normal stages are Checksum (loader only), PreInitialize, Initialize, PostInitialize, Checkup, Finalize. Ready contexts do not mean Active. Registration is staged until a Host-authorized group commit. Required dependency components commit atomically; failure rolls back the group and independent groups continue. Host Checkup failure precedes stopping hot-conflict peers and their required consumers; an earlier-stage failure must not stop old peers. Finalize cannot register new content. Activated is an event after commit, not another load stage.

Host owns permission checks, handle/session ownership, final Make decisions, idempotency, staged registry and commit decisions. The SDK never implements a local Make success fallback. Experimental IDs and supported old base combinations are permanent; promotion never removes an adapter. Error codes are authoritative; exceptions do not cross the transport boundary.

Implemented here: public contracts, ABI 100 typed dispatch wrappers, CoreCLR in-memory entry loading, package generation and an example. Package decryption/permits, group scheduler, Host broker/registry, binary transport integration, UI renderer integration and telemetry remain implementation work. No signed installable .nsc is produced by this change.

## Market delivery and offline authorization

Market owns catalog, ownership, encrypted acquisition and issuing signed offline grants. PluginSidecar independently verifies grants and decrypted bytes on every load. Host owns trusted publisher keys, device key custody, current subject selection and the final LoadPermit; plugins receive neither bearer tokens nor decryption keys. Delivery/cache binary encoding is a package format, not a replacement for the existing Host IPC ABI.

A successful acquisition persists ciphertext and a signed grant with a device-wrapped content key in one atomic cache file. Offline open has no network or login-session dependency. Perpetual ownership has no invented refresh TTL; a grant may carry the actual entitlement end time. Expiry is checked on load only. Running plugins are not periodically stopped. Offline revocation and adversarial local-clock rollback cannot be guaranteed without a trusted clock/online authority; local wall-clock checking is not DRM proof. Logout does not delete owned encrypted packages; Host must still select the authorized local subject.

The current Market backend disables plugin artifact delivery and has no signed offline-grant endpoint. This change implements the cryptographic delivery/cache component and Market acquisition port, not a live endpoint or an installed launcher route. Existing ownership, account eligibility, artifact security and publisher checks must remain authoritative before grant issuance. No production keys are generated or deployed here.
