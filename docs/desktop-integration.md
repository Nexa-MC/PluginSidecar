# Desktop integration

The independent worker now implements `plugin.execution.v1` through the existing
NexaCL authenticated Sidecar binary command channel. Public Host packages are built
from `eng/references/nexacl.json`; run `python eng/prepare_host_reference.py` before
building the worker or its process integration tests. This is an execution engine,
not a standalone plugin authority. The Desktop keeps lifecycle admission, grants,
registrations, storage and business routes. Only import-free Wasm runs remotely.

A start/resume exchange yields typed Host calls to the original admitted context.
The wire retains the existing GeneratedDto codec and session-assigned numeric
command ID; it adds no Sidecar transport opcodes. The contract name versions the
execution payload. Create/start/resume/dispose use strict little-endian length
fields, guest/run UUIDs and monotonic continuation indices. No retry replays a
callback after an uncertain reply. A failed machine requires explicit reload.

Physical messages carry at most 48 KiB of data. Begin/append/execute/read/release
transfers enforce ordered offsets, declared lengths, SHA-256, a 26 MiB request
limit, 4 MiB reply limit, 64 MiB session aggregate, 64 transfer slots and five-minute
expiry. The endpoint permits 16 guests and the peer permits 32 in-flight requests.
Deactivation retires the engine; it cannot reactivate stale guests.

`python eng/package_worker.py --rid <rid> --signing-key <release-key>` publishes
NativeAOT and creates the `.nsc` and detached signature. Deploy its native Wasmtime
library beside the worker as well. Without a key this emits an unsigned development
artifact, never an installable production release. Protect the complete deployment
with the launcher distribution signature; a hash inventory is not authentication.

## Desktop authoring example

`eng/build_wasm_sample.py` also produces `desktop.zip` from `samples/WasmDesktop`.
It opens `page.nxml` with `Files.OpenPackageFile`, registers an owned page during
Initialize, and registers a user-confirmed action. In the Desktop implementation,
open the protected Plugins page and use **Load developer package** in developer
mode. The production worker must first be admitted as a signed
`Nexa.Plugin.Sidecar.nsc`. Unsigned plugin ZIP admission is an explicit developer
flow with a per-load permission confirmation; it is not Market verification.

The Host maps `UIModelUpdate.View` to the owned page's qualified ID string.
Models use registered ABI schemas; this initial Desktop projection accepts scalar
map fields. Page markup cannot bind private Host state, issue product commands or
load arbitrary images. `ui.plugin.action` takes an owned action ID as its Argument;
the Host invokes Actions.Invoke with the current model (Empty by default).
Stopping a plugin removes its page and invalidates file and business handles.

## Current business integration

The Desktop implementation now connects every operation in the current catalog to
an owner or a validated provider consumer. See the public SDK's
[business extension guide](https://docs.pcln.top/en/plugin-sdk/how-to/extend-business-features)
and [support manifest](https://github.com/Nexa-MC/NexaCL-Plugin-SDK/blob/main/contracts/host-support.json)
for request models, supported behavior and the exact verified Host commit.

Provider registration publishes a typed callback contract; it does not grant raw
paths, credentials or arbitrary execution. Query callbacks run under a read-only
probe fence. Effects run through owned operations, revisions, previews and Host
confirmation. Unloading the provider retires its callbacks and invalidates plans
that depended on the published definition. Host callbacks into the same plugin are
permitted only at the root; nested callback cycles remain rejected.

Instance and content changes run through the existing domain owners in both the
managed and native deployment. Restore/import require explicit owned destinations;
Java distribution installation verifies bounded ZIP contents and a SHA-256 digest;
launch contributions expose only the supported temporary settings. The public guide
describes these limits and failure cases rather than treating registration as execution.

## 简体中文

Desktop 负责准入、权限、业务路由和持久状态；独立 Wasm 工作进程通过现有认证
二进制通道执行插件。当前目录中的操作均已连接到业务所有者或实际消费 Provider
的入口。可用范围及对应宿主提交以公共 SDK 的 `host-support.json` 为准。

Provider 回调只收到声明的数据，查询回调不能借机产生业务副作用。修改操作仍须经过
宿主确认、版本检查和所有者服务；卸载插件会撤销回调并使关联计划失效。
完成接口接入不等于配置了生产 Market 凭据或发行签名。

### Theme configuration through the general Host

After discovering a `SidecarThemeOptionsClient`, bind it with
`PluginBusinessRoutes.BindThemeOptions(client)`. This supplies save, reset and
import operations without introducing a reference to Desktop or theme service
implementations. The existing guest route still resolves the caller's opaque
theme handle, checks its revision and asks for confirmation before invoking the
adapter. Import still reads through the caller's file grant.

`SidecarThemeOptionsEffects.Create(client)` is also available to composition and
contract tests. It returns three asynchronous effects. Each waits for the Host's
completion and preserves its result. No effect retries a lost or rejected reply.
The adapter maps option kinds and values to the neutral Host DTO namespace; it
never exports guest identifiers or a JSON command over the transport.

For import, the adapter accepts only `NTP-options/1`, with `themeId`, integer
`schemaVersion` and an object of option `values`. Duplicate or unknown fields,
missing required fields, invalid kinds, invalid strings and malformed JSON fail
with `SCHEMA_INVALID` before IPC. Documents above 1 MiB of UTF-16 characters fail
with `MESSAGE_TOO_LARGE`. Parsing allows depth 16, 256 options and 256 file entries
per option. The Host separately checks the imported header against its prepared
theme and performs authoritative option, revision and resource validation.

This is configuration transport, not preview ownership. `BindThemes` remains a
migration compatibility hook for the existing activation/revert delegates. The
production worker has not yet been switched to these business modules, and the
Host's old plugin controller must not be removed until the remaining ports and
single receive loop are composed and verified.

### Theme activation and preview retirement

Discover `SidecarThemeActivationClient`, then call
`PluginBusinessRoutes.BindThemeActivation(client, reportCleanupFailure)`. The
business module continues to own preparation plans, guest revisions, permission
checks, user confirmation and cleanup registrations. The adapter sends only source
identity, source revision, expected current revision and a local preview ID.
`SidecarThemeActivationEffects.Create` exposes the same two effects for composition
and contract tests. Supply a diagnostics callback: a failed compensating release
must not disappear silently.

Preview IDs are allocated with a process-wide monotonic counter before IPC. They
are distinct from the guest's opaque registration handle and from the Host's
native UI identity. Multiple client instances therefore cannot reuse a preview
ID on the same connection. On successful preview, the existing guest cleanup
registration stores that ID and ends it on release or guest retirement.

An unsuccessful or cancelled preview may already have changed the UI. The adapter
ends its known ID using a fresh five-second cancellation budget. It does this even
when the original request token is already cancelled. The Host treats end as
idempotent and fences an activation that arrives later. Failed compensation is
reported; activation is never retried. A persistent commit has different semantics:
it is never compensated merely because its reply was lost. Refresh theme state
before presenting the outcome or asking the user to submit again.

The Host also owns cleanup on Sidecar deactivation, disconnection and UI detachment,
covering process crashes where guest cleanup cannot run. A later user preview or
selection has its own native identity and survives cleanup of an older lease.
NXML page replacement does not end a theme preview. If the original committed
source is no longer valid, Desktop restores the built-in theme. If a user action
interrupts restoration and then fails, cleanup rechecks its native identity and
resumes restoration. A successful user replacement remains intact. The worker's
production receive loop and application composition remain separate migration work.

### Acknowledged guest notifications

Create `SidecarNotifications` with the authenticated `SidecarHostRequestClient`
and dispatch observer. `CreateAsync` discovers the separate general Host catalog;
it fails when that catalog is unavailable. Pass `notifier.NotifyAsync` to the
asynchronous `PluginApplication` constructor. Existing synchronous notification
callbacks remain supported for embedders during migration.

The `UI.Notify` and `Platform.Notify` routes remain active-phase mutations. The
local runtime checks their respective permissions and required idempotency key.
The application rejects notification actions and localization arguments before
calling the adapter. Title and body keep their existing guest limits of 256 and
4096 UTF-16 code units. The adapter adds the admitted plugin ID to the title and
sends an informational notification. Literal text, including markup-like content,
is not executed by the Host. Control characters other than newline, carriage
return and tab fail before IPC.

The route awaits Host acceptance; it no longer reports success solely because a
local callback scheduled delivery. Host acceptance does not mean a frame has been
painted. Cancellation and guest retirement cancel the pending call. A notification
already accepted by the Host may still appear if its reply is lost or cancelled,
so the adapter never retries. Runtime idempotency prevents a repeated completed
invocation with the same key from publishing twice; callers must not create a new
key merely to replay an uncertain effect.

This adapter is verified through the real application/guest route against an
independent binary Host fixture. Production worker composition, environment,
clipboard and advanced presentation migration remain outstanding. Native file selection now has an independent general Host adapter, described below.


### Native file selection

Create a `SidecarFilePickerClient` from the same bidirectional request client as the
other Host adapters. Call `DiscoverAsync` and check its result before passing the
client to `business.BindFilePicker(client)`. The worker must keep its single receive
loop running while guest invocations await the dialog. Binding the file-picker
client does not require a clipboard binding.

Both `Files.Pick` and `Platform.PickFile` use this adapter. The guest ID prefixes the
literal title, and Desktop adds its own verified Sidecar package label. The combined
guest title must fit 256 UTF-16 code units; localization arguments and control
characters are rejected. Zero to 32 extension filters are accepted, with at most
32 ASCII letters, digits or dots per filter. A filter consisting only of dots is
invalid. Open, directory and save selections retain their existing ABI enum values.

The Host returns strict UTF-8 path bytes, or null when no local item is selected.
A byte field retains long Unicode paths without exceeding the general protocol's
string limit. The adapter validates the result and rechecks the invocation token
before creating an owner-scoped writable grant. It never exposes a Host path as a
guest handle directly, and another guest cannot use the resulting grant.

User dismissal becomes guest `CANCELLED`. Native unavailability or admission
backpressure does not trigger another dialog automatically. Cancellation and guest
stop end the pending invocation; a late selection is drained without creating a
grant. On the Host, the native dialog keeps its single admission slot until it
actually closes, even after cancellation or UI rebinding.

Tests use the frozen Host binary contract and the real business/application owner
to verify source labels, grant isolation, user dismissal and stop during a pending
query. Adapter tests additionally exercise invalid filters, malformed UTF-8,
paths exceeding 32 KiB and no retry after rejection. Production worker composition
still remains a separate migration step.

### Clipboard

Discover `SidecarClipboardClient` on the same bidirectional request connection, then
call `business.BindClipboard(client, reportCleanupFailure)` once. The Host must
negotiate `LargeGeneratedDtos`. File selection and clipboard bindings are independent.

The adapter allocates a transfer ID before sending a begin request, serializes
begins, verifies strict UTF-8 and PNG metadata, and transfers images in 256 KiB
chunks. It preserves the existing 65,536 UTF-16 text and 16 MiB image limits.
SHA-256 checks protect the assembled snapshot; the Host's native backend validates
the complete image before writing. Null and empty text remain distinct.

Guest permissions, per-action confirmation and blob ownership remain in the
business/application layer. Rejected confirmation causes no clipboard IPC. Foreign
blob handles cannot be written. A stopped guest cannot acquire a late read result.
Every known transfer ID is released, including after a lost begin reply, using an
independent one-second cleanup budget. Report cleanup failures without replacing
the original operation result; the Host also expires transfers after two minutes.
Do not retry a commit after cancellation or a lost response: it may already have
changed the clipboard. The worker receive loop must keep processing replies and
cancellation while awaiting a native operation.

This adapter is covered by a frozen binary Host fixture and application/guest
ownership tests. It does not yet replace the deployed Host plugin controller;
production worker composition must complete the remaining generic UI contracts.
