# WebAssembly guest SDK

The production code profile is `nexa-wasm-1`: one WebAssembly module with no
imports. All effects go through negotiated public Host capabilities. The managed
SDK is the contract/adapter SDK; ordinary CLR plugin DLLs are trusted development
only.

## Build the C example

Use Clang with the WebAssembly target and `wasm-ld` (or the official WASI SDK's
Clang; the build explicitly selects `wasm32-unknown-unknown` and links no WASI):

```sh
python eng/generate_abi100.py --verify
python eng/build_wasm_sample.py --clang /path/to/clang
NEXA_WASM_SAMPLE=artifacts/wasm-sample/plugin.zip dotnet run --project tests/PluginSidecar.Tests -c Release
dotnet publish tests/PluginSidecar.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -o artifacts/aot-smoke
NEXA_WASM_SAMPLE=artifacts/wasm-sample/plugin.zip ./artifacts/aot-smoke/PluginSidecar.AotSmoke
```

`sdk/wasm/include/nexa/guest.h` supplies bounded value views, field access,
writers, continuation calls and lifecycle completion. `abi100_ids.h` is generated
from the same operation catalog as the C# SDK. It contains local method numbers,
not Sidecar transport opcodes. `samples/WasmHello/plugin.c` registers a definition
during Initialize, awaits the Host reply, and propagates registration failure.
The test Host explicitly installs its message schema policy and reads the
published definition; no arbitrary consumer is inferred from registration.

The ZIP is an unsigned **developer artifact**. Production acquisition must
authenticate its bytes through Market and the protected Host identity before
`VerifiedPluginPackage.Parse` is called. Never expose that trusted parsing method
as an unsigned user install route. Tests label development trust explicitly.

## Execution and values

The module exports `memory`, `nexa_alloc(i32)->i32`, and
`nexa_resume(i32,i32)->i64`. The runtime copies the encoded input to the allocated
address, resumes the guest and copies its reply. The result's high 32 bits are
the reply address; the low 32 bits are its length. Allocation and resume share
one fuel allowance. Borrowed views must not survive a resume call. The example
uses 64 KiB buffers; the runtime permits up to 4 MiB per encoded value.

The closed binary codec starts with little-endian UInt32 version `1`:

| Tag | Value |
| --- | --- |
| 0 | Null |
| 1 | Boolean byte, exactly 0 or 1 |
| 2, 3, 4 | Little-endian Int64, UInt64, finite Float64 |
| 5, 6 | Int32 byte length followed by strict UTF-8 or raw bytes |
| 7 | Int32 count followed by values |
| 8 | Int32 count followed by UTF-8 keys (length + bytes) and values |

Limits: 32 levels, 16,384 value nodes, 65,536 characters per string, 4 MiB total.
Negative lengths, duplicate keys, invalid UTF-8 and trailing bytes are rejected
by the runtime. Canonical Host encoding sorts keys ordinally. UUIDs are `D`
strings and timestamps are invariant `O` strings. This is the guest payload,
not a new IPC frame.

Lifecycle input contains `kind=callback`, `phase`, `pluginId`, `loadInstanceId`
and `correlationId`. Output is either:

- `kind=complete`, `value={success: bool, code?: string}`;
- `kind=invoke`, `contract`, `method`, `arguments`, optional `operationId`,
  `idempotencyKey` and `expectedRevision`.

An invoke suspends guest execution while the Host runs asynchronously. The next
input is `kind=reply`, `success`, `value` and structured `error`. Actual identity,
stage, scope and remaining deadline come from the Host. Guests cannot override
these by adding fields. Mutation keys are bound to their inputs; unknown outcomes
must remain explicit.

## Static content

`nexa-content-1` uses `declarativeEntryPath` and no code entry. Its payload is
`AbiValueBinary.Encode(DeclarativeContentPack.Write(pack))`, with
`pack.Schema = Abi100Schemas.DeclarativeContentPack`. The signed manifest must
request `nexa.definitions.write` and require `nexa.definitions` version 1.
`nexa-wasm-1` may include the same entry beside its code. Static and code
registration share context, validation, rollback and group commit.

Schema fingerprints include each model's transitive model/enum closure and
codec/scalar contract version. `Abi100Schemas.Validate` uses static readers and
checks exact ID/version/digest. Custom business schemas still require a trusted
schema policy or a future validated dynamic schema implementation.
