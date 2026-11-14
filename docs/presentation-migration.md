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
management UI, workspace contributions/replacement, dialogs, shortcuts and drawing
remain to be composed and verified. Ordinary-page content contributions and
whole-view replacement are implemented as described below. Production end-to-end verification is still
required before removing the old Desktop controller or publishing SDK Beta.

## Owning publication

`PluginPagePublisher` is the sole presentation writer for its Sidecar session.
Call `SynchronizeAsync` after activation, model changes, registration retirement
and plugin stop. It publishes at most 32 ordinary pages, sorted by qualified page
ID. It validates the complete candidate against the Host's source/model budgets
before sending it. It composes active, same-load content contributions locally;
workspace contributions/replacement, dialogs and drawing remain outside this
ordinary-page publisher.

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
page declaration and unchanged contribution ownership before invoking the ordinary action pipeline. Its result is an
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

## Confirming an action

`SidecarActionConfirmation.CreateAsync` discovers the general Host confirmation
catalog. Pass the resulting `IActionConfirmation` to `PluginApplication` and bind
it to `PluginBusinessRoutes`. This adapter only asks for a decision; the existing
action coordinator continues to enforce caller/provider identity, input binding,
permissions, idempotency and cancellation. It does not execute or retry an action.

`PluginConfirmationText` formats the caller, provider, action, title, target and
complete inputs as literal text. Map keys are sorted ordinally; numeric values
use invariant formatting, and string control characters are escaped. Byte values
are represented exactly as hexadecimal. Descriptions exceeding 65,536 characters
or 16 nested containers are rejected instead of being truncated. The Host
independently validates UTF-8 and display budgets and owns its accept/cancel
controls. Plugin-supplied text cannot replace an existing Host modal.

A successful decline returns `false`. Unavailable catalogs, a busy UI and failed
Host requests cannot become approval. Caller cancellation is propagated, and a
retired Host dialog cannot be reused for a different request. The adapter relies
on the worker's single receive loop to deliver replies while it waits.

Tests exercise the real Wasm action coordinator through the generated confirmation
client using a frozen Host wire fixture. They cover decline, rejected requests,
cancellation before sending, literal text formatting, oversized inputs, explicit
approval and terminal action completion. The Host repository separately exercises
actual dialog rendering/clicking and cancellation when its Sidecar connection
retires. Production worker composition is still pending.


## Ordinary-page content contributions

Use `UI.RegisterComponent` to register a package-owned `nxml-dynamic` asset, then
`UI.RegisterContribution` to target the owning plugin's page and its `content`
node. The application's registration and ownership checks still apply. During
synchronization, the Sidecar resolves active components and combines their markup
with the page before applying key and action namespacing. The Host receives a
normal page; it never receives plugin registrations or contribution DTOs.

The contribution uses the page model. `Append` and `Prepend` insert a component
at the corresponding end of the page's children. `Hide` clears those children;
`Replace` replaces the children with the component. `Wrap` places existing children
after a wrapper's own children and requires a supported layout/element container.
These modes target page content, not protected Host navigation or confirmation UI.
Whole-view replacement is a different operation, described in the next section.

`Before` and `After` form a dependency graph for that target. References to IDs
outside the target do not affect its order. Available nodes use ordinal ID order
as the tie-breaker; duplicate edges do not change ordering. Cycles and self-edges
fail before publication. At most 256 contributions may target a page. Compilation
retains the shared 16384-node/128-depth bounds and the final page's 256 KiB UTF-8
source limit, in addition to the publisher's aggregate limits.

A component/contribution/replacement handle change invalidates the owner's published page
actions and navigation immediately. The next successful synchronization replaces
the source set and rotates action epochs even if the markup is unchanged. This is
conservative: a registration affecting another page of the same owner also causes
republishing. Changes for a different owner do not invalidate that owner's actions
before synchronization. As with any source-set replacement, acknowledged
republishing rotates all page epochs in the shared session.

Validation covers all five modes, deterministic ordering, cycles, component
ownership, forbidden Host commands, model text and contributed form/action
translation. A real Wasm fixture removes a contribution while its page and load
remain active: old navigation is rejected before synchronization, and old action
tokens remain rejected after the source set is republished.


## Ordinary-page whole-view replacement

`UI.RegisterViewReplacement` selects another active page owned by the same plugin
load. Register both pages first, then register a `UIViewReplacement` with the
original page ID in `View`, the target page ID in `Page`, and the original page's
schema in `ViewContract`. Registration occurs during `Initialize`; both page
schemas must match. Only one replacement may target a view. Removing a target
page while its replacement is registered fails with `CONFLICT`; remove the
replacement definition before removing the target page.

The publisher passes its complete active page snapshot to
`PluginViewCompiler.Prepare(page, advancedUi, activePages, publicationEpoch)`.
Callers preparing a replacement directly must also supply that snapshot. The
shorter overload has only the supplied page available; it cannot resolve a
replacement referring to another page. A missing, foreign-load or schema-mismatched
target fails locally before Host publication. Workspace replacement is not yet
part of this publisher.

A replacement changes the rendered page, not the logical page's identity. Its
markup reads the **target page model**. Contributions registered for the original
page are applied afterward and read the **original page model**. Replacement is
one hop: replacements and contributions registered for the target page are not
recursively applied to the original page. The target also remains independently
publishable as an ordinary page.

For example, if both models contain `text`, the replacement's `{data text}` reads
the target value and an appended component's `{data text}` reads the original
value. This applies to mixed text such as `Status: {data text}`, data-bound actions
and inverted visibility bindings. The Sidecar assigns private aliases to target
bindings; values containing XML or `{data ...}` remain literal data. Components
cannot consume these aliases. A collision with the generated reserved prefix in
the original model is rejected, never overwritten.

Action translation retains the original page's ID, context and typed model.
Submitted form drafts overlay that model, even when the input controls came from
the replacement. Target-only data and private render aliases are not copied into
guest action inputs. This preserves the existing logical-page contract rather
than silently changing the action's input schema.

Changing target data updates the Host's model while keeping source documents,
control keys and drafts stable. Until synchronization installs that projection,
action and navigation admission reject the previous target revision. A replacement
registration change invalidates the owner's page projections immediately and
forces an epoch rotation, even if the resulting markup would be identical. When
replacement registration is removed, synchronization restores the original source.
Lost acknowledgements use the same conservative recovery as ordinary publication.

Verification includes distinct render/contribution/action models, mixed text,
inverted visibility, data-bound action changes, missing and foreign targets,
schema mismatch, duplicate replacements and private-alias access. A compiled Wasm
fixture registers two pages and a replacement through the frozen SDK, invokes an
action that returns the original model, updates the target without replacing Host
sources, and retires the replacement and target while checking stale admission.
These tests also run as a native executable; no launcher presentation assemblies
are loaded.
