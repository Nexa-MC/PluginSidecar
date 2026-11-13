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

Production publication, management UI, dialogs, contributions, shortcuts, drawing
and lifecycle visibility events remain to be composed and verified. These tests
do not authorize removing the old Desktop controller or publishing SDK Beta.
