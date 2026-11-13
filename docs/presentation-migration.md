# Guest presentation ownership

`PluginSidecar.Presentation` translates plugin pages into the general NXML and
form contracts published by NexaCL. It is an internal runtime component, not a
new guest SDK. The ABI 100 models, operation numbers and guest-facing schemas
remain unchanged. The worker has not yet switched to this presentation owner.

## Preparing a page

Pass `PluginViewCompiler.ValidatePage` and `ValidateComponent` to
`PluginApplication` when composing it. Registration validates the source with the
canonical parser. When a page becomes active, call `Prepare` with the application's
`PluginPage` snapshot. Preparation compiles the original page against scalar model
values, checks the expanded result, rewrites identifiers and actions, and compiles
the resulting document again.

Both source and expanded IR are checked. Guests cannot bind Host state, access
images, invoke product commands or introduce scroll/dismiss commands. Data values
are passed to the compiler as data; a value containing XML or binding syntax is
never reparsed as source. Numeric formatting is culture independent. The shared
compiler's node, source, dynamic-data and layout limits still apply.

The resulting `PluginPreparedView` contains a general `SidecarNxmlDocument` and a
read-only model. Its page key derives from the plugin load identity and page ID.
Local control keys and anchor references occupy the same private key space, so
separate pages cannot accidentally address each other's controls. Static keys
remain static: they do not consume dynamic-data entries. Changing ordinary model
values or a data-bound action retains the same source document; the publisher can
use the Host's model-update operation to preserve input drafts.

## Receiving an action

Register `PluginViewCompiler.ActionCommand` as a generated DTO command and
negotiate `UiForms`. The Host submits only the invoking exposure's input drafts.
It sends the action's opaque token and scoped input keys, not a guest action
request or a plugin identity.

Resolve the token against the current published projection, check that its owner
is still in `PluginApplication.Runtime.Active`, and call `TranslateAction` with
that active identity. Translation validates the binary form, rejects foreign keys
and tokens, restores guest input names and overlays drafts on the original typed
model. It returns the ordinary SDK `ActionRequest` for the application's existing
action pipeline. Confirmation and asynchronous task completion remain in that
pipeline; accepting an action request does not mean its callback has completed.

The publisher must discard its projection and token map when the corresponding
load or source exposure retires. A prepared object alone is not proof that a page
is published or its plugin is active. A reloaded plugin receives a new load
identity, and its old tokens cannot be translated by the new projection.

## Verification and remaining integration

`PluginSidecar.Presentation.Tests` runs in managed and NativeAOT builds. It checks
model preservation, source stability, control/action scoping, forged and retired
identities, forbidden markup, and a real Wasm action through confirmation and
terminal task completion. The test also rejects any loaded Desktop, Avalonia or
`Nexa.Services` assembly.

The ordinary-page publisher below is implemented. Production worker composition,
management UI, dialogs, contributions, shortcuts and drawing remain to be composed
and verified. These tests
do not authorize removing the old Desktop controller or publishing SDK Beta.

## Owning publication

`PluginPagePublisher` is the sole presentation writer for its Sidecar session.
Call `SynchronizeAsync` after activation, model changes, registration retirement
and plugin stop. It publishes at most 32 ordinary pages, sorted by qualified page
ID. It validates the complete candidate against the Host's source/model budgets
before sending it. Advanced contributions, dialogs and drawing are not included
in this ordinary-page publisher.

The publisher distinguishes source replacement from model updates. Source-set
changes rotate a publication epoch for every page, so delayed commands from an old
source set cannot resolve to a new action. Model updates keep the epoch, source
and control identities, preserving drafts. Typed model changes that produce the
same display text still replace the local action model without a redundant Host
write.

A mutation may have reached the Host even when its response is lost or cancelled.
In that case, the publisher stops admitting actions and discards its assumed Host
revision. The next explicit synchronization rereads the revision and replaces the
complete source set with a new epoch. It never automatically replays navigation
or a guest action. Recovery therefore does not mean the previous action ran twice.

Use `SetOpenAsync` with the exact active `PluginIdentity` and page ID. The
publisher resolves them locally to an owned document index. Periodically call
`ReportVisibilityAsync` to feed actual Host visibility into the application's
existing view lifecycle subscriptions. Route generated DTO command payloads to
`InvokeActionAsync`; it checks the current published token, active load and active
page declaration before invoking the ordinary action pipeline. Its result is an
`OperationRef`; observe that operation for terminal success or failure.

Disposing the publisher cancels outstanding Host work and rejects late callers.
The process/session owner must also retire the Host session to remove its pages.
Do not create two publishers for one session, or combine a publisher with another
writer of that session's presentation revision.

Publisher tests use a simulated Host port with two real Wasm guests. They cover a
committed replacement with a lost reply, explicit recovery, retired publication
tokens, model-only publication, foreign navigation, plugin stop and cancellation
of an outstanding visibility query during disposal. A separate frozen wire fixture
exercises the actual generated Host clients, including discovery, document/model
encoding, navigation, visibility and rejected operations, in managed and NativeAOT
builds. The launcher repository separately tests those catalogs against its real
renderer. These checks do not replace a production worker-to-launcher test. This component is not yet the production
worker composition. Management UI, advanced UI, platform ports and release
packaging remain prerequisites for removing the legacy Host plugin controller.

`SidecarPresentationHost.CreateAsync(requests, observer, token)` discovers both
view-state and page-control catalogs and returns the generated binary adapter for
`PluginPagePublisher`. Use it with shared packages `0.1.0-alpha.9`. The worker's
single receive loop must route Host replies to `SidecarHostRequestClient` while
publisher work runs outside that loop. The adapter sends documents, UTF-8 model
fields, revisions and document indices only; it does not send guest DTOs.
