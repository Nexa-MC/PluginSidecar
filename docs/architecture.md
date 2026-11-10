# Architecture

This repository owns the future plugin execution process and its experimental lifecycle contracts. NexaCL owns Host supervision, transport/protocol compatibility and renderer integration. No launcher source is copied into this repository.

The current CLI has no Host connection and performs no plugin discovery or loading. `IPlugin` is an in-process lifecycle abstraction for statically registered modules, not a serialized object exchange or a stable SDK.

Future Host integration must use upstream versioned binary frames, generated numeric dispatch, asynchronous cancellation and explicit resource retirement. Do not introduce synchronous IPC, JSON hot paths, reflection dispatch or cross-process CLR objects. UI declarations render in Host; plugins never manipulate Host UI objects directly.

NativeAOT excludes arbitrary managed DLL loading from this initial design. External plugin execution, permissions and isolation require a documented design and validation before implementation. Process isolation alone does not restrict filesystem or network access.

Upstream reference: [NexaCL Sidecar](https://github.com/Nexa-MC/NexaCL/blob/main/docs/sidecar.md). Pin a reviewed upstream revision before adding protocol dependencies.
