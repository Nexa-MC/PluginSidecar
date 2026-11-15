# Native Host integration

PluginSidecar owns guest admission, permissions, lifecycle, business translation,
offline installation records and presentation. NexaCL exposes generic native
capabilities and remains usable without this process. The production executable
selects `StandalonePluginRuntime`; it does not ask a Desktop plugin kernel to
interpret yielded guest calls. [Worker composition](worker-composition.md)
describes the complete startup and retirement order.

## Build and verify the boundary

Run `python eng/prepare_host_reference.py` before building. It verifies the public
shared packages pinned in `eng/references/nexacl.json`. The Sidecar does not
reference launcher implementation assemblies. Generated numeric binary catalogs
carry public DTOs, and each native effect is scoped to the authenticated session.
Credentials remain in their native owners; guest capabilities and opaque handles
remain in the Sidecar.

The real-process fixture starts the actual executable through the existing
bootstrap challenge, admits its registrations, discovers native catalogs, imports
a real Wasm package, invokes a confirmed action, disables it and rejects an old
action token. It then checks graceful shutdown. CI repeats this against the
NativeAOT worker. These fixtures are not a production Market acceptance test.

## Developer packages

`eng/build_wasm_sample.py` produces `desktop.zip` from `samples/WasmDesktop`.
The sample registers an owned page and a user-confirmed action. Once an admitted
Worker has published its navigation, enable developer mode and choose **Load
developer package**. Review the package and requested grants. Loading once and
saving an approval for later restoration are separate choices.

The Sidecar compiles guest markup, scopes keys and inputs, and translates actions
into the guest ABI. Private Host state bindings, arbitrary product commands and
unowned resource references are rejected locally. Stop removes the guest's views
and invalidates its handles. Offline restoration revalidates recorded bytes and
grants; developer packages require developer mode. This flow is not Market
publisher or entitlement verification.

## Deployment limits

`eng/package_worker.py --rid <rid> --signing-key <key-id>` builds a NativeAOT
candidate. Native Wasmtime and Skia dependencies must be deployed with the exact
worker that uses them, with complete integrity and signature coverage. A lone
unsigned image or hash inventory is not a production installation. The coordinated
launcher cutover removes its embedded SDK and fixed plugin navigation; signed
release integration and production Market acquisition are still pending.

## 简体中文

插件准入、权限、业务翻译、离线记录和界面全部由独立 Sidecar 负责。启动器仅提供
通用原生 ABI，组件缺失或失败不影响普通宿主功能。生产源码入口已切换到独立运行时；
真实进程测试覆盖认证、目录发现、Wasm 导入、确认操作、停用和退出。
开发包需要开发模式及明确批准，与 Market 的发行者和权益验证是不同流程。
完整原生依赖的签名打包和生产 Market 获取链路仍需完成发布验证。

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
