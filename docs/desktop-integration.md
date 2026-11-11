# Desktop integration (alpha.5)

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

This adds an actual Desktop vertical slice; it does not claim every SDK operation
has a Host mapping. Production Market admission/entitlements, persisted enablement,
UI contributions/replacement/shortcuts/drawing and the remaining domain methods
still require separate mappings. Unsupported operations stay unavailable.

## 简体中文

alpha.5 使用现有 Sidecar 认证和二进制通道运行独立 Wasm 工作进程。Desktop
持有准入、权限、状态和业务路由。连接失败后不自动重放插件操作。
新增 `Files.OpenPackageFile` 只返回已验证包内资产的实例级引用。
`desktop.zip` 示例覆盖包内页面注册、实际页面展示与宿主确认后的操作。
开发包必须在开发者模式下逐次确认权限；生产工作进程仍要求 `.nsc` 发行签名。
尚未映射的 UI/业务接口继续返回不可用，不能把本次接入等同于全部功能完成。
