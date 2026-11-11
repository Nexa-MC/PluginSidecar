# Loading contract v0.1 — 2026-11-11 revision

Design input: [revised proposal](specifications/loading-abi-v0.1.proposal.zh-CN.md).
Host traffic continues to use the existing NexaCL binary Sidecar ABI. Proposal
sections 1.2 and 11 do not authorize JSON framing or new transport message IDs.
Local managed facades never cross that boundary; adapters send opaque handles
and explicit bounded data through reviewed upstream operations.

## Preview compatibility

Bootstrap and SDK packages are now `0.1.0-alpha.2`. The requested revision changes
managed signatures and moves a preview method: **rebuild alpha.1 plugins**. These
unpublished previews are not binary-compatible. Bootstrap assembly identity remains
`1.0.0.0`; that identity alone does not make old preview binaries compatible.
After formal freezing, equivalent changes require an explicit new compatibility
version. Stable ABI and package versions remain separate axes.

## Unified context

`GetABIContext` returns a Draft `ABIContext`; Host-confirmed `MakeABIContext`
returns the final facade. `GetExpermentABI` retains its intentional spelling.
Requests must be positive multiples of 100 and never implicitly fall back.
`AbiReply<T>.Fail` carries a structured error and default value (null for local
facades, zero for opaque handles). Ready does not mean Active.

Bootstrap freezes only the generic context facade:

- `Info`: immutable identity, opaque handle, stable version, confirmed experiment
  and capability descriptions, permissions and session/permit information.
- `GetAbility(contractId)`: access an already confirmed capability.
- `GetExperiment(expId, contractId)`: access an interface belonging to an already
  confirmed experiment. Neither accessor negotiates, grants permission or calls Make.

Initialize, PostInitialize, Checkup and Finalize receive **the same final
ABIContext**, plus an independently issued StageContext. StageContext owns the
StageInvocationId, cancellation, budget and optional read-only CheckupInfo.
Its monotonic remaining budget starts when the local callback context is created;
creating another SDK view does not reset it. Abort can carry a null final context
when negotiation failed. Stop carries the original final context.

## ABI 100 SDK views

Use `abi.AsAbi100()` for runtime views and `abi.ForStage100(stage)` inside a load
callback. Both expose `Events`, `UI`, `Plugins`, `Diagnostics` and `Resources`.
Views lazily request exact contract IDs and preserve structured Host errors.
A different stable ABI is rejected, never silently upgraded. `GetExperiment<T>`
uses the static `IExperimentView<T>` ContractId/Create mapping; no reflection is
used. Saved experiment proxies recheck access before dispatch.

| Contract | Callable methods | Notes |
| --- | --- | --- |
| `nexa.events/100` | 1, 2, 4, 5 | Slot 3 reserved; no service export |
| `nexa.ui/100` | 1–6 | Existing UI methods |
| `nexa.interplugin/100` | 1–7 | ExportService = 6; Probe = 7 |
| `nexa.diagnostics/100` | 1–4 | Report, ReportFinding, QueryRegistrations, QueryBindingStatus |
| `nexa.resources/100` | 1 | RegisterCleanup |

There are 22 callable methods. Inputs are typed; outputs use the closed AbiValue
union pending Host integration. Diagnostic source/ownership is assigned by Host.
The pre-Make `PreInitContext.Resources` port and post-Make `api.Resources` MUST
map to one instance resource table, preserving operation IDs and avoiding duplicate
cleanup. This SDK does not create another cleanup registry.

```csharp
public async ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi, StageContext stage)
{
    using var api = abi.ForStage100(stage);
    var result = await api.Diagnostics.QueryBindingStatus(
        new(Guid.NewGuid(), stage.Budget, stage.Cancellation));
    return result.Success
        ? AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], []))
        : AbiReply<CheckupResult>.Fail(result.Error!);
}
```

Dispose the stage view before returning. It attaches the stage invocation ID,
links cancellation and forwards the smaller remaining/caller budget. Saved proxies
from a disposed view fail with STAGE_FROZEN. Runtime views do not inherit stage IDs
or deadlines. This convenience is **not authorization**: callers can retain or
recreate StageContext and can call Bootstrap facades directly. The coordinator must
retire invocation IDs on callback completion; Host must reject stale IDs, wrong
phases, cross-instance access, expired contexts and missing permissions on every
call. It must also reject unconfirmed experiments through either accessor.

ExportService is valid in Initialize, Bind in PostInitialize, Probe in Checkup,
and ordinary Call in Active. Finalize cannot add registrations. Abort/Stop only
allow corresponding cleanup, unbinding and diagnostic operations. Merely retaining
`api.Plugins` must never broaden the allowed methods.

## Verification and remaining integration

Tests cover all 22 dispatch slots, reserved event slot exclusion, exact UInt64,
structured error propagation, lazy views, draft/unconfirmed experiment errors,
stage-ID forwarding and conflict rejection, disposed proxies, runtime budgets,
and a real in-memory sample using a private SDK against shared Bootstrap types.
Host-phase responses in these tests come from explicit **test authority ports**;
they are not a production Host implementation.

The coordinator, authenticated Host adapter, staged registry, permission checks,
atomic groups, shared cleanup execution and hot-conflict Checkup ordering still
require implementation. The remaining proposal acceptance matrix is not claimed
complete. Manifest/version validation and permanent experimental compatibility
likewise require Host implementations and retained compatibility fixtures.
