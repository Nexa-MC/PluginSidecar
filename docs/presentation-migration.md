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

The publisher below implements ordinary pages, content contributions and
whole-view replacement. When explicitly configured, it also publishes Sidecar
management controls and isolated workspace extensions. The management controller
and native interaction adapter now implement import, consent, offline restoration
and disable. The same publisher now owns guest dialogs when supplied with the
modal-control port and publishes scoped shortcuts through the shortcut-control
port. Production Worker composition remains to be completed. Production end-to-end verification is still
required before removing the old Desktop controller or publishing SDK Beta.

## Owning publication

`PluginPagePublisher` is the sole presentation writer for its Sidecar session.
Call `SynchronizeAsync` after activation, model changes, registration retirement
and plugin stop. Ordinary pages are sorted by qualified page ID. At most 32
documents may be published, including management, navigation and workspace
fragments when management is enabled. It validates the complete candidate against the Host's source/model budgets
before sending it. It composes active, same-load content contributions locally.
Workspace composition is enabled with the management options described below;
modal publication is enabled with the separate control port described below.
Shortcut callbacks use the separate shortcut-control port described below.
Drawing uses the native raster port described below.

The publisher distinguishes source reconciliation from model updates. With
`IPluginRetainedPresentationHost`, each ordinary page and workspace has its own
publication epoch. Adding, removing or reordering another view retains unchanged
sources by their previous indices. A source or ownership change rotates only the
affected view's keys and action tokens. The trusted manager and its navigation
share a target; replacing the manager also replaces workspace fragments mounted
under that target. Unrelated pages retain their drafts and pending submissions. Previously reported
visibility is carried forward only for retained ordinary pages, so rebuilding the
manager does not generate a false hidden/open lifecycle transition.

Model-only updates keep epochs and source identities. Typed model changes that
produce the same display text still replace the local action model without a
redundant Host write. Ports implementing only `IPluginPresentationHost` preserve
the earlier whole-set replacement behavior, including rotating every view's epoch.
Native `SidecarPresentationHost.CreateAsync` discovers the ViewReconcile catalog
in addition to ViewState and PageControl; a missing catalog fails creation.

A mutation may have reached the Host even when its response is lost or cancelled.
In that case, the publisher stops admitting actions and discards its assumed Host
revision. The next explicit synchronization rereads the revision and replaces the
complete source set with fresh epochs. It never automatically replays navigation
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
committed replacement or reconciliation with a lost reply, explicit recovery, retired publication
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

A component/contribution/replacement/command handle change invalidates the owner's published page
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
target fails locally before Host publication. The management-enabled publisher also resolves workspace replacement against
the same active-page snapshot.

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


## Sidecar management and workspace publication

Construct `PluginPagePublisher` with `PluginManagementOptions` to opt into this
owner. Omitting the options preserves the ordinary-page-only mode. The Worker
supplies a current-policy reader, an `IPluginManagementActions` implementation,
localized labels and, optionally, a general navigation anchor. The default anchor
is `#NavigationSettings`. These are trusted composition inputs, not guest SDK
operations. The deployed Worker has not enabled this owner yet.

The publisher includes a management page and a navigation item before the anchor.
The page lists known installations, their states, active-load disable controls and
links to ordinary pages. Each active load with workspace content or registered
commands receives a separate NXML fragment mounted into a private manager slot.
Everything uses the existing generated Host presentation and page-control clients;
the Host receives no plugin status, registration or management DTO.

Register two distinct generated-DTO commands in the Worker:

| Command | Receiver | Meaning |
| --- | --- | --- |
| `PluginViewCompiler.ActionCommand` | `InvokeActionAsync` | An admitted guest action from its own published page or workspace exposure. |
| `PluginPagePublisher.ManagementCommand` | `InvokeManagementAsync` | An opaque management token from the current Sidecar management page. |

A guest source may emit only `ui.plugin.action`, which the compiler rewrites to
the first command. It cannot author management commands. The manager rejects
forms containing any input fields; guest forms cannot masquerade as package
management. A management token sent to the guest dispatcher, or a guest token
sent to the management dispatcher, is rejected.

`PluginManagementPolicy` expresses developer mode, safe mode, engine readiness,
a busy flag and literal status text. Import is available only in developer mode,
outside safe mode, with a ready engine and no pending operation. The publisher
checks the policy again on each click, even before the next visual refresh.
Disable captures the exact load identity; a stopped or reloaded identity cannot
reuse its button. A page link resolves only to a current ordinary page and checks
its registration dependencies before opening it. `OpenManagerAsync` opens the
manager through the same revision-fenced page-control channel.

The effects port is intentionally separate from rendering. Its production
implementation must perform native file selection, package verification, explicit
permission consent and offline-installation updates, then recheck policy after
user interaction. Disable must use `PluginApplication.StopLoadAsync(exactLoad, token)` and update
its offline records through the single installation owner. The application checks
the exact identity under its load/stop gate and returns all identities retired by
dependency shutdown. A stale stop queued behind a reload fails without touching
the new load's assets or registrations. A successful button
admission is not proof that any of these effects completed. The publisher permits
one import/disable effect at a time, passes cancellation through and never
automatically retries an uncertain effect. The Worker must observe and drain these calls as part of its
lifetime supervision. The tests use a controlled effects port; they do not claim
that production import or offline restoration has switched owners.

The approved-package transaction, management controller and remaining production requirements are
described in [Installation ownership migration](installation-migration.md).

Workspace content uses the stable logical view `nexa:plugins/content` and an empty
original model for each load. Ordinary contribution ordering and replacement
schema checks apply. A replacement target therefore needs the workspace's
`Empty` schema. Contributions modify only that load's content. Registered command
buttons are added afterward, outside the replaceable content; hiding or replacing
content cannot remove them or any management control. Command labels are literal
model data and command actions receive empty inputs, preserving their existing
contract. Ordinary contributed actions receive their own exposure's drafts.

Fragments have separate scoped keys, tokens and form exposures, even though their
logical workspace view ID is shared. They cannot be opened with `SetOpenAsync`,
and they are not reported as ordinary page visibility. Their source and model
changes participate in the publisher's single revision and epoch protocol.
Management, navigation, pages and fragments together consume the 32-document,
4 MiB source and 4 MiB model budgets; exceeding a limit rejects the entire candidate
before a Host write.

Tests compile and mount workspace fragments with the shared renderer, verify
separate form ownership and protected command controls, then exercise a real Wasm
command through the publisher. Management tests cover policy changes before
refresh, wrong-channel tokens, forged fields, stale load/page controls, lost
publication acknowledgement, duplicate clicks and cancellation of a pending
effect. No Desktop, Avalonia or `Nexa.Services` assembly is loaded.

## Modal control transport

`SidecarDialogHost.CreateAsync(requests, observer, token)` discovers the separate
DialogControl catalog. `SetOpenAsync(revision, document, open, token)` opens or
closes a committed modal document; `ReadAsync(revision, token)` returns validated
source indices, targets and open states. Negotiate `Nxml` and `Dialogs`, and publish
`SidecarNxmlOperation.Dialog` sources before opening them. A source index includes
ordinary pages and fragments earlier in the same source set.

Use the acknowledged presentation revision and serialize these calls with source
and model updates. Discovery failure, a rejected request and malformed state are
errors; caller cancellation remains cancellation. The adapter never retries an open
command automatically. After an uncertain reply, query state before deciding
what to do. The Host may already have opened the window.

This adapter is tested against the generated binary protocol, including malformed
state, cancellation and lost acknowledgements. Pass it to the sole publisher to
enable guest dialog publication and native-close reconciliation. Creating the
adapter alone does not enable dialogs or switch the production Worker owner.

```csharp
var dialogs = await SidecarDialogHost.CreateAsync(requests, observer, token);
await dialogs.SetOpenAsync(presentationRevision, documentIndex, true, token);
var state = await dialogs.ReadAsync(presentationRevision, token);
bool open = state.Single(item => item.Document == documentIndex).Open;
```

The state query does not reserve the window. A user may close it immediately after
the query completes. New sources start closed, model updates preserve open state,
and source replacement or session retirement closes old windows. The Host limits
open modals to eight per Sidecar session and sixteen globally, and rejects opening
a new modal while a Host-owned confirmation or onboarding flow has priority.
Do not convert a transport error into a synthetic closed state: that could retire
a guest dialog whose window is still open.


## Retained publication transport

`IPluginRetainedPresentationHost.ReconcileAsync(revision, views, previousDocuments, token)`
sends the complete ordered candidate set. Each mapping entry is a unique prior
source index, or `-1` for a new source. The native adapter rejects mismatched
counts, duplicate retained indices and indices outside the 32-document limit
before sending. Retained candidates send no source bytes; every candidate sends
its complete model. Only the single publisher may decide which acknowledged
sources are identical and safe to retain.

The Host validates the session revision and complete candidate before committing.
A successful command advances the revision once. All queued controls using the
previous source-set snapshot retire, while retained documents keep their action
lifetimes. Their new indices must be used for subsequent navigation or modal
control. Retention never reopens a native-closed modal. The same serialization
gate must own future dialog publication and close-state reconciliation.

Managed and NativeAOT tests exercise the frozen binary catalog and real Wasm
pages through both legacy replacement and retained publication. They cover
reordering after another load appears, unchanged action tokens, contribution
retirement, manager/workspace dependencies, malformed index maps and explicit
fresh-source recovery after a lost reconciliation acknowledgement.


## Preparing a guest dialog

`PluginDialogCompiler.Prepare(dialog, advancedUi, activePages, publicationEpoch)`
uses the same source, contribution and replacement validation as ordinary pages.
It returns a `PluginPreparedView` with `DialogReference` set and a general NXML
`Dialog` document. Its opaque target, keys and action tokens include the exact
reference ID and view scope as well as the load identity and publication epoch.
A form captured from the ordinary page, a different dialog scope or a previous
publication epoch cannot be translated by this projection.

The logical action target remains the original guest page. Drafts overlay the
original typed dialog model, even when a replacement page supplies the rendered
content. Model-only updates keep source identity; typed values are refreshed in
the local action model. The compiler does not publish or open the dialog. The
single publisher checks exact-reference lifetime and reconciles native closure
when its modal-control port is configured. Production Worker composition still
needs to select that owner.


## Publishing and retiring dialogs

Compose one publisher with both native ports:

```csharp
var views = await SidecarPresentationHost.CreateAsync(requests, observer, token);
var modals = await SidecarDialogHost.CreateAsync(requests, observer, token);
await using var publisher = new PluginPagePublisher(application, views,
    management: managementOptions, dialogs: modals);
await publisher.SynchronizeAsync(token);
await publisher.ReportVisibilityAsync(token);
```

The process owner must supervise subsequent synchronization after application
changes and periodically while dialogs are open, so native closure reaches the
guest lifecycle subscriptions. All publication and modal-control work uses the
publisher's one gate. The modal port requires retained presentation; a legacy
whole-set-only port cannot be used for modal publication. The ordinary page
navigation and visibility methods never select a modal projection.

A new dialog is compiled and published closed, then receives one open attempt.
Its original page and another dialog keep independent targets, input keys and
action tokens. Model-only updates preserve the window and drafts. A guest action
is admitted only while the exact reference, owner load and model revision remain
current. The original typed dialog model is used even if another page replaces
its rendered content.

Before each synchronization, the publisher reads native state at the acknowledged
revision. The response must describe every published modal exactly once with the
matching index and target. Invalid, incomplete or failed responses leave guest
lifetimes intact. A valid closed state for an attempted dialog closes that exact
application reference; its lifecycle subscribers receive `closed`. A native close
that occurs after the read is preserved by source retention and is observed on
the next synchronization. It cannot cause an implicit reopen.

An open command may have committed before its reply is lost. The publisher marks
the attempt before sending and preserves its acknowledged publication on control
failure. The next synchronization queries actual state; it does not replay the
open. A definite failed open is also terminal for that reference once native
closed state is observed. Callers must surface synchronization failures and keep
state reconciliation supervised instead of swallowing them.

If a source/model mutation has an uncertain result, action admission is sealed
and explicit recovery uses fresh sources. Previously attempted dialogs are not
reopened during recovery; their new closed state retires the logical references
on the next state read. New references that have never received an open attempt
may open after their source is acknowledged. This deliberate recovery behavior
prevents a previously closed window from returning after a lost acknowledgement.

Changing or retiring a dialog's contributing source retires that dialog instead
of reopening a replacement window from an earlier state snapshot. Contributions
are tracked for that specific view; unrelated commands and components from the
same load do not retire it. Closing a guest dialog or retiring its load removes
its source on synchronization and immediately blocks its old action tokens.
Publisher disposal cancels and drains pending native requests. The process owner
must also retire the Host session to remove its remaining documents.

Tests use two real Wasm guests and a controlled native-state port. They cover
ordinary/modal form isolation, typed model updates, simultaneous windows,
native-close races, lost open and mutation replies, malformed state, targeted
contribution retirement, lifecycle events and cancellation during a state read.
These tests do not claim that production Worker composition has switched owners.

## Keyboard shortcuts

Register `UICommand` and `UIShortcut` during the plugin's initialization stages.
The SDK rejects runtime shortcut registration with `STAGE_DENIED`. A shortcut's
scope must be a page owned by that load or the public workspace view
`nexa:plugins/content`. Registration alone does not imply a mounted page or an
available native shortcut service.

The production presentation composition supplies one optional shortcut port to
the same publisher that owns ordinary pages, workspace content and dialogs:

```csharp
var shortcuts = await SidecarShortcutHost.CreateAsync(requests, observer, token);
await using var publisher = new PluginPagePublisher(application, presentation,
    management, dialogs: dialogPort, shortcuts: shortcuts);
await publisher.SynchronizeAsync(token);
```

The port requires the independently discovered ShortcutControl catalog from
shared Host packages `0.1.0-alpha.20`. An unavailable catalog must be reported by
the Worker composition; successful local SDK registration cannot stand in for
native publication. The deployed Worker is still the remote-execution composition
while the remaining migration is in progress.

After acknowledging the document set, the publisher binds each shortcut to its
own ordinary page or per-load workspace document. Modal copies of a page do not
inherit its shortcuts. The native request contains a document index, platform
gesture, the single registered Sidecar action command and an opaque token. Host
indices may change after reconciliation; plugin IDs and registration handles stay
local. Tokens include the load, page, publication epoch and both registration
handles, and therefore cannot refer to a replacement load or re-registered action.

The callback carries an empty form. The publisher rejects supplied form fields
and calls `Actions.Invoke` with `Empty` inputs through the ordinary confirmation,
permission and asynchronous operation pipeline. The action must accept that
schema. A typed page model or text draft is not a shortcut input. Both the shortcut
and command must still be registered when the callback arrives; removing either
invalidates its token before another synchronization tick.

For duplicate gestures within one document, the first shortcut in ordinal
`DefinitionId` order wins. Matching ignores case and trims surrounding whitespace.
After that registration is removed, synchronization can publish the next matching
registration. The complete native set is limited to 64 bindings; an oversized set
fails instead of silently omitting arbitrary shortcuts. The Host additionally
checks visible-page scope, text focus and modal/onboarding state, and applies its
own keyboard handlers first.

Model-only updates retain tokens. Source replacement and uncertain source-update
recovery create fresh publication epochs. A lost shortcut-set acknowledgement
clears local shortcut admission while preserving the acknowledged document
revision. Explicit synchronization can re-establish the absolute binding set; it
never invokes or replays an action. Native bindings remain tied to the session,
so the process owner must retire that session when disposing the publisher.

`shortcuts.zip`, produced by `eng/build_wasm_sample.py`, is a real Wasm test guest
that registers page and workspace shortcuts plus a duplicate gesture during
initialization. Publisher tests exercise selection, real command results, empty
inputs, immediate retirement, uncertain acknowledgements and cancellation.

## Drawing pixels in the Sidecar

`PluginDrawingRenderer` converts an application drawing snapshot into a PNG. It
runs in the Sidecar and uses the application's blob reader to enforce load and
resource ownership:

```csharp
var renderer = new PluginDrawingRenderer(application.CopyBlob);
foreach (var drawing in application.Drawings())
{
    PluginRasterImage image = renderer.Render(drawing, cancellationToken);
    // The native raster adapter must bind image to this drawing's exact view
    // and scoped slot after checking that the view is still published.
}
```

Construct the renderer once and reuse it. Each call has independent native state;
clipping, transforms and decoded images do not carry over to the next drawing.
The caller owns the returned encoded bytes. `Width` and `Height` describe the
pixel buffer, rounded up from the logical surface dimensions. Preserve the
original logical dimensions when placing the image in a view.

Commands run in submission order against a transparent surface. Supported kinds
are line, rectangle, ellipse, path, text, image, clip and affine transform. Line
width is clamped to 0.1–128 pixels and font size to 1–128 pixels, matching the
existing drawing behavior. Text uses the platform's default typeface when it contains usable glyphs. If
the system default cannot be decoded, it selects an installed fallback; a system
without any usable font reports an error instead of drawing blank text. Exact
font pixels can differ between operating systems. Image commands decode the first
frame and preserve alpha; they do not open paths or fetch URLs.

The renderer rejects surfaces outside 1–2048 logical pixels per dimension, more
than 1,024 commands, malformed command shapes and nonfinite or out-of-range
coordinates. Images must decode successfully, fit within 4,096 pixels on each
axis and contain at most 4,194,304 pixels. The per-call decoded-image cache has the
same pixel budget: it evicts earlier images when necessary and can decode them
again later. It therefore bounds memory without rejecting a valid sequence that
uses several large images. Output is limited to 16 MiB of encoded PNG data.

The blob callback must return a private byte array. The renderer clears that array
on both success and failure, including cancellation, and disposes all decoded
images at the end of the call. `application.CopyBlob` verifies the full reference
and load owner; a released blob or stopped load is rejected on the next render.
No decoded cache survives a call to bypass those checks. The publication owner
must separately verify that a completed render still belongs to a live view
before sending it to the Host.

Cancellation is observed before allocation, between commands, after image decode
and after PNG encoding. A native decode or encode already in progress finishes
before cancellation is reported. Invalid input throws `InvalidDataException`;
blob ownership failures retain the application's ABI error. Do not retry a stale
resource under a different load identity.

The presentation project includes SkiaSharp and its platform native assets. Keep
the RID-specific `libSkiaSharp.so`, `libSkiaSharp.dylib` or `libSkiaSharp.dll` in the
published output. `DrawingRendererChecks` exercises actual pixel output, bounded
image-cache eviction, malformed images, cleared temporary data and resource
retirement with a real Wasm load, in managed and NativeAOT test binaries.

Native raster upload, scoped slot attachment, input callbacks and production
Worker composition are not enabled by this renderer alone. The old Desktop
renderer remains active until those paths have passed integration checks.

## Publishing drawing surfaces and forwarding input

Supply `IPluginRasterHost` as the publisher's `rasters` argument. The publisher
synchronizes the complete raster set after acknowledging documents, models and
modal state. A live drawing without this port reports `CAPABILITY_UNAVAILABLE`;
it is not silently treated as a successfully displayed canvas.

A surface named `example.plugin:canvas` resolves the local key `canvas` in the
owner's published view. `PluginPreparedView.ScopedKeys` records the native key
mapping. Ordinary surfaces use the load's view scope; modal surfaces require the
exact dialog view scope. The publisher omits a surface if no eligible slot exists
or more than one view matches. It never attaches a guest surface to management or
workspace controls, or to another plugin's view.

Each `PluginRasterBinding` contains the document index at the acknowledged
revision, scoped slot key, logical dimensions, encoded image and input token.
`ReplaceAsync` represents the entire desired set: an empty set clears all
bindings. The native adapter must validate document ownership and the expected
revision, bound uploads, and remove resources when the session or document
retires. The publisher retains raster output while the drawing snapshot is
unchanged. Updating the drawing revision generates fresh pixels; a model-only
update retains both image and input identity when the source and slot survive.

Register `PluginViewCompiler.DrawingInputCommand` as a separate generated-DTO
callback and forward its payload to `ForwardDrawingInputAsync`. The payload uses
the general `SidecarUiForms` envelope: the argument is the binding's opaque token,
and the observation fields are:

| Field | Meaning and bounds |
| --- | --- |
| `kind` | Exact `Pointer`, `Key`, `Text` or `Focus` name; numeric enum values are rejected. |
| `phase` | Native observation phase, at most 64 characters. |
| `x`, `y` | Finite coordinates formatted with invariant culture. |
| `text` | Observation text, at most 4,096 characters; use an empty value when absent. |
| `gap` | Optional `true` or `false`; reports coalesced or truncated native input. Omission means `false`. |

These fields describe a native observation, not the surrounding page's draft
inputs. The native owner must send only events from the visible, enabled owned
surface and respect the current page and modal boundary. The publisher checks the
live plugin, current published scope and existing surface before forwarding the
event. Ordinary action callbacks cannot consume drawing tokens. Existing
application subscriptions retain their permission, bounded queue and callback
coalescing behavior.

A lost raster-set reply disables local input admission until an explicit next
synchronization establishes the complete set. It does not discard the acknowledged
source revision or replay input. A lost source/model reply instead requires source
recovery, which creates a new publication epoch and rejects old input tokens.
Closing a dialog or stopping its plugin rejects input immediately, before the next
synchronization. Disposal cancels an in-flight port call and drains the publisher's
serialization gate.

`DrawingPublisherChecks` tests these rules with a real Wasm application and an
in-memory raster port, including native-reply loss and cancellation. This test
establishes Sidecar policy and ownership. The native adapter now discovers the independent Host catalog;
binary fixture tests cover chunking, deduplication, exact callback registration,
uncertain acknowledgements, explicit recovery and cancellation cleanup. Native
Host tests separately verify actual PNG decoding, atomic binding replacement,
visible-surface input routing and session retirement. Production Worker
composition is still required before switching the deployed drawing owner.

## Connecting the native raster port

Create the adapter after Host activation and keep it alive for the publisher's
lifetime:

```csharp
await using var rasters = await SidecarRasterHost.CreateAsync(requests, observer, token);
// Pass rasters: rasters to PluginPagePublisher, together with the native page,
// dialog and shortcut ports. Register PluginViewCompiler.DrawingInputCommand
// as a GeneratedDto callback and negotiate Nxml, BinaryPayloads and UiForms.
```

Each synchronization sends an absolute binding set for the acknowledged source
revision. Pixel buffers are copied before asynchronous work; identical PNG
content shares one upload. Each chunk is at most 64 KiB. SHA-256 verification
and full image decoding occur before the Host admits the sealed image.

If any reply is lost, the adapter discards its trusted upload cache. It does not
retry the mutation: the publisher disables input until the next explicit
synchronization. Cleanup uses an independent three-second deadline, including
after caller cancellation. Bound images cannot be released; the next
acknowledged replacement releases obsolete IDs, and session retirement removes
all remaining resources. An expired cached resource can cause one rejected
replacement; the following explicit synchronization uploads fresh content.

Dispose the publisher before the raster port. Local disposal drains pending
publication; native session retirement owns final remote cleanup. Do not reuse
the adapter across Host sessions or attempt to use its IDs as guest blob handles.
