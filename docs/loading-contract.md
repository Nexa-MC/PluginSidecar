# Loading contract v0.1

Authoritative design input: [proposal](specifications/loading-abi-v0.1.proposal.zh-CN.md). User correction: Host traffic MUST use the existing NexaCL binary Sidecar ABI. Proposal sections 1.2 and 11 do not authorize a JSON transport. These libraries define logical managed contracts, not wire format or newly assigned Host message IDs.

Bootstrap 1 and ABI 100 have separate assemblies and package versions. `GetExpermentABI` deliberately preserves the requested spelling. Stable version requests must be positive multiples of 100, with no implicit fallback. Zero handles are ABI_FALSE; `AbiReply<T>.Fail` returns the default value plus a structured error. Get produces Draft, Host-confirmed Make produces Ready, and only a group commit produces Active. Interface implementations must enforce these states; SDK dispatch is not an authorization boundary.

All method slots from proposal section 8 are represented. Contract IDs are `nexa.events/100`, `nexa.ui/100`, `nexa.interplugin/100`; diagnostics/resources use Bootstrap ports. No experiment IDs are invented. Permanent compatibility requires future implementations and retained fixtures, not just ID constants.

The manifest is a logical DTO with explicit required/optional dependencies and runtime target. It is not an encrypted package parser or a wire JSON schema. Version bounds are structured; the future planner must validate canonical SemVer. Make/Prepare/Commit operations carry idempotency identities; Host implementations must bind actual connection/session identity and reject reused IDs with changed contents. Host ports do not grant authority by being called.

Stage restrictions, atomic groups, dependency cycles, rollback, cleanup budgets, authorization-at-load-only and hot-conflict Checkup ordering follow the preserved proposal. They are implementation obligations for the coordinator/Host, not behaviors already provided by this SDK. An incomplete integration must fail closed rather than activate a plugin.

## Verified in this repository

- Invalid stable version detection and zero failure handles.
- Exact 16 ABI 100 dispatch IDs, context facade use and preservation of operation options.
- UInt64 data without narrowing; Host error propagation through SDK and sample negotiation.
- Actual sample DLL loaded from memory in a collectible CoreCLR context sharing Bootstrap type identity.
- Missing payload/type rejection and entry invalidation after disposal.
- Preview NuGet generation; CLI NativeAOT smoke across three operating systems.

The proposal's remaining acceptance matrix requires an implemented coordinator, authenticated Host adapter and registry. No end-to-end launcher integration is claimed.
