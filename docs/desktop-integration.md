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
